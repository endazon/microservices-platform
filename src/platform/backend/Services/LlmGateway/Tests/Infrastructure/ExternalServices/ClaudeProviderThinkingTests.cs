using System.Text.Json;
using AwesomeAssertions;
using LlmGateway.Infrastructure.ExternalServices;
using LlmGateway.Domain.Ports;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// FR-04, FR-11, ADR-0010, IADR-0114 (AST#290), IADR-0562 (#1749): thinking（拡張思考）ブロック・未知のブロックを含む応答でも
// ClaudeProvider が本文を取り出せることの回帰テスト（T-24 / T-35）。
//
// ［2026-10-17 追記 / #1749・IADR-0562］SDK を公式 `Anthropic`（12.53.0）へ移した。公式 SDK は thinking を型として知り、
// 判別子に無い型も例外にせず保持するので、旧 SDK 向けのサニタイズ（委譲ハンドラ）は撤去した。下の「背景」は旧 SDK の事情である。
// 本ファイルは**サニタイズ無し**で同じ応答を与え、本文が取り出せることを固定する。旧来の変異（サニタイズを外すと例外）は
// 前提ごと消えたので、代わりに「本文は text ブロックだけから取る」ことが load-bearing であることを、本文の前に置いた
// 非 text ブロックの文字列が本文へ混ざらないことで固定する（ExtractText の条件を外すと赤）。
//
// 背景: 用途別の割当モデル（trade-decision / report-daily = claude-sonnet-5-5、report-weekly /
// report-monthly / analysis / default = claude-opus-5-5）はいずれも thinking が
// 既定で有効であり、応答の content には必ず thinking ブロックが載る。Anthropic.SDK 4.0.0 は
// 当該型を知らないため、サニタイズが無いと `JsonException: Unknown type thinking` で
// **応答全体**が失われ、/complete は Sent=false へ縮退する（＝AI 自律取引が成立しない）。
//
// ［2026-08-18 追記 / #850］計画 ADR-0038 決定 1 により analysis の割当を claude-fable-5 → claude-opus-5 へ
// 改めたので、上の背景記述を現行値へ書き改めた。**本テストの挙動は変わらない** —— ここは背景の説明であって
// テストが渡すモデル文字列ではない（本ファイルが実際に渡すのは claude-sonnet-5-5・claude-haiku-5-5 と null だけである）。
// ［2026-10-10 追記 / #1875・IADR-0531］割当を 5.5 系へ切り替えたので、背景の割当名を現行値へ改めた。
// 5.5 系では **Haiku も thinking が既定で有効**になった（4.5 は無効）。rerank・trade-decision-screening・
// 鎖の第 2 候補の haiku-5-5 も thinking 先頭の応答を返すので、その形を非ストリーム・ストリームの両方で固定する。
[Trait("TestKind", "Unit")]
public class ClaudeProviderThinkingTests
{
    private static string Envelope(string blocks, string stopReason = "end_turn", string model = "claude-sonnet-5-5") =>
        "{\"id\":\"msg_01\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"" + model + "\","
        + "\"content\":[" + blocks + "],\"stop_reason\":\"" + stopReason + "\","
        + "\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}";

    private const string ThinkingBlock = """{"type":"thinking","thinking":"","signature":"s"}""";

    private static string TextBlock(string text) =>
        "{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}";

    // (1) 例外を投げない／(2) 本文テキストを正しく取り出す。
    [Fact]
    public async Task CompleteAsync_WithThinkingBlock_ReturnsTextInsteadOfThrowing()
    {
        var provider = CreateProvider(Envelope($"{ThinkingBlock},{TextBlock("要約本文")}"));

        var result = await provider.CompleteAsync(new CompletionRequest("要約して", 4096, "claude-sonnet-5-5"), TestContext.Current.CancellationToken);

        result.Text.Should().Be("要約本文");
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(5);
        result.StopReason.Should().Be("end_turn");
    }

    // (3) 取引判断の構造化出力（JSON 本文）が Buy / Sell / Hold として正しく取り出せる。
    // AST 側の TradeDecisionParser は本文から JSON を抽出して action を読むため、
    // 本文が原文のまま届くことがそのまま「判断が成立する」ことの前提になる。
    [Theory]
    [InlineData("Buy")]
    [InlineData("Sell")]
    [InlineData("Hold")]
    public async Task CompleteAsync_WithThinkingBlock_PreservesStructuredDecisionBody(string action)
    {
        var body = $$"""{"action":"{{action}}","rationale":"根拠","referencePrice":190.5,"stopLossDistancePerShare":5.5}""";
        var provider = CreateProvider(Envelope($"{ThinkingBlock},{TextBlock(body)}"));

        var result = await provider.CompleteAsync(new CompletionRequest("判断して", 4096, "claude-sonnet-5-5"), TestContext.Current.CancellationToken);

        result.Text.Should().Be(body);
        JsonSerializer.Deserialize<JsonElement>(result.Text)
            .GetProperty("action").GetString().Should().Be(action);
    }

    // 未知型が将来増えても同じ経路で救われる（型名の列挙に依存しないことの回帰）。
    [Fact]
    public async Task CompleteAsync_WithUnknownFutureBlock_ReturnsText()
    {
        var provider = CreateProvider(
            Envelope($$"""{"type":"some_future_block","payload":1},{{TextBlock("本文")}}"""));

        (await provider.CompleteAsync(new CompletionRequest("q", 4096, null), TestContext.Current.CancellationToken)).Text.Should().Be("本文");
    }

    // ［2026-10-17 / #1749］旧来の変異（サニタイズを外すと `Unknown type thinking`）の置き換え。
    // 本文の前に、文字列の欄を持つ thinking・型名の無い要素・将来型が並んでも、本文は text ブロックからだけ取る。
    // 本文を「最初のブロックの文字列」等で取る実装へ崩すと赤になる。
    [Fact]
    public async Task CompleteAsync_NonTextBlocksBeforeBody_AreNeverUsedAsBody()
    {
        var provider = CreateProvider(Envelope(
            """{"type":"thinking","thinking":"思考の文字列","signature":"s"},{"text":"型の無い要素"},{"type":"some_future_block","text":"将来型の文字列"},"""
            + TextBlock("本文")));

        var result = await provider.CompleteAsync(new CompletionRequest("q", 4096, "claude-sonnet-5-5"), TestContext.Current.CancellationToken);

        result.Text.Should().Be("本文");
    }

    // 全ブロックが本文でない（thinking だけ）なら例外にせず空へ縮退する（IADR-0114 決定 6 と同じ帰結。呼び出し側の空応答分岐へ合流）。
    [Fact]
    public async Task CompleteAsync_ThinkingOnly_ReturnsEmptyTextWithoutThrowing()
    {
        var provider = CreateProvider(Envelope(ThinkingBlock, stopReason: "max_tokens"));

        var result = await provider.CompleteAsync(new CompletionRequest("q", 4096, null), TestContext.Current.CancellationToken);

        result.Text.Should().BeEmpty();
        result.StopReason.Should().Be("max_tokens");
    }

    // 既知型だけの応答でも本文を取り出せる（回帰）。
    [Fact]
    public async Task CompleteAsync_WithKnownBlocksOnly_IsReadableAfterInspection()
    {
        var provider = CreateProvider(Envelope(TextBlock("素通し本文")));

        (await provider.CompleteAsync(new CompletionRequest("q", 4096, null), TestContext.Current.CancellationToken)).Text.Should().Be("素通し本文");
    }

    // IADR-0104 の安全既定は不変: 拒否（refusal）は thinking の有無に関わらず本文を返さない。
    [Fact]
    public async Task CompleteAsync_RefusalWithThinkingBlock_StillReturnsEmptyText()
    {
        var provider = CreateProvider(
            Envelope($"{ThinkingBlock},{TextBlock("断片")}", stopReason: "refusal"));

        var result = await provider.CompleteAsync(new CompletionRequest("q", 4096, null), TestContext.Current.CancellationToken);

        result.Text.Should().BeEmpty();
        result.StopReason.Should().Be("refusal");
    }

    // FR-03, FR-11, IADR-0531 (#1875, T-35): haiku-5-5 は thinking が既定で有効（4.5 は無効だった）。
    // rerank の応答（JSON の順位）が thinking ブロックの後ろに来ても、非ストリームで本文を取り出せる。
    [Fact]
    public async Task CompleteAsync_Haiku55ThinkingFirst_ReturnsRankingBody()
    {
        const string ranking = """{"ranking":[3,1,2]}""";
        var provider = CreateProvider(
            Envelope($"{ThinkingBlock},{TextBlock(ranking)}", model: "claude-haiku-5-5"));

        var result = await provider.CompleteAsync(
            new CompletionRequest("並べ替えて", 1024, "claude-haiku-5-5", "rerank"), TestContext.Current.CancellationToken);

        result.Text.Should().Be(ranking);
        result.StopReason.Should().Be("end_turn");
    }

    // FR-11, IADR-0531 (#1875, T-35): ストリーム経路は thinking ブロック（content_block_start の thinking・
    // thinking_delta・signature_delta）を本文デルタとして流さず、後続の本文だけを流す。
    [Fact]
    public async Task StreamAsync_Haiku55ThinkingFirst_StreamsOnlyTextDeltas()
    {
        const string sse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_h","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[],"stop_reason":null,"usage":{"input_tokens":11,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"考えている"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"回答"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":42}}

        event: message_stop
        data: {"type":"message_stop"}


        """;
        var provider = TestClaudeProviders.Create(new StubAnthropicHandler(sse, "text/event-stream"));

        var chunks = new List<CompletionChunk>();
        await foreach (var chunk in provider.StreamAsync(
            new CompletionRequest("要約して", 4096, "claude-haiku-5-5"), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        string.Concat(chunks.Select(c => c.TextDelta)).Should().Be("回答");
        chunks.Should().NotContain(c => c.TextDelta.Contains("考えている"));
        var done = chunks.Should().ContainSingle(c => c.Done).Subject;
        done.StopReason.Should().Be("end_turn");
        done.InputTokens.Should().Be(11);
        done.OutputTokens.Should().Be(42);
    }

    private static ClaudeProvider CreateProvider(string responseBody)
        => TestClaudeProviders.Create(new StubAnthropicHandler(responseBody));
}
