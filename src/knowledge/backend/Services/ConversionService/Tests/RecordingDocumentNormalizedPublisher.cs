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
        IReadOnlyList<string> Tags);

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
        CancellationToken ct = default)
    {
        _calls.Add(new Call(documentId, sourceId, title, markdownUri, assetUris, attributes, tags));
        return Task.CompletedTask;
    }
}
