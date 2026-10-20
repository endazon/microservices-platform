using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Infrastructure.ExternalServices;

// ADR-0010: Claude SDK デフォルト実装。既定モデルは claude-opus-5-5（ADR-0025 追従・IADR-0101。
// 既定 Opus 経路そのものの決定は IADR-0022）。
// 定型用途は claude-sonnet-5-5（ADR-0022 追従・IADR-0106）/ claude-haiku-5-5 を
// ルーター（用途別）で選択する。
// ［2026-08-18 追記 / #850］計画 ADR-0038（Accepted）決定 1・2 により、最難関用途 analysis の割当は
// claude-fable-5 → claude-opus-5 へ改定し、claude-fable-5 は claude-managed の Models からも外した
// （基盤のいかなる用途でも用いない。ZDR 有効化の優先）。旧割当の根拠は IADR-0022 を参照。
// ［2026-10-10 追記 / #1875］利用者裁定（planning#783）で全割当を 5.5 系へ切り替えた
// （opus-5 → opus-5-5・sonnet-5 → sonnet-5-5・haiku-4-5 → haiku-5-5。IADR-0531）。fable は引き続き用いない。
// ⚠️ 5.5 系は 3 モデルとも thinking（adaptive）が既定で有効で**無効にできない**。MaxTokens は思考トークンと本文の
// 合算上限になる。切り詰めると本文が途中で切れるため、既定値は思考分の余裕を含める（IADR-0101）。
// 🔴 本実装は thinking / temperature / top_p / top_k / tool_choice / assistant prefill を送らない
// （5.5 系で 400 になる。要求本文に無いことを ClaudeProviderRequestShapeTests が固定する。IADR-0531 決定 4）。
// 思考の量を絞る手段は effort だけであり、用途別に `Llm:PurposeEffort` で与える（IADR-0531 決定 3）。
// ［2026-10-17 / #1749・IADR-0562］SDK を公式 `Anthropic`（12.53.0）へ移した。effort は `OutputConfig.Effort` で送り、
// 未知の content ブロックは SDK が例外にせず保持する（旧 SDK 向けの要求整形・応答サニタイズの委譲ハンドラは撤去した）。
// 🔴 SDK の例外（AnthropicApiException 等）はこのクラスの外へ出さない（ThrowTranslated。IADR-0562 決定 1）。
public class ClaudeProvider(
    AnthropicClient client,
    IConfiguration config,
    AnthropicContentBlockMetrics contentBlockMetrics,
    ILogger<ClaudeProvider> logger,
    IOptionsMonitor<ClaudePurposeEffortOptions>? effortOptions = null) : ILlmProvider
{
    private readonly string _model = config["Llm:Model"] ?? "claude-opus-5-5";

    // FR-11, #1997, IADR-0565: 逐次の無通信の期限（`Llm:AnthropicStreamIdleTimeoutSeconds`。未設定は一括の期限と同じ値）。
    private readonly TimeSpan _streamIdleTimeout = AnthropicClientFactory.ResolveStreamIdleTimeout(config, logger);

    // 用途とモデルから送る effort を決める（未設定・非対応モデルは null＝送らない）。
    private string? EffortFor(CompletionRequest request, string model)
        => ClaudeEffort.Resolve(effortOptions?.CurrentValue, request.Purpose, model);

    // 要求の組み立て（非ストリーム・ストリーム共通。stream=true は SDK の CreateStreaming が足す）。
    // messages は user の 1 件だけ（assistant prefill を送らない）。effort が無ければ output_config を送らない。
    private MessageCreateParams BuildParams(CompletionRequest request, string model)
    {
        var effort = EffortFor(request, model);
        return new MessageCreateParams
        {
            Model = model,
            MaxTokens = request.MaxTokens,
            Messages = [new MessageParam { Role = Role.User, Content = request.Prompt }],
            OutputConfig = effort is null ? null : new OutputConfig { Effort = effort },
        };
    }

    public async Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        var model = request.Model ?? _model;

        // IADR-0562 決定 4（2026-10-17 PR #1996 のレビューで追加）: 一括は**全文の受信まで**を期限に入れる。
        // SDK の Timeout は 1 試行の応答ヘッダの到着までしか効かない（ResponseHeadersRead）。ヘッダの後に本文の受信が止まると、
        // 呼び出し側が取り消さない限り待ち続ける（旧 SDK の HttpClient.Timeout は本文の受信完了まで効いていた）。
        // そこで呼び出し全体に同じ期限の取り消しを掛け、期限切れは従前と同じ TaskCanceledException（内側 TimeoutException）にする。
        // 呼び出し側の取り消しは包まずにそのまま伝える（ct が立っているかで区別できる）。
        var timeout = client.Timeout ?? TimeSpan.FromSeconds(AnthropicClientFactory.DefaultTimeoutSeconds);
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        Message msg;
        try
        {
            msg = await client.Messages.Create(BuildParams(request, model), linked.Token);
        }
        catch (AnthropicException ex) when (IsTranslated(ex))
        {
            throw Translate(ex);
        }
        catch (OperationCanceledException ex)
            when (deadline.IsCancellationRequested && !ct.IsCancellationRequested && ex.InnerException is not TimeoutException)
        {
            throw new TaskCanceledException(
                $"Claude の呼び出しが期限 {timeout} を過ぎたため取り消した（{AnthropicClientFactory.ConfigKey}）。",
                new TimeoutException(ex.Message, ex));
        }

        // IADR-0104 (#379), ADR-0025: content を読む前に stop_reason を確認する。refusal は HTTP 200・
        // 例外なしで到着するため、判別しないと空文字へ静かに縮退し「送信したが空応答」と区別できない。
        // 未知の値は enum へ丸めず文字列のまま透過する（Raw）。
        var stopReason = msg.StopReason?.Raw();

        // IADR-0114 決定 5・IADR-0535・IADR-0562 決定 5: 本文に使わないブロック（thinking 等）の型名を数え、段階を分けてログに残す。
        var text = ExtractText(msg.Content);

        // 拒否時は本文を返さない。安全性分類器は本文の途中で停止し得るため、断片が非空のまま
        // 下流へ流れると（AST 取引判断は Text の非空を根拠に売買判断へ進む）fail-safe が破れる。
        // max_tokens の途中結果は正当な観測対象であり破棄しない（IADR-0101 / IADR-0104 §決定）。
        if (CompletionStopReasons.IsRefusal(stopReason))
            text = string.Empty;

        return new CompletionResult(
            text ?? string.Empty, (int)msg.Usage.InputTokens, (int)msg.Usage.OutputTokens, stopReason);
    }

    // IADR-0037: Anthropic SDK の SSE ストリーミングで本文デルタを逐次返す（真のストリーミング・主経路）。
    // 呼び出し側（LlmGateway の逐次生成 CompleteStream）は egress ルーティングで送信可と判定した後にのみ本メソッドを呼ぶ。
    public async IAsyncEnumerable<CompletionChunk> StreamAsync(
        CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var model = request.Model ?? _model;
        int inputTokens = 0, outputTokens = 0;
        string? stopReason = null;

        // IADR-0562 決定 4: 逐次には呼び出し全体の期限を掛けない。期限（Timeout）が効くのは応答ヘッダの到着までで、
        // 長い出力が長く流れ続けるのは正常であり、全体に期限を掛けると正当な長い応答を途中で切る。旧 SDK も同じ範囲だった（IADR-0528）。
        // ［2026-10-20 / #1997・IADR-0565］代わりに**イベントの間の無通信**に期限を掛ける。ヘッダの後に上流が止まると、
        // 呼び出し側（RAG の回答）はどれも全体の期限を持たないので、利用者が停止を押すまで待ち続けていた。
        // 次のイベントを待つ間だけ時計を回し、受け取ったら止める（yield の先＝gRPC への書き込みが遅い呼び出し側の時間は数えない）。
        // 期限切れは TimeoutException で上げる（OperationCanceledException ではない）。CompletionUseCase は upstream_error へ縮退し、
        // 計器の上流状態は transport になる。呼び出し側の取り消しはそのまま OperationCanceledException で伝える。
        // SDK の例外は最初の MoveNextAsync（要求の送信）か途中の読み取りで出る。yield を try/catch の中に置けないので、
        // 列挙子を手で回して MoveNextAsync だけを包む（IADR-0562 決定 1。ストリームでも Domain に SDK の型を見せない）。
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var events = client.Messages.CreateStreaming(BuildParams(request, model), idle.Token).GetAsyncEnumerator(idle.Token);
        while (true)
        {
            RawMessageStreamEvent ev;
            idle.CancelAfter(_streamIdleTimeout);
            try
            {
                if (!await events.MoveNextAsync())
                    break;
                ev = events.Current;
            }
            catch (Exception ex) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Claude の逐次応答が無通信の期限 {_streamIdleTimeout} を過ぎたため打ち切った（{AnthropicClientFactory.StreamIdleConfigKey}）。",
                    ex);
            }
            catch (AnthropicException ex) when (IsTranslated(ex))
            {
                throw Translate(ex);
            }

            idle.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);

            // message_start で入力トークン、message_delta で出力トークンが逐次確定する。
            if (ev.TryPickStart(out var start))
                inputTokens = (int)start.Message.Usage.InputTokens;

            // IADR-0104: 終了理由は message_delta（末尾）で確定する。既に送出したデルタは撤回できないため、
            // 最終チャンクへ載せて呼び出し側が破棄判断できるようにする（トレードオフは IADR-0104 §結果）。
            if (ev.TryPickDelta(out var delta))
            {
                if (delta.Usage.OutputTokens > 0)
                    outputTokens = (int)delta.Usage.OutputTokens;
                if (delta.Delta.StopReason?.Raw() is { Length: > 0 } deltaStop)
                    stopReason = deltaStop;
            }

            // 本文デルタ（text_delta）だけを流す。thinking_delta / signature_delta は流さない（T-35）。
            if (ev.TryPickContentBlockDelta(out var blockDelta)
                && blockDelta.Delta.TryPickText(out var textDelta)
                && !string.IsNullOrEmpty(textDelta.Text))
                yield return new CompletionChunk(textDelta.Text);
        }

        // 最終チャンク: 本文増分なし・トークン数と終了理由を伴う（呼び出し側が集計と縮退判断を確定できる）。
        yield return new CompletionChunk(string.Empty, Done: true, inputTokens, outputTokens, stopReason);
    }

    // 本文は先頭の text ブロック（旧 SDK の実装と同じ取り方）。それ以外のブロックは本文に使わずに捨て、その型名を記録する。
    // 公式 SDK は未知の型（判別子に無い型・type の無い要素）も例外にせず ContentBlock(element) として保持する。
    private string? ExtractText(IReadOnlyList<ContentBlock> content)
    {
        string? text = null;
        var textBlocks = 0;
        List<string>? dropped = null;
        foreach (var block in content)
        {
            if (block.TryPickText(out var textBlock))
            {
                textBlocks++;
                text ??= textBlock.Text;
                continue;
            }

            (dropped ??= []).Add(TypeNameOf(block));
        }

        if (dropped is not null)
        {
            contentBlockMetrics.RecordDropped(dropped);
            LogDropped(dropped, textBlocks);
        }

        return text;
    }

    // 型名が取れない要素も記録用の表示名を持たせる（IADR-0114 の表示名をそのまま使う）。
    private static string TypeNameOf(ContentBlock block)
    {
        var json = block.Json;
        return json.ValueKind == JsonValueKind.Object
               && json.TryGetProperty("type", out var type)
               && type.ValueKind == JsonValueKind.String
            ? type.GetString()!
            : "(型不明)";
    }

    // IADR-0535 決定 1 の 3 条件（ログは型名のみ。本文は載せない。IADR-0114 決定 5）。
    // 「既知ブロックが残る」は、本文に使う text ブロックが 1 個以上あること（IADR-0562 決定 5）。
    private void LogDropped(IReadOnlyList<string> droppedTypes, int textBlocks)
    {
        var types = string.Join(", ", droppedTypes);
        if (textBlocks == 0)
        {
            logger.LogWarning(
                "Anthropic 応答の content に本文（text ブロック）がありませんでした（本文に使わず捨てた型: {DroppedTypes}）。"
                + "本文が無い応答として扱います。",
                types);
            return;
        }

        var unexpected = droppedTypes
            .Where(t => !AnthropicContentBlockMetrics.ExpectedDroppedTypes.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unexpected.Count > 0)
        {
            logger.LogWarning(
                "Anthropic 応答に想定外の content ブロック型がありました（想定外の型: {UnexpectedTypes}／本文に使わず捨てた型: {DroppedTypes}）。"
                + "本文は保持しています。",
                string.Join(", ", unexpected), types);
            return;
        }

        logger.LogDebug(
            "Anthropic 応答の thinking 系の content ブロックを本文に使わず捨てました（型: {DroppedTypes}）。本文は保持しています。",
            types);
    }

    // FR-11, ADR-0038 決定 4, IADR-0225, IADR-0374, IADR-0562 決定 1: SDK の例外を、判定器（LlmFallbackPolicy）と
    // 計器（LlmCompletionMetrics）が読む形＝旧 SDK と同じ HttpRequestException へ包み直す。
    //   - AnthropicApiException（非 2xx）→ HttpRequestException(StatusCode 付き)。400 系のフォールバックと 429 の区別がこれに懸かる。
    //   - AnthropicIOException（送信の失敗）→ HttpRequestException(状態なし)＝ transport。
    //   - それ以外（応答の形の異常など）は包まずにそのまま伝える（catch の when で外す。状態の取れない失敗＝フォールバックしない。
    //     旧 SDK の JsonException と同じ扱い）。
    // 元の例外は InnerException に残す（ログで原因を追える）。
    internal static bool IsTranslated(AnthropicException exception)
        => exception is AnthropicApiException or AnthropicIOException;

    internal static HttpRequestException Translate(AnthropicException exception) => exception switch
    {
        AnthropicApiException api => new HttpRequestException(api.Message, api, api.StatusCode),
        _ => new HttpRequestException(exception.Message, exception),
    };
}
