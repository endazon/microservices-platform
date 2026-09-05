namespace GraphService.Domain;

// FR-18, ADR-0051 決定 1, IADR-0380 (#1244): 文書 1 件の**語の出現数**（類似度候補の材料）。
//
// **本文そのものは持たない。** 持つのは `TermProfile.Extract` が切った語（CJK 2-gram・小文字化した語）と
// その出現数だけである（上位 128 語）。ABAC 判定には使わず、判定に使う属性は GraphDocument が持つ。
//
// 🔴 **これはスコープを跨いで読まれる行である**（ADR-0051 決定 1 が認めた自システム内の演算）。
// したがって**ここに表題・本文・スニペットを足してはならない** —— 足すと、類似度の計算経路を通って
// スコープ外の本文が呼び出し側のメモリ・ログへ載る（SimilarityCandidate が本文を運ばないのと同じ理由）。
//
// 作成契機は却下解除・リンク抽出と同じ「本文指紋の変化」（GraphDocumentSyncConsumer）。
// 行が無い文書は供給元が表題から作る（縮退）ので、既存文書の backfill は要らない。
public class GraphDocumentTermProfile
{
    public Guid DocumentId { get; private set; }

    // 語 → 出現数（表題の語は TermProfile.TitleWeight 倍）。jsonb。
    public Dictionary<string, int> Terms { get; private set; } = [];

    // 作成時点の本文指紋。GraphDocument.BodyHash と同じ値であれば本文入り、null は表題だけ（または指紋不明）。
    public string? BodyHash { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private GraphDocumentTermProfile() { }

    public static GraphDocumentTermProfile Create(
        Guid documentId, IReadOnlyDictionary<string, int> terms, string? bodyHash, DateTimeOffset at)
        => new()
        {
            DocumentId = documentId,
            Terms = new Dictionary<string, int>(terms, StringComparer.Ordinal),
            BodyHash = bodyHash,
            UpdatedAt = at,
        };

    // 出現数を丸ごと置き換える（差分更新はしない —— 語の集合は本文ごとに決まる）。
    public void Replace(IReadOnlyDictionary<string, int> terms, string? bodyHash, DateTimeOffset at)
    {
        Terms = new Dictionary<string, int>(terms, StringComparer.Ordinal);
        BodyHash = bodyHash;
        UpdatedAt = at;
    }
}
