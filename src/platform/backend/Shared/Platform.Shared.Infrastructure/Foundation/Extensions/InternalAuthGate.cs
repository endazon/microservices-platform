using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

// NFR-18, ADR-0084 決定 1, IADR-0685 (#2430): 内部サービスの REST の群に掛ける**段階つきの認証の門**。
//
// 計画 ADR-0084 決定 1 は門を端点（群）ごとに掛けることを求める。`/analysis/*`・`/wiki/*` は門を持たず、
// 未認証をハンドラの中で「閲覧できるものが無い」へ倒していた（IADR-0335）。素の `RequireAuthorization()` を足すと
// 応答が 200 から 401 へ変わり、稼働の呼び手を止め得るので、段階で入れる。
//   - 段 1（`Auth:InternalGate:Mode` 未設定・`Report`）: 未認証の要求を計器と Warning で数えるだけ。応答は従来どおり。
//   - 段 2（`Enforce`）: 未認証の要求を 401 で拒む（ハンドラへ入らない）。
// 🔴 段の値が不正なら起動の時点で落とす（「止めたつもりで報告だけ」へ黙って倒さない）。
public static class InternalAuthGate
{
    public const string ModeConfigKey = "Auth:InternalGate:Mode";
    public const string MeterName = "microservices-platform.internal-auth-gate";
    public const string AnonymousCounterName = "auth.internal_gate.anonymous.total";
    public const string GroupTag = "auth.gate.group";
    public const string ModeTag = "auth.gate.mode";

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
        throw new InvalidOperationException($"{ModeConfigKey} の値が不正です（{raw}）。Report か Enforce を指定してください（#2430）。");
    }

    // 群に門を掛ける。`groupName` は計器とログの属性（固定の文字列。利用者由来の値を渡さない）。
    public static RouteGroupBuilder RequireAuthenticatedOrReport(this RouteGroupBuilder group, string groupName)
    {
        var state = new GateState(groupName);
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (http.User.Identity?.IsAuthenticated == true)
                return await next(ctx);

            var services = http.RequestServices;
            var mode = state.Mode(services.GetRequiredService<IConfiguration>());
            state.Count(services, mode);
            return mode == GateMode.Enforce ? Results.Unauthorized() : await next(ctx);
        });
        return group;
    }

    private sealed class GateState(string group)
    {
        private GateMode? _mode;
        private Counter<long>? _counter;
        private long _lastLoggedTicks;
        private long _suppressed;

        // 構成は起動の後に変わらない前提で、最初の要求で読み、以後は使い回す（不正な値はここで例外になる）。
        public GateMode Mode(IConfiguration configuration) => _mode ??= ReadMode(configuration);

        public void Count(IServiceProvider services, GateMode mode)
        {
            _counter ??= services.GetRequiredService<IMeterFactory>().Create(MeterName).CreateCounter<long>(
                AnonymousCounterName, unit: "{request}",
                description: "内部の REST の群へ届いた未認証の要求の件数。auth.gate.mode=report は通した（段 1）、enforce は 401 で拒んだ（段 2）。");
            var modeTag = mode == GateMode.Enforce ? "enforce" : "report";
            _counter.Add(1, new TagList { { GroupTag, group }, { ModeTag, modeTag } });

            // 同じ群の Warning は 5 分に 1 行（抑えた件数つき）。
            var now = DateTimeOffset.UtcNow.UtcTicks;
            var last = Interlocked.Read(ref _lastLoggedTicks);
            if (now - last < LogInterval.Ticks)
            {
                Interlocked.Increment(ref _suppressed);
                return;
            }
            if (Interlocked.CompareExchange(ref _lastLoggedTicks, now, last) != last)
            {
                Interlocked.Increment(ref _suppressed);
                return;
            }
            var suppressed = Interlocked.Exchange(ref _suppressed, 0);
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(InternalAuthGate).FullName!)
                .LogWarning(
                    "Unauthenticated request reached internal group {Group} (mode={Mode}; suppressedSinceLast={Suppressed}; logged at most once per {Interval}).",
                    group, modeTag, suppressed, LogInterval);
        }
    }
}
