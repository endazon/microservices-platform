using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc.Retrieval.V1;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Features.Search.AttributeValues;

// FR-04, FR-05, NFR-09, NFR-16, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0043, ADR-0075,
// 計画 ADR-0086 決定 1, [[IADR-0151]], [[IADR-0253]], [[IADR-0379]], [[IADR-0401]],
// [[IADR-0410]], [[IADR-0415]], [[IADR-0416]], [[IADR-0417]] (#1255):
// 権限内属性値の照会の **east-west gRPC 面**。
//
// 🔴 **本体は持たない。** `AttributeValuesEndpoint.ListAsync` を呼ぶだけであり、
// REST `POST /search/attribute-values` と**同じ関数**を通る（問い合わせを 2 つにしない）。
//
// 🔴 **利用者文脈を本文で受け、スコープは自分で解決する**（`ADR-0086` 決定 1 / [[IADR-0416]]）。
// **呼び出し元が解決したスコープを受ける口は開かない**（[[IADR-0410]]）——
// 開くと、そこへ到達できる誰もが任意の scope を主張できる。
//
// 🔴 **ServiceCaller を要求する。** REST の受け口は認可を持たない（#1318 欠陥 B）が、
// gRPC 面は [[IADR-0379]] 決定 4 に従い `platform-service` を要求する ——
// **権限が狭まる向き**である。**REST の口は残す**（並走中の正は REST）。
//
// 🔴 **「候補が無い」と「権限が無い」を区別させない**（[[IADR-0151]] 決定 5）——
// どちらも**空の配列**で返る。引けなかったのは gRPC status である。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class AttributeValuesGrpcService(
    IVectorStore store, ISearchAccessResolver access)
    : Knowledge.Contracts.Grpc.Retrieval.V1.AttributeValues.AttributeValuesBase
{
    public override async Task<ListValuesResponse> ListValues(
        ListValuesRequest request, ServerCallContext context)
    {
        // 🔴 **利用者が分からないのは要求の誤りである。** deny へ畳むと、
        // 呼び出し元の配線誤りが「候補が 1 件も無い」と見分けられなくなる
        // （呼び出し元は資格情報が無ければ**呼ばない**）。`graph_neighbors.proto` と同じ姿勢。
        if (string.IsNullOrWhiteSpace(request.User?.UserId))
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "user.user_id は必須である（利用者文脈が無い呼び出しは受けない）。"));

        var response = new ListValuesResponse();
        if (string.IsNullOrWhiteSpace(request.Key))
            return response;

        // 🔴 **権限の根拠は自分で引く。** 本文が運ぶのは判定の**入力**であって結果ではない。
        var authoritative = await access.ResolveForUserAsync(
            request.User.UserId,
            request.User.UserAttributes,
            context.CancellationToken);

        // 🔴 **絞り込みは別項目で運ばれている**（`narrow_to`）。権限とは混ぜない。
        // 🔴 **口を増やさない** —— 呼び出し元側（AiAnalysis のデータ範囲）と**同じ関数**である。
        var scope = ScopeNarrowing.Resolve(authoritative, ToNarrowing(request.NarrowTo));
        if (!scope.GrantsAccess)
            return response;

        response.Values.AddRange(
            await AttributeValuesEndpoint.ListAsync(store, request.Key, scope, context.CancellationToken));
        return response;
    }

    // 空の指定は「絞らない」である（REST 側の `null` と同義。判定の枝を増やさない）。
    private static Dictionary<string, List<string>>? ToNarrowing(
        IDictionary<string, NarrowTo> narrowTo)
        => narrowTo is { Count: > 0 }
            ? narrowTo.ToDictionary(kv => kv.Key, kv => kv.Value.Values.ToList(), StringComparer.OrdinalIgnoreCase)
            : null;
}
