using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

// NFR-18, ADR-0032, ADR-0044, IADR-0686 (#2432): BFF の LLM を伴う口の、**利用者ごとの要求数の上限**（段階つき）。
//
// LLM ゲートウェイの上限は呼び手のサービス単位であり、利用者 1 人の量を区別しない。外周（BFF）で利用者ごとに数える。
//   - 段 1（`Bff:UserRateLimits:Mode` 未設定・`Report`）: 上限を超えた要求を計器と Warning で数えるだけ。応答は従来どおり。
//   - 段 2（`Enforce`）: 429（Retry-After: 60）で拒み、後段へ転送しない。
// 上限は群ごと（`Bff:UserRateLimits:Groups:<群>` = 直近 60 秒の件数）。設定が無い群は既定値を使う。
// 状態はプロセス内（レプリカごと）。鍵は「利用者 × 群」で、利用者は検証済みの主体（sub → NameIdentifier → Name）。
public static class UserRateGate
{
    public const string ModeConfigKey = "Bff:UserRateLimits:Mode";
    public const string GroupsConfigSection = "Bff:UserRateLimits:Groups";
    public const string MeterName = "microservices-platform.bff-user-rate";
    public const string ExceededCounterName = "bff.user_rate.exceeded.total";
    public const string GroupTag = "bff.rate.group";
    public const string ModeTag = "bff.rate.mode";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(5);

    public enum GateMode
    {
        Report,
        Enforce,
    }

    public static GateMode ReadMode(IConfiguration configuration)
    {
        var raw = configuration[ModeConfigKey];
        if (string.IsNullOrWhiteSpace(raw)) return GateMode.Report;
        if (Enum.TryParse<GateMode>(raw.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode)) return mode;
        throw new InvalidOperationException($"{ModeConfigKey} の値が不正です（{raw}）。Report か Enforce を指定してください（#2432）。");
    }

    public static int ReadLimit(IConfiguration configuration, string group, int defaultPerMinute)
    {
        var raw = configuration[$"{GroupsConfigSection}:{group}"];
        if (string.IsNullOrWhiteSpace(raw)) return defaultPerMinute;
        if (int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
            return v;
        throw new InvalidOperationException($"{GroupsConfigSection}:{group} は正の整数でなければなりません（{raw}。#2432）。");
    }

    // 群に上限を掛ける。`group` は計器とログの属性（固定の文字列）。`defaultPerMinute` は設定が無いときの上限。
    public static RouteGroupBuilder LimitPerUserOrReport(this RouteGroupBuilder group, string name, int defaultPerMinute)
    {
        Attach(group, name, defaultPerMinute);
        return group;
    }

    public static RouteHandlerBuilder LimitPerUserOrReport(this RouteHandlerBuilder endpoint, string name, int defaultPerMinute)
    {
        Attach(endpoint, name, defaultPerMinute);
        return endpoint;
    }

    private static void Attach<TBuilder>(TBuilder builder, string name, int defaultPerMinute)
        where TBuilder : IEndpointConventionBuilder
    {
        var state = new GateState(name, defaultPerMinute);
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var user = UserKeyOf(http.User);
            // 未認証は群の門（RequireAuthorization）が先に落とす。ここへ来たら数えずに通す。
            if (user is null) return await next(ctx);

            var services = http.RequestServices;
            var (mode, limit) = state.Settings(services.GetRequiredService<IConfiguration>());
            var now = DateTimeOffset.UtcNow;
            var exceeded = state.CountAndCheck(user, now, limit, consumeWhenExceeded: mode == GateMode.Report);
            if (!exceeded) return await next(ctx);

            state.Report(services, mode);
            if (mode != GateMode.Enforce) return await next(ctx);

            http.Response.Headers.RetryAfter = ((int)Window.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
                title: "短い間に要求が多すぎます。しばらく待ってから、もう一度試してください。");
        });
    }

    internal static string? UserKeyOf(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        var id = user.FindFirst("sub")?.Value
                 ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? user.Identity?.Name;
        return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    }

    private sealed class GateState(string group, int defaultPerMinute)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);
        private (GateMode Mode, int Limit)? _settings;
        private Counter<long>? _counter;
        private DateTimeOffset _lastLogged = DateTimeOffset.MinValue;
        private long _suppressed;

        // 構成は起動の後に変わらない前提で、最初の要求で読む（不正な値はここで例外になる）。
        public (GateMode Mode, int Limit) Settings(IConfiguration configuration)
            => _settings ??= (ReadMode(configuration), ReadLimit(configuration, group, defaultPerMinute));

        // 直近 60 秒の件数が上限に達していれば true。段 2 で拒んだ要求は枠を消費しない。
        public bool CountAndCheck(string user, DateTimeOffset now, int limit, bool consumeWhenExceeded)
        {
            lock (_gate)
            {
                if (!_hits.TryGetValue(user, out var q))
                    _hits[user] = q = new Queue<DateTimeOffset>();
                while (q.Count > 0 && now - q.Peek() >= Window) q.Dequeue();
                var exceeded = q.Count >= limit;
                if (!exceeded || consumeWhenExceeded) q.Enqueue(now);
                // 古い利用者の鍵を残し続けない（空になった列は捨てる）。
                if (_hits.Count > 10_000)
                    foreach (var key in _hits.Where(kv => kv.Value.Count == 0 || now - kv.Value.Last() >= Window).Select(kv => kv.Key).ToList())
                        _hits.Remove(key);
                return exceeded;
            }
        }

        public void Report(IServiceProvider services, GateMode mode)
        {
            var modeTag = mode == GateMode.Enforce ? "enforce" : "report";
            _counter ??= services.GetRequiredService<IMeterFactory>().Create(MeterName).CreateCounter<long>(
                ExceededCounterName, unit: "{request}",
                description: "BFF の LLM を伴う口で、利用者ごとの直近 60 秒の上限を超えた要求の件数（群・段。利用者は属性にしない）。");
            _counter.Add(1, new TagList { { GroupTag, group }, { ModeTag, modeTag } });

            long suppressed;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastLogged < LogInterval)
                {
                    _suppressed++;
                    return;
                }
                _lastLogged = now;
                suppressed = _suppressed;
                _suppressed = 0;
            }
            // 🔴 利用者の識別子はログにも載せない（群と件数だけ）。
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(UserRateGate).FullName!)
                .LogWarning(
                    "BFF user rate limit exceeded in group {Group} (mode={Mode}; suppressedSinceLast={Suppressed}; logged at most once per {Interval}).",
                    group, modeTag, suppressed, LogInterval);
        }
    }
}
