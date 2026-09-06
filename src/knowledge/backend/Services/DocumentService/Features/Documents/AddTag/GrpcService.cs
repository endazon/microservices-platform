using FluentValidation;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.AddTag;

// FR-05, FR-18, NFR-09, NFR-16, SC-03, SC-05, SC-09, ADR-0004, ADR-0029, ADR-0030, ADR-0036 D-07,
// ADR-0063 決定 1〜3, ADR-0065 決定 2, ADR-0075, ADR-0080, 計画 ADR-0086 決定 1・3・5,
// [[IADR-0044]], [[IADR-0364]], [[IADR-0371]], [[IADR-0379]], [[IADR-0398]], [[IADR-0401]],
// [[IADR-0402]], [[IADR-0410]] (#1255): タグ反映の **east-west gRPC 面**。
//
// 🔴 **本体は持たない。** `AddDocumentTagUseCase` を呼ぶだけであり、REST の
// `POST /documents/{id}/tags` と**同じ関数**を通る（判定器を 2 つにしない）。
// 入力検証も**同じ `IValidator<AddDocumentTagRequest>`** を引く —— 器（ProblemDetails か
// gRPC status か）だけが輸送ごとに違う。
//
// 🔴 **ServiceCaller を要求する。** 利用者のトークンは（管理者であっても）通らない ——
//   通すと呼び出し先が「利用者が直接呼んだ」と「サービスが利用者のために呼んだ」を区別できず
//   confused deputy が成立する（[[IADR-0379]] 決定 4）。これを機械で守るのは
//   `GrpcDocumentTagWriteTests` の「管理者トークンでも PERMISSION_DENIED」1 本である。
//   REST 側の `tagReflection` group は `RequireAuthorization()`（認証のみ）なので、
//   この面は現状より**狭い**（[[IADR-0401]] 決定 1 / [[IADR-0402]] 決定 3 と同じ向き）。
//   **REST 側は変えない。**
//
// 🔴 **判定の位置を動かさない。** 承認者の身元（所有者束縛）と管理者ロールは**本文で運ばれる**が
//   （計画 `ADR-0086` 決定 1）、**判定するのは本サービスである**（[[IADR-0044]] の最終防衛線）。
//   呼び出し元の判定結果を受け取る口はこの面に無い。
//
// 🔴 **拒否は `NOT_WRITABLE` の応答であって `PERMISSION_DENIED` ではない。**
//   REST は「所有者でも管理者でもない」と「文書が無い」を**同じ 404** で返す。
//   status で割ると、割り方そのものが文書の実在を漏らす。**`PERMISSION_DENIED` は
//   s2s の門だけが返す。**
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class DocumentTagWriteGrpcService(
    AddDocumentTagUseCase tags,
    IValidator<AddDocumentTagRequest> validator)
    : DocumentTagWrite.DocumentTagWriteBase
{
    public override async Task<AddTagResponse> AddTag(
        AddTagRequest request, ServerCallContext context)
    {
        // 🔴 **不正な GUID は「書けない」ではなく要求の誤りである**（REST では route 制約 `{id:guid}`
        // が弾く）。`NOT_WRITABLE` に畳むと、呼び出し元の綴り誤りが「その文書は書けない」に化ける。
        if (!Guid.TryParse(request.DocumentId, out var documentId))
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "document_id は GUID である必要があります。"));

        // 🔴 **承認者が分からない要求は拒否である。** deny（`NOT_WRITABLE`）へ畳まない ——
        // 畳むと呼び出し元の配線誤り（文脈を積み忘れた）が「承認できない文書だった」に化け、
        // **故障が「該当なし」に見える**。
        if (string.IsNullOrWhiteSpace(request.UserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id は必須です。"));

        // 🔴 **検証は取得・認可より前**（REST と同じ位置。後ろへ動かすと 404 に化ける）。
        // 規則は `AddDocumentTagValidator` が持つ 1 つだけである。
        var gate = validator.Validate(new AddDocumentTagRequest(request.TagName));
        if (!gate.IsValid)
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, gate.Errors[0].ErrorMessage));

        var outcome = await tags.ExecuteAsync(
            documentId,
            request.TagName,
            request.UserId,
            // 🔴 **ロールは `user_attributes` ではなく `user_roles` から読む**（proto の 🔴 を参照）。
            // ABAC 属性とロール判定は別の経路である（計画 `07_abac-attribute-model` §利用者属性）。
            request.UserRoles.Contains(PlatformAuthPolicies.AdminRole),
            context.CancellationToken);

        return new AddTagResponse { Result = ToProto(outcome.Status) };
    }

    // REST の状態コードと 1:1（200 / 400 / 404）。
    // 🔴 **`UNSPECIFIED` を返す枝を作らない** —— 既定値が線上へ出ると、呼び出し元は
    // それを `Unavailable` として扱う（成功に化けさせないための既定である）。
    internal static TagWriteResult ToProto(AddDocumentTagStatus status) => status switch
    {
        AddDocumentTagStatus.Applied => TagWriteResult.Applied,
        AddDocumentTagStatus.UnknownTag => TagWriteResult.UnknownTag,
        _ => TagWriteResult.NotWritable,
    };
}
