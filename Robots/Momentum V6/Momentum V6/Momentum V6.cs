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
    public class MomentumV6 : Robot
    {
        #region Strategy Parameters

        [Parameter("Momentum Length", Group = "Momentum Strategy", DefaultValue = 12, MinValue = 1)]
        public int MomentumLength { get; set; }

        [Parameter("ATR Length", Group = "ATR Trailing Stop", DefaultValue = 14, MinValue = 1)]
        public int AtrLength { get; set; }

        [Parameter("ATR Multiplier", Group = "ATR Trailing Stop", DefaultValue = 2.0, MinValue = 0.1, Step = 0.1)]
        public double AtrMultiplier { get; set; }

        [Parameter("ATR Step Filter (Pips)", Group = "ATR Trailing Stop", DefaultValue = 0.5, MinValue = 0.1, Step = 0.1)]
        public double AtrStepPips { get; set; }

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

        [Parameter("Instance Label", Group = "Execution", DefaultValue = "Momentum_V6")]
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
            // Wilder's Smoothing matches TradingView ta.atr()
            _atr = Indicators.AverageTrueRange(AtrLength, MovingAverageType.WilderSmoothing);

            Print("[INIT] Momentum V6 initialized successfully | Symbol: {0} | TF: {1} | Mom Length: {2} | ATR Length: {3}",
                SymbolName, TimeFrame, MomentumLength, AtrLength);
        }

        protected override void OnBar()
        {
            // 1. Manage ATR Trailing Stop on Completed Candle Close
            ManageAtrTrailingStop();

            // 2. Clear expired pending signals from previous bar
            if (_pendingSignal.HasValue && _pendingSignalBar != Bars.Count - 1)
            {
                Print("[SIGNAL EXPIRED] Pending {0} signal from bar #{1} expired unexecuted.", _pendingSignal, _pendingSignalBar);
                _pendingSignal = null;
                _pendingSignalBar = -1;
            }

            // 3. Require enough completed history for lookback calculations
            if (Bars.Count <= MomentumLength + 2)
                return;

            // Index 1 = Most recently closed candle (t-1)
            // Index 2 = Candle before index 1 (t-2)
            double currentMom = CalculateMomentum(1);
            double previousMom = CalculateMomentum(2);

            bool longCondition = currentMom > 0.0 && currentMom > previousMom;
            bool shortCondition = currentMom < 0.0 && currentMom < previousMom;

            var activePosition = GetActivePosition();
            int currentPositionState = activePosition == null ? 0 : (activePosition.TradeType == TradeType.Buy ? 1 : -1);

            if (longCondition && currentPositionState <= 0)
            {
                Print("[SIGNAL DETECTED] BULLISH Momentum | Bar #{0} | CurrentMom: {1:F5} > PrevMom: {2:F5}",
                    Bars.Count - 1, currentMom, previousMom);
                SetPendingSignal(TradeType.Buy);
            }
            else if (shortCondition && currentPositionState >= 0)
            {
                Print("[SIGNAL DETECTED] BEARISH Momentum | Bar #{0} | CurrentMom: {1:F5} < PrevMom: {2:F5}",
                    Bars.Count - 1, currentMom, previousMom);
                SetPendingSignal(TradeType.Sell);
            }

            // Attempt execution immediately on bar close
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

        private Position GetActivePosition()
        {
            return Positions.FirstOrDefault(p => p.Label == InstanceLabel && p.SymbolName == SymbolName);
        }

        private void SetPendingSignal(TradeType type)
        {
            _pendingSignal = type;
            _pendingSignalBar = Bars.Count - 1;
        }

        private double CalculateMomentum(int barIndex)
        {
            // Close[barIndex] - Close[barIndex + MomentumLength]
            return Bars.ClosePrices.Last(barIndex) - Bars.ClosePrices.Last(barIndex + MomentumLength);
        }

        private void ProcessPendingSignal()
        {
            if (!_pendingSignal.HasValue)
                return;

            TradeType targetType = _pendingSignal.Value;

            // Check Spread Filter
            double currentSpreadPips = Symbol.Spread / Symbol.PipSize;
            if (currentSpreadPips > MaxSpreadPips)
            {
                return; // Retries on next tick within the same bar
            }

            // Handle Active Positions / Atomic Reversals
            var activePosition = GetActivePosition();
            if (activePosition != null)
            {
                if (activePosition.TradeType == targetType)
                {
                    // Already holding position in target direction
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
                    return; // Retry on next tick
                }
            }

            // Calculate SL distance in pips based on ATR of previous closed bar
            double atrVal = _atr.Result.Last(1);
            if (double.IsNaN(atrVal) || atrVal <= 0)
            {
                Print("[ERROR] Invalid ATR value ({0}). Signal aborted.", atrVal);
                _pendingSignal = null;
                return;
            }

            double slDistancePips = (atrVal * AtrMultiplier) / Symbol.PipSize;
            if (slDistancePips <= 0)
            {
                Print("[ERROR] Calculated SL distance ({0:F2} pips) invalid. Signal aborted.", slDistancePips);
                _pendingSignal = null;
                return;
            }

            double volumeInUnits = CalculateVolume(slDistancePips);
            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print("[ERROR] Calculated volume ({0}) below symbol minimum ({1}). Order aborted.",
                    volumeInUnits, Symbol.VolumeInUnitsMin);
                _pendingSignal = null;
                return;
            }

            // Execute Market Order with initial ATR Stop Loss
            var result = ExecuteMarketOrder(targetType, SymbolName, volumeInUnits, InstanceLabel, slDistancePips, null);

            if (result.IsSuccessful)
            {
                Print("[ORDER SUCCESS] #{0} {1} | Vol: {2} units | SL: {3:F1} pips | TP: None",
                    result.Position.Id, targetType, volumeInUnits, slDistancePips);
                _pendingSignal = null;
            }
            else
            {
                Print("[ORDER FAILED] Reason: {0}", result.Error);
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
            var position = GetActivePosition();
            if (position == null)
                return;

            double atrVal = _atr.Result.Last(1);
            if (double.IsNaN(atrVal) || atrVal <= 0)
                return;

            double trailingDistance = atrVal * AtrMultiplier;
            double minStepInPrice = AtrStepPips * Symbol.PipSize;

            if (position.TradeType == TradeType.Buy)
            {
                double currentClose = Bars.ClosePrices.Last(1);
                double targetSl = currentClose - trailingDistance;

                if (!position.StopLoss.HasValue || (targetSl - position.StopLoss.Value >= minStepInPrice))
                {
                    ModifyPosition(position, targetSl, position.TakeProfit, ProtectionType.Absolute);
                    Print("[DYNAMIC ATR TRAIL] Updated Buy SL to {0:F5}", targetSl);
                }
            }
            else if (position.TradeType == TradeType.Sell)
            {
                double currentClose = Bars.ClosePrices.Last(1);
                double targetSl = currentClose + trailingDistance;

                if (!position.StopLoss.HasValue || (position.StopLoss.Value - targetSl >= minStepInPrice))
                {
                    ModifyPosition(position, targetSl, position.TakeProfit, ProtectionType.Absolute);
                    Print("[DYNAMIC ATR TRAIL] Updated Sell SL to {0:F5}", targetSl);
                }
            }
        }

        #endregion
    }
}
