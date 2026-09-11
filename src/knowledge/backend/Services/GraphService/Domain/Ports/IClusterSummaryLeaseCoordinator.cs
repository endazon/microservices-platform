namespace GraphService.Domain.Ports;

// FR-17, FR-18, ADR-0035 決定 3・6, [[IADR-0430]] 決定 5 (#1395):
// クラスタ要約の生成バッチを単一書き手化する排他リース。
//
// 🔴 **省いてはならない。** 2 レプリカが同時に入ると、**同じクラスタ × 機密区分を二重に生成する** ——
// 壊れるのは行ではなく**費用**である（LLM 呼び出しが倍になる）。書き込み自体は主キーで冪等だが、
// 呼び出しは冪等ではない。`graph` の steady state は `replicas: 1` だが、
// ローリング更新の maxSurge で新旧 2 pod が同時に生きる。
//
// 🔴 **クラスタ検出（`IClusterDetectionLeaseCoordinator`。キー "GCLD"）とも
// ナレッジ健全性（"GKHP"）とも別の口である。** 同じ口を共有すると、
// **LLM の応答を待つ長い処理**が検出や毎時の指標報告を塞ぐ。
// advisory lock のキーも別値（"GCSM"）にしてある。
public interface IClusterSummaryLeaseCoordinator
{
    // リースを取得する。取得できたら破棄可能なハンドル、
    // 他レプリカが保持中／一時障害なら null（呼び出し側は本周期をスキップ＝fail-safe）。
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct);
}
