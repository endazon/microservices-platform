using GraphService.Api.Foundation.Services;

namespace GraphService.Api.Foundation.Ports;

// FR-18, ADR-0034 決定 5, ADR-0051 決定 3, IADR-0266 決定 1: AI 提案の LLM 境界ポート（#915）。
//
// 🔴 **引数は SuggestionPrompt のみである。これが越境禁止の型による強制である。**
// SuggestionPrompt は private コンストラクタを持ち、構築経路は
// `SuggestionPrompt.Seal(AuthorizedNode 起点, IReadOnlyList<AuthorizedNode> 候補, …, AccessScopeResponse)`
// ただ 1 つである。**したがって「生の GraphDocument を LLM へ渡す」コードがコンパイルできない。**
//
// ADR-0034 決定 5 は「**送信そのものが違反であり、後段のフィルタでは償えない**」と定めている。
// 違反に後から気付いても直せない種類の規律であるため、規約ではなく型で表す。
// **署名を GraphDocument や string へ緩めてはならない**（IGraphStore.LoadIncidentEdgesAsync と同じ作法）。
public interface ISuggestionLlmClient
{
    Task<IReadOnlyList<LlmSuggestionProposal>> ProposeAsync(
        SuggestionPrompt prompt, CancellationToken ct = default);
}

// FR-18: LLM が返した提案 1 件（取り込み前）。
//
// 🔴 **これは「提案」ではなく「提案の候補」である。** IADR-0266 決定 5 により、
// 呼び出し側は **その実行の許可済み候補集合と突き合わせてから**取り込む
// —— 渡していない ID を LLM が返しても実体化させないためである。
public sealed record LlmSuggestionProposal(
    string Kind,
    Guid? TargetDocumentId,
    string? EdgeTypeName,
    string? TagValue,
    string Rationale);
