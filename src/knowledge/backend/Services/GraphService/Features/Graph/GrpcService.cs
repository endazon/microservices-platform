using GraphService.Domain.Ports;
using GraphService.Features.EdgeTypes.Catalog;
using GraphService.Features.Graph.Neighbors;
using GraphService.Infrastructure.Persistence;
using Grpc.Core;
using Knowledge.Contracts.Grpc.Graph.V1;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Kernel;

namespace GraphService.Features.Graph;

// FR-04, FR-05, FR-17, NFR-09, NFR-16, UC-10, SC-18, ADR-0004, ADR-0029, ADR-0034 決定 1・2,
// ADR-0035, ADR-0049, ADR-0065 決定 2, ADR-0075, 計画 ADR-0086 決定 1・3・5, [[IADR-0242]],
// [[IADR-0272]], [[IADR-0379]], [[IADR-0397]], [[IADR-0400]], [[IADR-0401]], [[IADR-0402]],
// [[IADR-0408]], [[IADR-0410]] (#1255): 近傍展開が使う読み取り 2 口の **east-west gRPC 面**。
//
// 🔴 **本体は持たない。** `ExpandNeighborsUseCase` と `ListEdgeTypeCatalogEndpoint.LoadCatalogAsync`
// を呼ぶだけであり、REST の 2 口と**同じ関数**を通る（判定器を 2 つにしない）。
// ここに在るのは「輸送の言葉へ写すこと」だけである。
//
// 🔴 **ServiceCaller を要求する。** 利用者のトークンは（管理者であっても）通らない ——
//   通すと呼び出し先が「利用者が直接呼んだ」と「サービスが利用者のために呼んだ」を区別できず
//   confused deputy が成立する（[[IADR-0379]] 決定 4 / 計画 `ADR-0086` 決定 1）。
//   これを機械で守るのは `GrpcGraphNeighborsTests` の「管理者トークンでも PERMISSION_DENIED」1 本である。
//
// 🔴 **判定の位置を動かさない**（`ADR-0034` 決定 1）。本文で運ばれた利用者文脈から
//   **本サービスが** `AuthzScope/Resolve` を呼んでスコープを解決する。
//   **呼び出し元が解決したスコープを受け取る口はこの面に無い** —— `ADR-0086` の
//   2026-09-07 追記が「判定結果を運ぶ形は採らない」と明示的に退けた形である。
//
// 🔴 **辺の型の重みは利用者文脈を持たない。** REST の `/graph/edge-types/catalog` は
//   `RequireAuthorization()` の門を持つが**主体を 1 バイトも読まない**（描画用の語彙であり
//   ABAC で絞られていない）。転送トークンは認証の門にしか使われていなかったので、
//   `ServiceCaller` がそれを**より狭く**置き換える（[[IADR-0401]] 決定 1 と同じ向き）。
//
// 🔴 **「見えない」は応答であって status ではない。** `found=false` を返し `NOT_FOUND` を投げない。
//   REST の 404 は 1 種類しかない（`ADR-0034` 決定 2 の存在秘匿）ので、status で割ると
//   割り方そのものが存在の有無を漏らす。
//
// ADR-0065 決定 2 の適用: 実体は `Features/<操作>/` に置くのが原則だが、**本クラスは 2 操作
// （近傍展開・辺の型の重み）が共有する 1 つの面**なので合成点と同じ階層に置く
// （`GraphEndpoints` と同じ扱い）。**各操作フォルダへ複写しない。**
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
internal sealed class GraphNeighborsGrpcService(
    ExpandNeighborsUseCase neighbors,
    GraphDbContext db)
    : Knowledge.Contracts.Grpc.Graph.V1.GraphNeighbors.GraphNeighborsBase
{
    public override async Task<ExpandNeighborsResponse> ExpandNeighbors(
        ExpandNeighborsRequest request, ServerCallContext context)
    {
        // 🔴 **不正な GUID は「見えない」ではなく要求の誤りである**（REST では route 制約 `{documentId:guid}`
        // が 404 を返す前に弾く）。`found=false` に畳むと、呼び出し元の綴り誤りが
        // 「その文書は見えない」に化けて気付けなくなる。
        if (!Guid.TryParse(request.DocumentId, out var documentId))
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "document_id は GUID である必要があります。"));

        var user = ToUserContext(request.User);

        var outcome = await neighbors.ExecuteAsync(
            documentId,
            // 🔴 proto3 に null は無い。`hops` の未指定は presence で運ばれる ——
            // `Has` を読まずに素の値を読むと、未指定が `0` になって上限検証の意味が変わる。
            request.HasHops ? request.Hops : null,
            // 間引きの基準と辺の型フィルタは**この面に無い**（呼び出し元が使っていない。
            // 面へ出さないことで、使われない引数が仕様として固まるのを避ける）。
            by: null,
            types: null,
            user,
            context.CancellationToken);

        if (outcome.IsFailure)
        {
            // 検証の失敗（hops 上限超過）は REST の 400 と同値である。
            var status = outcome.Error.Kind == ErrorKind.Validation
                ? StatusCode.InvalidArgument
                : StatusCode.Internal;
            throw new RpcException(new Status(status, outcome.Error.Message));
        }

        var response = new ExpandNeighborsResponse { Found = outcome.Value.Found };
        if (outcome.Value.View is { } view)
            response.Edges.AddRange(view.Edges.Select(e => new NeighborEdge
            {
                Id = e.Id.ToString(),
                SourceDocumentId = e.SourceDocumentId.ToString(),
                TargetDocumentId = e.TargetDocumentId.ToString(),
                EdgeTypeId = e.EdgeTypeId.ToString(),
            }));
        return response;
    }

    public override async Task<ListEdgeTypeWeightsResponse> ListEdgeTypeWeights(
        ListEdgeTypeWeightsRequest request, ServerCallContext context)
    {
        var catalog = await ListEdgeTypeCatalogEndpoint.LoadCatalogAsync(db, context.CancellationToken);
        var response = new ListEdgeTypeWeightsResponse();
        response.Weights.AddRange(catalog.Select(i => new EdgeTypeWeight
        {
            EdgeTypeId = i.Id.ToString(),
            Weight = i.Weight,
        }));
        return response;
    }

    // 🔴 **本文の利用者文脈を判定の入力へ写す**（計画 `ADR-0086` 決定 1）。
    //
    // 🔴 **`user_id` の空は deny へ畳まない。** 畳むと呼び出し元の配線誤り（文脈を積み忘れた）が
    // 「グラフには何も無い」と見分けられなくなる —— 呼び出し元は資格情報が無ければ**呼ばない**
    // 設計なので、空で届くことは要求の誤りである。**故障を「該当なし」に化けさせない。**
    //
    // 🔴 **`IsAuthenticated` は `true` で立てる。** 本文で身元が主張されており、s2s の門は
    // 既に通っている（`ServiceCaller`）。**主張をそのまま評価する構造は `ADR-0086` 決定 4 が
    // 受け入れたリスクとして記録した**ものであり、本実装が新設したのではない
    // （`AuthzScope/Resolve` は従前から呼び出し元の主張を評価している）。
    internal static GraphUserContext ToUserContext(UserContext? user)
    {
        if (user is null || string.IsNullOrWhiteSpace(user.UserId))
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "user.user_id は必須です。"));

        return new GraphUserContext(
            user.UserId, new Dictionary<string, string>(user.UserAttributes), true);
    }
}
