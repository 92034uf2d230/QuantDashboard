using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using QuantDashboard.Enums;
using QuantDashboard.Strategies;

namespace QuantDashboard.Managers;

public class BacktestResult
{
    public decimal TotalPnL { get; set; }
    public decimal FinalBalance { get; set; }
    public decimal MaxDrawdown { get; set; }
    public int WinCount { get; set; }
    public int LossCount { get; set; }
    public string Log { get; set; }
}

public class BacktestManager
{
    private BinanceRestClient _client;
    private RiskManager _riskManager;
    
    // 전략 리스트 (실전과 동일)
    private List<IStrategy> _strategies;
    private IStrategy _adxStrat;

    // 수수료 (0.05%)
    private const decimal FeeRate = 0.0005m;

    public BacktestManager()
    {
        _client = new BinanceRestClient();
        _riskManager = new RiskManager();
        
        // 20개 전략 초기화
        _strategies = new List<IStrategy>
        {
            new SuperTrendStrategy(), new IchimokuCloudStrategy(), new MaCrossStrategy(), new LinRegStrategy(), // 0~3
            // ADX(s5)는 별도 변수로 관리
            new OrderBlockStrategy(), new FairValueGapStrategy(), new VwapReversionStrategy(), new WhaleAggressionStrategy(), new SmartMoneyStrategy(), // 4~8 (s6~s10)
            new ZScoreStrategy(), new HurstExponentStrategy(), new EfficiencyRatioStrategy(), new VectorPatternStrategy(), new DeltaDivergenceStrategy(), // 9~13 (s11~s15)
            new InsideBarStrategy(), new FractalBreakoutStrategy(), new RsiDivergenceStrategy(), new VolatilitySqueezeStrategy(), new PatternCandleStrategy() // 14~18 (s16~s20)
        };
        _adxStrat = new AdxFilterStrategy();
    }

    // 과거 데이터 수집 (최대 1년치)
    private async Task<List<IBinanceKline>> FetchHistoryAsync(string symbol, KlineInterval interval)
    {
        var allCandles = new List<IBinanceKline>();
        var endTime = DateTime.UtcNow;
        var startTime = endTime.AddDays(-365); // 1년 전부터 조회

        // Console.WriteLine($"Fetching data for {symbol}..."); // 디버그용

        while (startTime < endTime)
        {
            var nextTime = startTime.AddMinutes((int)interval * 1000);
            if (nextTime > endTime) nextTime = endTime;

            try
            {
                var result = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, startTime, nextTime, limit: 1000);
                if (result.Success)
                {
                    var data = result.Data.ToList();
                    if (data.Count == 0) break;
                    
                    allCandles.AddRange(data);
                    startTime = data.Last().OpenTime.AddMinutes((int)interval);
                    await Task.Delay(50); 
                }
                else break;
            }
            catch { break; }
        }
        
        return allCandles.OrderBy(c => c.OpenTime).Distinct().ToList();
    }

    public async Task<BacktestResult> RunBacktestAsync(string symbol, KlineInterval interval, decimal startBalance, decimal leverage)
    {
        var result = new BacktestResult();
        var sb = new StringBuilder();
        
        // 1. 데이터 준비
        var history = await FetchHistoryAsync(symbol, interval);
        if (history.Count < 200) 
        {
            result.Log = "Not enough data (Need > 200 candles)";
            return result;
        }

        decimal balance = startBalance;
        decimal peakBalance = startBalance;
        decimal maxDd = 0;
        
        TradingSignal currentPos = TradingSignal.Hold;
        decimal entryPrice = 0;
        decimal posAmount = 0;

        sb.AppendLine($"=== BACKTEST REPORT ===");
        sb.AppendLine($"Target: {symbol} | Interval: {interval} | Leverage: {leverage}x");
        sb.AppendLine($"Data Range: {history.First().OpenTime:yyyy/MM/dd HH:mm} ~ {history.Last().OpenTime:yyyy/MM/dd HH:mm} ({history.Count} Candles)");
        sb.AppendLine("------------------------------------------------------------------");

        // 2. 시뮬레이션 루프
        for (int i = 100; i < history.Count; i++)
        {
            var closedCandles = history.GetRange(i - 100, 100); 
            var currentCandle = history[i];

            // ----------------------------------------------------
            // [A] 전략 분석 (실전 로직 복제)
            // ----------------------------------------------------
            var s5 = _adxStrat.Analyze(closedCandles); // ADX
            var sigs = new List<TradingSignal>();
            foreach (var strat in _strategies) sigs.Add(strat.Analyze(closedCandles));

            // 점수 계산
            int score = 0;
            // Tier 1 (3점)
            score += Sc(sigs[4], 3) + Sc(sigs[7], 3) + Sc(sigs[12], 3) + Sc(sigs[17], 3); 
            // Tier 2 (2점)
            score += Sc(sigs[0], 2) + Sc(sigs[1], 2) + Sc(sigs[5], 2) + Sc(sigs[6], 2) + Sc(sigs[8], 2) + Sc(sigs[13], 2) + Sc(sigs[14], 2) + Sc(sigs[15], 2) + Sc(sigs[16], 2) + Sc(sigs[18], 2);
            // Tier 3 (1점)
            score += Sc(sigs[2], 1) + Sc(sigs[3], 1) + Sc(sigs[9], 1) + Sc(sigs[11], 1);

            if (s5 == TradingSignal.Hold) score = (int)(score * 0.5);

            // ADX 역추세 필터
            bool adxPass = true;
            if (score > 0 && s5 == TradingSignal.Sell) adxPass = false;
            if (score < 0 && s5 == TradingSignal.Buy) adxPass = false;

            // ----------------------------------------------------
            // [B] 매매 시뮬레이션 (정밀 모드)
            // ----------------------------------------------------
            // 캔들 내부 움직임 시뮬레이션 [Open -> Low -> High -> Close] (롱 기준 최악의 경우)
            // 숏 포지션이면 [Open -> High -> Low -> Close] 순서로 체크해야 더 정확함
            
            decimal[] tickPrices;
            if (currentPos == TradingSignal.Sell)
                tickPrices = new[] { currentCandle.OpenPrice, currentCandle.HighPrice, currentCandle.LowPrice, currentCandle.ClosePrice };
            else
                tickPrices = new[] { currentCandle.OpenPrice, currentCandle.LowPrice, currentCandle.HighPrice, currentCandle.ClosePrice };

            foreach (var price in tickPrices)
            {
                if (currentPos == TradingSignal.Hold)
                {
                    // 진입 조건
                    if (score >= 7 && adxPass)
                    {
                        currentPos = TradingSignal.Buy;
                        entryPrice = price;
                        posAmount = CalculatePositionSize(balance, leverage, entryPrice, symbol, interval);
                        
                        // RiskManager 세팅
                        _riskManager.OnEntry(entryPrice);
                        _riskManager.UpdateDynamicSettings(interval, leverage, symbol);
                    }
                    else if (score <= -7 && adxPass)
                    {
                        currentPos = TradingSignal.Sell;
                        entryPrice = price;
                        posAmount = CalculatePositionSize(balance, leverage, entryPrice, symbol, interval);
                        
                        _riskManager.OnEntry(entryPrice);
                        _riskManager.UpdateDynamicSettings(interval, leverage, symbol);
                    }
                }
                else // 포지션 보유 중
                {
                    var exitSignal = _riskManager.AnalyzeExit(closedCandles, currentPos, entryPrice, price, leverage);
                    
                    // 스위칭 체크
                    bool rev = (currentPos == TradingSignal.Buy && score <= -7) || (currentPos == TradingSignal.Sell && score >= 7);
                    if (rev) { exitSignal.Action = ExitAction.CloseAll; exitSignal.Reason = "Signal Reversal"; }

                    // 청산 실행
                    if (exitSignal.Action == ExitAction.CloseAll)
                    {
                        decimal pnl = CalculatePnL(entryPrice, price, posAmount, currentPos, leverage);
                        balance += pnl;
                        
                        string winLose = pnl > 0 ? "WIN" : "LOSS";
                        decimal roe = (entryPrice == 0) ? 0 : (pnl / (posAmount * entryPrice / leverage)) * 100;

                        sb.AppendLine($"[{currentCandle.OpenTime:MM-dd HH:mm}] EXIT ({exitSignal.Reason}) | {winLose} | PnL: ${pnl:F2} | Bal: ${balance:F0}");

                        if (pnl > 0) result.WinCount++; else result.LossCount++;
                        
                        currentPos = TradingSignal.Hold;
                        break; // 이번 캔들 매매 종료
                    }
                    // (부분 익절은 로직 복잡성 상 생략, 전량 익절로 통합)
                }
            }

            // MDD 갱신
            if (balance > peakBalance) peakBalance = balance;
            decimal dd = (peakBalance > 0) ? (peakBalance - balance) / peakBalance * 100 : 0;
            if (dd > maxDd) maxDd = dd;

            if (balance <= 0) { sb.AppendLine("!!! BANKRUPTCY !!!"); break; }
        }

        result.FinalBalance = balance;
        result.TotalPnL = balance - startBalance;
        result.MaxDrawdown = maxDd;
        result.Log = sb.ToString();

        return result;
    }

    private int Sc(TradingSignal s, int w) => s == TradingSignal.Buy ? w : (s == TradingSignal.Sell ? -w : 0);

    // ★ [자금 관리] 손절 시 원금의 3%만 잃도록 포지션 규모 조절
// ★ [수정됨] 4시간봉, 1일봉 변동성 누락 수정
    private decimal CalculatePositionSize(decimal balance, decimal leverage, decimal price, string symbol, KlineInterval interval)
    {
        // 1. 코인별 가중치 (RiskManager와 동일하게 맞춤)
        decimal volMultiplier = 1.0m;
        if (symbol != "BTCUSDT") 
        {
            if (symbol == "ETHUSDT" || symbol == "BNBUSDT" || symbol == "XRPUSDT" || symbol == "ADAUSDT") 
                volMultiplier = 1.2m;
            else if (symbol == "SOLUSDT" || symbol == "AVAXUSDT") 
                volMultiplier = 1.3m;
            else 
                volMultiplier = 2.5m; // 밈코인 등
        }
        
        // 2. 시간별 기본 변동성 (여기가 문제였음! 4h, 1d 추가)
        decimal baseVol = interval switch
        {
            KlineInterval.OneMinute => 0.003m,      // 0.3%
            KlineInterval.FiveMinutes => 0.005m,    // 0.5%
            KlineInterval.FifteenMinutes => 0.008m, // 0.8%
            KlineInterval.OneHour => 0.015m,        // 1.5%
            KlineInterval.FourHour => 0.03m,        // 3.0% (누락되었던 부분)
            KlineInterval.OneDay => 0.05m,          // 5.0%
            _ => 0.008m
        };

        // 예상 손절폭 계산
        decimal estimatedSlPercent = baseVol * volMultiplier; 
        if (estimatedSlPercent == 0) estimatedSlPercent = 0.01m;

        // 3. 리스크 허용액 (원금의 2%로 하향 조정 추천)
        decimal riskAmount = balance * 0.02m; // 3% -> 2% (안전 제일)

        // 4. 안전 포지션 크기 계산
        decimal safeSize = riskAmount / estimatedSlPercent;

        // 5. 거래소 한도 체크
        decimal maxSize = balance * leverage;
        decimal finalSize = Math.Min(safeSize, maxSize);
        
        if (finalSize < 100) finalSize = 100; // 최소 주문금액

        return finalSize / price;
    }

    private decimal CalculatePnL(decimal entry, decimal curr, decimal amt, TradingSignal pos, decimal lev)
    {
        decimal rawDiff = (pos == TradingSignal.Buy) ? (curr - entry) : (entry - curr);
        decimal profit = rawDiff * amt;
        decimal fees = (entry * amt * FeeRate) + (curr * amt * FeeRate);
        return profit - fees;
    }
}