namespace DataSourceService.Domain.Ports;

// FR-01, UC-04, IADR-0083 (#305): 定期同期の単一書き手化のための排他リース抽象。
// 本番マルチレプリカ（HPA minReplicas 2）で 2 pod が同時に定期同期を回すと原本 fetch が冗長になる。
// 各同期サイクルの実行前にリースを取得し、取得できたレプリカのみが同期することで単一書き手化する。
public interface ISyncLeaseCoordinator
{
    // 定期同期の排他リースを取得する。取得できたら破棄可能なハンドル（Dispose で解放）、
    // 他レプリカが保持中／一時障害で取得できなければ null（呼び出し側は本サイクルをスキップ＝fail-safe）を返す。
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct);
}
