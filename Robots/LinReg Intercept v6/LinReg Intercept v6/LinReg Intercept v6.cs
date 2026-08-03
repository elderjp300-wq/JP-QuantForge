using System;
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

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class LinRegInterceptv6 : Robot
    {
        #region Strategy Parameters

        [Parameter("Fast LinReg Period", Group = "Strategy Indicators", DefaultValue = 7, MinValue = 2)]
        public int FastPeriod { get; set; }

        [Parameter("Slow LinReg Period", Group = "Strategy Indicators", DefaultValue = 21, MinValue = 2)]
        public int SlowPeriod { get; set; }

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

        [Parameter("Instance Label", Group = "Execution", DefaultValue = "LINREG_INT_v6")]
        public string InstanceLabel { get; set; }

        #endregion

        #region Private Fields

        private AverageTrueRange _atr;
        private TradeType? _pendingSignal = null;
        private int _pendingSignalBar = -1;

        #endregion

        #region Lifecycle Methods

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrLength, MovingAverageType.WilderSmoothing);

            Print("[INIT] LinReg Intercept v6 | Asset: {0} | TF: {1} | TrailingMode: {2} | Risk: {3}% ({4})",
                SymbolName, TimeFrame, TrailingType, RiskPercent, CapitalBase);
        }

        protected override void OnBar()
        {
            // 1. Manage Dynamic ATR Trailing on Completed Bar Close
            if (TrailingType == TrailingMode.AtrTrail)
            {
                ManageDynamicAtrTrailing();
            }

            // 2. Clear expired pending signals from previous bar
            if (_pendingSignal.HasValue && _pendingSignalBar != Bars.Count - 1)
            {
                Print("[SIGNAL EXPIRED] Pending {0} signal from bar #{1} expired unexecuted.", _pendingSignal, _pendingSignalBar);
                _pendingSignal = null;
                _pendingSignalBar = -1;
            }

            // 3. Ensure sufficient historical bars exist
            int minRequiredBars = Math.Max(FastPeriod, SlowPeriod) + 10;
            if (Bars.Count <= minRequiredBars)
                return;

            // 4. Compute Fast & Slow LinReg Intercepts for bar t=1 and t=2
            double fastCurr = CalculateLinRegIntercept(1, FastPeriod);
            double fastPrev = CalculateLinRegIntercept(2, FastPeriod);

            double slowCurr = CalculateLinRegIntercept(1, SlowPeriod);
            double slowPrev = CalculateLinRegIntercept(2, SlowPeriod);

            bool longCross = (fastPrev <= slowPrev) && (fastCurr > slowCurr);
            bool shortCross = (fastPrev >= slowPrev) && (fastCurr < slowCurr);

            if (longCross)
            {
                Print("[CROSSOVER DETECTED] BULLISH Cross at Bar #{0} | Fast Intercept: {1:F5} > Slow Intercept: {2:F5}",
                    Bars.Count - 1, fastCurr, slowCurr);
                SetPendingSignal(TradeType.Buy);
            }
            else if (shortCross)
            {
                Print("[CROSSOVER DETECTED] BEARISH Cross at Bar #{0} | Fast Intercept: {1:F5} < Slow Intercept: {2:F5}",
                    Bars.Count - 1, fastCurr, slowCurr);
                SetPendingSignal(TradeType.Sell);
            }

            // 5. Attempt execution immediately if tick conditions permit
            ProcessPendingSignal();
        }

        protected override void OnTick()
        {
            // Process pending signal if spread was too wide at bar open
            if (_pendingSignal.HasValue)
            {
                ProcessPendingSignal();
            }
        }

        #endregion

        #region Core Execution Engine

        private void SetPendingSignal(TradeType type)
        {
            _pendingSignal = type;
            _pendingSignalBar = Bars.Count - 1;
        }

        private void ProcessPendingSignal()
        {
            if (!_pendingSignal.HasValue)
                return;

            TradeType targetType = _pendingSignal.Value;

            // Check Session Filter
            if (!IsWithinTradingSession())
            {
                Print("[EXECUTION BLOCKED] Signal {0} suppressed - outside trading session ({1}:00 UTC).",
                    targetType, Server.Time.Hour);
                _pendingSignal = null;
                return;
            }

            // Check Spread Condition
            double currentSpreadPips = Symbol.Spread / Symbol.PipSize;
            if (currentSpreadPips > MaxSpreadPips)
            {
                // Wait for next tick within the same bar
                return;
            }

            // Handle Active Positions / Atomic Reversals
            var activePosition = Positions.FirstOrDefault(p => p.Label == InstanceLabel && p.SymbolName == SymbolName);
            if (activePosition != null)
            {
                if (activePosition.TradeType == targetType)
                {
                    _pendingSignal = null;
                    return;
                }

                Print("[REVERSAL] Closing existing {0} position #{1} prior to opening {2}.",
                    activePosition.TradeType, activePosition.Id, targetType);

                var closeResult = ClosePosition(activePosition);
                if (!closeResult.IsSuccessful)
                {
                    Print("[ERROR] Failed to close position #{0} for reversal. Reason: {1}",
                        activePosition.Id, closeResult.Error);
                    return; // Will retry on next tick
                }
            }

            // Calculate SL Distance
            double slDistancePips = GetInitialStopLossPips();
            if (slDistancePips <= 0)
            {
                Print("[ERROR] Invalid SL distance ({0:F2} pips). Signal aborted.", slDistancePips);
                _pendingSignal = null;
                return;
            }

            // Calculate Volume
            double volumeInUnits = CalculateVolume(slDistancePips);
            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print("[ERROR] Calculated volume ({0}) below symbol minimum ({1}). Order aborted.",
                    volumeInUnits, Symbol.VolumeInUnitsMin);
                _pendingSignal = null;
                return;
            }

            // Native Trailing Flag (True only for PipsTrail mode)
            bool useNativeTsl = (TrailingType == TrailingMode.PipsTrail);

            // Execute Market Order
            var result = ExecuteMarketOrder(targetType, SymbolName, volumeInUnits, InstanceLabel, slDistancePips, null, null, useNativeTsl);
            
            if (result.IsSuccessful)
            {
                Print("[ORDER SUCCESS] #{0} {1} | Vol: {2} units | SL: {3:F1} pips | Native TSL: {4}",
                    result.Position.Id, targetType, volumeInUnits, slDistancePips, useNativeTsl);
                _pendingSignal = null;
            }
            else
            {
                Print("[ORDER FAILED] Reason: {0}", result.Error);
            }
        }

        private double GetInitialStopLossPips()
        {
            if (TrailingType == TrailingMode.PipsTrail)
            {
                return PipsStopLoss;
            }

            // AtrTrail or Disabled with ATR
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
            var position = Positions.FirstOrDefault(p => p.Label == InstanceLabel && p.SymbolName == SymbolName);
            if (position == null)
                return;

            double atrVal = _atr.Result.Last(1);
            if (double.IsNaN(atrVal) || atrVal <= 0)
                return;

            double trailDistance = atrVal * AtrSlMultiplier;
            double minStepInPrice = AtrStepPips * Symbol.PipSize;

            if (position.TradeType == TradeType.Buy)
            {
                double currentClose = Bars.ClosePrices.Last(1);
                double targetSl = currentClose - trailDistance;

                if (!position.StopLoss.HasValue || (targetSl - position.StopLoss.Value >= minStepInPrice))
                {
                    ModifyPosition(position, targetSl, position.TakeProfit, ProtectionType.Absolute);
                    Print("[DYNAMIC ATR TRAIL] Updated Buy SL to {0:F5} (Step >= {1:F1} pips)", targetSl, AtrStepPips);
                }
            }
            else if (position.TradeType == TradeType.Sell)
            {
                double currentClose = Bars.ClosePrices.Last(1);
                double targetSl = currentClose + trailDistance;

                if (!position.StopLoss.HasValue || (position.StopLoss.Value - targetSl >= minStepInPrice))
                {
                    ModifyPosition(position, targetSl, position.TakeProfit, ProtectionType.Absolute);
                    Print("[DYNAMIC ATR TRAIL] Updated Sell SL to {0:F5} (Step >= {1:F1} pips)", targetSl, AtrStepPips);
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

        #region Custom Indicator Engine (Linear Regression Intercept)

        private double CalculateLinRegIntercept(int shift, int length)
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

            return intercept;
        }

        #endregion
    }
}
