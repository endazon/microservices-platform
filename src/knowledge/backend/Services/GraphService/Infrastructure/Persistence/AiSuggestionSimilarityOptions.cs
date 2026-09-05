namespace GraphService.Infrastructure.Persistence;

// FR-18, IADR-0380 (#1244): 類似度候補の供給元の構成（`AiSuggestions:Similarity`）。
//
//   Source   … `term-overlap`（既定。語の共起。TermOverlapSimilarityCandidateSource）
//              `none`（供給元を切る。UnconfiguredSimilarityCandidateSource。提案は 0 件になる）
//   MinScore … 候補に採る最小スコア（コサイン。0〜1）。未満は「無関係」として落とす
//
// 🔴 **未知の Source は起動を落とす**（Program.cs）。黙って `none` へ倒すと「構成の綴り間違いで提案が
// 静かに 0 件になる」—— 本 issue（#1244）が摘出した「テストも CI も緑のまま壊れている」型の再演である。
public sealed class AiSuggestionSimilarityOptions
{
    public const string SectionName = "AiSuggestions:Similarity";

    public const string TermOverlap = "term-overlap";
    public const string None = "none";

    public string Source { get; set; } = TermOverlap;

    // 既定 0.1。IDF 重み付きコサインで、全文書に共通する定型句しか共有しない文書はこの値を下回る
    // （TermProfileTests の T-43 が固定する）。実データでの調整は構成で行う。
    public double MinScore { get; set; } = 0.1;
}
