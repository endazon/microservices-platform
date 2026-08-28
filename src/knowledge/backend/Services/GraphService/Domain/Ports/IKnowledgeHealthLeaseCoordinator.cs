namespace GraphService.Domain.Ports;

// FR-10, FR-17, SC-10, [[IADR-0299]] 決定 3 (#443): ナレッジ健全性の報告を単一書き手化する排他リース。
//
// 🔴 **省いてはならない。** 受け口の契約は**指標 1 つ分の全量スナップショット置換**
// （当該指標の全行を DELETE してから INSERT）である。2 レプリカが同時に周期へ入ると、
// **片方の DELETE がもう片方の INSERT 済み行を消し、恒久的に過少な件数が残る**
// —— 次の周期でも同じ競合が起き得るため自然回復しない。
//
// `graph` の steady state は `replicas: 1` だが、**ローリング更新の maxSurge では新旧 2 pod が
// 同時に生きる**。リースが塞ぐのはその窓である。「今は 1 レプリカだから要らない」は成り立たない。
//
// **DataSourceService の `ISyncLeaseCoordinator` と同型だが、参照ではなく複製である** ——
// サービス間の直接参照は禁止であり（`Shared.Contracts` の契約と HTTP のみ）、
// 抽象を `Platform.Shared` へ上げるのは本作業の射程を超える（[[IADR-0299]] §検討した選択肢）。
public interface IKnowledgeHealthLeaseCoordinator
{
    // リースを取得する。取得できたら破棄可能なハンドル（Dispose で解放）、
    // 他レプリカが保持中／一時障害で取得できなければ null（呼び出し側は本周期をスキップ＝fail-safe）。
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct);
}
