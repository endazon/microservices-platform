namespace DataSourceService.Api.Foundation.Services;

// FR-01, UC-04（基本フロー: 定期取得）, IADR-0051: 定期同期の設定。
// 既定は無効（dev/test での意図せぬファイル走査を避ける）。本番は config で有効化する。
public sealed class DataSourceSyncOptions
{
    public const string SectionName = "DataSourceSync";

    // 定期同期を有効にするか（既定 false）。
    public bool Enabled { get; set; }

    // 同期間隔（秒）。既定 300（5 分）。実効は 30 秒未満に丸めない（過負荷防止）。
    public int IntervalSeconds { get; set; } = 300;
}
