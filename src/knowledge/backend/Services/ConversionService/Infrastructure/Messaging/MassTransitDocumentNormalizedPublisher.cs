using ConversionService.Domain.Ports;
using Knowledge.Contracts.Events;
using MassTransit;

namespace ConversionService.Infrastructure.Messaging;

// FR-12 / ADR-0003（Superseded by ADR-0027・注記は #580）: DocumentNormalized の発行。
//
// 🔴 **本ファイルには `using Wolverine;` を入れてはならない。**
// `check-event-topology.js` の `transportsOfFile` はファイル中の `using` からトランスポートを
// 導出する。両方が同居すると、この発行が `masstransit+wolverine` の両方として記録され、
// `transportMismatches()` の**発行側 union**に wolverine が混ざる。すると
// **DocumentNormalized の購読が Wolverine へ移ったとき（E2）に違反が報告されなくなる**が、
// 実際の発行は MassTransit のままなので**メッセージは黙って捨てられる**（IADR-0245 方向 1）。
//
// **辺 DocumentNormalized は E2 の射程である。** 本 PR（E1）はトランスポートを変えない ——
// 変えるのは「どのファイルに置くか」だけである。
public sealed class MassTransitDocumentNormalizedPublisher(IPublishEndpoint bus)
    : IDocumentNormalizedPublisher
{
    public Task PublishNormalizedAsync(
        Guid documentId,
        Guid sourceId,
        string title,
        string markdownUri,
        IReadOnlyList<string> assetUris,
        IReadOnlyDictionary<string, string> attributes,
        IReadOnlyList<string> tags,
        bool hasBody = true,
        string? originalPath = null,
        string? dataSourceName = null,
        CancellationToken ct = default) =>
        // 🔴 イベントの構築をここに置くのは、`findPublishers` が `Publish(new <Event>(` にしか
        // 一致しないためである。組み立て済みの変数を渡す形にすると**発行が検査器から見えなくなる**。
        bus.Publish(new DocumentNormalized(
            DocumentId: documentId,
            SourceId: sourceId,
            Title: title,
            MarkdownUri: markdownUri,
            AssetUris: [.. assetUris],
            Attributes: new Dictionary<string, string>(attributes),
            Tags: [.. tags],
            NormalizedAt: DateTimeOffset.UtcNow,
            HasBody: hasBody,
            OriginalPath: originalPath,
            DataSourceName: dataSourceName), ct);
}
