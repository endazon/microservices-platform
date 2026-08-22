using System.Net.Http.Json;
using System.Text.Json;
using GraphService.Api.Foundation.Domain;
using GraphService.Api.Foundation.Ports;
using GraphService.Api.Foundation.Services;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Composable.Adapters;

// FR-18, FR-11, ADR-0010, ADR-0034 決定 5, IADR-0266 決定 6・7 (#915):
// LLM ゲートウェイ /complete 経由で提案を生成する。
//
// 🔴 **引数は SuggestionPrompt のみである**（ISuggestionLlmClient）。送信本文は封が組み立てる
// （Render）—— 組み立てを本クラスへ出すと、封を通っていない文字列を送る経路が開く。
//
// **機密区分は封が持つ最高区分をそのまま渡す**（FR-11「文脈に含む文書のうち最も高い区分」）。
// ゲートウェイはこれで送信先ティアを決め、越境が許されなければ Sent=false を返す。
public sealed class LlmGatewaySuggestionClient(
    HttpClient http, ILogger<LlmGatewaySuggestionClient> logger) : ISuggestionLlmClient
{
    // 監査・課金集計で用途が識別できるようにする（ゲートウェイ側は自由文字列として扱う）。
    public const string PurposeName = "graph-suggestion";

    public async Task<IReadOnlyList<LlmSuggestionProposal>> ProposeAsync(
        SuggestionPrompt prompt, CancellationToken ct = default)
    {
        CompletionApiResponse? body;
        try
        {
            var resp = await http.PostAsJsonAsync("/complete", new CompletionApiRequest(
                prompt.Render(),
                Confidentiality: prompt.Confidentiality,
                Purpose: PurposeName), ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("LLM gateway returned {Status} for suggestion generation", resp.StatusCode);
                return [];
            }
            body = await resp.Content.ReadFromJsonAsync<CompletionApiResponse>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // **提案が 0 件になるだけで、越境も誤提案も起きない。** 呼び出し失敗を握り潰さず記録する。
            logger.LogWarning(ex, "LLM gateway call failed for suggestion generation");
            return [];
        }

        // IADR-0266 決定 6: **縮退した応答を根拠に使わない。**
        //   - Sent=false … 機密区分による送信拒否（越境させていない）
        //   - StopReason="refusal" … 送信は成立したがモデルが拒否した（ADR-0025 / IADR-0104）
        // どちらでも**提案を 1 件も作らない**。LlmGatewayEmbeddingService が Embedded を読んで
        // 降りるのと同じ作法である。
        if (body is null || !body.Sent || CompletionStopReasons.IsRefusal(body.StopReason))
            return [];

        return Parse(body.Text);
    }

    // FR-18: 応答本文（JSON 配列）を読む。**読めなければ空**（例外を投げない）——
    // 生成の失敗は「提案が付かない」で足り、利用者の要求を落とす理由にならない。
    private IReadOnlyList<LlmSuggestionProposal> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // モデルが前置きを付けた場合に備え、最初の '[' から最後の ']' までを採る。
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return [];

        try
        {
            var wire = JsonSerializer.Deserialize<List<ProposalWire>>(
                text[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (wire is null)
                return [];

            var result = new List<LlmSuggestionProposal>();
            foreach (var w in wire)
            {
                var kind = w.Kind?.Trim().ToLowerInvariant();
                if (kind is null || !SuggestionKind.IsValid(kind))
                    continue;
                result.Add(new LlmSuggestionProposal(
                    kind, w.TargetDocumentId, w.EdgeTypeName, w.TagValue, w.Rationale ?? string.Empty));
            }
            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "LLM gateway returned an unparsable suggestion payload");
            return [];
        }
    }

    private sealed record ProposalWire(
        string? Kind, Guid? TargetDocumentId, string? EdgeTypeName, string? TagValue, string? Rationale);
}
