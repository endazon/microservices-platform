using Grpc.Core;
using Knowledge.Contracts.Grpc;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents;

// FR-05, FR-06, UC-03, SC-03, SC-05, NFR-09, NFR-16, ADR-0002, ADR-0004, ADR-0029, ADR-0056,
// ADR-0065, ADR-0070, ADR-0075, [[IADR-0012]], [[IADR-0041]], [[IADR-0045]], [[IADR-0379]],
// [[IADR-0402]] (#1255), ADR-0119 決定 3 (#1614): 文書台帳の読み取り 4 口の **east-west gRPC 面**。
//
// 🔴 **本体は持たない。** 4 つとも `DocumentReadUseCase` を呼ぶだけであり、REST の 4 口と
// **同じ関数**を通る（判定器を 2 つにしない。[[IADR-0397]] / [[IADR-0400]] と同じ形）。
// ここに在るのは「輸送の言葉へ写すこと」だけである。
//
// 🔴 **ServiceCaller を要求する。** 利用者のトークンは（管理者であっても）通らない ——
//   通すと呼び出し先が「利用者が直接呼んだ」と区別できず confused deputy が成立する
//   （[[IADR-0379]] 決定 4）。これを機械で守るのは `GrpcDocumentReadTests` の
//   「管理者トークンでも PERMISSION_DENIED」1 本である。
//   REST 側の読み取り group は**ロールで塞いでいない**（SC-03 の一般利用者の閲覧）ので、
//   この面は現状より**狭い**（`AuthzScope/Resolve` を移したときと同じ向き）。
//
// ［2026-09-27 更新 / #1614］🔴 **主体は要求の `user` で決まる**（計画 ADR-0119 決定 3・ADR-0086 決定 1）。
//   在れば**その利用者**（呼び出し元サービスは利用者の代わりに読んでいる）、無ければ**呼び出し元サービス自身**
//   （機械の主体）。個人資料は所有者と共有先の利用者にだけ返り（`DocumentReadAccess`）、機械の主体には返らない
//   （ADR-0034 決定 9）。組織文書の内容の ABAC は引き続き BFF の `BffScopeResolver` が実施点である（#1615）。
//   🔴 `user.user_id` が空なら INVALID_ARGUMENT —— 「利用者が分からない」を機械の主体へ畳まない。
//
// 🔴 **不在は応答であって status ではない。** `found=false` を返し `NOT_FOUND` を投げない
//   （[[IADR-0401]] 決定 5 と同じ作法）。呼び出し元は「引けなかった」（`RpcException`）と
//   同じ縮退（404 秘匿 / 空一覧）へ落とすが、**それは呼び出し元の判断**であって
//   輸送の側で先取りしない。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class DocumentReadGrpcService(DocumentReadUseCase reads)
    : DocumentRead.DocumentReadBase
{
    public override async Task<ListDocumentsResponse> ListDocuments(
        ListDocumentsRequest request, ServerCallContext context)
    {
        var docs = await reads.ListAsync(PrincipalOf(request.User), context.CancellationToken);
        var resp = new ListDocumentsResponse();
        resp.Documents.AddRange(docs.Select(DocumentReadGrpcMapping.ToProto));
        return resp;
    }

    public override async Task<GetDocumentResponse> GetDocument(
        GetDocumentRequest request, ServerCallContext context)
    {
        // 🔴 **不正な GUID は「無い」ではなく要求の誤りである**（REST では route 制約 `{id:guid}` が
        // 404 を返す前に弾く）。`found=false` に畳むと、呼び出し元の綴り誤りが
        // 「その文書は存在しない」に化けて気付けなくなる。
        if (!Guid.TryParse(request.Id, out var id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "id は GUID である必要があります。"));

        var doc = await reads.GetAsync(PrincipalOf(request.User), id, context.CancellationToken);
        return doc is null
            ? new GetDocumentResponse { Found = false }
            : new GetDocumentResponse { Found = true, Document = DocumentReadGrpcMapping.ToProto(doc) };
    }

    public override async Task<ListVersionsResponse> ListVersions(
        ListVersionsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.DocumentId, out var id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "document_id は GUID である必要があります。"));

        var versions = await reads.ListVersionsAsync(PrincipalOf(request.User), id, context.CancellationToken);
        if (versions is null)
            return new ListVersionsResponse { Found = false };

        var resp = new ListVersionsResponse { Found = true };
        resp.Versions.AddRange(versions.Select(DocumentReadGrpcMapping.ToProto));
        return resp;
    }

    public override async Task<GetVersionResponse> GetVersion(
        GetVersionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.DocumentId, out var id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "document_id は GUID である必要があります。"));

        var snapshot = await reads.GetVersionAsync(PrincipalOf(request.User), id, request.Version,
            context.CancellationToken);
        return snapshot is null
            ? new GetVersionResponse { Found = false }
            : new GetVersionResponse { Found = true, Snapshot = DocumentReadGrpcMapping.ToProto(snapshot) };
    }

    // FR-19, 計画 ADR-0119 決定 3, ADR-0086 決定 1 (#1614): 本文の利用者文脈 → 主体。
    // 🔴 **主体の検証（`GrpcService` の外の `ServiceCaller`）を通った呼び出し元だけがここへ来る。**
    //   利用者文脈を信じてよいのは、それを運ぶのが realm の `platform-service` を持つサービスだからである
    //   （ADR-0086 決定 1。`DocumentTagWrite/AddTag` の `user_id` と同じ扱い）。
    internal static DocumentReadPrincipal PrincipalOf(UserContext? user)
    {
        if (user is null)
            return DocumentReadPrincipal.CallingService();
        if (string.IsNullOrWhiteSpace(user.UserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "user.user_id が空です。利用者文脈を運ばないなら user を省略してください。"));
        return DocumentReadPrincipal.RelayedUser(user.UserId);
    }
}
