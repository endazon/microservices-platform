namespace Platform.Shared.Contracts.Dtos;

// IADR-0104 (#379), ADR-0025: Anthropic 応答の終了理由（stop_reason）の語彙と判定。
// refusal（安全性分類器による拒否）は HTTP 200・例外なしで到着するため、判別しないと本文が空文字へ
// 静かに縮退し「送信したが空応答」と区別できなくなる。
// enum ではなく文字列で扱うのは、語彙が増えたときに未知の値が既定値へ黙って落ちるのを避けるため
// （未知の理由はそのまま透過し、ログと応答契約に残す）。
// ゲートウェイ（LlmGateway）と呼び出し側（AiAnalysisService 等）で二重定義しないよう共有契約に置く。
public static class CompletionStopReasons
{
    public const string EndTurn = "end_turn";
    public const string MaxTokens = "max_tokens";
    public const string Refusal = "refusal";
    public const string StopSequence = "stop_sequence";
    public const string ToolUse = "tool_use";

    // モデルが拒否した（安全性分類器による停止）。呼び出し側は本文を根拠に使ってはならない。
    public static bool IsRefusal(string? stopReason) =>
        string.Equals(stopReason, Refusal, StringComparison.OrdinalIgnoreCase);

    // 出力上限に到達した。thinking が既定で有効なモデルでは本文が空または途中で切れる（IADR-0101）。
    public static bool IsMaxTokens(string? stopReason) =>
        string.Equals(stopReason, MaxTokens, StringComparison.OrdinalIgnoreCase);
}

// FR-11, NFR-19, IADR-0104 (#1819): `Sent=false` の原因の種類（応答の `FailureKind`）。
// ゲートウェイの 3 つの縮退経路を**文言（Text）ではなく値で**区別できるようにする —— 呼び出し側
// （AST の取引判断等）が「越境拒否か・構成不備か・上流の不調か」を記録し分けられないと、
// 送信不可が連続したときに原因を事後に確定できない（#1819 の PoC で 132 件を追えなかった）。
// 値は計器 `llm.completion.total` の `llm.result` と**同じ文字列**である（IADR-0110。計器の定数はここを引く）。
// enum にしないのは CompletionStopReasons と同じ理由（語彙が増えたとき古い呼び出し側が既定値へ黙って落ちない）。
// 未知の値を受けた呼び出し側は「原因不明の Sent=false」として扱えばよい。`Sent=true` では null。
public static class CompletionFailureKinds
{
    // 機密区分×ティアの越境マトリクス・ZDR 要件により送信しなかった（設定どおりの拒否。再試行しても変わらない）。
    public const string EgressDenied = "egress_denied";

    // ルーティングが選んだプロバイダがゲートウェイに登録されていない（構成不備）。
    public const string ProviderMissing = "provider_missing";

    // 呼び出し先が例外を返した（HTTP 4xx/5xx・輸送の失敗）。HTTP 状態が取れれば UpstreamStatusCode に載る。
    public const string UpstreamError = "upstream_error";
}

// FR-04, FR-11, ADR-0010: LLM ゲートウェイ /complete の要求・応答契約。
// LlmGateway（実装側）と AiAnalysisService（呼び出し側）で二重管理せず、
// 契約変更時の追従漏れ（ドリフト）を防ぐため共有コントラクトに一元化する。

// FR-11: confidentiality（入力文書の最高機密区分）・purpose（用途）で呼び出し先を切り替える。
//   Model は任意の明示要求モデル。null の場合はゲートウェイが用途（purpose）に応じて選択する。
// IADR-0101: MaxTokens の既定は 4096。Opus 5 / Sonnet 5 のように thinking（拡張思考）が既定で
// 有効なモデルでは MaxTokens は思考トークンと本文の合算上限になるため、本文想定長（〜1024）＋
// 思考の作業領域（〜3000）を見込む。1024 のままだと思考が上限を食い、本文が空または途中で切れる
// （例外にならず静かに縮退する）。エンドポイントは req.MaxTokens を常に明示的にプロバイダへ渡すため、
// max_tokens を省略したクライアントに効く既定値は ILlmProvider 側ではなく本 DTO のこの値である。
public record CompletionApiRequest(
    string Prompt,
    int MaxTokens = 4096,
    string? Model = null,
    string? Confidentiality = null,
    string? Purpose = null);

// FR-11: Sent=false は機密区分による送信拒否（縮退）を示す。
//   Endpoint / RoutingReason は選択・拒否した呼び出し先と理由（監査・縮退表示用）。
// IADR-0104 (#379): StopReason は**送信が成立した**場合のモデル側の終了理由
//   （"end_turn" / "max_tokens" / "refusal" 等。ADR-0025）。Sent とは独立した軸である。
//   Sent=false（越境させていない）と StopReason="refusal"（送信したがモデルが拒否した）は別事象であり、
//   拒否でも Sent=true を保つ（越境監査・課金集計の意味を壊さないため）。
//   拒否時 Text は空になる（ゲートウェイが断片を破棄する）ため、本フィールドを見ない呼び出し側も
//   従来どおり安全側へ倒れる。未送信・未対応プロバイダでは null。
// IADR-0104 追記 (#1819): FailureKind は Sent=false の原因の種類（CompletionFailureKinds）。
//   UpstreamStatusCode は FailureKind="upstream_error" で上流が HTTP 状態を返したときだけ載る（輸送の失敗は null）。
//   いずれも Sent=true では null。Text / RoutingReason の意味は変えない（末尾追加＝旧い呼び出し側は無視できる）。
public record CompletionApiResponse(
    string Text,
    string Model,
    int InputTokens,
    int OutputTokens,
    bool Sent = true,
    string? Endpoint = null,
    string? RoutingReason = null,
    string? StopReason = null,
    string? FailureKind = null,
    int? UpstreamStatusCode = null);

// IADR-0037: /complete/stream（SSE）の 1 イベント（data: 行の JSON）。gateway ↔ AiAnalysisService の内部契約。
//   Delta        — 本文の増分（Done=false のとき）。
//   Done         — 最終イベント。Model/InputTokens/OutputTokens が確定する。
//   Sent         — false は egress 拒否・呼び出し失敗の縮退（Text に理由）。プロバイダ未呼出も含む。
//   Text         — 縮退時の理由（Sent=false のとき）。
//   StopReason   — IADR-0104: モデル側の終了理由（最終イベントにのみ載る）。"refusal" は送出済みの
//                  Delta を破棄すべきことを意味する（ストリームは撤回できないため呼び出し側の責務）。
//   FailureKind / UpstreamStatusCode — #1819: Sent=false の最終イベントにだけ載る原因の種類と上流の HTTP 状態
//                  （CompletionApiResponse と同じ意味）。呼び出し側が輸送の失敗で合成する done(Sent=false) では null。
public record CompletionStreamEvent(
    string Delta,
    bool Done = false,
    bool Sent = true,
    string? Text = null,
    string Model = "",
    int InputTokens = 0,
    int OutputTokens = 0,
    string? RoutingReason = null,
    string? StopReason = null,
    string? FailureKind = null,
    int? UpstreamStatusCode = null);
