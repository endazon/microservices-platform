using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace LlmGateway.Features.Embeddings.Embed;

// FR-02, FR-03, FR-05, NFR-09, NFR-16, ADR-0013, ADR-0016, ADR-0017, ADR-0029, ADR-0075, IADR-0379,
// IADR-0397 (#1255): 埋め込み生成の **gRPC 面**。
//
// REST の `POST /embed`（EmbeddingEndpoints）と**同じ判定器**（EmbedUseCase）を呼ぶ —— 判定器を 2 つにしない。
// REST と gRPC は並走し、**並走中の正は REST** である（IADR-0379 決定 5）。
//
// 🔴 **ServiceCaller を要求する。** REST の `/embed` はサービス間呼び出し専用として認可を掛けていない
// （メッシュの mTLS が第一防御）。gRPC の面では**呼び出し側サービス自身の資格情報**（client credentials の
// JWT・`platform-service` ロール）を要求し、**利用者のトークンでは通さない** —— 通すと「利用者が直接
// 呼んだ」と区別できず confused deputy になる（IADR-0379 決定 4）。この面は現行の REST より**強い**。
//
// 🔴 縮退は RpcException にしない。越境拒否・プロバイダ未登録・次元不整合・上流不調はすべて
// `embedded=false` の**応答**で返す（REST の 200 ＋ Embedded=false と同値）。RpcException になるのは
// s2s の面（UNAUTHENTICATED / PERMISSION_DENIED）と輸送不達（UNAVAILABLE）だけである。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class LlmEmbeddingGrpcService(EmbedUseCase useCase) : Pb.LlmEmbedding.LlmEmbeddingBase
{
    public override async Task<Pb.EmbedResponse> Embed(Pb.EmbedRequest request, ServerCallContext context)
    {
        // 🔴 proto3 に null は無い（IADR-0397 決定 3）。EMBED_PURPOSE_UNSPECIFIED（既定 0）は
        // REST の DTO 既定（EmbedPurpose.Index）へ**明示的に写す**。写し漏れは「用途が Index として
        // routing されない」形で静かに壊れる（例外にならない）ため、T-S-07 が固定する。
        // Confidentiality の空文字は SensitivityClasses.Parse が restricted（安全側）へ倒すので写し不要。
        var dto = new EmbedApiRequest(
            request.Text,
            Confidentiality: request.Confidentiality,
            Purpose: LlmGrpcMapping.ToDtoPurpose(request.Purpose));

        var result = await useCase.ExecuteAsync(dto, context.CancellationToken);
        return LlmGrpcMapping.ToProto(result);
    }
}
