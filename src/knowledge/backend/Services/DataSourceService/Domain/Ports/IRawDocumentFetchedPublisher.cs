using Knowledge.Contracts.Events;

namespace DataSourceService.Domain.Ports;

// FR-01, UC-04, ADR-0027 §決定, IADR-0657 (#2361): 同期が取得した原本のイベント `RawDocumentFetched` の発行口。
//
// 🔴 **返ったときには、イベントは送出表（Outbox）にコミット済みである。** 表へ書けなければ例外を投げる ——
// 呼び出し側（`DataSourceSyncService`）はその 1 件を失敗に数え、watermark を進めない。
// 素の `IMessageBus.PublishAsync` はこの約束を持たない（表への書き込みの失敗を例外にせず裏で再試行し、
// 上限で黙って捨てる。#2361 の実測）ので、同期はこの口だけを使う。
public interface IRawDocumentFetchedPublisher
{
    Task PublishAsync(RawDocumentFetched ev, CancellationToken ct = default);
}
