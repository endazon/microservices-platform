using Grpc.Core;
using GraphService.Domain.Clustering;
using GraphService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace GraphService.Infrastructure.ExternalServices;

// FR-11, FR-17, FR-18, NFR-09, NFR-16, ADR-0010, ADR-0029, ADR-0035 決定 3, ADR-0075, ADR-0084 決定 1, ADR-0089 決定 1,
// [[IADR-0266]] 決定 6・7, [[IADR-0379]], [[IADR-0400]] 決定 4・5, [[IADR-0430]] 決定 1・4, [[IADR-0533]] フォローアップ 2 (#1395, #1255):
// クラスタ要約の **east-west gRPC 経路**（`platform.llmgateway.v1.LlmCompletion/Complete`）。
// ［2026-10-17 / #1255］REST の `LlmGatewayClusterSummaryClient`（`POST /complete`）は撤去し、本クラスが唯一の実装である。
// 形は兄弟の `LlmGatewayGrpcSuggestionClient` に揃えてある（宛先未構成は `UNAVAILABLE` → 下の null の枝）。
//
// 🔴 **引数は ClusterSummaryPrompt のみである**（`IClusterSummaryLlmClient`）。送信本文は封が
// 組み立てる（`Render`）—— 組み立てを本クラスへ出すと、封を通っていない文字列を送る経路が開く。
//
// **機密区分は封が持つ区分をそのまま渡す。** 封には**その区分を超える文書が 1 件も入っていない**
// （`Seal` が落とす）ため、FR-11 の「文脈に含む文書のうち最も高い区分」と一致する。
// ゲートウェイはこれで送信先ティアを決め、越境が許されなければ `sent=false` を返す。
//
// 🔴 **モデルを指定しない。** `ADR-0035` 決定 3 は生成モデルに `claude-opus-5` を割り当てているが
// （［2026-10-10 / #1875］利用者裁定 planning#783 でゲートウェイの割当は `claude-opus-5-5`。IADR-0531）、
// 実際の宛先は**ゲートウェイの経路決定**（用途の割当と機密区分によるティア選択）が持つ。呼び出し側から
// `Model` を固定すると、越境判定が選んだ宛先と食い違い得る。proto3 では空文字が「未指定」である（IADR-0400 決定 4）。
//
// 🔴 **輸送の失敗は例外にせず null へ落とす**（IADR-0400 決定 5・IADR-0430 決定 4）。
// 1 区分の失敗はそのクラスタを未要約のまま残すだけであり、バッチを落とす理由にならない。
// 呼び出し元の取り消しだけは伝播する（バッチの停止を「要約が採れなかった」に化けさせない）。
public sealed class LlmGatewayGrpcClusterSummaryClient(
    Pb.LlmCompletion.LlmCompletionClient client,
    ILogger<LlmGatewayGrpcClusterSummaryClient> logger) : IClusterSummaryLlmClient
{
    // 🔴 **用途別計測の用途名。** 監査・課金集計で AI 提案（`graph-suggestion`）と**区別できる**ように、
    // 専用の値を持つ（同じ名前を共有すると、月次の費用がどちらの経路で出たのか分からなくなる）。
    // ［2026-10-17 / #1255］撤去した REST 実装から移した（値は変えていない。ゲートウェイの `PurposeModels` のキーと突き合う）。
    public const string PurposeName = "graph-cluster-summary";

    public async Task<string?> SummarizeAsync(
        ClusterSummaryPrompt prompt, CancellationToken ct = default)
    {
        CompletionApiResponse body;
        try
        {
            var resp = await client.CompleteAsync(
                LlmGrpcMapping.ToProto(new CompletionApiRequest(
                    prompt.Render(),
                    Confidentiality: prompt.Confidentiality,
                    Purpose: PurposeName)),
                cancellationToken: ct);
            body = LlmGrpcMapping.ToDto(resp);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            // RpcException（全 status。未構成の宛先の UNAVAILABLE を含む）と s2s トークン取得失敗（InvalidOperationException）。
            // **要約が付かないだけで、越境も嘘の要約も起きない。** 呼び出し失敗を握り潰さず記録する。
            logger.LogWarning(ex,
                "LLM gateway gRPC call failed for cluster summary (cluster={Cluster} tier={Tier})",
                prompt.ClusterId, prompt.Confidentiality);
            return null;
        }

        // [[IADR-0266]] 決定 6: **縮退した応答を根拠に使わない。**
        //   - Sent=false … 機密区分による送信拒否（越境させていない）
        //   - StopReason="refusal" … 送信は成立したがモデルが拒否した（ADR-0025 / [[IADR-0104]]）
        // どちらでも**要約を作らない**。提案生成が `[]` へ降りるのと同じ判断である。
        //
        // 🔴 `sent` は proto3 の既定（false）と DTO の既定（true）で向きが逆であり、ゲートウェイが明示的に書いている
        // （IADR-0400 決定 4）。写し漏れると**常に null**になる（例外にならない）ため、
        // `LlmGatewayGrpcClusterSummaryClientTests` が陽性と陰性の対で固定する。
        if (!body.Sent || CompletionStopReasons.IsRefusal(body.StopReason))
            return null;

        // 空応答も「採れなかった」に倒す —— 空の要約を書くと、
        // `UnsummarizedClusterRule` の条件 1 は満たすのに中身が無い行が残る。
        var text = body.Text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
