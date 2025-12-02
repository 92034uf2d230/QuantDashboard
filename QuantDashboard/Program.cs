using System;
using System.IO;
using QuantDashboard.Engine.Data;
using QuantDashboard.Enums;
using QuantDashboard.Strategies;

namespace QuantDashboard
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // 현재 실행 파일 위치 기준으로 솔루션 루트 찾기
            // (예: QuantDashboard/bin/Debug/net10.0/ → ../../../.. → 솔루션 루트)
            var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            Console.WriteLine(baseDir);

            // data/futures 경로
            var dataDir = Path.Combine(baseDir, "data", "futures");

            // 테스트용으로 BTCUSDT 15m 사용
            var filePath = Path.Combine(dataDir, "BTCUSDT_15m.csv");

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"CSV not found: {filePath}");
                Console.WriteLine("download_futures_klines.py 돌려서 data/futures 안에 CSV부터 만들어줘.");
                Console.ReadLine();
                return;
            }

            Console.WriteLine("Loading candles...");
            var candles = CsvCandleLoader.LoadFromFile(filePath);
            Console.WriteLine($"Loaded {candles.Count} candles.");

            if (candles.Count < 100)
            {
                Console.WriteLine("Not enough candles.");
                Console.ReadLine();
                return;
            }

            // 전체 과거를 히스토리로 사용해서 패턴 매칭 전략 생성
            // k: 유사 패턴 개수, threshold: 기대 로그 수익률 임계값
            var strategy = new PatternMatchingStrategy(
                historicalCandles: candles,
                k: 20,
                threshold: 0.001 // ~0.1% 이상 상승/하락일 때만 방향성
            );

            // 최근 N개 캔들을 "현재까지의 흐름"으로 사용 (예: 300개)
            int recentWindow = Math.Min(300, candles.Count);
            var recentCandles = candles.GetRange(candles.Count - recentWindow, recentWindow);

            // 전략에 현재 상태 넣고 신호 받아오기
            TradingSignal signal = strategy.Decide(recentCandles);

            var lastCandle = candles[^1];
            Console.WriteLine($"Last candle timestamp : {lastCandle.Timestamp:yyyy-MM-dd HH:mm}");
            Console.WriteLine($"Open={lastCandle.Open}, High={lastCandle.High}, Low={lastCandle.Low}, Close={lastCandle.Close}");
            Console.WriteLine($"Volume={lastCandle.Volume}, Trades={lastCandle.TradeCount}");

            Console.WriteLine($"PatternMatchingStrategy signal: {signal}  (Hold/Buy/Sell)");

            Console.WriteLine("Done.");
            Console.ReadLine();
        }
    }
}