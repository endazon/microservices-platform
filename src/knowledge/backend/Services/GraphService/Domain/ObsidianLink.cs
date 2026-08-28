namespace GraphService.Domain;

// FR-17, ADR-0033 決定 8 (#912): 正規化 Markdown 本文から抽出した 1 本のリンク。
//
// **構文の別を捨てない。** ADR-0033 決定 8 の理由が「すべて related に丸めると、**書き手が既に
// 表明していた意味を捨てることになる**」であり、抽出した時点で `[[note]]` と `![[note]]` を
// 同じものにしてしまうと、後段（EdgeTypeResolver）が既定型を分けられない。
//
// **本型は既定型を知らない。** 構文 → 型の写像は EdgeTypeResolver が持つ（辞書は実行時に
// 変わるため。ADR-0033 決定 3）。パーサは「何と書いてあったか」だけを返す。
public sealed record ObsidianLink(
    // リンク先の名前（Obsidian のノート名／Markdown リンクの最終セグメント。拡張子は除去済み）。
    string Target,
    // 見出し・ブロック指定（`#` の後ろ）。無ければ null。ADR-0033 決定 5 の `to_anchor` へ入る。
    string? Anchor,
    // フロントマターの明示指定のキー名（`supersedes:` 等）。本文中のリンクでは null。
    string? ExplicitTypeName,
    ObsidianLinkKind Kind);

// ADR-0033 決定 8 の「意味論的に 3 層に分かれる」構文の別。**型そのものではない**
// （型は実行時辞書であり、コード定義にしない —— 決定 3）。
public enum ObsidianLinkKind
{
    // `[[note]]` / `[[note|alias]]` —— 一般的な参照。関係の種類は判別できない。
    Reference,

    // `[[note#見出し]]` / `[[note#^block]]` —— 対象文書の**特定箇所**を指す参照。
    SectionReference,

    // `![[note]]` / `![[note#見出し]]` —— 対象文書の内容を自文書の一部として取り込む。
    Embed,

    // 標準 Markdown リンク `[text](target)` —— 種類を判別できない。
    MarkdownLink,

    // フロントマターでの明示指定（`supersedes: [[note]]` 等）。**書き手の明示が最も強い。**
    Explicit,
}
