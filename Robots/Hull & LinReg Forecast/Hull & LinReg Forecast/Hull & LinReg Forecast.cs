using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum TrailingMode
    {
        AtrTrail,
        PipsTrail,
        Disabled
    }

    public enum SizingMode
    {
        FixedLots,
        RiskPercentage
    }

    public enum CapitalType
    {
        Balance,
        Equity
    }

    public enum SignalCategory
    {
        PrimaryEntry,
        ScalingEntry
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class HullAndLinRegForecast : Robot
    {
        #region Strategy Parameters

        [Parameter("LinReg Forecast Period", Group = "Strategy Indicators", DefaultValue = 14, MinValue = 2)]
        public int LinRegPeriod { get; set; }

        [Parameter("Hull MA Period", Group = "Strategy Indicators", DefaultValue = 21, MinValue = 2)]
        public int HullPeriod { get; set; }

        [Parameter("Trailing Stop Mode", Group = "Trailing & Exits", DefaultValue = TrailingMode.AtrTrail)]
        public TrailingMode TrailingType { get; set; }

        [Parameter("Pips SL / Trailing Distance", Group = "Trailing & Exits", DefaultValue = 20.0, MinValue = 1.0, Step = 0.5)]
        public double PipsStopLoss { get; set; }

        [Parameter("ATR Length", Group = "Trailing & Exits", DefaultValue = 14, MinValue = 1)]
        public int AtrLength { get; set; }

        [Parameter("ATR SL Multiplier", Group = "Trailing & Exits", DefaultValue = 2.0, MinValue = 0.1, Step = 0.1)]
        public double AtrSlMultiplier { get; set; }

        [Parameter("ATR Step Filter (Pips)", Group = "Trailing & Exits", DefaultValue = 0.5, MinValue = 0.1, Step = 0.1)]
        public double AtrStepPips { get; set; }

        [Parameter("Enable Scaling", Group = "Scaling Engine", DefaultValue = false)]
        public bool EnableScaling { get; set; }

        [Parameter("Max Scaling Positions", Group = "Scaling Engine", DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int MaxPositions { get; set; }

        [Parameter("Allow 0.01 Min Lot Fallback", Group = "Scaling Engine", DefaultValue = true)]
        public bool AllowMinLotFallback { get; set; }

        [Parameter("Sizing Mode", Group = "Position Sizing", DefaultValue = SizingMode.RiskPercentage)]
        public SizingMode Sizing { get; set; }

        [Parameter("Capital Base", Group = "Position Sizing", DefaultValue = CapitalType.Equity)]
        public CapitalType CapitalBase { get; set; }

        [Parameter("Risk Percentage (%)", Group = "Position Sizing", DefaultValue = 0.25, MinValue = 0.01, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("Fixed Volume (Lots)", Group = "Position Sizing", DefaultValue = 0.1, MinValue = 0.01, Step = 0.01)]
        public double FixedVolumeLots { get; set; }

        [Parameter("Enable Session Filter", Group = "Filters", DefaultValue = false)]
        public bool EnableSessionFilter { get; set; }

        [Parameter("Start Hour (UTC)", Group = "Filters", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int StartHourUtc { get; set; }

        [Parameter("End Hour (UTC)", Group = "Filters", DefaultValue = 16, MinValue = 0, MaxValue = 23)]
        public int EndHourUtc { get; set; }

        [Parameter("Max Spread (Pips)", Group = "Filters", DefaultValue = 1.2, MinValue = 0.1, Step = 0.1)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Instance Label", Group = "Execution", DefaultValue = "HULL_LINREG_v2")]
        public string InstanceLabel { get; set; }

        #endregion

        #region Private Fields

        private AverageTrueRange _atr;

        private TradeType? _pendingSignalType = null;
        private SignalCategory _pendingSignalCategory = SignalCategory.PrimaryEntry;
        private int _pendingSignalBar = -1;
        private int _pendingScalingLevel = 0;
        private bool _isExecutingBasketClose = false;

        #endregion

        #region Lifecycle Methods

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrLength, MovingAverageType.WilderSmoothing);
            Positions.Closed += OnPositionsClosed;

            if (EnableScaling && MaxPositions < 1)
            {
                Print("[WARNING] Max Scaling Positions set to invalid value ({0}). Resetting to 1.", MaxPositions);
                MaxPositions = 1;
            }

            Print("[INIT] Hull & LinReg Forecast v2 | Asset: {0} | TF: {1} | Scaling: {2} (Max Pos: {3}) | TrailingMode: {4}",
                SymbolName, TimeFrame, EnableScaling ? "ENABLED" : "DISABLED", MaxPositions, TrailingType);
        }

        private void OnPositionsClosed(PositionClosedEventArgs args)
        {
            if (_isExecutingBasketClose)
                return;

            var pos = args.Position;
            if (pos.Label == InstanceLabel && pos.SymbolName == SymbolName)
            {
                var remainingBasket = GetBasketPositions();
                if (remainingBasket.Count > 0)
                {
                    Print("[SCALING EXIT PRIORITY] Position #{0} ({1}) closed by {2}. Liquidation triggered for remaining {3} basket position(s).",
                        pos.Id, pos.Comment ?? "Primary", args.Reason, remainingBasket.Count);

                    CloseBasket("Stop-Loss Priority Triggered");
                }
            }
        }

        protected override void OnBar()
        {
            // 1. Manage Dynamic ATR Trailing on Completed Bar Close for ALL basket positions
            if (TrailingType == TrailingMode.AtrTrail)
            {
                ManageDynamicAtrTrailing();
            }

            // 2. Clear expired pending signals from previous bar
            if (_pendingSignalType.HasValue && _pendingSignalBar != Bars.Count - 1)
            {
                Print("[SIGNAL EXPIRED] Pending {0} ({1}) signal from bar #{2} expired unexecuted.",
                    _pendingSignalCategory, _pendingSignalType, _pendingSignalBar);
                ClearPendingSignal();
            }

            // 3. Ensure sufficient historical bars exist
            int minRequiredBars = Math.Max(LinRegPeriod, HullPeriod) + 10;
            if (Bars.Count <= minRequiredBars)
                return;

            // 4. Compute Fast LinReg Forecast & Slow Hull MA for bar t=1 and t=2
            double linRegCurr = CalculateLinRegForecast(1, LinRegPeriod);
            double linRegPrev = CalculateLinRegForecast(2, LinRegPeriod);

            double hullCurr = CalculateHullMa(1, HullPeriod);
            double hullPrev = CalculateHullMa(2, HullPeriod);

            bool longCross = (linRegPrev <= hullPrev) && (linRegCurr > hullCurr);
            bool shortCross = (linRegPrev >= hullPrev) && (linRegCurr < hullCurr);

            bool primarySignalFired = false;

            if (longCross)
            {
                Print("[CROSSOVER DETECTED] BULLISH Cross at Bar #{0} | LinReg: {1:F5} > Hull: {2:F5}",
                    Bars.Count - 1, linRegCurr, hullCurr);
                SetPendingSignal(TradeType.Buy, SignalCategory.PrimaryEntry, 0);
                primarySignalFired = true;
            }
            else if (shortCross)
            {
                Print("[CROSSOVER DETECTED] BEARISH Cross at Bar #{0} | LinReg: {1:F5} < Hull: {2:F5}",
                    Bars.Count - 1, linRegCurr, hullCurr);
                SetPendingSignal(TradeType.Sell, SignalCategory.PrimaryEntry, 0);
                primarySignalFired = true;
            }

            // 5. Evaluate Risk-Aware Scaling Engine if no primary signal fired on this bar
            if (!primarySignalFired && EnableScaling)
            {
                EvaluateScalingEngine();
            }

            // 6. Attempt execution immediately if tick conditions permit
            ProcessPendingSignal();
        }

        protected override void OnTick()
        {
            // Process pending signal if spread was too wide at bar open
            if (_pendingSignalType.HasValue)
            {
                ProcessPendingSignal();
            }
        }

        #endregion

        #region Core Execution Engine

        private List<Position> GetBasketPositions()
        {
            return Positions.Where(p => p.Label == InstanceLabel && p.SymbolName == SymbolName)
                            .OrderBy(p => p.EntryTime)
                            .ToList();
        }

        private void SetPendingSignal(TradeType type, SignalCategory category, int scaleLevel)
        {
            _pendingSignalType = type;
            _pendingSignalCategory = category;
            _pendingScalingLevel = scaleLevel;
            _pendingSignalBar = Bars.Count - 1;
        }

        private void ClearPendingSignal()
        {
            _pendingSignalType = null;
            _pendingSignalCategory = SignalCategory.PrimaryEntry;
            _pendingScalingLevel = 0;
            _pendingSignalBar = -1;
        }

        private void EvaluateScalingEngine()
        {
            var basket = GetBasketPositions();
            if (basket.Count == 0)
                return;

            if (basket.Count >= MaxPositions)
            {
                return;
            }

            // Reference latest position in scaling chain
            var latestPos = basket.Last();

            bool isRiskFree = IsPositionRiskFree(latestPos);
            bool inProfit = latestPos.GrossProfit > 0;

            if (isRiskFree && inProfit)
            {
                int nextLevel = basket.Count;
                TradeType scaleDirection = latestPos.TradeType;

                Print("[SCALING TRIGGER] Position #{0} (Level {1}) is Risk-Free & in profit. Queueing Scale Level {2} (Position {3}).",
                    latestPos.Id, nextLevel - 1, nextLevel, GetPositionLetter(nextLevel));

                SetPendingSignal(scaleDirection, SignalCategory.ScalingEntry, nextLevel);
            }
        }

        private bool IsPositionRiskFree(Position pos)
        {
            if (!pos.StopLoss.HasValue)
                return false;

            double tolerance = Symbol.PipSize * 0.1;

            if (pos.TradeType == TradeType.Buy)
            {
                return pos.StopLoss.Value >= (pos.EntryPrice - tolerance);
            }
            else
            {
                return pos.StopLoss.Value <= (pos.EntryPrice + tolerance);
            }
        }

        private string GetPositionLetter(int level)
        {
            return ((char)('A' + level)).ToString();
        }

        private void ProcessPendingSignal()
        {
            if (!_pendingSignalType.HasValue)
                return;

            TradeType targetType = _pendingSignalType.Value;

            // Session Filter Check
            if (!IsWithinTradingSession())
            {
                Print("[EXECUTION BLOCKED] {0} {1} suppressed - outside trading session ({2}:00 UTC).",
                    _pendingSignalCategory, targetType, Server.Time.Hour);
                ClearPendingSignal();
                return;
            }

            // Spread Condition Check
            double currentSpreadPips = Symbol.Spread / Symbol.PipSize;
            if (currentSpreadPips > MaxSpreadPips)
            {
                return; // Retry on next tick within the same bar
            }

            if (_pendingSignalCategory == SignalCategory.PrimaryEntry)
            {
                ExecutePrimaryEntry(targetType);
            }
            else if (_pendingSignalCategory == SignalCategory.ScalingEntry)
            {
                ExecuteScalingEntry(targetType, _pendingScalingLevel);
            }
        }

        private void ExecutePrimaryEntry(TradeType targetType)
        {
            var basket = GetBasketPositions();
            if (basket.Count > 0)
            {
                var firstPos = basket.First();
                if (firstPos.TradeType == targetType)
                {
                    ClearPendingSignal();
                    return;
                }

                Print("[REVERSAL] Opposite signal detected. Closing active basket of {0} position(s) prior to opening {1}.",
                    basket.Count, targetType);

                if (!CloseBasket("Strategy Reversal"))
                {
                    return; // Retry on next tick if close failed
                }
            }

            double slDistancePips = GetInitialStopLossPips();
            if (slDistancePips <= 0)
            {
                Print("[ERROR] Primary entry SL distance ({0:F2} pips) invalid. Order aborted.", slDistancePips);
                ClearPendingSignal();
                return;
            }

            double volumeInUnits = CalculateVolume(slDistancePips);
            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print("[ERROR] Primary entry volume ({0}) below minimum ({1}). Order aborted.",
                    volumeInUnits, Symbol.VolumeInUnitsMin);
                ClearPendingSignal();
                return;
            }

            bool useNativeTsl = (TrailingType == TrailingMode.PipsTrail);
            string comment = "Position_A";

            var result = ExecuteMarketOrder(targetType, SymbolName, volumeInUnits, InstanceLabel, slDistancePips, null, comment, useNativeTsl);

            if (result.IsSuccessful)
            {
                Print("[ORDER SUCCESS] Primary Position A #{0} {1} | Vol: {2} units | SL: {3:F1} pips | Mode: {4}",
                    result.Position.Id, targetType, volumeInUnits, slDistancePips, TrailingType);
                ClearPendingSignal();
            }
            else
            {
                Print("[ORDER FAILED] Reason: {0}", result.Error);
            }
        }

        private void ExecuteScalingEntry(TradeType targetType, int level)
        {
            var basket = GetBasketPositions();
            if (basket.Count == 0)
            {
                Print("[SCALING SKIPPED] Primary position no longer active.");
                ClearPendingSignal();
                return;
            }

            if (basket.Count >= MaxPositions)
            {
                Print("[SCALING SKIPPED] Maximum basket positions ({0}) reached.", MaxPositions);
                ClearPendingSignal();
                return;
            }

            var posA = basket.First();
            double originalVolume = posA.VolumeInUnits;

            double slDistancePips = GetInitialStopLossPips();
            if (slDistancePips <= 0)
            {
                Print("[SCALING SKIPPED] Calculated SL distance ({0:F2} pips) invalid.", slDistancePips);
                ClearPendingSignal();
                return;
            }

            double scalingVolume = CalculateScalingVolume(slDistancePips, originalVolume);
            if (scalingVolume <= 0)
            {
                ClearPendingSignal();
                return;
            }

            bool useNativeTsl = (TrailingType == TrailingMode.PipsTrail);
            string comment = string.Format("Position_{0}", GetPositionLetter(level));

            var result = ExecuteMarketOrder(targetType, SymbolName, scalingVolume, InstanceLabel, slDistancePips, null, comment, useNativeTsl);

            if (result.IsSuccessful)
            {
                Print("[SCALING SUCCESS] Scale Position {0} (Level {1}) #{2} {3} | Vol: {4} units | SL: {5:F1} pips",
                    GetPositionLetter(level), level, result.Position.Id, targetType, scalingVolume, slDistancePips);
                ClearPendingSignal();
            }
            else
            {
                Print("[SCALING FAILED] Broker rejected order. Reason: {0}", result.Error);
            }
        }

        private double CalculateScalingVolume(double slDistancePips, double originalVolume)
        {
            double calculatedVolume = CalculateVolume(slDistancePips);

            // Cap volume at original Position A volume
            if (calculatedVolume > originalVolume)
            {
                calculatedVolume = originalVolume;
            }

            // Min lot fallback logic
            if (calculatedVolume < Symbol.VolumeInUnitsMin)
            {
                if (AllowMinLotFallback)
                {
                    Print("[SCALING VOLUME] Calculated scaling volume ({0}) below minimum. Fallback to minimum lot size ({1} units) applied.",
                        calculatedVolume, Symbol.VolumeInUnitsMin);
                    calculatedVolume = Symbol.VolumeInUnitsMin;
                }
                else
                {
                    Print("[SCALING SKIPPED] Calculated scaling volume ({0}) below minimum allowed ({1}). Fallback disabled.",
                        calculatedVolume, Symbol.VolumeInUnitsMin);
                    return 0;
                }
            }

            // Free Margin Check (1:20 Leverage Protection)
            double requiredMargin = Symbol.GetEstimatedMargin(TradeType.Buy, calculatedVolume);
            if (Account.FreeMargin < requiredMargin)
            {
                Print("[SCALING SKIPPED] Insufficient Free Margin ({0:F2}) for required margin ({1:F2}). Scale aborted.",
                    Account.FreeMargin, requiredMargin);
                return 0;
            }

            return calculatedVolume;
        }

        private bool CloseBasket(string reason)
        {
            var basket = GetBasketPositions();
            if (basket.Count == 0)
                return true;

            _isExecutingBasketClose = true;
            bool allSuccessful = true;

            Print("[BASKET CLOSE] Closing all {0} position(s) in basket. Reason: {1}", basket.Count, reason);

            foreach (var pos in basket)
            {
                var result = ClosePosition(pos);
                if (!result.IsSuccessful)
                {
                    Print("[ERROR] Failed to close basket position #{0}. Reason: {1}", pos.Id, result.Error);
                    allSuccessful = false;
                }
            }

            _isExecutingBasketClose = false;
            return allSuccessful;
        }

        private double GetInitialStopLossPips()
        {
            if (TrailingType == TrailingMode.PipsTrail)
            {
                return PipsStopLoss;
            }

            double atrVal = _atr.Result.Last(1);
            if (double.IsNaN(atrVal) || atrVal <= 0)
                return PipsStopLoss;

            return (atrVal * AtrSlMultiplier) / Symbol.PipSize;
        }

        private double CalculateVolume(double slDistancePips)
        {
            if (Sizing == SizingMode.FixedLots)
            {
                return Symbol.QuantityToVolumeInUnits(FixedVolumeLots);
            }

            double capital = CapitalBase == CapitalType.Equity ? Account.Equity : Account.Balance;
            double riskAmount = capital * (RiskPercent / 100.0);

            double lossPerUnit = slDistancePips * Symbol.PipValue;
            if (lossPerUnit <= 0)
                return Symbol.VolumeInUnitsMin;

            double rawVolume = riskAmount / lossPerUnit;
            double normalizedVolume = Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down);

            if (normalizedVolume < Symbol.VolumeInUnitsMin)
                normalizedVolume = Symbol.VolumeInUnitsMin;

            if (normalizedVolume > Symbol.VolumeInUnitsMax)
                normalizedVolume = Symbol.VolumeInUnitsMax;

            return normalizedVolume;
        }

        private void ManageDynamicAtrTrailing()
        {
            var basket = GetBasketPositions();
            if (basket.Count == 0)
                return;

            double atrVal = _atr.Result.Last(1);
            if (double.IsNaN(atrVal) || atrVal <= 0)
                return;

            double trailDistance = atrVal * AtrSlMultiplier;
            double minStepInPrice = AtrStepPips * Symbol.PipSize;
            double currentClose = Bars.ClosePrices.Last(1);

            foreach (var position in basket)
            {
                if (position.TradeType == TradeType.Buy)
                {
                    double targetSl = currentClose - trailDistance;

                    if (!position.StopLoss.HasValue || (targetSl - position.StopLoss.Value >= minStepInPrice))
                    {
                        ModifyPosition(position, targetSl, position.TakeProfit, ProtectionType.Absolute);
                        Print("[DYNAMIC ATR TRAIL] Updated Buy SL for #{0} ({1}) to {2:F5} (Step >= {3:F1} pips)",
                            position.Id, position.Comment ?? "Primary", targetSl, AtrStepPips);
                    }
                }
                else if (position.TradeType == TradeType.Sell)
                {
                    double targetSl = currentClose + trailDistance;

                    if (!position.StopLoss.HasValue || (position.StopLoss.Value - targetSl >= minStepInPrice))
                    {
                        ModifyPosition(position, targetSl, position.TakeProfit, ProtectionType.Absolute);
                        Print("[DYNAMIC ATR TRAIL] Updated Sell SL for #{0} ({1}) to {2:F5} (Step >= {3:F1} pips)",
                            position.Id, position.Comment ?? "Primary", targetSl, AtrStepPips);
                    }
                }
            }
        }

        private bool IsWithinTradingSession()
        {
            if (!EnableSessionFilter)
                return true;

            int currentHour = Server.Time.Hour;
            if (StartHourUtc <= EndHourUtc)
            {
                return currentHour >= StartHourUtc && currentHour < EndHourUtc;
            }
            else
            {
                return currentHour >= StartHourUtc || currentHour < EndHourUtc;
            }
        }

        #endregion

        #region Custom Indicator Engine (Hull MA & LinReg Forecast)

        private double CalculateLinRegForecast(int shift, int length)
        {
            if (Bars.Count < shift + length)
                return Bars.ClosePrices.Last(shift);

            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

            for (int i = 0; i < length; i++)
            {
                double x = i;
                double y = Bars.ClosePrices.Last(shift + length - 1 - i);

                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;
            }

            double divisor = (length * sumX2 - sumX * sumX);
            if (Math.Abs(divisor) < 1e-9)
                return Bars.ClosePrices.Last(shift);

            double slope = (length * sumXY - sumX * sumY) / divisor;
            double intercept = (sumY - slope * sumX) / length;

            return intercept + slope * (length - 1);
        }

        private double CalculateHullMa(int shift, int length)
        {
            int halfLength = length / 2;
            int sqrtLength = (int)Math.Sqrt(length);

            if (halfLength < 1) halfLength = 1;
            if (sqrtLength < 1) sqrtLength = 1;

            if (Bars.Count < shift + length + sqrtLength)
                return Bars.ClosePrices.Last(shift);

            double[] rawHmaSeries = new double[sqrtLength];

            for (int j = 0; j < sqrtLength; j++)
            {
                int currentShift = shift + j;
                double wmaHalf = CalculateWma(currentShift, halfLength);
                double wmaFull = CalculateWma(currentShift, length);

                rawHmaSeries[j] = (2.0 * wmaHalf) - wmaFull;
            }

            double weightedSum = 0;
            double weightSum = 0;

            for (int k = 0; k < sqrtLength; k++)
            {
                double weight = sqrtLength - k;
                weightedSum += rawHmaSeries[k] * weight;
                weightSum += weight;
            }

            return weightSum > 0 ? weightedSum / weightSum : Bars.ClosePrices.Last(shift);
        }

        private double CalculateWma(int shift, int length)
        {
            double weightedSum = 0;
            double weightSum = 0;

            for (int i = 0; i < length; i++)
            {
                double weight = length - i;
                double price = Bars.ClosePrices.Last(shift + i);

                weightedSum += price * weight;
                weightSum += weight;
            }

            return weightSum > 0 ? weightedSum / weightSum : Bars.ClosePrices.Last(shift);
        }

        #endregion
    }
}
