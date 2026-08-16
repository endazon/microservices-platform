using LlmGateway.Api.Foundation.Ports;
using LlmGateway.Api.Foundation.Routing;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LlmGateway.Api.Composable.Adapters;

// FR-02, FR-05, ADR-0016, ADR-0017: セルフホスト埋め込みプロバイダ（高機密・ティアA=社外送信なし）。
// confidential/restricted 文書の埋め込みをこの経路に固定する。モデルは Ruri v3（第一採用・次元 768 系、ADR-0017）。
// OpenAI 互換の /v1/embeddings を持つ社内基盤（TEI / vLLM 等）を呼ぶ想定。ADR-0010/0013 のとおり「後付け可能」とし、
// 既定では無効エンドポイントとして扱う（BaseUrl 未設定時は利用不可＝高機密は fail-closed で外部送信しない）。
public sealed class SelfHostedEmbeddingProvider(IHttpClientFactory httpFactory, IConfiguration config)
    : IEmbeddingProvider
{
    private readonly string _baseUrl = config["Embedding:SelfHosted:BaseUrl"] ?? string.Empty;

    // #809, ADR-0017: 用途別プレフィクスは**モデル固有**である。Ruri v3 は 1+3 プレフィクス
    // （"検索クエリ: " / "検索文書: "）を必須とするが、ADR-0017 が劣化時の代替に挙げる BGE-M3 は使わない。
    // したがってハードコードせず設定駆動にし、**既定は空**＝素の本文を送る（現行と同じ挙動）。
    // 付けっぱなしにすると、モデルを差し替えたときに今度は別の意味で埋め込みが歪む。
    //
    // **appsettings.json の `Embedding:SelfHosted:QueryPrefix` / `:DocumentPrefix` が既定値を持つ。**
    // モデルを差し替えるときは、そこを空にすること —— JSON にコメントを書く手段が無いため、
    // 設定の意味はここに置く（`"//"` のようなダミーキーは、将来この節を POCO へバインドしたとき
    // 未知キーとして問題化しうるので入れない）。
    private const string QueryPrefixKey = "Embedding:SelfHosted:QueryPrefix";
    private const string DocumentPrefixKey = "Embedding:SelfHosted:DocumentPrefix";
    private readonly string _queryPrefix = config[QueryPrefixKey] ?? string.Empty;
    private readonly string _documentPrefix = config[DocumentPrefixKey] ?? string.Empty;

    public async Task<float[]> EmbedAsync(
        string text, string model, int dimensions, EmbeddingRoutePurpose purpose, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new InvalidOperationException("セルフホスト埋め込みの BaseUrl が未設定です（Embedding:SelfHosted:BaseUrl）。");

        var client = httpFactory.CreateClient("SelfHostedEmbedding");
        client.BaseAddress = new Uri(_baseUrl);

        var prefix = purpose == EmbeddingRoutePurpose.Query ? _queryPrefix : _documentPrefix;
        var body = new { input = prefix + text, model };

        var resp = await client.PostAsJsonAsync("/v1/embeddings", body, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(ct);

        return payload?.Data?.FirstOrDefault()?.Embedding ?? [];
    }

    private sealed record OpenAiEmbeddingResponse(
        [property: JsonPropertyName("data")] List<OpenAiEmbeddingItem>? Data);

    private sealed record OpenAiEmbeddingItem(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
