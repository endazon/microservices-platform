using System.Net.Http.Json;
using GraphService.Domain.Ports;
using Knowledge.Contracts.Dtos;

namespace GraphService.Infrastructure.ExternalServices;

// FR-18, SC-09, ADR-0063 決定 2, IADR-0361 決定 2 (#1014):
// DocumentService の内部口 `GET /internal/tags/names` から辞書の**名前集合**を読むアダプタ。
//
// 🔴 **利用者の資格情報を転送しない。** 読み取り主体は本サービス自身である ——
// タグ辞書の照会口（`/tags`）は管理者・運用者限定（SC-05 Q18）であり、利用者の資格で引くと
// 一般利用者の生成が全件 403 で 0 件になる。内部口は認証を要求しない（メッシュ内部 API。
// `/internal/knowledge-health/observations` と同じ統制）。
//
// 🔴 **fail-closed である。** 到達できない・非 2xx・読めない応答は **null**（引けなかった）を返し、
// 生成段はタグ提案を 1 件も作らない。**空集合へ縮退しない** —— 空集合は「辞書が空」であり、
// 「分からない」とは別の事実である。
public sealed class HttpTagDictionaryReader(
    IHttpClientFactory httpFactory,
    ILogger<HttpTagDictionaryReader> logger) : ITagDictionaryReader
{
    public const string ClientName = HttpDocumentTagWriter.ClientName;

    // 🔴 受け口 DocumentService.Features.Tags.Names.TagNamesEndpoint.NamesPath と同値。
    // **サービスを跨ぐため定数を共有できない**（サービス間は直接参照しない）。両側のテストで固定する。
    public const string NamesPath = "/internal/tags/names";

    public async Task<IReadOnlySet<string>?> ReadNamesAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await httpFactory.CreateClient(ClientName).GetAsync(NamesPath, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("タグ辞書を引けなかった（status={Status}）。タグ提案は生成しない。", (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadFromJsonAsync<TagNamesResponse>(ct);
            if (body is null)
                return null;

            // 辞書側の一意性は正規化後の名前（Trim）で保たれている。比較は Ordinal（DocumentService の
            // `TagResolver.ToIdsAsync` と同じ）。
            return body.Names.Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "タグ辞書の読み取り先へ到達できない。タグ提案は生成しない。");
            return null;
        }
    }
}
