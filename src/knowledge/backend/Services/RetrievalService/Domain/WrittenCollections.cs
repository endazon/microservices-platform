using RetrievalService.Domain.Ports;

namespace RetrievalService.Domain;

// FR-06, FR-19, ADR-0057 決定 1 (#2137): **取り込みが書くが、検索が読まないコレクション**（削除だけの対象）。
//
// 文書の削除（`DocumentDeleted`）は検索が読むコレクション（主・束ねる追加・語彙索引）から点を消す。取り込みが書く集合
// （`Embedding:Collections`。取り込みの `appsettings.json` と同じ値を本サービスの `appsettings.json` にも置き、
// `Knowledge.IntegrationTests` の試験が一致を固定する）が読む集合より広いとき —— 決定的ローカル埋め込みの検証スタックで
// 主を決定的なコレクションへ差し替えた場合の voyage —— その差の分をここに持ち、削除だけを行う。
// 既定の配備では差は空（主が voyage）であり、削除は従来と同一である。
public sealed class WrittenCollections(IReadOnlyList<WrittenCollection> items)
{
    public static WrittenCollections None { get; } = new([]);

    public IReadOnlyList<WrittenCollection> Items { get; } = items;
}

public sealed record WrittenCollection(string Collection, IVectorStore Store);
