using System.Runtime.CompilerServices;
using LlmGateway.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.Options;

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
// SDK（Anthropic.SDK 4.0.0）は effort を送れないので、AnthropicRequestShapingHandler が要求本文へ足す。
public class ClaudeProvider(
    AnthropicClient client,
    IConfiguration config,
    IOptionsMonitor<ClaudePurposeEffortOptions>? effortOptions = null) : ILlmProvider
{
    private readonly string _model = config["Llm:Model"] ?? "claude-opus-5-5";

    // 用途とモデルから送る effort を決める（未設定・非対応モデルは null＝送らない）。
    private string? EffortFor(CompletionRequest request, string model)
        => ClaudeEffort.Resolve(effortOptions?.CurrentValue, request.Purpose, model);

    public async Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        var model = request.Model ?? _model;
        using var effortScope = AnthropicRequestContext.UseEffort(EffortFor(request, model));
        var msg = await client.Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Model = model,
            MaxTokens = request.MaxTokens,
            Messages =
            [
                new Message
                {
                    Role = RoleType.User,
                    Content = [new TextContent { Text = request.Prompt }]
                }
            ]
        }, ct);

        // IADR-0104 (#379), ADR-0025: content を読む前に stop_reason を確認する。refusal は HTTP 200・
        // 例外なしで到着するため、判別しないと空文字へ静かに縮退し「送信したが空応答」と区別できない。
        var stopReason = msg.StopReason;

        // 拒否時は本文を返さない。安全性分類器は本文の途中で停止し得るため、断片が非空のまま
        // 下流へ流れると（AST 取引判断は Text の非空を根拠に売買判断へ進む）fail-safe が破れる。
        // max_tokens の途中結果は正当な観測対象であり破棄しない（IADR-0101 / IADR-0104 §決定）。
        var text = CompletionStopReasons.IsRefusal(stopReason)
            ? string.Empty
            : msg.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";

        return new CompletionResult(text, msg.Usage.InputTokens, msg.Usage.OutputTokens, stopReason);
    }

    // IADR-0037: Anthropic SDK の SSE ストリーミングで本文デルタを逐次返す（真のストリーミング・主経路）。
    // 呼び出し側（LlmGateway /complete/stream）は egress ルーティングで送信可と判定した後にのみ本メソッドを呼ぶ。
    public async IAsyncEnumerable<CompletionChunk> StreamAsync(
        CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var model = request.Model ?? _model;
        var parameters = new MessageParameters
        {
            Model = model,
            MaxTokens = request.MaxTokens,
            Stream = true,
            Messages =
            [
                new Message
                {
                    Role = RoleType.User,
                    Content = [new TextContent { Text = request.Prompt }]
                }
            ]
        };

        int inputTokens = 0, outputTokens = 0;
        string? stopReason = null;
        // IADR-0531: 要求の送信は最初の MoveNextAsync（この反復子の同じ段）で起きるので、ここで張った文脈が届く。
        using var effortScope = AnthropicRequestContext.UseEffort(EffortFor(request, model));
        await foreach (var res in client.Messages.StreamClaudeMessageAsync(parameters, ct))
        {
            // message_start で入力トークン、message_delta で出力トークンが逐次確定する。
            if (res.StreamStartMessage?.Usage is { } startUsage)
                inputTokens = startUsage.InputTokens;
            if (res.Usage is { } usage && usage.OutputTokens > 0)
                outputTokens = usage.OutputTokens;

            // IADR-0104: 終了理由は message_delta（末尾）で確定する。既に送出したデルタは撤回できないため、
            // 最終チャンクへ載せて呼び出し側が破棄判断できるようにする（トレードオフは IADR-0104 §結果）。
            if (res.Delta?.StopReason is { Length: > 0 } deltaStop)
                stopReason = deltaStop;
            else if (res.StopReason is { Length: > 0 } msgStop)
                stopReason = msgStop;

            var delta = res.Delta?.Text;
            if (!string.IsNullOrEmpty(delta))
                yield return new CompletionChunk(delta);
        }

        // 最終チャンク: 本文増分なし・トークン数と終了理由を伴う（呼び出し側が集計と縮退判断を確定できる）。
        yield return new CompletionChunk(string.Empty, Done: true, inputTokens, outputTokens, stopReason);
    }
}
