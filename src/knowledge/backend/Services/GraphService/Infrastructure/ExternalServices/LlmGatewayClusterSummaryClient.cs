using System.Net.Http.Json;
using GraphService.Domain.Clustering;
using GraphService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Infrastructure.ExternalServices;

// FR-11, FR-17, FR-18, NFR-09, ADR-0010, ADR-0035 決定 3, ADR-0084 決定 1,
// [[IADR-0266]] 決定 6・7, [[IADR-0424]], [[IADR-0430]] 決定 1・4 (#1395):
// LLM ゲートウェイ /complete 経由でクラスタ要約を生成する。
// 形は兄弟の `LlmGatewaySuggestionClient` に合わせてある。
//
// 🔴 **引数は ClusterSummaryPrompt のみである**（`IClusterSummaryLlmClient`）。送信本文は封が
// 組み立てる（`Render`）—— 組み立てを本クラスへ出すと、封を通っていない文字列を送る経路が開く。
//
// **機密区分は封が持つ区分をそのまま渡す。** 封には**その区分を超える文書が 1 件も入っていない**
// （`Seal` が落とす）ため、FR-11 の「文脈に含む文書のうち最も高い区分」と一致する。
// ゲートウェイはこれで送信先ティアを決め、越境が許されなければ `Sent=false` を返す。
//
// 🔴 **モデルを指定しない。** `ADR-0035` 決定 3 は生成モデルに `claude-opus-5` を割り当てているが、
// 実際の宛先は**ゲートウェイの経路決定**（機密区分によるティア選択）が持つ。呼び出し側から
// `Model` を固定すると、越境判定が選んだ宛先と食い違い得る（作業仕様書 §未決事項 3）。
public sealed class LlmGatewayClusterSummaryClient(
    HttpClient http, ILogger<LlmGatewayClusterSummaryClient> logger) : IClusterSummaryLlmClient
{
    // 🔴 **用途別計測の用途名。** 監査・課金集計で AI 提案（`graph-suggestion`）と**区別できる**ように、
    // 専用の値を持つ（同じ名前を共有すると、月次の費用がどちらの経路で出たのか分からなくなる）。
    // ゲートウェイ側は自由文字列として扱う。
    public const string PurposeName = "graph-cluster-summary";

    public async Task<string?> SummarizeAsync(
        ClusterSummaryPrompt prompt, CancellationToken ct = default)
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
                logger.LogWarning(
                    "LLM gateway returned {Status} for cluster summary (cluster={Cluster} tier={Tier})",
                    resp.StatusCode, prompt.ClusterId, prompt.Confidentiality);
                return null;
            }
            body = await resp.Content.ReadFromJsonAsync<CompletionApiResponse>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // **要約が付かないだけで、越境も嘘の要約も起きない。** 呼び出し失敗を握り潰さず記録する。
            logger.LogWarning(ex,
                "LLM gateway call failed for cluster summary (cluster={Cluster} tier={Tier})",
                prompt.ClusterId, prompt.Confidentiality);
            return null;
        }

        // [[IADR-0266]] 決定 6: **縮退した応答を根拠に使わない。**
        //   - Sent=false … 機密区分による送信拒否（越境させていない）
        //   - StopReason="refusal" … 送信は成立したがモデルが拒否した（ADR-0025 / [[IADR-0104]]）
        // どちらでも**要約を作らない**。提案生成が `[]` へ降りるのと同じ判断である。
        if (body is null || !body.Sent || CompletionStopReasons.IsRefusal(body.StopReason))
            return null;

        // 空応答も「採れなかった」に倒す —— 空の要約を書くと、
        // `UnsummarizedClusterRule` の条件 1 は満たすのに中身が無い行が残る。
        var text = body.Text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
