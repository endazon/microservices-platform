using LlmGateway.Common.Observability;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Options;

namespace LlmGateway.Domain.Limits;

// NFR-18, NFR-21, FR-11, ADR-0044, IADR-0678 (#2404): **送る前の呼び出しの上限の唯一の判定器。**
//
// `CompletionUseCase` の一括・逐次の両方が、プロバイダへ送る直前にここを呼ぶ（判定器を 2 つにしない。
// IADR-0400 決定 2 と同型）。輸送（REST・gRPC）は呼び手の名前を渡すだけである。
//
// 🔴 **呼び手は検証済みのトークンの主体、用途は計器と同じ正規化の後の値で数える**（IADR-0678 決定 3）。
// 用途の名前を変えても `other` の 1 つの枠に入り、枠を分けられない。
// 🔴 **合成監視の標識が付いた呼び出しも数える。** 費用の計上から外すこと（IADR-0378）と上限から外すことは別である。
// 🔴 **段 1（Report）は送信を止めない。** 止めるのは Enforce のときだけ（IADR-0678 決定 4）。
//
// 状態はプロセス内（IADR-0678 決定 5・残余 1）。鍵の数は「呼び手 × 正規化した用途」で閉じる。
public sealed class LlmCallLimiter(
    IOptionsMonitor<LlmCallLimitOptions> options,
    IOptionsMonitor<LlmRoutingOptions> routing,
    ModelPriceTable prices,
    TimeProvider clock,
    LlmCallLimitMetrics metrics,
    LogOccurrenceThrottle logThrottle,
    ILogger<LlmCallLimiter> logger)
{
    public const string UnknownCaller = "unknown";

    // 種類（llm.limit.kind）の値域。6 値で閉じる。
    public const string KindMaxTokens = "max_tokens";
    public const string KindPromptLength = "prompt_length";
    public const string KindCallerRate = "caller_rate";
    public const string KindPurposeRate = "purpose_rate";
    public const string KindCallerCost = "caller_cost";
    public const string KindPurposeCost = "purpose_cost";

    public const string ModeReport = "report";
    public const string ModeEnforce = "enforce";

    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _rate = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CostBucket> _cost = new(StringComparer.Ordinal);

    private sealed class CostBucket
    {
        public DateOnly Day { get; set; }
        public decimal Total { get; set; }
    }

    // 送る直前に 1 回呼ぶ。戻り値の Denied が真なら送らない（Enforce のときだけ真になる）。
    // 送った後は Settle（実額）、送らなかったときは Release を呼ぶ（どちらも 2 回目以降は何もしない）。
    public LlmCallAdmission Admit(
        string? caller, string purpose, string? model, int promptChars, int maxTokens)
    {
        var o = options.CurrentValue;
        // AI レビュー #2408: 呼び手は小文字へ揃える（Callers の照合は大小を区別しないので、枠の鍵と計器の綴りも揃える）。
        var who = string.IsNullOrWhiteSpace(caller) ? UnknownCaller : caller.Trim().ToLowerInvariant();
        var normalizedPurpose = LlmMetricValues.NormalizePurpose(routing.CurrentValue, purpose);
        var limits = EffectiveCallerLimits.For(o, who);
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var callerKey = "c|" + who;
        var purposeKey = "p|" + who + "|" + normalizedPurpose;

        var exceeded = new List<string>(2);
        if (maxTokens <= 0 || maxTokens > o.MaxOutputTokens) exceeded.Add(KindMaxTokens);
        if (promptChars > o.MaxPromptChars) exceeded.Add(KindPromptLength);

        // 費用の上限が無い呼び手では換算しない（単価表の警告を増やさない。IADR-0678 決定 2）。
        decimal? estimate = null;
        if (limits.HasCostLimit)
        {
            estimate = CostOf(o, model, Math.Max(0, promptChars), Math.Max(0, maxTokens), now, default, out var unpriced);
            if (unpriced)
                metrics.RecordUnpricedEstimate(who, normalizedPurpose);
        }

        var enforce = o.Mode == LlmCallLimitMode.Enforce;
        lock (_gate)
        {
            if (limits.RequestsPerMinute is { } callerRpm && CountRecent(callerKey, now) >= callerRpm)
                exceeded.Add(KindCallerRate);
            if (limits.PurposeRequestsPerMinute is { } purposeRpm && CountRecent(purposeKey, now) >= purposeRpm)
                exceeded.Add(KindPurposeRate);

            if (estimate is { } est)
            {
                if (limits.DailyCostLimit is { } callerCap && Spent(callerKey, today) + est > callerCap)
                    exceeded.Add(KindCallerCost);
                if (limits.PurposeDailyCostLimits.TryGetValue(normalizedPurpose, out var purposeCap)
                    && Spent(purposeKey, today) + est > purposeCap)
                    exceeded.Add(KindPurposeCost);
            }

            // 段 2 で止めた呼び出しは枠を消費しない（IADR-0678 決定 4）。
            if (!(enforce && exceeded.Count > 0))
            {
                Enqueue(callerKey, now);
                Enqueue(purposeKey, now);
                if (estimate is { } reserve)
                {
                    AddCost(callerKey, today, reserve);
                    AddCost(purposeKey, today, reserve);
                }
            }
        }

        var mode = enforce ? ModeEnforce : ModeReport;
        foreach (var kind in exceeded)
        {
            metrics.RecordExceeded(kind, mode, who, normalizedPurpose);
            LogExceeded(kind, mode, who, normalizedPurpose);
        }

        var denied = enforce && exceeded.Count > 0;
        return new LlmCallAdmission(
            this, denied, exceeded,
            denied ? null : estimate, callerKey, purposeKey, today);
    }

    // 見込み額を実額へ置き換える（送った後）。
    internal void Settle(LlmCallAdmission admission, string? model, long inputTokens, long outputTokens, PromptCacheTokens cache)
    {
        if (admission.ReservedCost is not { } reserved) return;
        var now = clock.GetUtcNow();
        var actual = CostOf(options.CurrentValue, model, inputTokens, outputTokens, now, cache, out _);
        Adjust(admission, reserved, actual, DateOnly.FromDateTime(now.UtcDateTime));
    }

    // 見込み額を戻す（送らなかった・送れなかったとき）。
    internal void Release(LlmCallAdmission admission)
    {
        if (admission.ReservedCost is not { } reserved) return;
        Adjust(admission, reserved, 0m, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
    }

    // IADR-0678 決定 2（AI レビュー #2408）: 単価表に無いモデルは 0 にせず、表に無いモデル用の計上単価で数える。
    private decimal CostOf(
        LlmCallLimitOptions o, string? model, long inputTokens, long outputTokens, DateTimeOffset at,
        PromptCacheTokens cache, out bool unpriced)
    {
        var e = prices.Estimate(model, inputTokens, outputTokens, at, cache);
        unpriced = !e.IsPriced;
        if (e.IsPriced) return e.Cost;
        const decimal PerMillion = 1_000_000m;
        return inputTokens * o.UnknownModelPrice.InputPerMillionTokens / PerMillion
             + outputTokens * o.UnknownModelPrice.OutputPerMillionTokens / PerMillion;
    }

    private void Adjust(LlmCallAdmission admission, decimal reserved, decimal actual, DateOnly today)
    {
        lock (_gate)
        {
            foreach (var key in (string[])[admission.CallerKey, admission.PurposeKey])
            {
                // 予約した日のうちなら差分を、日を跨いだなら（予約は新しい日の枠に無いので）実額だけを今日へ足す。
                var delta = admission.ReservedDay == today ? actual - reserved : actual;
                AddCost(key, today, delta);
            }
        }
    }

    private int CountRecent(string key, DateTimeOffset now)
    {
        if (!_rate.TryGetValue(key, out var q)) return 0;
        while (q.Count > 0 && now - q.Peek() >= RateWindow) q.Dequeue();
        return q.Count;
    }

    private void Enqueue(string key, DateTimeOffset now)
    {
        if (!_rate.TryGetValue(key, out var q))
            _rate[key] = q = new Queue<DateTimeOffset>();
        while (q.Count > 0 && now - q.Peek() >= RateWindow) q.Dequeue();
        q.Enqueue(now);
    }

    private decimal Spent(string key, DateOnly today)
        => _cost.TryGetValue(key, out var b) && b.Day == today ? b.Total : 0m;

    private void AddCost(string key, DateOnly today, decimal amount)
    {
        if (!_cost.TryGetValue(key, out var b))
            _cost[key] = b = new CostBucket { Day = today };
        if (b.Day != today)
        {
            b.Day = today;
            b.Total = 0m;
        }
        b.Total = Math.Max(0m, b.Total + amount);
    }

    // purpose は**正規化の後の値**（既知の用途・default・other の閉じた集合）を受ける。抑止の鍵も同じ値で作るので、任意の値で鍵は増えない。
    // ログへ載せる値は呼び手・用途とも Sanitize を通す（NFR-28。ログ行の偽造を防ぐ）。
    private void LogExceeded(string kind, string mode, string caller, string purpose)
    {
        if (!logThrottle.ShouldLog($"call-limit|{kind}|{caller}|{purpose}", out var suppressed))
            return;
        logger.LogWarning(
            "LLM call limit exceeded: kind={LimitKind} mode={LimitMode} caller={Caller} purpose={Purpose} "
            + "suppressedSinceLast={Suppressed} (sent={Sent}; the same kind, caller and purpose is logged at most once per {Interval})",
            kind, mode, LlmRouter.Sanitize(caller), LlmRouter.Sanitize(purpose), suppressed, mode == ModeReport, LogOccurrenceThrottle.SummaryInterval);
    }
}

// 1 回の判定の結果。Settle / Release は最初の 1 回だけが効く。
public sealed class LlmCallAdmission
{
    private readonly LlmCallLimiter _owner;
    private int _closed;

    internal LlmCallAdmission(
        LlmCallLimiter owner, bool denied, IReadOnlyList<string> exceeded,
        decimal? reservedCost, string callerKey, string purposeKey, DateOnly reservedDay)
    {
        _owner = owner;
        Denied = denied;
        Exceeded = exceeded;
        ReservedCost = reservedCost;
        CallerKey = callerKey;
        PurposeKey = purposeKey;
        ReservedDay = reservedDay;
    }

    // 真なら送らない（Enforce で上限を超えた）。
    public bool Denied { get; }

    // 超えた種類（Report でも埋まる）。
    public IReadOnlyList<string> Exceeded { get; }

    internal decimal? ReservedCost { get; }
    internal string CallerKey { get; }
    internal string PurposeKey { get; }
    internal DateOnly ReservedDay { get; }

    public void Settle(string? model, long inputTokens, long outputTokens, PromptCacheTokens cache = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            _owner.Settle(this, model, inputTokens, outputTokens, cache);
    }

    public void Release()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            _owner.Release(this);
    }
}
