using System.Collections.Generic;
using QuantDashboard.Engine.Data;
using QuantDashboard.Engine.Features;
using QuantDashboard.Engine.Models;
using QuantDashboard.Engine.Similarity;
using QuantDashboard.Enums;

namespace QuantDashboard.Strategies
{
    public class PatternMatchingStrategy
    {
        private readonly List<FeaturePoint> _history;
        private readonly int _k;
        private readonly double _threshold;

        public PatternMatchingStrategy(
            List<Candle> historicalCandles,
            int k = 20,
            double threshold = 0.001)
        {
            _history = FeatureEngine.BuildFeatures(historicalCandles);
            _k = k;
            _threshold = threshold;
        }

        public TradingSignal Decide(List<Candle> recentCandles)
        {
            if (recentCandles == null || recentCandles.Count == 0)
                return TradingSignal.Hold;

            var tempFeatures = FeatureEngine.BuildFeatures(recentCandles);
            if (tempFeatures.Count == 0)
                return TradingSignal.Hold;

            var current = tempFeatures[^1];
            var nearest = SimilaritySearch.FindNearestNeighbors(current, _history, _k);

            double sumFuture = 0;
            int cnt = 0;

            foreach (var item in nearest)
            {
                var p = item.Item1;
                if (!p.FutureReturn.HasValue) continue;
                sumFuture += p.FutureReturn.Value;
                cnt++;
            }

            if (cnt == 0)
                return TradingSignal.Hold;

            double avgFuture = sumFuture / cnt;

            if (avgFuture > _threshold)
                return TradingSignal.Buy;
            if (avgFuture < -_threshold)
                return TradingSignal.Sell;
            return TradingSignal.Hold;
        }
    }
}