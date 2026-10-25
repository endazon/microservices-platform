using Knowledge.Contracts.Events;

namespace ConversionService.Domain.Ports;

// FR-12 例外フロー, UC-06, ADR-0027 §決定, IADR-0609 (#2169): 再変換（`/jobs/{id}/retry`）の原本イベントの発行口。
//
// 再変換は「ジョブを queued へ戻す（DB）」と「`RawDocumentFetched` を再発行する」が対である。従前は保存の**後**に
// 素の `IMessageBus.PublishAsync` で出しており、コミットの後・発行の前にプロセスが止まると、ジョブは queued のまま
// 永久に処理されず、しかも再変換は「失敗以外は 409」で受け付けない（**利用者からは取り返しが付かない**）。
//
// 形は段 2（IADR-0608 決定 1）と同じ「保存と発行を 1 つの関数で受けて確定する」`CommitAsync` である。
// 再変換の状態遷移（`IConversionJobStore.PrepareRetryAsync`）は自分で保存するので、トランザクションを**保存より前に**開く
// この形なら、ストアの約束を変えずに保存と発行を 1 つの確定に載せられる。
public interface IConversionRetryPublisher
{
    // 再変換の原本イベントを積む（送らない）。`CommitAsync` の関数の中でだけ呼べる（外では `InvalidOperationException`）。
    // ⚠️ Wolverine の `PublishAsync` は CancellationToken を取らない（E1 で受容済みの API 差）。ct は形を揃えるためだけに受ける。
    Task PublishRetryAsync(RawDocumentFetched ev, CancellationToken ct = default);

    // トランザクションを開き、関数（保存 → 発行を積む）を走らせ、DB の変更と積んだ発行を一緒にコミットしてから送る。
    // 確定（保存・コミット）の失敗は巻き戻して例外をそのまま出す。確定の**後**の送出の失敗は例外にしない（Outbox の表に残り再送される）。
    // DbContext のトランザクションが開いていない状態でだけ呼べる（開いていれば `InvalidOperationException`）。
    Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct = default);
}
