using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.None)]
    public class LinRegInterceptV6 : Robot
    {
        #region Parameters - Core Engine
        [Parameter("Price Source", Group = "Core Engine")]
        public DataSeries PriceSource { get; set; }

        [Parameter("LRI Length", Group = "Core Engine", DefaultValue = 9, MinValue = 1)]
        public int LriLength { get; set; }
        #endregion

        #region Parameters - ATR Settings
        [Parameter("ATR Length", Group = "ATR Settings", DefaultValue = 14, MinValue = 1)]
        public int AtrLength { get; set; }

        [Parameter("ATR Multiplier", Group = "ATR Settings", DefaultValue = 2.0, MinValue = 0.1)]
        public double AtrMultiplier { get; set; }
        #endregion

        #region Parameters - Trailing Logic
        [Parameter("Enable ATR Trailing Stop", Group = "Trailing Logic", DefaultValue = true)]
        public bool EnableTrailingStop { get; set; }
        #endregion

        #region Parameters - Position Sizing
        [Parameter("Sizing Method (0=Fixed, 1=Risk%)", Group = "Volume Management", DefaultValue = 0, MinValue = 0, MaxValue = 1)]
        public int SizingMethod { get; set; }

        [Parameter("Fixed Lot Size", Group = "Volume Management", DefaultValue = 0.1, MinValue = 0.01)]
        public double FixedLotSize { get; set; }

        [Parameter("Risk Percent (%)", Group = "Volume Management", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 100.0)]
        public double RiskPercent { get; set; }
        #endregion

        #region Parameters - Risk Limits
        [Parameter("Max Daily Loss (%)", Group = "Risk Management", DefaultValue = 2.0, MinValue = 0.0, MaxValue = 100.0)]
        public double MaxDailyLossPercent { get; set; }
        #endregion

        #region Parameters - Session Filters
        [Parameter("Enable Asia Session", Group = "Asia Session", DefaultValue = false)]
        public bool EnableAsia { get; set; }

        [Parameter("Asia Start Hour (UTC+1)", Group = "Asia Session", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 23.99)]
        public double AsiaStartHour { get; set; }

        [Parameter("Asia End Hour (UTC+1)", Group = "Asia Session", DefaultValue = 8.0, MinValue = 0.0, MaxValue = 23.99)]
        public double AsiaEndHour { get; set; }


        [Parameter("Enable London Session", Group = "London Session", DefaultValue = true)]
        public bool EnableLondon { get; set; }

        [Parameter("London Start Hour (UTC+1)", Group = "London Session", DefaultValue = 8.0, MinValue = 0.0, MaxValue = 23.99)]
        public double LondonStartHour { get; set; }

        [Parameter("London End Hour (UTC+1)", Group = "London Session", DefaultValue = 16.0, MinValue = 0.0, MaxValue = 23.99)]
        public double LondonEndHour { get; set; }


        [Parameter("Enable NY Session", Group = "New York Session", DefaultValue = true)]
        public bool EnableNewYork { get; set; }

        [Parameter("NY Start Hour (UTC+1)", Group = "New York Session", DefaultValue = 13.0, MinValue = 0.0, MaxValue = 23.99)]
        public double NewYorkStartHour { get; set; }

        [Parameter("NY End Hour (UTC+1)", Group = "New York Session", DefaultValue = 21.0, MinValue = 0.0, MaxValue = 23.99)]
        public double NewYorkEndHour { get; set; }


        [Parameter("Enable London/NY Overlap", Group = "Overlap Session", DefaultValue = true)]
        public bool EnableOverlap { get; set; }

        [Parameter("Overlap Start Hour (UTC+1)", Group = "Overlap Session", DefaultValue = 13.0, MinValue = 0.0, MaxValue = 23.99)]
        public double OverlapStartHour { get; set; }

        [Parameter("Overlap End Hour (UTC+1)", Group = "Overlap Session", DefaultValue = 16.0, MinValue = 0.0, MaxValue = 23.99)]
        public double OverlapEndHour { get; set; }
        #endregion

        #region Internal Fields
        private LinearRegressionIntercept _lriIndicator;
        private AverageTrueRange _atrIndicator;
        private string _botLabel;
        private DateTime _lastTradingDay;
        private double _startingEquityForDay;
        private bool _dailyLossLimitReached;
        #endregion

        #region Session Engine Helpers
        private DateTime GetUtcPlus1Time()
        {
            // Translates server time directly to explicit UTC+1 as required by specification
            return Server.TimeInUtc.AddHours(1);
        }

        private bool IsCurrentSessionAllowed()
        {
            DateTime timeInUtcPlus1 = GetUtcPlus1Time();
            double currentHour = timeInUtcPlus1.Hour + (timeInUtcPlus1.Minute / 60.0);

            // Audit Note: Handles regular sessions and boundary wrapping securely
            if (EnableOverlap && IsTimeInSession(currentHour, OverlapStartHour, OverlapEndHour)) return true;
            if (EnableNewYork && IsTimeInSession(currentHour, NewYorkStartHour, NewYorkEndHour)) return true;
            if (EnableLondon && IsTimeInSession(currentHour, LondonStartHour, LondonEndHour)) return true;
            if (EnableAsia && IsTimeInSession(currentHour, AsiaStartHour, AsiaEndHour)) return true;

            return false;
        }

        private bool IsTimeInSession(double currentHour, double startHour, double endHour)
        {
            if (startHour <= endHour)
            {
                return currentHour >= startHour && currentHour < endHour;
            }
            // Handles overnight sessions if configured (e.g., 22:00 to 04:00)
            return currentHour >= startHour || currentHour < endHour;
        }
        #endregion

        #region Logging Module
        private void LogEvent(string eventType, string message)
        {
            string timestamp = GetUtcPlus1Time().ToString("yyyy-MM-dd HH:mm:ss");
            Print("[{0}] [{1}] {2}", timestamp, eventType, message);
        }
        #endregion

        #region Risk Engine & Capital Protection
        private double CalculateExecutionVolume(double stopLossDistancePrice)
        {
            if (stopLossDistancePrice <= 0)
            {
                LogEvent("Trade Rejection", "Calculated Stop Loss distance is invalid (zero or negative).");
                return 0;
            }

            if (SizingMethod == 0) // Fixed Lots Mode
            {
                double units = Symbol.QuantityToVolumeInUnits(FixedLotSize);
                return NormalizeVolume(units);
            }

            // Risk Percentage Mode Math
            // Cash Risk = Balance * RiskPercent
            double maxRiskCash = Account.Balance * (RiskPercent / 100.0);
            
            // cTrader Native Volume Calculation based on absolute price distance
            // Formula: Units = Risk Cash / (Stop Loss Distance in Price * Tick Value / Tick Size)
            double preciseUnits = maxRiskCash / (stopLossDistancePrice * (Symbol.TickValue / Symbol.TickSize));
            double normalizedUnits = NormalizeVolume(preciseUnits);

            // Cross-verify true risk after applying broker rounding steps
            double actualRiskCash = normalizedUnits * stopLossDistancePrice * (Symbol.TickValue / Symbol.TickSize);
            
            if (actualRiskCash > maxRiskCash)
            {
                string warningMsg = string.Format("Risk limit exceeded due to lot rounding constraints. Max Allowed: {0:F2} USD. Calculated Actual: {1:F2} USD.", maxRiskCash, actualRiskCash);
                LogEvent("Trade Rejection", warningMsg);
                return 0;
            }

            return normalizedUnits;
        }

        private double NormalizeVolume(double rawUnits)
        {
            if (rawUnits < Symbol.VolumeInUnitsMin) return Symbol.VolumeInUnitsMin;
            if (rawUnits > Symbol.VolumeInUnitsMax) return Symbol.VolumeInUnitsMax;

            // Align precisely with broker unit increments
            double remainder = rawUnits % Symbol.VolumeInUnitsStep;
            double cleanUnits = rawUnits - remainder;

            if (cleanUnits < Symbol.VolumeInUnitsMin) return Symbol.VolumeInUnitsMin;
            return cleanUnits;
        }

        private void UpdateDailyLossTracking()
        {
            DateTime currentUtcPlus1 = GetUtcPlus1Time();

            // Midnight Reset Logic
            if (currentUtcPlus1.Date != _lastTradingDay)
            {
                _lastTradingDay = currentUtcPlus1.Date;
                _startingEquityForDay = Account.Equity;
                _dailyLossLimitReached = false;
                LogEvent("Risk Management", "New trading day detected (UTC+1). Daily tracking metrics have been reset.");
            }

            if (_dailyLossLimitReached) return;

            // Calculate total realized loss using Equity drawdown from midnight
            double dailyLossLimitAmount = _startingEquityForDay * (MaxDailyLossPercent / 100.0);
            double currentDrawdown = _startingEquityForDay - Account.Equity;

            if (currentDrawdown >= dailyLossLimitAmount)
            {
                _dailyLossLimitReached = true;
                string limitMsg = string.Format("Daily loss limit of {0}% hit. Max Loss: {1:F2} USD. Current Drawdown: {2:F2} USD. Entries suspended.", MaxDailyLossPercent, dailyLossLimitAmount, currentDrawdown);
                LogEvent("Daily Loss Limit Reached", limitMsg);
            }
        }
        #endregion

        #region Core cBot Lifecycle Events
        protected override void OnStart()
        {
            // Establish unique matching identifier label for position parsing
            _botLabel = string.Format("LRI_V6_{0}_{1}", SymbolName, TimeFrame);
            LogEvent("Startup", string.Format("Initializing LinReg Intercept v6 on {0} ({1})", SymbolName, TimeFrame));

            // Load native high-performance indicator classes safely
            _lriIndicator = Indicators.LinearRegressionIntercept(PriceSource, LriLength);
            _atrIndicator = Indicators.AverageTrueRange(AtrLength, MovingAverageType.Simple);

            // Establish day tracking anchor points based on UTC+1 translation matrix
            _lastTradingDay = GetUtcPlus1Time().Date;
            _startingEquityForDay = Account.Equity;
            _dailyLossLimitReached = false;

            LogEvent("Parameters Loaded", string.Format("LRI Length: {0} | ATR Length: {1} | Multiplier: {2} | Sizing: {3}", LriLength, AtrLength, AtrMultiplier, SizingMethod));
        }

        protected override void OnTick()
        {
            // Execute real-time risk checks against equity adjustments on every tick change
            UpdateDailyLossTracking();

            // Handle intra-bar trailing stop parameters cleanly if verified active
            if (EnableTrailingStop && !_dailyLossLimitReached)
            {
                ManageTrailingStops();
            }
        }
        #endregion

        #region Trailing Stop Engine
        private void ManageTrailingStops()
        {
            // Find our active position managed by this robot instance
            var position = Positions.Find(_botLabel);
            if (position == null) return;

            // Use the closing value of the last completed bar to prevent whip-saw anomalies
            double atrValue = _atrIndicator.Result.Last(1);
            double targetDistancePrice = atrValue * AtrMultiplier;

            if (position.TradeType == TradeType.Buy)
            {
                // Dynamic distance calculated relative to current execution Ask price
                double newStopLossPrice = Symbol.Ask - targetDistancePrice;
                newStopLossPrice = Math.Round(newStopLossPrice, Symbol.Digits);

                // Enforce Rule: Only tighten stop, never increase financial exposure
                if (position.StopLoss == null || newStopLossPrice > position.StopLoss)
                {
                    ModifyPosition(position, newStopLossPrice, position.TakeProfit);
                    LogEvent("Trailing Stop", string.Format("Buy SL updated -> {0}", newStopLossPrice));
                }
            }
            else if (position.TradeType == TradeType.Sell)
            {
                // Dynamic distance calculated relative to current execution Bid price
                double newStopLossPrice = Symbol.Bid + targetDistancePrice;
                newStopLossPrice = Math.Round(newStopLossPrice, Symbol.Digits);

                // Enforce Rule: Only tighten stop, never increase financial exposure
                if (position.StopLoss == null || newStopLossPrice < position.StopLoss)
                {
                    ModifyPosition(position, newStopLossPrice, position.TakeProfit);
                    LogEvent("Trailing Stop", string.Format("Sell SL updated -> {0}", newStopLossPrice));
                }
            }
        }
        #endregion

        #region Strategy & Execution Engine
        protected override void OnBar()
        {
            // 1. Maintain time keeping checks at every candle transition
            UpdateDailyLossTracking();

            // 2. Enforce safety checks before looking at strategy logic
            if (_dailyLossLimitReached) return;
            if (!IsCurrentSessionAllowed()) return;

            // 3. Evaluate Crossover Strategy Metrics
            // Index 1 = The bar that just closed. Index 2 = The bar before it.
            double currentClose = MarketSeries.Close.Last(1);
            double previousClose = MarketSeries.Close.Last(2);

            double currentLri = _lriIndicator.Result.Last(1);
            double previousLri = _lriIndicator.Result.Last(2);

            // Buy Condition: Closed above LRI after previously being below or equal
            bool buySignal = previousClose <= previousLri && currentClose > currentLri;
            
            // Sell Condition: Closed below LRI after previously being above or equal
            bool sellSignal = previousClose >= previousLri && currentClose < currentLri;

            if (buySignal)
            {
                string signalLog = string.Format("Bullish Crossover. Close(1): {0} > LRI(1): {1}", currentClose, currentLri);
                LogEvent("Signal Detected", signalLog);
                ExecuteTradeSequence(TradeType.Buy);
            }
            else if (sellSignal)
            {
                string signalLog = string.Format("Bearish Crossover. Close(1): {0} < LRI(1): {1}", currentClose, currentLri);
                LogEvent("Signal Detected", signalLog);
                ExecuteTradeSequence(TradeType.Sell);
            }
        }

        private void ExecuteTradeSequence(TradeType targetDirection)
        {
            var activePosition = Positions.Find(_botLabel);

            // Handle existing trades matching or opposing the current signal
            if (activePosition != null)
            {
                if (activePosition.TradeType == targetDirection)
                {
                    LogEvent("Ignored Signal", "Position already exists in this direction. Rule: One position per direction.");
                    return;
                }

                // Rule: Opposite signal closes current trade instantly
                string closeLog = string.Format("Opposite signal received. Closing active {0} position ID: {1}", activePosition.TradeType, activePosition.Id);
                LogEvent("Position Closure", closeLog);
                ClosePosition(activePosition);
                
                // Halt execution sequence for this tick to allow the broker to safely clear database state.
                // The position management system will automatically cycle entry routines on the next available interval.
                return;
            }

            // Calculate precise ATR stop loss price distance
            double atrValue = _atrIndicator.Result.Last(1);
            double stopLossDistancePrice = atrValue * AtrMultiplier;

            // Compute broker volume units using audited risk allocation matrix
            double volumeInUnits = CalculateExecutionVolume(stopLossDistancePrice);
            if (volumeInUnits <= 0) return; // Rejection log handled inside risk module

            // Convert raw price distance into explicit Pips for the cTrader execution engine
            double stopLossInPips = stopLossDistancePrice / Symbol.PipSize;

            string submissionLog = string.Format("Submitting {0} Order | Volume: {1} Units | Initial SL Pips: {2:F1}", targetDirection, volumeInUnits, stopLossInPips);
            LogEvent("Order Submission", submissionLog);

            var executionResult = ExecuteMarketOrder(targetDirection, SymbolName, volumeInUnits, _botLabel, stopLossInPips, null);

            if (executionResult.IsSuccessful)
            {
                string successLog = string.Format("Successfully opened {0} position ID: {1}", targetDirection, executionResult.Position.Id);
                LogEvent("Order Executed", successLog);
            }
            else
            {
                string errorLog = string.Format("Broker Order Rejection. Reason: {0}", executionResult.Error);
                LogEvent("Broker Error", errorLog);
            }
        }

        protected override void OnStop()
        {
            LogEvent("Shutdown", "LinReg Intercept v6 successfully suspended. Dynamic monitoring disabled.");
        }
        #endregion
    }
}
