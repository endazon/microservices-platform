using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Domain.Ports;

// FR-04, FR-11, NFR-02, ADR-0010, ADR-0029, ADR-0075, IADR-0037, IADR-0379, IADR-0398 (#1255):
// LlmGateway のテキスト生成を呼ぶ**輸送のポート**。REST（HttpLlmCompletionTransport）と
// gRPC（GrpcLlmCompletionTransport）の 2 実装があり、Program.cs が `Services:LlmGatewayGrpc` の
// 有無で選ぶ。**並走中の正は REST**（IADR-0379 決定 5）。
//
// 🔴 **ポートが返すのは「ゲートウェイが何と答えたか」だけである。**
// 合成監視の抑止（SuppressLlmForSynthetic）・出典の組み立て・機密区分の算出・回答文の選択は
// RagOrchestrator に残る —— それらは輸送では変わらない業務判断であり、2 実装へ写すと片方だけが
// 直る事故の口になる。輸送が持つのは**縮退の写し**（下記）だけである。
//
// 🔴 **縮退の写しは輸送ごとに書くが、落とす先は同じ枝でなければならない**（IADR-0398 決定 5）。
public interface ILlmCompletionTransport
{
    /// <summary>
    /// 逐次生成（REST は SSE `POST /complete/stream`、gRPC は <c>LlmCompletion/CompleteStream</c>）。
    /// <para>
    /// 🔴 <b>到着した順に 1 件ずつ返す。まとめてから返してはならない。</b>
    /// 呼び出し側は最初の非空 <c>Delta</c> で north-south の最初の <c>token</c> を書き、そこが
    /// NFR-02 の SLI（<c>rag.answer.first_token.duration</c>。IADR-0354）の終点である。
    /// </para>
    /// <para>
    /// 送信・確立の失敗は例外にせず <c>done(Sent=false, Text="LLM が現在利用できません。")</c> を、
    /// 受信途中の失敗は <c>done(Sent=false, Text="LLM 応答の受信に失敗しました。")</c> を
    /// <b>最後のイベントとして返して正常終了する</b>（現行 REST 実装と同じ枝）。
    /// </para>
    /// </summary>
    IAsyncEnumerable<CompletionStreamEvent> StreamAsync(
        CompletionApiRequest request, bool isSynthetic, CancellationToken ct);

    /// <summary>
    /// 一括生成（REST は `POST /complete`、gRPC は <c>LlmCompletion/Complete</c>）。
    /// 結果の読み方は <see cref="LlmCompletionOutcome"/> を参照。
    /// </summary>
    Task<LlmCompletionOutcome> CompleteAsync(
        CompletionApiRequest request, bool isSynthetic, CancellationToken ct);
}

/// <summary>
/// 一括生成の結果。🔴 <b>3 値である</b> —— 現行 REST 実装が持つ 3 つの枝を潰さないための型である。
/// <list type="bullet">
/// <item><c>Reached=false</c> … ゲートウェイの答えを得られなかった（REST の非 2xx／gRPC の
/// <c>RpcException</c>・s2s トークン取得失敗）。呼び出し側は「LLM が現在利用できないため、
/// 関連文書の一覧を返します。」へ倒す。</item>
/// <item><c>Reached=true, Response=null</c> … 応答は得たが本文を復元できなかった
/// （REST で 2xx かつ JSON が null）。呼び出し側は「回答を生成できませんでした。」へ倒す。
/// 🔴 <b>gRPC ではこの状態は起こり得ない</b>（proto のメッセージは欠落しない）。
/// それでも型から消さないのは、<b>REST 実装がこの枝を現に持っている</b>からである ——
/// 消すと REST 側の挙動が変わる。</item>
/// <item><c>Reached=true, Response</c> … 通常。<c>Sent=false</c>（越境拒否）と
/// <c>StopReason="refusal"</c> の読み分けは呼び出し側が行う（IADR-0104）。</item>
/// </list>
/// </summary>
public readonly record struct LlmCompletionOutcome(bool Reached, CompletionApiResponse? Response)
{
    /// <summary>ゲートウェイの答えを得られなかった（到達不能・非 2xx・RpcException）。</summary>
    public static LlmCompletionOutcome NotReached() => new(false, null);

    /// <summary>ゲートウェイが答えた（本文の復元に失敗した場合は <paramref name="response"/> が null）。</summary>
    public static LlmCompletionOutcome Answered(CompletionApiResponse? response) => new(true, response);
}
