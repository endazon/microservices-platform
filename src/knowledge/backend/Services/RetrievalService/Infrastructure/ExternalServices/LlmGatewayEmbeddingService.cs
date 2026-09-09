using RetrievalService.Domain.Ports;
using System.Net.Http.Json;
using Platform.Shared.Contracts.Dtos;

namespace RetrievalService.Infrastructure.ExternalServices;

// ADR-0013, ADR-0016: LLM ゲートウェイ経由でクエリ埋め込みを生成する（検索経路 = Purpose=Query）。
// クエリは検索対象コレクションへ整合させるため、ゲートウェイが送信先を決める（**既定では**外部経路
// ＝ voyage / 1024 次元。測定時は `Embedding:Routing:QueryProfile` で名指しできる。IADR-0422 決定 2）。
// 高機密（ruri / 768 次元）コレクションの横断検索は FR-03 の後続課題。
public class LlmGatewayEmbeddingService(
    HttpClient http,
    QueryEmbeddingTarget? target = null,
    ILogger<LlmGatewayEmbeddingService>? logger = null) : IEmbeddingService
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            "/embed",
            new EmbedApiRequest(text, Confidentiality: null, Purpose: EmbedPurpose.Query),
            ct);
        // **到達できない・非 2xx はここで例外にする（潰さない）。** ゲートウェイの故障を
        // 「該当なし」に化けさせないため（[[IADR-0256]] 決定 3）。
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<EmbedApiResponse>(ct);

        // FR-02, FR-03, ADR-0016, #995: **`Embedded` を読む。** `/embed` は送信拒否（fail-closed）・
        // 次元不整合・呼び出し失敗のいずれでも **200 ＋ `Vector: [], Embedded: false`** を返す契約であり
        // （`EmbedApiResponse` / `EmbeddingEndpoints`）、**呼び出し側が `Embedded` を見て降りる**のが前提である。
        // 従前は `result?.Vector ?? []` と Vector だけを読んでおり、**契約の意図に暗黙依存していた**。
        // 空ベクトルは「意味検索の系統が使えない」の合図であり、呼び出し側（HybridSearchService）が読む。
        if (result is not { Embedded: true })
            return [];

        // FR-02, FR-03, ADR-0016, IADR-0422 決定 3 (#336): **答えたコレクションと読むコレクションを照合する。**
        // 食い違ったベクトルで検索すると**別モデルの空間へ問い合わせて順位が返る** —— 0 件にすらならず、
        // 壊れた結果が正常に見える。gRPC 実装と**同じ判定**を通す。
        if (!QueryEmbeddingCollection.Matches(result.Collection, target))
        {
            logger?.LogError(
                "Query embedding collection mismatch: gateway answered {Answered} but search reads {Target} (endpoint {Endpoint}). "
                + "Falling back to keyword-only search.",
                result.Collection, target!.Collection, result.Endpoint);
            return [];
        }

        return result.Vector;
    }
}
