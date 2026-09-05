namespace GraphService.Features.AiSuggestions.List;

// FR-18, SC-21, ADR-0068 決定 2 / IADR-0395: AI 提案の一覧の**検証対象**を表す要求モデル。
//
// `AbstractValidator<T>` は型に対して規則を宣言するので、クエリ引数を検証するには器が要る。
// **この 1 操作でしか使わない**ので 3 段目（`Features/AiSuggestions/List/`）に置く。
//
// 🔴 **端点の引数一覧の複製ではない。** `documentId` は検証しない（不存在・権限外の ID でも
// 404 に倒さないのが仕様である。[[IADR-0323]] 決定 2）ので載せない —— 載せると
// 「検証されているように見えるが規則が無い」欄ができる。
internal sealed record ListAiSuggestionsQuery(string? State, string? Kind);
