using DataSourceService.Domain;
using DataSourceService.Features.DataSources.Create;
using DataSourceService.Features.DataSources.Disable;
using DataSourceService.Features.DataSources.GetById;
using DataSourceService.Features.DataSources.List;
using DataSourceService.Features.DataSources.Patch;
using DataSourceService.Features.DataSources.Sync;
using DataSourceService.Features.DataSources.Update;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DataSourceService.Features.DataSources;

// FR-01, UC-04: データソース管理集約の登録表（ADR-0068 決定 1）。
//
// `MapGroup` とタグ付け・グループ単位の認可は集約の全操作が使うものであり、特定の 1 操作に属さない。
// 各操作の処理は `Features/DataSources/<操作>/` に居る（ADR-0065 決定 2）。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— route group と、
// 5 つの操作が使う応答投影（秘密のマスクを含む）。
public static class DataSourceEndpoints
{
    public static IEndpointRouteBuilder MapDataSourceEndpoints(this IEndpointRouteBuilder app)
    {
        // FR-09, IADR-0044: 多層防御。データソースは運用資産で、閲覧・操作は管理者・運用者に限定する
        // （[[IADR-0039]] の BFF ゲートと同一要件）。BFF 迂回の直接呼び出しでも認可を実効化する
        // （サービスが最終防衛線）。利用者トークンは BFF が後段へ伝播する。
        var g = app.MapGroup("/datasources").WithTags("DataSources")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        ListDataSourcesEndpoint.Map(g);
        GetDataSourceEndpoint.Map(g);
        CreateDataSourceEndpoint.Map(g);
        SyncDataSourceEndpoint.Map(g);
        UpdateDataSourceEndpoint.Map(g);
        PatchDataSourceEndpoint.Map(g);
        DisableDataSourceEndpoint.Map(g);

        return app;
    }

    // IADR-0053 / IADR-0295, claude-review #222: API 応答用の投影。エンティティをそのまま返すと
    // Config 内の秘密（apiToken 等）と ConnectionUri 内の資格情報が平文露出するため、
    // **どちらもマスクして返す**（Vault 移行までの暫定）。
    // SC-06: nextSyncAt はエンティティに持たない導出値（共通間隔の次回実行時刻）であり、呼び出し側が渡す。
    //
    // **一覧・個別・登録・更新・部分更新の 5 操作が使う**ため 2 段目に残る（ADR-0068 決定 2）。
    internal static object ToResponse(DataSource ds, DateTimeOffset? nextSyncAt) => new
    {
        ds.Id,
        ds.Name,
        ds.SourceType,
        // IADR-0295 決定 3: `ConnectionUri` も伏せる。**`SecretConfigMask` は `Config` にしか
        // 掛からず、ここは素通しだった。** `SecretMask` の URI 規則が `scheme://user:pass@host` を
        // 明示的に想定して伏せている以上、**そこへ資格情報が入り得ることをコード自身が認めている。**
        // `DatabaseConnector` は本値を ADO.NET 接続文字列の土台に使うので `Host=..;Password=..`
        // 形式も入り得る（キー=値 規則が捕まえる）。
        ConnectionUri = SecretMask.RedactText(ds.ConnectionUri),
        ds.Status,
        ds.LastSyncedAt,
        Config = RedactSecrets(ds.Config),
        ds.DefaultAttributes,
        ds.CreatedAt,
        NextSyncAt = nextSyncAt,
        // SC-06（Q14 / #537）: 同期健全性。RetryLimit は画面が「3/5」の分母を契約から得るために返す
        // （画面へ定数を複写すると IADR-0127 決定 2 が禁じた「契約から導出できない表示」に戻る）。
        // LastSyncError は保存時点でマスク済み（SyncErrorRedactor）。
        ds.ConsecutiveFailureCount,
        RetryLimit = DataSourceSyncService.AlertThreshold,
        ds.LastSyncError,
        ds.LastSyncErrorAt,
    };

    // 秘密キーの定義とマスク値は SecretConfigMask が単一情報源である（IADR-0148 決定 6）。
    // **読み（ここ）と書き（DataSource.Update / Patch）で同じ知識を使う** —— 2 箇所に持つと、
    // マーカーを足したときに片方が黙って古くなる。
    private static Dictionary<string, string> RedactSecrets(IReadOnlyDictionary<string, string> config)
        => SecretConfigMask.Redact(config);
}
