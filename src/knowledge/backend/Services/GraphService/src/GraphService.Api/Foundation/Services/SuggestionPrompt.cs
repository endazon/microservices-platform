using System.Text;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Foundation.Services;

// FR-18: LLM へ渡す 1 文書の表現。**表題と ID のみ。**
//
// GraphService は本文を持たない（ABAC 判定に要る属性の複製と表題だけ。ADR-0033 決定 2）。
// **本文を運ぶ欄をここに足さないこと** —— 足すなら供給元（DocumentService）の取得経路ごと
// 設計し直す必要があり、そのときも本型の封（Seal）を通す（IADR-0266 §結果）。
public sealed record PromptDocument(Guid DocumentId, string Title);

// FR-18, ADR-0034 決定 5, ADR-0051 決定 2・3, IADR-0266 決定 1:
// **LLM へ送ってよいものの封（型ゲート）。**
//
// ADR-0051 決定 3 は順序を「全文書横断で類似度 → **スコープで絞る** → LLM へ渡す」と定め、
// **絞りを LLM 呼び出しより後ろに置くことを禁じた。** 散文の規約では守られないため型で表す。
//
//   - コンストラクタは private であり、構築経路は Seal ただ 1 つである
//   - Seal は **AuthorizedNode**（＝ ABAC 述語を通った文書。IADR-0242 決定 2）と
//     **AccessScopeResponse** の両方を要求する
//   - ISuggestionLlmClient は本型しか受け取らない
//
// **結果として、非許可ノードを持つ値が LLM 呼び出しの引数として存在し得ない。**
//
// 🔴 **Seal は述語を再適用する**（多層防御）。入口のゲート（候補列挙）を通っていれば恒等だが、
// 迂回経路が生まれても**出口で必ず濾される**。AuthorizedGraphView.Seal と同じ理由である。
//
// 構築経路の単一性は SuggestionPromptGateTests がリフレクションで固定する。
public sealed class SuggestionPrompt
{
    public Guid OriginDocumentId { get; }
    public string OriginTitle { get; }

    // **スコープ内の候補だけが入る。** 件数もスコープ外については何も表さない（ADR-0051 決定 2）。
    public IReadOnlyList<PromptDocument> Candidates { get; }

    // ADR-0033 決定 3: 辺の型は実行時辞書である。**LLM に選ばせる値集合をここで渡す。**
    public IReadOnlyList<string> EdgeTypeNames { get; }

    // FR-11, IADR-0266 決定 7: 封に入っている文書の**最高機密区分**。
    // ゲートウェイはこれで送信先ティアを決める（越境判定は「文脈に含む文書のうち最も高い区分」）。
    // 語彙と順位は Knowledge.Contracts の ConfidentialityLevels が単一情報源であり、ここで再定義しない。
    public string Confidentiality { get; }

    // 🔴 **private である。** ここを緩めると型ゲートが無効になる
    // （SuggestionPromptGateTests が公開コンストラクタの不在を assert する）。
    private SuggestionPrompt(
        Guid originDocumentId, string originTitle,
        IReadOnlyList<PromptDocument> candidates,
        IReadOnlyList<string> edgeTypeNames,
        string confidentiality)
    {
        OriginDocumentId = originDocumentId;
        OriginTitle = originTitle;
        Candidates = candidates;
        EdgeTypeNames = edgeTypeNames;
        Confidentiality = confidentiality;
    }

    // FR-18, ADR-0034 決定 5: **唯一の構築経路。** スコープを渡さずに封は作れない。
    //
    // 起点が許可されなければ null を返す（送るものが無い）。候補のうち許可されないものは黙って落ちる
    // —— **件数も返さない**（ADR-0051 決定 2「件数・存在も出さない」）。
    public static SuggestionPrompt? Seal(
        AuthorizedNode origin,
        IReadOnlyList<AuthorizedNode> candidates,
        IReadOnlyList<string> edgeTypeNames,
        AccessScopeResponse scope)
    {
        // FR-05: deny-by-default。許可ポリシーが無ければ何も送らない。
        if (!scope.Granted)
            return null;

        // 多層防御: 入口のゲートを通っていれば恒等だが、迂回経路があってもここで濾す。
        if (AuthorizedNode.Authorize(origin.Node, scope) is null)
            return null;

        var visible = new List<PromptDocument>();
        var levels = new List<string> { ConfidentialityLevels.FromAttributes(origin.Node.Attributes) };
        foreach (var candidate in candidates)
        {
            if (AuthorizedNode.Authorize(candidate.Node, scope) is null)
                continue;
            visible.Add(new PromptDocument(candidate.DocumentId, candidate.Node.Title));
            levels.Add(ConfidentialityLevels.FromAttributes(candidate.Node.Attributes));
        }

        var highest = levels
            .OrderByDescending(ConfidentialityLevels.Rank)
            .First();

        return new SuggestionPrompt(
            origin.DocumentId, origin.Node.Title, visible, edgeTypeNames, highest);
    }

    // FR-18: **実際に送信する本文。** 封の外で本文を組み立てられないよう、ここで作る
    // （組み立てを呼び出し側へ出すと、封を通っていない文字列を送る経路が開く）。
    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("あなたはナレッジベースの文書間の関連（リンク候補）とタグ候補を提案する。");
        sb.AppendLine("提案は人間の承認を経て確定する。**確実でないものは提案しないこと。**");
        sb.AppendLine();
        sb.AppendLine("## 起点文書");
        sb.AppendLine($"- id: {OriginDocumentId}");
        sb.AppendLine($"- 表題: {OriginTitle}");
        sb.AppendLine();
        sb.AppendLine("## 候補文書");
        sb.AppendLine("**この一覧に無い文書を提案してはならない。** 一覧が空ならリンク候補を提案しない。");
        foreach (var c in Candidates)
            sb.AppendLine($"- id: {c.DocumentId} / 表題: {c.Title}");
        sb.AppendLine();
        sb.AppendLine("## 辺の型");
        sb.AppendLine("次の値集合から選ぶ。該当が無ければ related を使う。");
        sb.AppendLine($"- {string.Join(", ", EdgeTypeNames)}");
        sb.AppendLine();
        sb.AppendLine("## 出力");
        sb.AppendLine("JSON 配列のみを返す。説明文を付けない。要素の形は次のいずれか。");
        sb.AppendLine("""{"kind":"link","targetDocumentId":"<候補文書の id>","edgeTypeName":"<辺の型>","rationale":"<根拠>"}""");
        sb.AppendLine("""{"kind":"tag","tagValue":"<タグ値>","rationale":"<根拠>"}""");
        return sb.ToString();
    }
}
