using System.Runtime.CompilerServices;

namespace LlmGateway.Api.Foundation.Ports;

// ADR-0010: LLM 抽象化レイヤー — P1以降で実装を差し替え可能にする。
// 埋め込みは別系統（IEmbeddingProvider / ADR-0013・ADR-0016）へ分離した。
public interface ILlmProvider
{
    Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default);

    // IADR-0037: トークンストリーミング。既定実装は CompleteAsync を 1 チャンクで返す（未対応プロバイダは
    // 単一チャンクへ縮退＝壊れない）。真のストリーミングを行うプロバイダ（ClaudeProvider）が override する。
    // 呼び出し側（LlmGateway /complete/stream）は egress ルーティング判定を通した後にのみ本メソッドを呼ぶ。
    async IAsyncEnumerable<CompletionChunk> StreamAsync(
        CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await CompleteAsync(request, ct);
        yield return new CompletionChunk(
            result.Text, Done: true, result.InputTokens, result.OutputTokens, result.StopReason);
    }
}

// IADR-0101: MaxTokens の既定は 4096（HTTP 経路の既定は CompletionApiRequest 側。エンドポイントが
// req.MaxTokens を常に明示的に渡すため、本既定値はプロバイダを直接呼ぶ内部経路にのみ効く）。
// thinking（拡張思考）が既定で有効なモデル（Opus 5・Sonnet 5。現行の既定 claude-opus-5 が該当）では
// MaxTokens は思考トークンと本文の合算上限になるため、本文想定長（〜1024）＋思考の作業領域（〜3000）
// を見込む。1024 のままだと思考が上限を食い、本文が途中で切れる（例外にならず静かに縮退する）。
public record CompletionRequest(string Prompt, int MaxTokens = 4096, string? Model = null);

// IADR-0104: StopReason は応答の終了理由（CompletionStopReasons）。未対応プロバイダは null。
// 位置引数の末尾に既定値つきで足しているため、既存の 3 引数呼び出しは不変である。
public record CompletionResult(string Text, int InputTokens, int OutputTokens, string? StopReason = null);

// IADR-0037: ストリーミングの 1 チャンク。TextDelta は増分本文。Done=true は最終チャンク（トークン数を伴う）。
// IADR-0104: StopReason は最終チャンクにのみ載る（message_delta で確定するため）。
public record CompletionChunk(
    string TextDelta, bool Done = false, int InputTokens = 0, int OutputTokens = 0, string? StopReason = null);
