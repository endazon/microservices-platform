using DocumentService.Application.Foundation.Ports;
using Knowledge.Contracts.Events;
using Wolverine;

namespace DocumentService.Api.Composable.Adapters;

// FR-06, UC-03 / ADR-0027（E3b）: DocumentUpdated の発行（Wolverine）。
//
// 🔴 **本ファイルには `using MassTransit;` を入れてはならない**（transportsOfFile の union 汚染。
// WolverineDocumentDeletedPublisher と同じ作法 —— 1 ファイル 1 トランスポート）。
public sealed class WolverineDocumentUpdatedPublisher(IMessageBus bus) : IDocumentUpdatedPublisher
{
    public async Task PublishUpdatedAsync(
        Guid documentId,
        string title,
        string status,
        string? markdownUri,
        Dictionary<string, string> attributes,
        List<string> tags,
        DateTimeOffset updatedAt,
        CancellationToken ct = default) =>
        // 🔴 イベントの構築をここに置くのは、`findPublishers` が `Publish(new <Event>(` にしか
        // 一致しないためである（E3b の発行が表から見える唯一の点）。
        // ⚠️ Wolverine の PublishAsync は CancellationToken を取らない（E1 で受容済みの API 差）。
        await bus.PublishAsync(new DocumentUpdated(
            documentId, title, status, markdownUri, attributes, tags, updatedAt));
}
