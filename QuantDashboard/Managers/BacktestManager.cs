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
    
    // 실전과 똑같은 전략 리스트
    private List<IStrategy> _strategies;
    private IStrategy _adxStrat;

    // 수수료 (0.05%)
    private const decimal FeeRate = 0.0005m;

    public BacktestManager()
    {
        _client = new BinanceRestClient();
        
        // ★ [핵심] 실전과 동일한 RiskManager 인스턴스 사용
        _riskManager = new RiskManager();
        
        // 전략 초기화 (메인 윈도우와 순서/구성 100% 동일)
        _strategies = new List<IStrategy>
        {
            new SuperTrendStrategy(), new IchimokuCloudStrategy(), new MaCrossStrategy(), new LinRegStrategy(), // 0~3
            // ADX는 별도로 필터링 (s5)
            new OrderBlockStrategy(), new FairValueGapStrategy(), new VwapReversionStrategy(), new WhaleAggressionStrategy(), new SmartMoneyStrategy(), // 4~8 (s6~s10)
            new ZScoreStrategy(), new HurstExponentStrategy(), new EfficiencyRatioStrategy(), new VectorPatternStrategy(), new DeltaDivergenceStrategy(), // 9~13 (s11~s15)
            new InsideBarStrategy(), new FractalBreakoutStrategy(), new RsiDivergenceStrategy(), new VolatilitySqueezeStrategy(), new PatternCandleStrategy() // 14~18 (s16~s20)
        };
        _adxStrat = new AdxFilterStrategy();
    }

    // 데이터 수집 (최대 1년치)
    private async Task<List<IBinanceKline>> FetchHistoryAsync(string symbol, KlineInterval interval)
    {
        var allCandles = new List<IBinanceKline>();
        var endTime = DateTime.UtcNow;
        var startTime = endTime.AddDays(-365); // 1년 전부터

        // (로그용)
        // Console.WriteLine($"[Backtest] Downloading data for {symbol}...");

        while (startTime < endTime)
        {
            // 1000개씩 끊어서 요청
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
                    
                    // 다음 요청 시작점 갱신
                    startTime = data.Last().OpenTime.AddMinutes((int)interval);
                    await Task.Delay(50); // API 과부하 방지
                }
                else
                {
                    break;
                }
            }
            catch 
            {
                break; 
            }
        }
        
        // 중복 제거 및 시간순 정렬
        return allCandles.OrderBy(c => c.OpenTime).Distinct().ToList();
    }

    public async Task<BacktestResult> RunBacktestAsync(string symbol, KlineInterval interval, decimal startBalance, decimal leverage)
    {
        var result = new BacktestResult();
        var sb = new StringBuilder();
        
        // 1. 과거 데이터 준비
        var history = await FetchHistoryAsync(symbol, interval);
        if (history.Count < 200) 
        {
            result.Log = "데이터 부족 (최소 200개 필요)";
            return result;
        }

        decimal balance = startBalance;
        decimal peakBalance = startBalance;
        decimal maxDd = 0;
        
        TradingSignal currentPos = TradingSignal.Hold;
        decimal entryPrice = 0;
        decimal posAmount = 0; // 코인 개수

        sb.AppendLine($"=== BACKTEST REPORT ===");
        sb.AppendLine($"Target: {symbol} | Interval: {interval} | Leverage: {leverage}x");
        sb.AppendLine($"Data Range: {history.First().OpenTime} ~ {history.Last().OpenTime} ({history.Count} Candles)");
        sb.AppendLine("------------------------------------------------------------------");

        // 2. 타임머신 가동 (과거 -> 현재 루프)
        // 지표 계산을 위해 앞부분 100개는 건너뜀
        for (int i = 100; i < history.Count; i++)
        {
            // [환경 설정]
            // 과거 시점의 '확정된 캔들들' (분석용)
            var closedCandles = history.GetRange(i - 100, 100); 
            
            // 현재 진행 중인 캔들 (매매 체결용)
            var currentCandle = history[i];

            // ----------------------------------------------------
            // [A] 전략 분석 (실전과 동일한 로직)
            // ----------------------------------------------------
            
            // 1. 각 전략 실행
            var s5 = _adxStrat.Analyze(closedCandles); // ADX
            var sigs = new List<TradingSignal>();
            foreach (var strat in _strategies) sigs.Add(strat.Analyze(closedCandles));

            // 2. 점수 계산 (가중치 적용)
            int score = 0;
            // 순서: 0~3(Trend), 4~8(Smart), 9~13(Quant), 14~18(Pattern)
            // 인덱스 매핑 주의 (List 순서대로)
            
            // Tier 1 (3점)
            score += Sc(sigs[4], 3) + Sc(sigs[7], 3) + Sc(sigs[12], 3) + Sc(sigs[17], 3); 
            // Tier 2 (2점)
            score += Sc(sigs[0], 2) + Sc(sigs[1], 2) + Sc(sigs[5], 2) + Sc(sigs[6], 2) + Sc(sigs[8], 2) + Sc(sigs[13], 2) + Sc(sigs[14], 2) + Sc(sigs[15], 2) + Sc(sigs[16], 2) + Sc(sigs[18], 2);
            // Tier 3 (1점)
            score += Sc(sigs[2], 1) + Sc(sigs[3], 1) + Sc(sigs[9], 1) + Sc(sigs[11], 1);

            // ADX 약세 필터
            if (s5 == TradingSignal.Hold) score = (int)(score * 0.5);

            // ADX 역추세 필터 (수정된 로직 반영)
            bool adxPass = true;
            if (score > 0 && s5 == TradingSignal.Sell) adxPass = false;
            if (score < 0 && s5 == TradingSignal.Buy) adxPass = false;

            // ----------------------------------------------------
            // [B] 매매 시뮬레이션 (RiskManager 정밀 검증)
            // ----------------------------------------------------
            
            // 하나의 캔들 안에서도 고가/저가를 오가며 SL/TP가 터질 수 있음
            // 정교함을 위해 [시가 -> 저가 -> 고가 -> 종가] 순서로 가격을 흘려보냄 (롱 기준 불리한 순서)
            // (숏 기준이면 [시가 -> 고가 -> 저가 -> 종가]가 불리함)
            
            decimal[] tickPrices;
            if (currentPos == TradingSignal.Buy)
                tickPrices = new[] { currentCandle.OpenPrice, currentCandle.LowPrice, currentCandle.HighPrice, currentCandle.ClosePrice };
            else if (currentPos == TradingSignal.Sell)
                tickPrices = new[] { currentCandle.OpenPrice, currentCandle.HighPrice, currentCandle.LowPrice, currentCandle.ClosePrice };
            else
                tickPrices = new[] { currentCandle.ClosePrice }; // 포지션 없을 땐 종가만 봄 (단순화)

            foreach (var price in tickPrices)
            {
                if (currentPos == TradingSignal.Hold)
                {
                    // 진입 조건 체크
                    // (쿨타임은 백테스트에서 캔들 단위라 자동 적용됨)
                    if (score >= 7 && adxPass)
                    {
                        currentPos = TradingSignal.Buy;
                        entryPrice = price;
                        posAmount = (balance * leverage) / entryPrice;
                        
                        // ★ RiskManager 초기화 & 설정 (실전과 동일)
                        _riskManager.OnEntry(entryPrice);
                        _riskManager.UpdateDynamicSettings(interval, leverage, symbol);
                        
                        // sb.AppendLine($"[{currentCandle.OpenTime:MM-dd HH:mm}] LONG ENTRY @ {entryPrice} (Score {score})");
                    }
                    else if (score <= -7 && adxPass)
                    {
                        currentPos = TradingSignal.Sell;
                        entryPrice = price;
                        posAmount = (balance * leverage) / entryPrice;
                        
                        _riskManager.OnEntry(entryPrice);
                        _riskManager.UpdateDynamicSettings(interval, leverage, symbol);
                        
                        // sb.AppendLine($"[{currentCandle.OpenTime:MM-dd HH:mm}] SHORT ENTRY @ {entryPrice} (Score {score})");
                    }
                }
                else // 포지션 보유 중
                {
                    // ★ RiskManager에게 매 틱마다 물어봄 ("지금 팔까요?")
                    var exitSignal = _riskManager.AnalyzeExit(closedCandles, currentPos, entryPrice, price, leverage);

                    // 스위칭 조건 체크 (실전 로직)
                    bool rev = (currentPos == TradingSignal.Buy && score <= -7) || (currentPos == TradingSignal.Sell && score >= 7);
                    if (rev) { exitSignal.Action = ExitAction.CloseAll; exitSignal.Reason = "Signal Reversal"; }

                    // 청산 실행
                    if (exitSignal.Action == ExitAction.CloseAll)
                    {
                        decimal pnl = CalculatePnL(entryPrice, price, posAmount, currentPos, leverage);
                        balance += pnl;
                        
                        string winLose = pnl > 0 ? "WIN" : "LOSS";
                        sb.AppendLine($"[{currentCandle.OpenTime:MM-dd HH:mm}] EXIT ({exitSignal.Reason}) | {winLose} | PnL: ${pnl:F2} | Bal: ${balance:F0}");

                        if (pnl > 0) result.WinCount++; else result.LossCount++;
                        
                        currentPos = TradingSignal.Hold;
                        break; // 청산했으니 이번 캔들 루프 종료
                    }
                    // (부분 익절은 로직 복잡성을 위해 여기선 생략하거나 전량 익절로 처리)
                }
            }

            // MDD 계산
            if (balance > peakBalance) peakBalance = balance;
            decimal dd = (peakBalance - balance) / peakBalance * 100;
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

    // 수수료 포함 PnL 계산 (실전과 동일)
    private decimal CalculatePnL(decimal entry, decimal curr, decimal amt, TradingSignal pos, decimal lev)
    {
        decimal rawDiff = (pos == TradingSignal.Buy) ? (curr - entry) : (entry - curr);
        decimal profit = rawDiff * amt;
        
        // 수수료: (진입가 * 수량 * 0.0005) + (청산가 * 수량 * 0.0005)
        decimal fees = (entry * amt * FeeRate) + (curr * amt * FeeRate);
        
        return profit - fees;
    }
}