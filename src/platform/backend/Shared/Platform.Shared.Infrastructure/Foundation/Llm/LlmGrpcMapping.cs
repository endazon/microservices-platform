using Platform.Shared.Contracts.Dtos;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace Platform.Shared.Infrastructure.Foundation.Llm;

// FR-02, FR-03, FR-05, ADR-0016, ADR-0029, ADR-0075, IADR-0379, IADR-0397 (#1255):
// LlmGateway の REST DTO ↔ proto の**写像だけ**を置く。
//
// 🔴 **ここに置いてよいのは写像だけである。** キャッシュ・タイムアウト・リトライ・fail-safe は
// 呼び出し元サービスの Infrastructure に置く（ADR-0029 2026-08-04 追記）。呼び出し元ごとに縮退の
// 落とし先が違う（Retrieval は空ベクトル、Ingestion は Retryable 経由で再試行）ため、
// ここへ寄せると 1 つの縮退規則をすべての呼び出し元へ押しつけることになる。
//
// 呼び出し元と呼び出し先が**同じ写像**を使う（gRPC 面のサーバもこれを呼ぶ）ので、
// 「送る側と受ける側で項目名が 1 つずれる」形の壊れ方（#992 で実際に踏んだ形）が起こらない。
public static class LlmGrpcMapping
{
    // 🔴 proto3 に null は無い（IADR-0397 決定 3）。EMBED_PURPOSE_UNSPECIFIED（proto3 の既定 0）は
    // REST の DTO 既定である **Index** へ写す。Query 以外はすべて Index（REST 側の
    // `req.Purpose == EmbedPurpose.Query ? Query : Index` と同じ倒し方）。
    public static EmbedPurpose ToDtoPurpose(Pb.EmbedPurpose purpose) =>
        purpose == Pb.EmbedPurpose.Query ? EmbedPurpose.Query : EmbedPurpose.Index;

    public static Pb.EmbedPurpose ToProtoPurpose(EmbedPurpose purpose) =>
        purpose == EmbedPurpose.Query ? Pb.EmbedPurpose.Query : Pb.EmbedPurpose.Index;

    public static Pb.EmbedRequest ToProto(EmbedApiRequest req) => new()
    {
        Text = req.Text,
        // proto3 の string は null を持てない。REST の null（未指定）は空文字へ写し、
        // 受け側の SensitivityClasses.Parse が null と同じ restricted（安全側）へ倒す。
        Confidentiality = req.Confidentiality ?? string.Empty,
        Purpose = ToProtoPurpose(req.Purpose),
    };

    public static Pb.EmbedResponse ToProto(EmbedApiResponse resp)
    {
        var proto = new Pb.EmbedResponse
        {
            Dimensions = resp.Dimensions,
            Model = resp.Model,
            Collection = resp.Collection,
            Embedded = resp.Embedded,
            Endpoint = resp.Endpoint ?? string.Empty,
            RoutingReason = resp.RoutingReason ?? string.Empty,
            Retryable = resp.Retryable,
        };
        proto.Vector.AddRange(resp.Vector);
        return proto;
    }

    public static EmbedApiResponse ToDto(Pb.EmbedResponse resp) => new(
        Vector: [.. resp.Vector],
        Dimensions: resp.Dimensions,
        Model: resp.Model,
        Collection: resp.Collection,
        Embedded: resp.Embedded,
        // 空文字は REST の null（未設定）へ戻す。REST 応答の JSON でも null で届く項目である。
        Endpoint: string.IsNullOrEmpty(resp.Endpoint) ? null : resp.Endpoint,
        RoutingReason: string.IsNullOrEmpty(resp.RoutingReason) ? null : resp.RoutingReason,
        Retryable: resp.Retryable);
}
