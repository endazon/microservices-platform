using System.Text;
using Knowledge.Contracts.Dtos;

namespace GraphService.Domain.Clustering;

// FR-17, FR-18, ADR-0033 決定 2, ADR-0035 決定 3・8, [[IADR-0430]] 決定 1 (#1395):
// 要約の入力に載せる 1 文書。**表題と ID、そして選別に要る属性だけ**である。
//
// **本文を運ぶ欄をここに足さないこと** —— GraphService は本文を持たない（ADR-0033 決定 2）。
// 足すなら供給元（DocumentService）の取得経路ごと設計し直す必要があり、そのときも封を通す。
public sealed record ClusterMemberDocument(
    Guid DocumentId, string Title, IReadOnlyDictionary<string, string> Attributes);

// FR-17, FR-18, ADR-0035 決定 3・5・8, ADR-0051 決定 2, [[IADR-0430]] 決定 1・2 (#1395):
// **クラスタ要約の LLM へ送ってよいものの封（型ゲート）。**
//
// `SuggestionPrompt`（AI 提案の封。[[IADR-0266]] 決定 1）と同じ作法である ——
//
//   - コンストラクタは private であり、構築経路は `Seal` ただ 1 つである
//   - `IClusterSummaryLlmClient` は本型しか受け取らない
//   - 送信本文の組み立て（`Render`）も封が持つ（呼び出し側へ出すと封を通らない文字列を送る経路が開く）
//
// **結果として、個人資料や区分を超える文書を持つ値が LLM 呼び出しの引数として存在し得ない。**
//
// 🔴 **提案の封を再利用しない。** あちらは起点文書・候補・辺の型・タグ辞書を運び、
// **`AccessScopeResponse`（利用者スコープ）を要求する**。クラスタ要約は利用者に紐づかない
// （ADR-0035 決定 3・7「要約側は利用者を知らない」）ため、同じ封には収まらない。
// **本封が守る不変条件は「1 生成 = 1 機密区分」である**（[[IADR-0430]] 決定 2）。
public sealed class ClusterSummaryPrompt
{
    // 要約の対象クラスタ（ADR-0083 決定 1 の「Leiden 法による検出結果」1 個）。
    public Guid ClusterId { get; }

    // ADR-0035 決定 3: 作り分けの軸。**この区分を超える文書は 1 件も入っていない。**
    // ゲートウェイはこの値で送信先ティアを決める（FR-11 の越境判定）。
    public string Confidentiality { get; }

    // 🔴 **区分内の構成員だけが入る。** 件数も区分外については何も表さない。
    public IReadOnlyList<ClusterMemberDocument> Members { get; }

    private ClusterSummaryPrompt(
        Guid clusterId, string confidentiality, IReadOnlyList<ClusterMemberDocument> members)
    {
        ClusterId = clusterId;
        Confidentiality = confidentiality;
        Members = members;
    }

    // **唯一の構築経路。** 落とすものが 2 つある。
    //
    //   1. 🔴 **個人資料**（ADR-0035 決定 8「共有グラフは個人資料を含まない 1 つとする
    //      （決定 3 のクラスタリング入力除外と同じ）」）。検出の時点で既に除かれているため
    //      ここは**多層防御**である —— 迂回経路が生まれても出口で必ず濾される
    //      （`SuggestionPrompt.Seal` が述語を再適用するのと同じ理由）。
    //      判定は `GraphDocumentScope.IsPrivateNote`（**集合帰属**。否定形で書かない）。
    //   2. **区分を超える文書**（ADR-0035 決定 6「機密区分ごとに入力文書集合が異なる」）。
    //      順位は `ConfidentialityLevels.Rank` が単一情報源であり、未知・欠落は安全側
    //      （`restricted`）へ倒れる —— 属性を持たない文書が `public` の要約へ紛れ込まない。
    //
    // 1 件も残らなければ null（送るものが無い）。
    public static ClusterSummaryPrompt? Seal(
        Guid clusterId,
        string confidentiality,
        IReadOnlyList<ClusterMemberDocument> members)
    {
        var tier = ConfidentialityLevels.Normalize(confidentiality);
        var tierRank = ConfidentialityLevels.Rank(tier);

        var visible = new List<ClusterMemberDocument>();
        foreach (var member in members)
        {
            if (GraphDocumentScope.IsPrivateNote(member.Attributes))
                continue;
            if (ConfidentialityLevels.Rank(
                    ConfidentialityLevels.FromAttributes(member.Attributes)) > tierRank)
                continue;
            visible.Add(member);
        }

        if (visible.Count == 0)
            return null;

        // 走査順は文書 ID の昇順で固定する（[[IADR-0425]] 決定 2 と同じ向き ——
        // 同じ入力から同じ本文が出るようにし、無意味な作り直しを生まない）。
        visible.Sort((a, b) => a.DocumentId.CompareTo(b.DocumentId));
        return new ClusterSummaryPrompt(clusterId, tier, visible);
    }

    // **実際に送信する本文。**
    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("あなたはナレッジベースの文書群（1 つのクラスタ）の要約を書く。");
        sb.AppendLine("要約は横断的な問いへ答えるための材料であり、**書かれていないことを補わない。**");
        sb.AppendLine();
        sb.AppendLine("## 前提");
        sb.AppendLine($"- 機密区分: {Confidentiality}");
        sb.AppendLine("- 与えられるのは**表題のみ**である（本文は渡さない）。");
        sb.AppendLine();
        sb.AppendLine("## 文書一覧");
        foreach (var m in Members)
            sb.AppendLine($"- id: {m.DocumentId} / 表題: {m.Title}");
        sb.AppendLine();
        sb.AppendLine("## 出力");
        sb.AppendLine("この文書群が何についての集まりかを、日本語の散文で簡潔に述べる。");
        sb.AppendLine("見出し・箇条書き・前置き・後書きを付けない。**要約の本文だけ**を返す。");
        return sb.ToString();
    }
}
