using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace DocumentService.Infrastructure.Messaging;

// FR-06, UC-03 / ADR-0027（E3b）: DocumentUpdated の発行（Wolverine）。
//
// 🔴 **本ファイルには `using MassTransit;` を入れてはならない**（transportsOfFile の union 汚染。
// WolverineDocumentDeletedPublisher と同じ作法 —— 1 ファイル 1 トランスポート）。
// （`using Wolverine.EntityFrameworkCore;` は同検査の `using Wolverine` に一致し、Wolverine として数えられる。）
//
// FR-06, ADR-0027 §決定, IADR-0608 (#2168): **Transactional Outbox 経由で発行する。**
// `IDbContextOutbox<DocumentDbContext>` はスコープ内の `DocumentDbContext` と同じインスタンスを持つ（DI のスコープで解決される）。
// `CommitAsync` がトランザクションを開き、呼び出し側の関数（保存 → 発行を積む）を走らせ、DB の変更と envelope を
// 一緒にコミットしてから送る。段 1（IADR-0604）の削除の発行口と、失敗の扱いは同じである。
public sealed class WolverineDocumentUpdatedPublisher(
    IDbContextOutbox<DocumentDbContext> outbox, ILogger<WolverineDocumentUpdatedPublisher> logger)
    : IDocumentUpdatedPublisher
{
    // `CommitAsync` の関数の中に居るか。外で積まれた発行は確定されないまま捨てられるので、拒否する（IADR-0608 決定 2）。
    private bool _inCommit;

    public async Task PublishUpdatedAsync(
        Guid documentId,
        string title,
        string status,
        string? markdownUri,
        Dictionary<string, string> attributes,
        List<string> tags,
        DateTimeOffset updatedAt,
        string? contentFingerprint = null,
        bool hasBody = true,
        string? originalPath = null,
        string? dataSourceName = null,
        List<string>? sharedWith = null,
        CancellationToken ct = default)
    {
        if (!_inCommit)
            throw new InvalidOperationException(
                "DocumentUpdated は IDocumentUpdatedPublisher.CommitAsync の関数の中でだけ積める（IADR-0608 決定 2）。"
                + " 外で積むと DB の確定と同じトランザクションに載らず、確定されないまま捨てられる。");

        // 🔴 イベントの構築をここに置くのは、`findPublishers` が `Publish(new <Event>(` にしか
        // 一致しないためである（E3b の発行が表から見える唯一の点）。
        // ⚠️ Wolverine の PublishAsync は CancellationToken を取らない（E1 で受容済みの API 差）。
        // IADR-0608: envelope は `CommitAsync` が開いたトランザクションへ INSERT される。送出はまだしない。
        await outbox.PublishAsync(new DocumentUpdated(
            documentId, title, status, markdownUri, attributes, tags, updatedAt,
            contentFingerprint, hasBody, originalPath, dataSourceName, sharedWith));
    }

    public async Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(saveAndPublish);
        if (_inCommit)
            throw new InvalidOperationException("IDocumentUpdatedPublisher.CommitAsync は入れ子にできない（IADR-0608 決定 2）。");

        // 🔴 外側のトランザクションの中では呼べない（IADR-0608 決定 2）。外側を持つ呼び出し側から呼ぶと、ここでのコミットが
        // 外側のトランザクションを勝手に確定し、巻き戻しは外側の変更まで捨てる。呼び出し側は外側を持たない約束なので、例外にする。
        var db = outbox.DbContext;
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "IDocumentUpdatedPublisher.CommitAsync は DbContext のトランザクションが開いていない状態で呼ぶ（IADR-0608 決定 2）。"
                + " 開いた外側のトランザクションを、この確定が勝手にコミットしないためである。");

        // 🔴 `AllowMultiples`: 既定（OnlyOnce）は 2 回目以降の送出を**警告 1 行で黙って捨てる**（IADR-0604 決定 2）。
        if (outbox is MessageContext messaging)
            messaging.MultiFlushMode = MultiFlushMode.AllowMultiples;

        // ① 確定。トランザクションを**保存より前に**開く —— 保存（`SaveChangesAsync`）がその中で流れ、
        // 関数の続き（共有先・タグの表示名の読み出し）が自分の書き込みを読める。envelope の INSERT も同じトランザクションに載る。
        try
        {
            await db.Database.BeginTransactionAsync(ct);
            _inCommit = true;
            try
            {
                await saveAndPublish(ct);
            }
            finally
            {
                _inCommit = false;
            }
            await db.Database.CommitTransactionAsync(ct);
        }
        catch
        {
            // 🔴 確定が失敗したら、積んだ発行を捨ててトランザクションを巻き戻す（IADR-0604 決定 2 と同じ理由。巻き戻さないと、
            // 確定しなかった更新のイベントが同じスコープの後続の確定と一緒に送られる）。
            if (outbox is MessageContext context)
                await context.ClearAllAsync();
            if (db.Database.CurrentTransaction is { } open)
                await open.RollbackAsync(CancellationToken.None);
            throw;
        }

        // ② 送出。🔴 **確定の後の送出の失敗は呼び出し側へ出さない**（IADR-0604 決定 2）。更新は確定済みであり、
        // 送れなかったイベントは Outbox の表に残って耐久エージェントが再送する。失敗は Warning で残す。
        try
        {
            await outbox.FlushOutgoingMessagesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "更新イベントの送出に失敗した。更新は確定済みで、イベントは Outbox の表に残り再送される");
        }
    }
}
