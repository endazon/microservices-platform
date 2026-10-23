namespace WikiService.Domain.Ports;

// 🔴 FR-13, UC-07, ADR-0011, NFR-05, [[IADR-0563]] 残余リスク (a), [[IADR-0599]] 決定 1 (#2100): Wiki.js への書き込みの、**レプリカをまたぐ**枠。
//
// 書き込みの門（`WikiJsWriteGate`）はプロセスの中の枠（`SemaphoreSlim`）だけでは、wiki-service のレプリカの数だけ Wiki.js への書き込みが並ぶ
// （helm の既定は HPA の minReplicas 2。ローリング更新の maxSurge でも新旧 2 pod が同時に生きる）。Wiki.js 2.5 の木の再構築は並ぶと
// `pageTree` の主キーで衝突する（#1971）。この口は、全レプリカで合わせて `slots` 本までしか書き込みを通さない枠を取る。
//
// - 取れるまで待つ。待ちは `ct` で切る（門が待ちの期限を連結して渡す）。切られたら例外を投げ、**枠を持ったまま返らない**。
// - 返したハンドルの破棄で枠を返す。破棄は何度呼んでもよい（2 回目以降は何もしない）。
// - 取得に失敗したら（DB へ届かない等）例外のまま投げる。**枠なしで書き込みを強行しない**（fail-closed。受け口の再試行へ委ねる）。
public interface IWikiJsReplicaWriteLock
{
    Task<IAsyncDisposable> AcquireAsync(int slots, CancellationToken ct);
}
