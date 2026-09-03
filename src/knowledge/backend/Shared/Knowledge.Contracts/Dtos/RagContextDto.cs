namespace Knowledge.Contracts.Dtos;

// FR-21 受け入れ基準 ⑨, FR-19: **検索結果の集合と RAG 回答のコンテキストの集合を別物として扱う。**
//
// 計画（`02_requirements` の FR-21 受け入れ基準）は次を要求する。
//
// > 「横断検索に含める」が ON、「AI の入力に含める」が OFF の個人資料は、
// > **検索結果に現れるが RAG 回答のコンテキストには含まれない**
//
// そして同じ節が「⑧⑨⑩ は、いずれも**実装が素直に作ると満たされない**性質の基準である
// （…**⑨＝検索結果をそのまま LLM へ渡す構造では分離できない**…）」と名指ししている。
//
// 🔴 **本型は「2 つの集合が別物である」という構造だけを固定する。**
// どのチャンクを AI の入力から外すかの**判定条件はここで決めない** —— 判定の主語である
// 3 トグル（横断検索／ナレッジグラフ／AI 入力）は FR-19 が定めるものであり、属性キーの値域も
// そちら側が持つ。ここで仮のキーを決め打つと、FR-19 の実装と語彙が二重になる。
//
// **［2026-08-28 追記 / #447］判定条件は `AiInputExposure`（同ディレクトリ）が持つ**
// （[[IADR-0283]] 決定 1・2）。属性キーは `ai_input`、値は `included` / `excluded` である。
// **本型の構造は変えていない** —— 述語を必須引数で受ける形のまま、その中身が決まっただけである。
public sealed record RagContextSelection(
    // 利用者へ返す検索結果（絞り込まない）。
    IReadOnlyList<SearchResultDto> SearchResults,
    // LLM へ渡してよいチャンクだけの集合。**SearchResults の部分集合である。**
    IReadOnlyList<SearchResultDto> ContextChunks,
    // 検索結果には出したが、AI の入力からは外したチャンク（監査・説明のために残す）。
    IReadOnlyList<Guid> ExcludedFromContextChunkIds);

// FR-21 受け入れ基準 ⑨: 検索結果から RAG コンテキストを**導出する**唯一の点。
//
// **検索結果をそのまま LLM へ渡す経路を作らせない**ことが目的である。呼び出し側は
// `RagContextSelection.ContextChunks` を使い、`SearchResults` をプロンプトへ流さない。
public static class RagContextPolicy
{
    // `isAiInputAllowed` は「この文書属性を持つチャンクを AI の入力に含めてよいか」を返す述語である。
    // **既定を用意しない。** 既定を置くと、判定を渡し忘れた呼び出しが黙って「全件許可」へ倒れ、
    // ⑨ が静かに破れる（計画が名指しした「素直に作ると満たされない」がまさにこの形である）。
    public static RagContextSelection Select(
        IReadOnlyList<SearchResultDto> searchResults,
        Func<IReadOnlyDictionary<string, string>, bool> isAiInputAllowed)
    {
        ArgumentNullException.ThrowIfNull(searchResults);
        ArgumentNullException.ThrowIfNull(isAiInputAllowed);

        var context = new List<SearchResultDto>(searchResults.Count);
        var excluded = new List<Guid>();
        foreach (var result in searchResults)
        {
            // FR-02, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 5:
            // **本文を持たない文書（メタデータだけで索引した点）は文脈に入れない。**
            // 検索結果には出す（ADR-0070 決定 4 の「結果から除外しない」）が、
            // **根拠に使える本文が無い以上、出典にも文脈にもならない** ——
            // 入れると `[n] タイトル` だけの空の根拠が LLM へ渡り、中身を知らない文書について答えることになる。
            if (!result.HasBody || !isAiInputAllowed(result.Attributes)) excluded.Add(result.ChunkId);
            else context.Add(result);
        }

        return new RagContextSelection(searchResults, context, excluded);
    }
}
