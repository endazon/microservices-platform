namespace GraphService.Domain.Ports;

// FR-17, ADR-0035 決定 3, [[IADR-0425]] 決定 6 (#1363): 日次のクラスタ検出を単一書き手化する排他リース。
//
// 🔴 **省いてはならない。** 検出は「クラスタの全量を検出結果へ突き合わせる」書き込みであり、
// 2 レプリカが同時に入ると**互いの対応づけの前提を壊す**（片方が作ったクラスタを他方が
// 「対応がつかない既存クラスタ＝消滅」として消す）。**要約の生成時刻もろとも消える**ため、
// 自然回復しない。`graph` の steady state は `replicas: 1` だが、ローリング更新の maxSurge で
// 新旧 2 pod が同時に生きる。
//
// **`IKnowledgeHealthLeaseCoordinator` と同型だが別の口である。** 🔴 **同じ口を共有してはならない** ——
// 日次のクラスタ検出（長い）が毎時のナレッジ健全性の報告（短い）を塞ぎ、指標が丸ごと止まる。
// advisory lock のキーも別値にしてある。
public interface IClusterDetectionLeaseCoordinator
{
    // リースを取得する。取得できたら破棄可能なハンドル、
    // 他レプリカが保持中／一時障害なら null（呼び出し側は本周期をスキップ＝fail-safe）。
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct);
}
