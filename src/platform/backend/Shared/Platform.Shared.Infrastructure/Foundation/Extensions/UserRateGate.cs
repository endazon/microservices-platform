using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

// NFR-18, ADR-0032, ADR-0044, IADR-0686 (#2432): BFF の LLM を伴う口の、**利用者ごとの要求数の上限**（段階つき）。
//
// LLM ゲートウェイの上限は呼び手のサービス単位であり、利用者 1 人の量を区別しない。外周（BFF）で利用者ごとに数える。
//   - 段 1（`Bff:UserRateLimits:Mode` 未設定・`Report`）: 上限を超えた要求を計器と Warning で数えるだけ。応答は従来どおり。
//   - 段 2（`Enforce`）: 429（Retry-After: 60）で拒み、後段へ転送しない。
// 上限は群ごと（`Bff:UserRateLimits:Groups:<群>` = 直近 60 秒の件数）。設定が無い群は既定値を使う。
// 🔴 設定は**起動時に**検証する（`AddUserRateLimits` の ValidateOnStart）。最初の要求で 500 にしない。
// 状態はプロセス内（レプリカごと）。鍵は「利用者 × 群」で、利用者は検証済みの主体（sub → NameIdentifier → Name）。
public static class UserRateGate
{
    public const string SectionName = "Bff:UserRateLimits";
    public const string MeterName = "microservices-platform.bff-user-rate";
    public const string ExceededCounterName = "bff.user_rate.exceeded.total";
    public const string GroupTag = "bff.rate.group";
    public const string ModeTag = "bff.rate.mode";

    // 群の名前の値域（設定のキーと計器の属性）。群を足すときはここにも足す（設定の綴り違いを起動時に落とすため）。
    public const string GroupAnalysis = "analysis";
    public const string GroupSearch = "search";
    public const string GroupGraphSuggestion = "graph-suggestion";

    public static readonly IReadOnlySet<string> KnownGroups =
        new HashSet<string>([GroupAnalysis, GroupSearch, GroupGraphSuggestion], StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(5);

    public enum GateMode
    {
        Report,
        Enforce,
    }

    public sealed class Options
    {
        public GateMode Mode { get; set; } = GateMode.Report;

        // 群 → 直近 60 秒の件数の上限。書かなかった群は端点に書いた既定値を使う。
        // 🔴 文字列で受けて検証で解く（int で受けると、数値でない値を束ねる段で黙って捨てる。実測）。
        public Dictionary<string, string> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        internal static bool TryParseLimit(string? raw, out int limit)
            => int.TryParse(raw?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit)
               && limit > 0;
    }

    private sealed class Validator : IValidateOptions<Options>
    {
        public ValidateOptionsResult Validate(string? name, Options o)
        {
            var errors = new List<string>();
            if (!Enum.IsDefined(o.Mode))
                errors.Add($"{SectionName}:Mode が不正です（{o.Mode}）。Report か Enforce を指定してください。");
            foreach (var (group, raw) in o.Groups)
            {
                if (!KnownGroups.Contains(group))
                    errors.Add($"{SectionName}:Groups:{group} は既知の群ではありません（{string.Join(", ", KnownGroups)}）。");
                if (!Options.TryParseLimit(raw, out _))
                    errors.Add($"{SectionName}:Groups:{group} は正の整数でなければなりません（{raw}）。");
            }
            return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        }
    }

    // 設定を束ね、起動時に検証する（Mode の綴り違い・数値でない値は束ねる時点、0 以下・未知の群は検証で落ちる）。
    public static IServiceCollection AddUserRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<Options>().Bind(configuration.GetSection(SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<Options>, Validator>();
        return services;
    }

    // 群に上限を掛ける。`name` は KnownGroups のどれか（計器の属性。固定の文字列）。
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
        if (!KnownGroups.Contains(name))
            throw new ArgumentException($"未知の群 '{name}' です。UserRateGate.KnownGroups に足してください。", nameof(name));

        var state = new GateState(name);
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var user = UserKeyOf(http.User);
            // 未認証は群の門（RequireAuthorization）が先に落とす。ここへ来たら数えずに通す。
            if (user is null) return await next(ctx);

            var services = http.RequestServices;
            var o = services.GetRequiredService<IOptions<Options>>().Value;
            // 値は起動時に検証済み（正の整数）。
            var limit = o.Groups.TryGetValue(name, out var raw) && Options.TryParseLimit(raw, out var configured)
                ? configured
                : defaultPerMinute;
            if (!state.Window.CountAndCheck(user, DateTimeOffset.UtcNow, limit))
                return await next(ctx);

            state.Report(services, o.Mode);
            if (o.Mode != GateMode.Enforce) return await next(ctx);

            http.Response.Headers.RetryAfter = ((int)UserRateWindow.Length.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    private sealed class GateState(string group)
    {
        private readonly object _logGate = new();
        private Counter<long>? _counter;
        private DateTimeOffset _lastLogged = DateTimeOffset.MinValue;
        private long _suppressed;

        public UserRateWindow Window { get; } = new();

        public void Report(IServiceProvider services, GateMode mode)
        {
            var modeTag = mode == GateMode.Enforce ? "enforce" : "report";
            _counter ??= services.GetRequiredService<IMeterFactory>().Create(MeterName).CreateCounter<long>(
                ExceededCounterName, unit: "{request}",
                description: "BFF の LLM を伴う口で、利用者ごとの直近 60 秒の上限を超えた要求の件数（群・段。利用者は属性にしない）。");
            _counter.Add(1, new TagList { { GroupTag, group }, { ModeTag, modeTag } });

            long suppressed;
            lock (_logGate)
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

// NFR-18, IADR-0686 (#2432): 利用者ごとの直近 60 秒の窓。
// 🔴 **超えた要求は、段に依らず記録しない**（段 2 で拒んだ要求と同じ扱い）。記録は利用者ごとに上限の件数で頭打ちになり、
// 段 1 と段 2 で「どの要求が超えたか」の判定が変わらない（段 1 の計器が、段 2 で拒む件数をそのまま表す）。
internal sealed class UserRateWindow
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);

    // 直近 60 秒の件数が上限に達していれば true（このとき記録しない）。達していなければ記録して false。
    public bool CountAndCheck(string user, DateTimeOffset now, int limit)
    {
        lock (_gate)
        {
            if (!_hits.TryGetValue(user, out var q))
                _hits[user] = q = new Queue<DateTimeOffset>();
            while (q.Count > 0 && now - q.Peek() >= Length) q.Dequeue();
            if (q.Count >= limit) return true;
            q.Enqueue(now);
            // 古い利用者の鍵を残し続けない。
            if (_hits.Count > 10_000)
                foreach (var key in _hits.Where(kv => kv.Value.Count == 0 || now - kv.Value.Last() >= Length).Select(kv => kv.Key).ToList())
                    _hits.Remove(key);
            return false;
        }
    }

    // 試験用: 利用者の記録の件数。
    internal int Tracked(string user)
    {
        lock (_gate) return _hits.TryGetValue(user, out var q) ? q.Count : 0;
    }
}
