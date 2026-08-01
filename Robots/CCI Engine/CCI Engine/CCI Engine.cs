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
    public class CCIEngine : Robot
    {
        #region Strategy Parameters

        [Parameter("CCI Period (L)", Group = "CCI Math", DefaultValue = 20, MinValue = 2)]
        public int CciPeriod { get; set; }

        [Parameter("Crossover Threshold (T)", Group = "CCI Math", DefaultValue = 100.0, MinValue = 1.0)]
        public double Threshold { get; set; }

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

        [Parameter("Instance Label", Group = "Execution", DefaultValue = "CCI_Engine")]
        public string InstanceLabel { get; set; }

        #endregion

        #region Internal Fields

        private AverageTrueRange _atr;
        
        /// <summary>
        /// State Variable P: -1 = Short, 0 = Neutral, +1 = Long
        /// </summary>
        private int _stateP = 0;

        #endregion

        #region Lifecycle Methods

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrLength, MovingAverageType.WilderSmoothing);

            // Reconstruct internal state P on startup
            SyncStateP();

            Print("CCI Engine initialized on {0} [{1}] | Period: {2}, Threshold: +/-{3}, Initial State P: {4}",
                SymbolName, TimeFrame, CciPeriod, Threshold, _stateP);
        }

        protected override void OnBar()
        {
            // 1. Manage Trailing Stop on candle close
            if (EnableTrailingStop)
            {
                ManageAtrTrailingStop();
            }

            // Sync state if position was closed by SL/TP externally
            var activePosition = Positions.FirstOrDefault(p => p.Label == InstanceLabel && p.SymbolName == SymbolName);
            if (activePosition == null)
            {
                _stateP = 0;
            }

            // Require sufficient completed bars
            int requiredBars = CciPeriod + 5;
            if (Bars.Count <= requiredBars)
                return;

            // 2. Compute Close-Based CCI for index 1 (current completed bar t) and index 2 (previous completed bar t-1)
            double cciCurr = CalculateCloseCci(1, CciPeriod);
            double cciPrev = CalculateCloseCci(2, CciPeriod);

            bool longCondition = cciCurr > Threshold && cciPrev <= Threshold;
            bool shortCondition = cciCurr < -Threshold && cciPrev >= -Threshold;

            if (!longCondition && !shortCondition)
                return;

            // 3. State Machine Logic
            if (longCondition && _stateP <= 0)
            {
                if (!IsWithinTradingSession())
                {
                    Print("Long CCI crossover detected ({0:F2}), but trading is outside session window.", cciCurr);
                    return;
                }

                ExecuteSignal(TradeType.Buy);
                _stateP = 1;
            }
            else if (shortCondition && _stateP >= 0)
            {
                if (!IsWithinTradingSession())
                {
                    Print("Short CCI crossunder detected ({0:F2}), but trading is outside session window.", cciCurr);
                    return;
                }

                ExecuteSignal(TradeType.Sell);
                _stateP = -1;
            }
        }

        #endregion

        #region Custom Math Engine

        /// <summary>
        /// Manual Close-Based CCI Calculation
        /// Formula: (Close - SMA) / (0.015 * MeanDeviation)
        /// </summary>
        /// <param name="shift">Bar index offset (1 = index t, 2 = index t-1)</param>
        /// <param name="length">Lookback window length (L)</param>
        private double CalculateCloseCci(int shift, int length)
        {
            if (Bars.Count < shift + length)
                return 0.0;

            // Step 1: Calculate SMA of Close
            double sumClose = 0.0;
            for (int i = 0; i < length; i++)
            {
                sumClose += Bars.ClosePrices.Last(shift + i);
            }
            double sma = sumClose / length;

            // Step 2: Calculate Mean Deviation
            double sumAbsDev = 0.0;
            for (int i = 0; i < length; i++)
            {
                sumAbsDev += Math.Abs(Bars.ClosePrices.Last(shift + i) - sma);
            }
            double meanDeviation = sumAbsDev / length;

            // Step 3: Guard clause against division by zero
            if (Math.Abs(meanDeviation) < 1e-9)
                return 0.0;

            // Step 4: Final CCI Output
            double currentClose = Bars.ClosePrices.Last(shift);
            return (currentClose - sma) / (0.015 * meanDeviation);
        }

        #endregion

        #region Execution & Helper Methods

        private void SyncStateP()
        {
            var openPos = Positions.FirstOrDefault(p => p.Label == InstanceLabel && p.SymbolName == SymbolName);
            if (openPos != null)
            {
                _stateP = openPos.TradeType == TradeType.Buy ? 1 : -1;
            }
            else
            {
                _stateP = 0;
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

        private void ExecuteSignal(TradeType targetType)
        {
            var activePosition = Positions.FirstOrDefault(p => p.Label == InstanceLabel && p.SymbolName == SymbolName);

            // Reversal check: close active opposite trade
            if (activePosition != null)
            {
                if (activePosition.TradeType != targetType)
                {
                    Print("Closing opposite active {0} position #{1}.", activePosition.TradeType, activePosition.Id);
                    ClosePosition(activePosition);
                }
                else
                {
                    return;
                }
            }

            // Spread check
            double currentSpreadPips = Symbol.Spread / Symbol.PipSize;
            if (currentSpreadPips > MaxSpreadPips)
            {
                Print("Execution suppressed for {0}: Spread ({1:F2} pips) exceeds maximum allowed ({2:F2} pips).",
                    targetType, currentSpreadPips, MaxSpreadPips);
                return;
            }

            // Calculate SL distance using ATR
            double atrVal = _atr.Result.Last(1);
            double slDistancePips = (atrVal * AtrSlMultiplier) / Symbol.PipSize;

            if (slDistancePips <= 0)
            {
                Print("ERROR: Invalid SL distance ({0:F2} pips). Order aborted.", slDistancePips);
                return;
            }

            double volumeInUnits = CalculateVolume(slDistancePips);

            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print("Volume ({0}) below symbol minimum ({1}). Order aborted.", volumeInUnits, Symbol.VolumeInUnitsMin);
                return;
            }

            // Execute Market Order
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
