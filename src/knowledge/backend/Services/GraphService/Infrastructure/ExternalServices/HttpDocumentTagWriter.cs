using System.Net;
using System.Net.Http.Json;
using GraphService.Domain.Ports;
using Knowledge.Contracts.Dtos;

namespace GraphService.Infrastructure.ExternalServices;

// FR-18, SC-03, ADR-0063 決定 1〜3, IADR-0361 決定 1 (#1187):
// DocumentService の `POST /documents/{id}/tags` を**承認者本人の資格で**呼ぶアダプタ。
//
// 🔴 **権限伝播は方式 A（`Authorization` ヘッダの転送）である**（`RagOrchestrator` → RetrievalService、
// BFF → GraphService と同型）。後段は自分で「①所有者 または ②管理者ロール」を再判定する
// （最終防衛線。[[IADR-0044]]）。**サービスアカウントを持たない** —— 持つと承認者の権限を超えた
// 書き込みになり、監査で誰の意思か追えない（決定 3 が案 C を退けた理由）。
//
// ⚠️ **ヘッダを転送し忘れると後段が匿名として拒み、承認が静かに全件 404 になる。**
// 転送の陽性対照は `HttpDocumentTagWriterTests` が `HttpMessageHandler` 層で固定する。
//
// 🔴 **fail-closed である。** 到達できない・5xx・読めない応答はすべて `Unavailable` に倒し、
// 呼び出し側が 502 にする。**成功へ縮退しない** —— 承認できていないのに承認済みと見えるのが最悪である。
public sealed class HttpDocumentTagWriter(
    IHttpClientFactory httpFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<HttpDocumentTagWriter> logger) : IDocumentTagWriter
{
    public const string ClientName = "DocumentService";

    public async Task<TagWriteOutcome> AddTagAsync(
        Guid documentId, string tagName, CancellationToken ct = default)
    {
        var client = httpFactory.CreateClient(ClientName);

        // ★ 承認者本人の資格情報。要求の外（バックグラウンド）から呼ばれる経路は無い。
        var auth = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

        try
        {
            var resp = await client.PostAsJsonAsync(
                $"/documents/{documentId}/tags", new AddDocumentTagRequest(tagName), ct);

            if (resp.IsSuccessStatusCode)
                return TagWriteOutcome.Applied;

            switch (resp.StatusCode)
            {
                case HttpStatusCode.BadRequest:
                    // 辞書に無い（`UnknownTagsProblem`）。**承認できず却下のみ**（ADR-0063 決定 2）。
                    return TagWriteOutcome.UnknownTag;
                case HttpStatusCode.NotFound:
                    // 後段の「所有者でも管理者でもない」「文書が無い」。どちらも 404 の一本道。
                    return TagWriteOutcome.NotWritable;
                default:
                    logger.LogError(
                        "タグ提案の反映に失敗した（status={Status}）。documentId={DocumentId}。タグ値は本文へ出さない。",
                        (int)resp.StatusCode, documentId);
                    return TagWriteOutcome.Unavailable;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "タグ提案の反映先へ到達できない。documentId={DocumentId}。", documentId);
            return TagWriteOutcome.Unavailable;
        }
    }
}
