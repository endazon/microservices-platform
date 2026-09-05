using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Platform.Shared.Infrastructure.Foundation.Observability;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace LlmGateway.Features.Completions;

// FR-04, FR-11, NFR-02, NFR-09, NFR-16, ADR-0010, ADR-0025, ADR-0029, ADR-0044, ADR-0075, ADR-0076,
// IADR-0104, IADR-0378, IADR-0379, IADR-0397, IADR-0398 (#1255): テキスト生成の **gRPC 面**。
//
// REST の `POST /complete` / `POST /complete/stream` と**同じ判定器**（CompletionUseCase）を呼ぶ ——
// 判定器を 2 つにしない。REST と gRPC は並走し、**並走中の正は REST** である（IADR-0379 決定 5）。
//
// 🔴 **ServiceCaller を要求する。** REST の `/complete` 系はサービス間呼び出し専用として認可を掛けて
// いない（メッシュの mTLS が第一防御）。gRPC の面では**呼び出し側サービス自身の資格情報**
// （client credentials の JWT・`platform-service` ロール）を要求し、**利用者のトークンでは通さない**
// —— 通すと「利用者が直接呼んだ」と区別できず confused deputy になる（IADR-0379 決定 4）。
// この面は現行の REST より**強い**（緩めていない）。
//
// 🔴 **縮退は RpcException にしない**（IADR-0398 決定 5。埋め込みの呼び出し元側とは向きが逆である）。
// 越境拒否・プロバイダ未登録・上流不調はすべて `sent=false` の**応答**（一括）または
// `done=true, sent=false` の**メッセージ**（逐次）で返す —— REST が 500 を伝播させないのと同値である。
// RpcException になるのは s2s の面（UNAUTHENTICATED / PERMISSION_DENIED）・輸送不達（UNAVAILABLE）・
// 値域外の要求（INVALID_ARGUMENT）だけである。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class LlmCompletionGrpcService(CompletionUseCase useCase) : Pb.LlmCompletion.LlmCompletionBase
{
    public override async Task<Pb.CompleteResponse> Complete(
        Pb.CompleteRequest request, ServerCallContext context)
    {
        var dto = ToDtoOrThrow(request);
        var result = await useCase.ExecuteAsync(dto, IsSynthetic(context), context.CancellationToken);
        return LlmGrpcMapping.ToProto(result);
    }

    // 🔴 **サーバストリーミング**（IADR-0398 決定 1）。unary へ潰すと最初の delta が生成完了後にしか
    // 届かず、NFR-02 の SLI（初回トークン）が応答完了 p95 を測ることになる（ADR-0076 決定 5 が却下した形）。
    //
    // 🔴 **判定器が yield した 1 メッセージを、その場で WriteAsync する。**
    // ここで一旦リストへ溜めてから書くと、輸送を server-streaming にした意味が消える ——
    // gRPC は「サーバが書いた順に届く」ことは保証するが「サーバが早く書く」ことは保証しない。
    // これはコードの性質であり、GrpcCompleteStreamTests（最初の delta が done より前に到着する）
    // だけが守れる。
    public override async Task CompleteStream(
        Pb.CompleteRequest request,
        IServerStreamWriter<Pb.CompletionStreamEvent> responseStream,
        ServerCallContext context)
    {
        var dto = ToDtoOrThrow(request);
        await foreach (var ev in useCase.StreamAsync(dto, IsSynthetic(context), context.CancellationToken))
            await responseStream.WriteAsync(LlmGrpcMapping.ToProto(ev), context.CancellationToken);
    }

    // 🔴 proto3 に null は無い（IADR-0398 決定 4）。REST の既定値の写しは LlmGrpcMapping.ToDto が持つ
    // （max_tokens=0 → 4096。model / confidentiality / purpose の空文字は受け側が null と同じに扱う）。
    //
    // 負数だけはここで弾く。**REST には無い検証である** —— REST の DTO は 0 を「未指定」に使わないため
    // 0 未満を区別する必要が無かった。gRPC では 0 が「未指定」を担う以上、0 未満は意味を持たない。
    // 黙って 4096 へ倒すと「送った値と違う上限で課金された」ことに呼び出し元が気付けない。
    private static Platform.Shared.Contracts.Dtos.CompletionApiRequest ToDtoOrThrow(Pb.CompleteRequest request)
    {
        if (request.MaxTokens < 0)
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "max_tokens は 0（未指定＝既定 4096）以上でなければなりません。"));
        return LlmGrpcMapping.ToDto(request);
    }

    // NFR-02, ADR-0076 決定 4, IADR-0378, IADR-0398 決定 3: 合成監視の標識は**メタデータ**で運ぶ。
    // ASP.NET Core gRPC では `GetHttpContext().Request` が REST と同じ `HttpRequest` なので、
    // **判定は既存の単一情報源をそのまま呼ぶ**（定義を 2 つにしない）。
    private static bool IsSynthetic(ServerCallContext context) =>
        SyntheticTraffic.IsSyntheticInternalRequest(context.GetHttpContext().Request);
}
