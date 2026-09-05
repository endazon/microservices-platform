using System.Net.Http.Json;
using GraphService.Domain;
using GraphService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Infrastructure.ExternalServices;

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

        // IADR-0398 (#1255): 読み取りは共通の SuggestionProposalParser にある
        // （gRPC 実装が同じものを呼ぶ。輸送によって採れる提案が変わらないようにする）。
        return SuggestionProposalParser.Parse(body.Text, logger);
    }
}
