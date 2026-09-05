using ConversionService.Domain.Ports;

namespace ConversionService.Tests;

// ADR-0027（#441 E1）: `IDocumentNormalizedPublisher` の記録用テストダブル。
//
// E1 で `RawDocumentFetchedConsumer` の購読が Wolverine へ移り、発行は
// `IDocumentNormalizedPublisher`（MassTransit 実装は別ファイル）へ切り出された。
// **本ダブルが記録するのは「ハンドラが発行口へ何を渡したか」だけ**である。
//
// ⚠️ **引数 → `DocumentNormalized` の写像は本ダブルでは測れない**（写像はアダプタ側にある）。
// そこは `MassTransitDocumentNormalizedPublisherTests` が別に固定する ——
// ここで組み立てて突き合わせると、**ダブルとアダプタの二重実装を突き合わせるだけ**になり、
// 本番の写像が入れ替わっても気づけない。
public sealed class RecordingDocumentNormalizedPublisher : IDocumentNormalizedPublisher
{
    public sealed record Call(
        Guid DocumentId,
        Guid SourceId,
        string Title,
        string MarkdownUri,
        IReadOnlyList<string> AssetUris,
        IReadOnlyDictionary<string, string> Attributes,
        IReadOnlyList<string> Tags,
        // ADR-0070 決定 3 / IADR-0356 (#1192): 本文なしで完了したか（ハンドラが発行口へ渡した値）。
        bool HasBody = true,
        // ADR-0070 決定 4 / [[IADR-0388]] 決定 4 (#1253): 原本の所在とデータソースの表示名。
        string? OriginalPath = null,
        string? DataSourceName = null);

    private readonly List<Call> _calls = [];

    public IReadOnlyList<Call> Calls => _calls;

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
        CancellationToken ct = default)
    {
        _calls.Add(new Call(documentId, sourceId, title, markdownUri, assetUris, attributes, tags,
            hasBody, originalPath, dataSourceName));
        return Task.CompletedTask;
    }
}
