namespace DocumentService.Domain.Ports;

// 🔴 FR-19, FR-20, FR-22, ADR-0096 決定 1・2, [[IADR-0599]] 決定 5 (#2100): 個人資料の定期処理（`PrivateNoteMaintenanceHostedService`）を
// 単一実行にする排他リース。
//
// document-service は helm の既定で HPA（minReplicas 2）であり、ローリング更新の maxSurge でも新旧 2 pod が同時に生きる。
// 定期処理は「読んでから消す・読んでから通知の発火記録を足す」の形であり、2 レプリカが同じ周期に並ぶと、
// 同じ行の削除・同じ通知の送出を 2 本が同時に判定し得る（発火記録で重複を止めるのは、記録が確定した**後**に読んだ側だけである）。
//
// **GraphService の `IKnowledgeHealthLeaseCoordinator`（[[IADR-0299]] 決定 3）と同型の複製である** —— サービス間の直接参照は禁止であり、
// 抽象を `Platform.Shared` へ上げるのは本作業の射程を超える（同 IADR §検討した選択肢と同じ判断）。
public interface IPrivateNoteMaintenanceLeaseCoordinator
{
    // リースを取得する。取得できたら破棄可能なハンドル（Dispose で解放）、
    // 他レプリカが保持中／一時障害で取得できなければ null（呼び出し側は本周期をスキップ＝fail-safe）。
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct);
}
