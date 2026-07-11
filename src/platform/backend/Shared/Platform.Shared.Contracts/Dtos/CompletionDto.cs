namespace Platform.Shared.Contracts.Dtos;

// FR-04, FR-11, ADR-0010: LLM ゲートウェイ /complete の要求・応答契約。
// LlmGateway（実装側）と AiAnalysisService（呼び出し側）で二重管理せず、
// 契約変更時の追従漏れ（ドリフト）を防ぐため共有コントラクトに一元化する。

// FR-11: confidentiality（入力文書の最高機密区分）・purpose（用途）で呼び出し先を切り替える。
//   Model は任意の明示要求モデル。null の場合はゲートウェイが用途（purpose）に応じて選択する。
public record CompletionApiRequest(
    string Prompt,
    int MaxTokens = 1024,
    string? Model = null,
    string? Confidentiality = null,
    string? Purpose = null);

// FR-11: Sent=false は機密区分による送信拒否（縮退）を示す。
//   Endpoint / RoutingReason は選択・拒否した呼び出し先と理由（監査・縮退表示用）。
public record CompletionApiResponse(
    string Text,
    string Model,
    int InputTokens,
    int OutputTokens,
    bool Sent = true,
    string? Endpoint = null,
    string? RoutingReason = null);

// IADR-0037: /complete/stream（SSE）の 1 イベント（data: 行の JSON）。gateway ↔ AiAnalysisService の内部契約。
//   Delta        — 本文の増分（Done=false のとき）。
//   Done         — 最終イベント。Model/InputTokens/OutputTokens が確定する。
//   Sent         — false は egress 拒否・呼び出し失敗の縮退（Text に理由）。プロバイダ未呼出も含む。
//   Text         — 縮退時の理由（Sent=false のとき）。
public record CompletionStreamEvent(
    string Delta,
    bool Done = false,
    bool Sent = true,
    string? Text = null,
    string Model = "",
    int InputTokens = 0,
    int OutputTokens = 0,
    string? RoutingReason = null);
