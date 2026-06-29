using System.Text.Json.Serialization;

namespace KnowledgePlatform.Shared.Contracts.Dtos;

// FR-07, UC-02: 指定データ範囲での分析・比較・抽出を依頼するための DTO 群。
// 利用者は「データ範囲（range）」で分析対象を絞り込み、タスク種別で AI の作業を指定する。

// FR-07: AI に依頼するタスクの種別。プロンプトの組み立てを切り替える。
// JSON では文字列（"Analyze"/"Compare"/"Extract"）で表現する（API の可読性のため）。
[JsonConverter(typeof(JsonStringEnumConverter<AnalysisTaskType>))]
public enum AnalysisTaskType
{
    // 分析: 指定範囲の文書を読み解き、指示に沿って要点・傾向・洞察をまとめる。
    Analyze,
    // 比較: 指定範囲の文書間の共通点・相違点を対比する。
    Compare,
    // 抽出: 指定範囲の文書から、指示された情報（項目・数値・条件等）を抜き出す。
    Extract
}

// FR-07: 分析対象の「指定データ範囲」。
//   Query           — 範囲内での関連箇所を絞る検索クエリ（省略時は Instruction を流用）。
//   AttributeFilters — 属性キー → 許可値集合（例: department ∈ {sales}, year ∈ {2025}）。
//                      retrieval の属性フィルタ（FR-03/FR-05）と同じ意味論で評価される。
//   TopK            — 文脈として取り込む最大チャンク数。
// 重要: このデータ範囲は ABAC 許可スコープ（FR-05）と AND で交差し、権限を一切広げない（narrowing-only）。
public record AnalysisDataRange(
    string? Query = null,
    Dictionary<string, List<string>>? AttributeFilters = null,
    int TopK = 8);

// FR-07, UC-02: 分析・比較・抽出の依頼。
public record AnalysisTaskRequest(
    string Instruction,
    AnalysisTaskType TaskType = AnalysisTaskType.Analyze,
    AnalysisDataRange? Range = null);
