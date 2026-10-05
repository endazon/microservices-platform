using RetrievalService.Domain.Ports;

namespace RetrievalService.Domain;

// FR-03, FR-05, ADR-0016, ADR-0092 決定 1・2・3, [[IADR-0467]] (#336): **束ねる追加コレクション。**
//
// ADR-0016 はモデル別にコレクションを分けた（異なるモデルのベクトルは同一空間で比較できない）。
// ADR-0092 決定 1 は、分けたコレクションを **1 回の検索で束ね、順位（RRF）で合成する**と決めた。
// 主コレクション（`Qdrant:CollectionName`）は従来どおり `IVectorStore` / `IEmbeddingService` が担い、
// 本型は**それ以外に読むコレクション**を 1 つずつ、**読み手（ストア）と、そのコレクションのモデルで
// クエリを埋める客体（埋め込み）の対**で持つ。
//
// 🔴 **対で持つ理由。** ストアだけを増やして埋め込みを主と共用すると、ティア A（Ruri / 768 次元）の
// コレクションへティア B（voyage / 1024 次元）で埋めたクエリを投げることになる（ADR-0092 実測 4:
// 「(a) を実行するには (b) が要る」）。コレクションと埋め込みは**組でしか正しくない**。
//
// 🔴 **既定は空**（`None`）。空のとき、検索・属性値・削除の振る舞いは本型が無かったときと同一である。
//
// FR-03, FR-05, ADR-0127 決定 1・2, [[IADR-0497]] 決定 5 (#1746): `LexicalOnly = true` は**語彙索引**
// （ベクトルを持たないコレクション。高機密文書の置き場所）である。全文の系統だけで束ね、
// **ベクトルの系統を持たない** —— クエリを埋めない・ベクトル検索を引かない・意味検索のモードには入らない。
// 埋め込みの客体には `NoQueryEmbedding`（常に空ベクトル）を組む（二重の守り）。
public sealed record FusedCollection(
    string Collection, IVectorStore Store, IEmbeddingService Embed, bool LexicalOnly = false);

// FR-03, ADR-0127 決定 1・2, [[IADR-0497]] 決定 5 (#1746): **クエリを埋めない埋め込みの客体**（常に空ベクトル）。
// 語彙索引にはベクトルが無いので、クエリを埋めても引く先が無い。ゲートウェイを呼ばないので、
// 検索語を外部へ送る回数も増えない。空ベクトルは既存の縮退（ベクトルの系統を落とし、全文は引く）と同じ意味を持つ。
public sealed class NoQueryEmbedding : IEmbeddingService
{
    public static NoQueryEmbedding Instance { get; } = new();

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]>([]);
}

public sealed class FusedCollections(IReadOnlyList<FusedCollection> items)
{
    public static FusedCollections None { get; } = new([]);

    public IReadOnlyList<FusedCollection> Items { get; } = items;
}
