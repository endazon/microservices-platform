using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Http;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace Knowledge.Bff.Endpoints.Documents;

// FR-06, UC-03, SC-03, ADR-0014, [[IADR-0543]] (#1905): `GET /bff/documents/{id}/content` の応答の写し。
//
// 🔴 **本文の代わりに案内文を 200 で返さない。** 本文が返せないときは、理由ごとに状態と問題詳細を返す。
//   画面は状態で出し分け（404 は「本文が保管されていない」、413 は「大きすぎる」、502 は「取得できない」）、
//   監視は状態コードで気付ける。
//
// 問題詳細の `type` は画面・運用者が機械的に引く語であり、文言（`title` / `detail`）を変えても変えない。
public static class DocumentBodyProblems
{
    public const string BodyUnavailableType = "urn:microservices-platform:problem:document-body-unavailable";
    public const string BodyTooLargeType = "urn:microservices-platform:problem:document-body-too-large";
    public const string BodyUnreachableType = "urn:microservices-platform:problem:document-body-unreachable";

    /// <summary>
    /// 後段の応答 → HTTP の応答。<paramref name="content"/> が <c>null</c> は「後段で読めない・無い」で、
    /// 詳細（<c>FetchAuthorizedAsync</c>）と同じく問題詳細の無い 404 にする（存在秘匿。後段の判定が BFF より狭い場合の AND 合成）。
    /// </summary>
    public static IResult ToResult(Pb.GetDocumentContentResponse? content, DocumentDto doc)
    {
        if (content is null)
            return Results.NotFound();

        return content.State switch
        {
            Pb.DocumentContentState.Available =>
                Results.Ok(new DocumentContentDto(doc.Id, doc.Title, content.Markdown, doc.MarkdownUri)),
            Pb.DocumentContentState.NotStored => Unavailable(
                "本文の参照がオブジェクトストレージを指していません。原本を参照してください。", "not_stored"),
            Pb.DocumentContentState.Missing => Unavailable(
                "本文のオブジェクトがオブジェクトストレージにありません。原本を参照してください。", "missing"),
            Pb.DocumentContentState.TooLarge => Results.Problem(
                type: BodyTooLargeType,
                title: "本文が大きすぎるため返せません。",
                detail: $"本文は {content.SizeBytes} バイトで、上限 {content.LimitBytes} バイトを超えています。原本を参照してください。",
                statusCode: StatusCodes.Status413PayloadTooLarge,
                extensions: new Dictionary<string, object?>
                {
                    ["sizeBytes"] = content.SizeBytes,
                    ["limitBytes"] = content.LimitBytes,
                }),
            // 🔴 0（UNSPECIFIED）や未知の値は「分からない応答」である。本文として 200 へ畳まない。
            _ => Unreachable(),
        };
    }

    /// <summary>本文を後段から受け取れなかった（宛先未構成・ストレージ未構成・一時障害）。</summary>
    public static IResult Unreachable() => Results.Problem(
        type: BodyUnreachableType,
        title: "本文を取得できませんでした。",
        detail: "文書サービスから本文を受け取れませんでした。時間をおいて再度お試しください。",
        statusCode: StatusCodes.Status502BadGateway);

    private static IResult Unavailable(string detail, string reason) => Results.Problem(
        type: BodyUnavailableType,
        title: "本文が保管されていません。",
        detail: detail,
        statusCode: StatusCodes.Status404NotFound,
        extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
