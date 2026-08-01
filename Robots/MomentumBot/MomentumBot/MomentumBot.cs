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
    public class MomentumBot : Robot
    {
        #region Strategy Parameters

        [Parameter("Momentum Length", Group = "Momentum Strategy", DefaultValue = 12, MinValue = 1)]
        public int MomentumLength { get; set; }

        [Parameter("ATR Length", Group = "ATR Trailing Stop", DefaultValue = 14, MinValue = 1)]
        public int AtrLength { get; set; }

        [Parameter("ATR Multiplier", Group = "ATR Trailing Stop", DefaultValue = 2.0, MinValue = 0.1, Step = 0.1)]
        public double AtrMultiplier { get; set; }

        [Parameter("Sizing Mode", Group = "Risk Management", DefaultValue = SizingMode.RiskPercentage)]
        public SizingMode Sizing { get; set; }

        [Parameter("Capital Base", Group = "Risk Management", DefaultValue = CapitalType.Equity)]
        public CapitalType CapitalBase { get; set; }

        [Parameter("Risk Percentage (%)", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.01, Step = 0.1)]
        public double RiskPercent { get; set; }

        [Parameter("Fixed Volume (Lots)", Group = "Risk Management", DefaultValue = 0.1, MinValue = 0.01, Step = 0.01)]
        public double FixedVolumeLots { get; set; }

        [Parameter("Max Spread (Pips)", Group = "Filters", DefaultValue = 3.0, MinValue = 0.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Instance Label", Group = "Execution", DefaultValue = "MomentumBot_v1")]
        public string InstanceLabel { get; set; }

        #endregion

        #region Internal Fields

        private AverageTrueRange _atr;
        private int _currentPositionState; // 0 = Flat, 1 = Long, -1 = Short

        #endregion

        #region Lifecycle Methods

        protected override void OnStart()
        {
            _currentPositionState = 0;

            // Wilder's Smoothing matches TradingView's default ta.atr() calculation model
            _atr = Indicators.AverageTrueRange(AtrLength, MovingAverageType.WildersSmoothing);

            // Synchronize state with any surviving position on startup
            var activePosition = Positions.FirstOrDefault(p => p.Label == InstanceLabel && p.SymbolName == SymbolName);
            if (activePosition != null)
            {
                _currentPositionState = activePosition.TradeType == TradeType.Buy ? 1 : -1;
            }

            Print("MomentumBot initialized successfully for symbol {0}.", SymbolName);
        }

        protected override void OnBar()
        {
            // Require enough completed history for lookback calculations
            if (Bars.Count <= MomentumLength + 2)
                return;

            // Index 1 = Most recently closed candle
            // Index 2 = Previous closed candle
            double currentMom = CalculateMomentum(1);
            double previousMom = CalculateMomentum(2);

            bool longCondition = currentMom > 0.0 && currentMom > previousMom;
            bool shortCondition = currentMom < 0.0 && currentMom < previousMom;

            // Signal 1: Buy Signal
            if (longCondition && _currentPositionState <= 0)
            {
                if (IsSpreadAcceptable())
                {
                    ExecuteSignal(TradeType.Buy);
                    _currentPositionState = 1;
                }
                else
                {
                    Print("Buy signal suppressed due to spread filter ({0:F2} pips > {1:F2} pips)", 
                        Symbol.Spread / Symbol.PipSize, MaxSpreadPips);
                }
            }
            // Signal 2: Sell Signal
            else if (shortCondition && _currentPositionState >= 0)
            {
                if (IsSpreadAcceptable())
                {
                    ExecuteSignal(TradeType.Sell);
                    _currentPositionState = -1;
                }
                else
                {
                    Print("Sell signal suppressed due to spread filter ({0:F2} pips > {1:F2} pips)", 
                        Symbol.Spread / Symbol.PipSize, MaxSpreadPips);
                }
            }
        }

        protected override void OnTick()
        {
            // Trailing stop is updated in real-time on price ticks
            ManageAtrTrailingStop();
        }

        #endregion

        #region Signal & Calculation Engine

        /// <summary>
        /// Calculates Momentum = Close[barIndex] - Close[barIndex + MomentumLength]
        /// </summary>
        private double CalculateMomentum(int barIndex)
        {
            return Bars.ClosePrices.Last(barIndex) - Bars.ClosePrices.Last(barIndex + MomentumLength);
        }

        private bool IsSpreadAcceptable()
        {
            double currentSpreadPips = Symbol.Spread / Symbol.PipSize;
            return currentSpreadPips <= MaxSpreadPips;
        }

        private void ExecuteSignal(TradeType targetType)
        {
            // Close existing position if it's in the opposite direction (Reversal)
            foreach (var pos in Positions.Where(p => p.Label == InstanceLabel && p.SymbolName == SymbolName))
            {
                if (pos.TradeType != targetType)
                {
                    ClosePosition(pos);
                }
                else
                {
                    // Position is already opened in target direction
                    return;
                }
            }

            // Calculate SL distance in pips based on ATR
            double atrVal = _atr.Result.Last(1);
            double slDistancePips = (atrVal * AtrMultiplier) / Symbol.PipSize;

            if (slDistancePips <= 0)
                return;

            double volumeInUnits = CalculateVolume(slDistancePips);

            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print("Calculated volume ({0}) is below symbol minimum ({1}). Order aborted.", 
                    volumeInUnits, Symbol.VolumeInUnitsMin);
                return;
            }

            // Execute Market Order with initial ATR Stop Loss
            ExecuteMarketOrder(targetType, SymbolName, volumeInUnits, InstanceLabel, slDistancePips, null);
        }

        #endregion

        #region Risk Management & Sizing

        private double CalculateVolume(double slDistancePips)
        {
            if (Sizing == SizingMode.FixedLots)
            {
                return Symbol.QuantityToVolumeInUnits(FixedVolumeLots);
            }

            double capital = CapitalBase == CapitalType.Equity ? Account.Equity : Account.Balance;
            double riskAmount = capital * (RiskPercent / 100.0);

            // Monetary risk per unit = SL distance in pips * PipValue
            double lossPerUnit = slDistancePips * Symbol.PipValue;

            if (lossPerUnit <= 0)
                return Symbol.VolumeInUnitsMin;

            double rawVolume = riskAmount / lossPerUnit;

            // Normalize volume to broker steps and limits
            double normalizedVolume = Symbol.NormalizeVolumeInUnits(rawVolume, RoundMode.Down);

            if (normalizedVolume > Symbol.VolumeInUnitsMax)
                normalizedVolume = Symbol.VolumeInUnitsMax;

            return normalizedVolume;
        }

        #endregion

        #region Dynamic ATR Trailing Stop

        private void ManageAtrTrailingStop()
        {
            var position = Positions.FirstOrDefault(p => p.Label == InstanceLabel && p.SymbolName == SymbolName);
            if (position == null)
                return;

            double atrVal = _atr.Result.Last(1);
            double trailingDistance = atrVal * AtrMultiplier;

            if (position.TradeType == TradeType.Buy)
            {
                double targetSl = Symbol.Bid - trailingDistance;

                // Stop loss must only ratchet upward, never widen
                if (!position.StopLoss.HasValue || targetSl > position.StopLoss.Value + (Symbol.PipSize * 0.1))
                {
                    ModifyPosition(position, targetSl, position.TakeProfit);
                }
            }
            else if (position.TradeType == TradeType.Sell)
            {
                double targetSl = Symbol.Ask + trailingDistance;

                // Stop loss must only ratchet downward, never widen
                if (!position.StopLoss.HasValue || targetSl < position.StopLoss.Value - (Symbol.PipSize * 0.1))
                {
                    ModifyPosition(position, targetSl, position.TakeProfit);
                }
            }
        }

        #endregion
    }
}
