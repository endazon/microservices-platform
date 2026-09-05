using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiAnalysisService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace AiAnalysisService.Infrastructure.ExternalServices;

// FR-04, FR-11, NFR-02, ADR-0010, ADR-0044, ADR-0076 決定 4, IADR-0037, IADR-0101, IADR-0378,
// IADR-0379 決定 5, IADR-0398 (#1255): テキスト生成の **REST 輸送**（SSE ＋ JSON）。
//
// **並走中の正はこちらである。** 本クラスは RagOrchestrator が従来インラインで持っていたコードを
// **そのまま移した**ものであり、縮退の枝・メッセージ文言・例外の捕まえ方を 1 つも変えていない
// （移行の不変条件は「挙動を変えない」）。gRPC 実装（GrpcLlmCompletionTransport）は本クラスの
// 縮退の枝を写す先として読む。
public sealed class HttpLlmCompletionTransport(IHttpClientFactory httpFactory) : ILlmCompletionTransport
{
    public const string HttpClientName = "LlmGateway";

    // SSE data 行の JSON（camelCase）を CompletionStreamEvent へ復元する。
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    // IADR-0037: LlmGateway /complete/stream の SSE を消費し、CompletionStreamEvent を逐次返す。
    // 反復子内で yield を跨ぐ try/catch を避けるため、送信・読み取りの失敗は捕捉後に done(Sent=false) を
    // yield して終了する（呼び出し側は縮退表示に切り替えられる）。egress 判定はゲートウェイ側で保持される。
    public async IAsyncEnumerable<CompletionStreamEvent> StreamAsync(
        CompletionApiRequest body, bool isSynthetic, [EnumeratorCancellation] CancellationToken ct)
    {
        var llmClient = httpFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/complete/stream")
        {
            Content = JsonContent.Create(body),
        };
        // ADR-0044, ADR-0076 決定 4 (#1203): 標識を引き継ぐ（費用計測の除外はゲートウェイ側で行う）。
        SyntheticTraffic.PropagateTo(request, isSynthetic);

        HttpResponseMessage? resp = null;
        var sendFaulted = false;
        try
        {
            resp = await llmClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            sendFaulted = true;
        }

        if (sendFaulted || resp is null || !resp.IsSuccessStatusCode)
        {
            resp?.Dispose();
            yield return new CompletionStreamEvent(string.Empty, Done: true, Sent: false,
                Text: "LLM が現在利用できません。");
            yield break;
        }

        using (resp)
        await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
        using (var reader = new StreamReader(stream))
        {
            while (true)
            {
                string? line = null;
                var readFaulted = false;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException && !ct.IsCancellationRequested)
                {
                    readFaulted = true;
                }

                if (readFaulted)
                {
                    yield return new CompletionStreamEvent(string.Empty, Done: true, Sent: false,
                        Text: "LLM 応答の受信に失敗しました。");
                    yield break;
                }

                if (line is null)
                    yield break; // ストリーム終端

                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue; // SSE の空行・コメント行は読み飛ばす

                CompletionStreamEvent? ev = null;
                try
                {
                    ev = JsonSerializer.Deserialize<CompletionStreamEvent>(
                        line["data: ".Length..], SseJson);
                }
                catch (JsonException)
                {
                    ev = null; // 壊れた行は無視
                }

                if (ev is not null)
                    yield return ev;
            }
        }
    }

    // FR-04: 一括生成。
    // 🔴 **接続失敗は捕まえない**（現行の挙動をそのまま保つ）。RagOrchestrator.GenerateAsync は
    // `PostAsJsonAsync` の周りに try/catch を持っておらず、HttpRequestException は呼び出し側へ
    // 伝播する。ここで握り潰すと**挙動が変わる**ため、非 2xx（= NotReached）だけを縮退として扱う。
    // gRPC 実装との差は IADR-0398 決定 5 に記録した（gRPC には非 2xx に相当する概念が無い）。
    public async Task<LlmCompletionOutcome> CompleteAsync(
        CompletionApiRequest body, bool isSynthetic, CancellationToken ct)
    {
        var llmClient = httpFactory.CreateClient(HttpClientName);
        PropagateSyntheticTo(llmClient, isSynthetic);

        var completionResp = await llmClient.PostAsJsonAsync("/complete", body, ct);
        if (!completionResp.IsSuccessStatusCode)
            return LlmCompletionOutcome.NotReached();

        return LlmCompletionOutcome.Answered(
            await completionResp.Content.ReadFromJsonAsync<CompletionApiResponse>(ct));
    }

    // ADR-0044, ADR-0076 決定 4 (#1203): LlmGateway へ標識を引き継ぐ。
    // **費用計測の除外はゲートウェイ側で行う** —— 単価を解決して金額を積む主体がそこだからである
    // （ADR-0044 決定 3。呼び出し側で引き算する形にすると、経路が増えるたびに引き算が漏れる）。
    private static void PropagateSyntheticTo(HttpClient client, bool isSynthetic)
    {
        if (isSynthetic)
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                SyntheticTraffic.HeaderName, SyntheticTraffic.HeaderValue);
    }
}
