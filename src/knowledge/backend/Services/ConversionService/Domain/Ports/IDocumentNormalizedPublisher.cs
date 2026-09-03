namespace ConversionService.Domain.Ports;

// FR-12 / ADR-0027（#441 E1）: 正規化完了イベントの発行口。
//
// 🔴 **この抽象を挟む理由は設計の好みではない。トポロジ検査の導出単位がファイルだからである。**
//
// `scripts/check-event-topology.js` の `transportsOfFile` は、**ファイル中の `using` から
// そのファイルのトランスポートを導出する**。E1 で `RawDocumentFetchedConsumer` の購読を
// Wolverine へ移した結果、同ファイルに `using MassTransit;`（DocumentNormalized の発行用）と
// `using Wolverine;`（購読用）が同居し、**同ファイルの DocumentNormalized 発行が
// `masstransit+wolverine` の両方として記録された**（実測）。
//
// `transportMismatches()` は**発行側のトランスポートを和集合で取る**ため、これを放置すると
// **E2 が DocumentService の購読を Wolverine へ移した瞬間、union に既に wolverine が入っていて
// 違反が報告されない**。しかし実際の発行は MassTransit のままなので、
// **メッセージは黙って捨てられる**（IADR-0245 方向 1）。**E1 が E2 の地雷を埋めることになる。**
//
// よって発行を別ファイルへ切り出し、**1 ファイルに 1 トランスポートだけが現れる**ようにする。
//
// ⚠️ **イベントの構築は実装側（アダプタ）に置く。** ここで組み立てて `Publish(ev)` の形にすると、
// `findPublishers` の regex（`Publish(new <Event>(` にしか一致しない）から**発行が見えなくなり**、
// 別の不可視発行元を新たに作ってしまう。
public interface IDocumentNormalizedPublisher
{
    Task PublishNormalizedAsync(
        Guid documentId,
        Guid sourceId,
        string title,
        string markdownUri,
        IReadOnlyList<string> assetUris,
        IReadOnlyDictionary<string, string> attributes,
        IReadOnlyList<string> tags,
        // ADR-0070 決定 3 / IADR-0356 (#1192): 本文なし（テキスト層の無い PDF）で完了したか。
        // 後続（カタログ・索引）が本文由来のチャンクを作らず、メタデータで検索に載せる判断に使う（決定 4 の射程）。
        bool bodyAbsent = false,
        CancellationToken ct = default);
}
