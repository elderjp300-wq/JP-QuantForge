using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
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
    public class EMACross : Robot
    {
        #region Strategy Parameters

        [Parameter("Fast EMA Period", Group = "Moving Averages", DefaultValue = 9, MinValue = 1)]
        public int FastEmaPeriod { get; set; }

        [Parameter("Slow EMA Period", Group = "Moving Averages", DefaultValue = 21, MinValue = 2)]
        public int SlowEmaPeriod { get; set; }

        [Parameter("Enable Session Filter", Group = "Session Filter", DefaultValue = false)]
        public bool EnableSessionFilter { get; set; }

        [Parameter("Start Hour (UTC)", Group = "Session Filter", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int StartHourUtc { get; set; }

        [Parameter("End Hour (UTC)", Group = "Session Filter", DefaultValue = 17, MinValue = 0, MaxValue = 23)]
        public int EndHourUtc { get; set; }

        [Parameter("ATR Length", Group = "Risk & Exits", DefaultValue = 14, MinValue = 1)]
        public int AtrLength { get; set; }

        [Parameter("ATR SL Multiplier", Group = "Risk & Exits", DefaultValue = 2.0, MinValue = 0.1, Step = 0.1)]
        public double AtrSlMultiplier { get; set; }

        [Parameter("Enable Trailing Stop", Group = "Risk & Exits", DefaultValue = true)]
        public bool EnableTrailingStop { get; set; }

        [Parameter("Exit on Opposite Cross", Group = "Risk & Exits", DefaultValue = true)]
        public bool ExitOnOppositeCross { get; set; }

        [Parameter("Sizing Mode", Group = "Position Sizing", DefaultValue = SizingMode.RiskPercentage)]
        public SizingMode Sizing { get; set; }

        [Parameter("Capital Base", Group = "Position Sizing", DefaultValue = CapitalType.Equity)]
        public CapitalType CapitalBase { get; set; }

        [Parameter("Risk Percentage (%)", Group = "Position Sizing", DefaultValue = 1.0, MinValue = 0.01, Step = 0.1)]
        public double RiskPercent { get; set; }

        [Parameter("Fixed Volume (Lots)", Group = "Position Sizing", DefaultValue = 0.1, MinValue = 0.01, Step = 0.01)]
        public double FixedVolumeLots { get; set; }

        [Parameter("Max Spread (Pips)", Group = "Filters", DefaultValue = 3.0, MinValue = 0.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Instance Label", Group = "Execution", DefaultValue = "EMA_Cross")]
        public string InstanceLabel { get; set; }

        #endregion

        #region Internal Fields

        private ExponentialMovingAverage _fastEma;
        private ExponentialMovingAverage _slowEma;
        private AverageTrueRange _atr;

        #endregion

        #region Lifecycle Methods

        protected override void OnStart()
        {
            if (FastEmaPeriod >= SlowEmaPeriod)
            {
                Print("WARNING: Fast EMA Period ({0}) should be smaller than Slow EMA Period ({1}).", FastEmaPeriod, SlowEmaPeriod);
            }

            _fastEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, FastEmaPeriod);
            _slowEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, SlowEmaPeriod);
            _atr = Indicators.AverageTrueRange(AtrLength, MovingAverageType.WilderSmoothing);

            Print("EMA Cross initialized on {0} [{1}] (Fast: {2}, Slow: {3}).", SymbolName, TimeFrame, FastEmaPeriod, SlowEmaPeriod);
        }

        protected override void OnBar()
        {
            // 1. Manage Trailing Stop on candle close
            if (EnableTrailingStop)
            {
                ManageAtrTrailingStop();
            }

            // Require sufficient completed bars
            int requiredBars = Math.Max(SlowEmaPeriod, AtrLength) + 2;
            if (Bars.Count <= requiredBars)
                return;

            // Index 1 = Most recently closed candle, Index 2 = Candle before index 1
            double fastPrev = _fastEma.Result.Last(2);
            double fastCurr = _fastEma.Result.Last(1);
            double slowPrev = _slowEma.Result.Last(2);
            double slowCurr = _slowEma.Result.Last(1);

            bool bullishCross = fastPrev <= slowPrev && fastCurr > slowCurr;
            bool bearishCross = fastPrev >= slowPrev && fastCurr < slowCurr;

            if (!bullishCross && !bearishCross)
                return;

            // 2. Check Time Session Filter
            if (!IsWithinTradingSession())
            {
                Print("Signal detected ({0}), but trading is restricted outside session window ({1}:00 - {2}:00 UTC).", 
                    bullishCross ? "Bullish Cross" : "Bearish Cross", StartHourUtc, EndHourUtc);
                return;
            }

            // 3. Process Crossover Signals
            if (bullishCross)
            {
                HandleSignal(TradeType.Buy);
            }
            else if (bearishCross)
            {
                HandleSignal(TradeType.Sell);
            }
        }

        #endregion

        #region Execution & Helper Methods

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
                // Overnight session window (e.g., 22:00 to 06:00 UTC)
                return currentHour >= StartHourUtc || currentHour < EndHourUtc;
            }
        }

        private void HandleSignal(TradeType targetType)
        {
            var activePosition = Positions.FirstOrDefault(p => p.Label == InstanceLabel && p.SymbolName == SymbolName);

            // Reversal / Exit check
            if (activePosition != null)
            {
                if (activePosition.TradeType != targetType)
                {
                    if (ExitOnOppositeCross)
                    {
                        Print("Opposite cross detected. Closing active {0} position #{1}.", activePosition.TradeType, activePosition.Id);
                        ClosePosition(activePosition);
                    }
                    else
                    {
                        // Opposite exit disabled, skip new trade
                        return;
                    }
                }
                else
                {
                    // Already holding position in target direction
                    return;
                }
            }

            // Spread check
            double currentSpreadPips = Symbol.Spread / Symbol.PipSize;
            if (currentSpreadPips > MaxSpreadPips)
            {
                Print("Execution suppressed for {0}: Current spread ({1:F2} pips) exceeds maximum allowed ({2:F2} pips).",
                    targetType, currentSpreadPips, MaxSpreadPips);
                return;
            }

            // Calculate SL distance based on ATR
            double atrVal = _atr.Result.Last(1);
            double slDistancePips = (atrVal * AtrSlMultiplier) / Symbol.PipSize;

            if (slDistancePips <= 0)
            {
                Print("ERROR: Calculated SL distance is invalid ({0:F2} pips). Order cancelled.", slDistancePips);
                return;
            }

            double volumeInUnits = CalculateVolume(slDistancePips);

            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print("Calculated volume ({0}) is below symbol minimum ({1}). Order aborted.", volumeInUnits, Symbol.VolumeInUnitsMin);
                return;
            }

            // Place Order
            var result = ExecuteMarketOrder(targetType, SymbolName, volumeInUnits, InstanceLabel, slDistancePips, null);
            if (result.IsSuccessful)
            {
                Print("SUCCESS: Opened {0} order #{1} | Volume: {2} units | SL: {3:F1} pips.", 
                    targetType, result.Position.Id, volumeInUnits, slDistancePips);
            }
            else
            {
                Print("ERROR: Failed to open {0} order. Reason: {1}", targetType, result.Error);
            }
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

        private void ManageAtrTrailingStop()
        {
            var position = Positions.FirstOrDefault(p => p.Label == InstanceLabel && p.SymbolName == SymbolName);
            if (position == null)
                return;

            double atrVal = _atr.Result.Last(1);
            double trailingDistance = atrVal * AtrSlMultiplier;

            if (position.TradeType == TradeType.Buy)
            {
                double targetSl = Bars.ClosePrices.Last(1) - trailingDistance;
                if (!position.StopLoss.HasValue || targetSl > position.StopLoss.Value + (Symbol.PipSize * 0.1))
                {
                    ModifyPosition(position, targetSl, position.TakeProfit, ProtectionType.Absolute);
                }
            }
            else if (position.TradeType == TradeType.Sell)
            {
                double targetSl = Bars.ClosePrices.Last(1) + trailingDistance;
                if (!position.StopLoss.HasValue || targetSl < position.StopLoss.Value - (Symbol.PipSize * 0.1))
                {
                    ModifyPosition(position, targetSl, position.TakeProfit, ProtectionType.Absolute);
                }
            }
        }

        #endregion
    }
}
