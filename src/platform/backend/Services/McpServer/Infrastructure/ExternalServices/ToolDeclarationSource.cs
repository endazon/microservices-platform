using McpServer.Domain;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace McpServer.Infrastructure.ExternalServices;

// FR-16, FR-15, ADR-0024 §2: 各サービスの自己申告（gRPC `platform.mcp.v1.McpToolDeclarations/Declare`）を集める口。
// FR-15 の自己申告（`platform.introspection.v1.ServiceIntrospection/Get`）と同じ規約系に置く（メッシュ内部限定）。
// ［2026-10-10 / #1517・[[IADR-0533]]］REST の `GET /internal/mcp-tools` は撤去した。
public interface IToolDeclarationSource
{
    Task<IReadOnlyList<ServiceToolDeclarations>> CollectAsync(CancellationToken ct);
}

// FR-16, NFR-16, ADR-0024 §2・§5, ADR-0029, ADR-0075, 計画 ADR-0089 決定 1, IADR-0462（#1515, #1255 経路 ④-a）,
// [[IADR-0533]] 決定 3 (#1517): MCP サーバーが使う申告の収集器。
//
// 収集先は構成（`Mcp:Services:<サービス名>` = 申告元サービスの **gRPC（h2c）アドレス**）で与える。**コードへサービス名を
// 書かない** —— 新サービスがツールを公開する手順を「自サービスに申告の gRPC 面を実装」＋「公開構成に追記」の
// 2 手順に保つため（ADR-0024 §決定「コア改修不要の追従」）。
//
// ［2026-10-10 / #1517］**REST（`GET /internal/mcp-tools`）の収集 `HttpToolDeclarationSource` は撤去した。**
// 従前は `Mcp:Services`（REST）と `Mcp:GrpcServices`（gRPC）のキーの和を宛先とし、gRPC 側に在る宛先だけを gRPC で
// 集めていた（並走中の正は REST）。いまは構成キーを `Mcp:Services` の 1 つへ一本化し、値は gRPC の宛先である。
// 収集の順序（構成の順・逐次）と失敗の扱い（申告なしへ畳む）は従前と同じ。
public sealed class ToolDeclarationSource(
    GrpcToolDeclarationCollector grpc,
    IConfiguration configuration,
    ILogger<ToolDeclarationSource>? logger = null) : IToolDeclarationSource
{
    private readonly ILogger _logger =
        logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolDeclarationSource>.Instance;

    public async Task<IReadOnlyList<ServiceToolDeclarations>> CollectAsync(CancellationToken ct)
    {
        // 逐次に収集する（起動時＋定期の背景処理であり応答時間を待つ利用者は居ない）。
        var collected = new List<ServiceToolDeclarations>();
        foreach (var (service, address) in GrpcToolDeclarationCollector.ConfiguredTargetList(configuration))
        {
            var declared = await grpc.CollectOneAsync(service, address, ct);
            // ［2026-09-27 / #1516 監査 M-1］封筒の `service` を収集先のキーへ結び付ける（下の DeclarationBinding）。
            if (DeclarationBinding.Bind(service, declared, _logger) is { } bound)
                collected.Add(bound);
        }
        return collected;
    }
}

// 🔴 FR-16, NFR-09, ADR-0024 §5, ADR-0117 決定 1, IADR-0462（2026-09-27 追記 / #1516 監査 M-1）:
// **申告の封筒の `service` を、収集先のキー（`Mcp:Services` の名前）へ結び付ける。**
//
// 申告の `service` は ToolCatalog の突合キーになり、ツールの実行先（`Mcp:Services:<service>`）もそこから引く。
// 封筒の名乗りをそのまま信じると、キー X で集めたサービスが `service = "Y"` と名乗るだけで、Y のツールとして
// **自分の説明・必要スコープ・越境分類を公開**でき、その実行は Y へ送られる —— ADR-0117 決定 1「申告の中身で他のサービスを
// 宛先にできない」の破れであり、同じ `Y::name` が 2 つ届くと突合も壊れていた。
//
// 🔴 **食い違いは書き換えず拒否する**（申告なしとして扱い、Error で記録する）。書き換え（`service` をキーで上書き）を採らない理由:
//   1. 食い違いは「構成のキーが別のサービスのアドレスを指している」配線の誤りか、名乗りの偽装のどちらかであり、
//      どちらの場合も**その申告がキーのサービスのものだという根拠が無い**。書き換えると、他人の申告をキーの名で公開する推測になる
//      （ADR-0024 §5「推測で公開しない」）。
//   2. 拒否すれば公開構成が要求するツールは「申告なし」の構成ドリフトとして管理 API とログに現れ、運用者が配線を直せる。
//      書き換えは誤配線を静かに通してしまう。
//   3. 他のサービスの申告には影響しない（1 宛先ぶんを捨てるだけ）。
// 比較は大文字小文字を区別する（公開構成・ToolCatalog のキーと同じ Ordinal）。
internal static class DeclarationBinding
{
    public static ServiceToolDeclarations? Bind(string target, ServiceToolDeclarations? declared, ILogger logger)
    {
        if (declared is null || string.Equals(declared.Service, target, StringComparison.Ordinal))
            return declared;

        logger.LogError(
            "MCP tool declarations collected from {Target} name another service ({DeclaredService}); rejected "
            + "(the envelope service must equal the collection target key — check the Mcp:Services address)",
            target, ForLog(declared.Service));
        return null;
    }

    // 相手の応答に由来する名前を行指向のログへそのまま落とさない（制御文字を潰し、長さを切る）。
    private static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(empty)";
        var cleaned = new string(Array.ConvertAll(value.ToCharArray(), c => char.IsControl(c) ? '_' : c));
        return cleaned.Length <= 128 ? cleaned : cleaned[..128] + "…";
    }
}

// FR-16, NFR-16, IADR-0462（2026-09-26 追記 / #1515）, [[IADR-0533]] 決定 3 (#1517): 申告の収集器の登録。
public static class ToolDeclarationSourceExtensions
{
    // 🔴 撤去した旧キー `Mcp:GrpcServices` が残っていれば、ここ（組み立て時）で起動を止める。
    // gRPC の収集器と s2s トークンの発行側は常に登録する（収集の輸送は gRPC だけである）。
    public static IServiceCollection AddMcpToolDeclarationSources(
        this IServiceCollection services, IConfiguration configuration)
    {
        GrpcToolDeclarationCollector.EnsureRetiredKeyAbsent(configuration);
        services.AddPlatformServiceToken(configuration);
        services.AddSingleton<GrpcToolDeclarationCollector>();
        services.AddScoped<IToolDeclarationSource, ToolDeclarationSource>();
        return services;
    }
}

// FR-16, ADR-0024 §2: 起動時＋定期（既定 5 分）に自己申告を収集し、公開構成と突合する。
public sealed class ToolCatalogRefresher(
    IServiceProvider services,
    ToolCatalog catalog,
    ToolPublicationConfigLoader loader,
    IConfiguration configuration,
    ILogger<ToolCatalogRefresher> logger) : BackgroundService
{
    public const string IntervalKey = "Mcp:RefreshIntervalSeconds";

    // #1604: 周期の実際の長さ。**試験だけが与える**（構成の周期は 10 秒未満に丸められ、試験で待てない）。
    // 本番の組み立て（DI）は触らない（null なら構成から読む）。形は #1598 の `CycleInterval` と同じ。
    internal TimeSpan? CycleInterval { get; init; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue<int?>(IntervalKey) ?? 300;
        var interval = CycleInterval ?? TimeSpan.FromSeconds(Math.Max(seconds, 10));

        while (!stoppingToken.IsCancellationRequested)
        {
            // 🔴 構成の破損（検証失敗）と、収集の一時失敗は別物である（#445 レビュー指摘）。
            // 前者は再試行しても直らず、握り潰すと「公開されているつもりの公開されていない」状態が
            // エラーログだけを吐きながら継続する。ホストを止めて気づかせる
            // （BackgroundServiceExceptionBehavior の既定は StopHost）。
            ToolPublicationConfig published;
            try
            {
                published = loader.Load();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogCritical(ex, "MCP publication config is invalid; stopping host");
                throw;
            }

            // 後者（自己申告の収集）は到達不能なサービスがあり得るため、次の周期へ持ち越す。
            try
            {
                using var scope = services.CreateScope();
                var source = scope.ServiceProvider.GetRequiredService<IToolDeclarationSource>();
                catalog.Refresh(published, await source.CollectAsync(stoppingToken));
            }
            // ［2026-09-26 / #1604・IADR-0462 追記］🔴 **素通しするのは停止要求（stoppingToken）の取り消しだけである。**
            // 収集器の内側で畳み損ねた取り消し（下流の時間切れ等）は収集の一時失敗であり、ここで記録して次の周期へ進む。
            // 型だけで素通しすると ExecuteAsync から漏れ、既定の StopHost でホスト全体が止まる。
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "MCP tool catalog refresh failed");
            }

            // #1604: 想定外の取り消しを黙って「シャットダウン」と読まない（停止要求のときだけ抜ける）。
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
