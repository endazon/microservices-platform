using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc.Retrieval.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Observability;
using RetrievalService.Domain;
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
// **権限が狭まる向き**である。**REST の口は残す**（north-south の受け口でもある。［2026-10-10 / #1255・[[IADR-0533]]］east-west の REST 呼び出し元は撤去した）。
//
// 🔴 **「候補が無い」と「権限が無い」を区別させない**（[[IADR-0151]] 決定 5）——
// どちらも**空の配列**で返る。引けなかったのは gRPC status である。
//
// ［2026-09-27 追記 / #1636］🔴 **本文の `user` を信じるのは、許可集合（`AttributeValuesRelayOptions`。
//   既定 `bff` だけ）の機械クライアントが運んだときだけである**（[[IADR-0417]] 追記 1）。
//   `ServiceCaller`（`platform-service`）だけでは信じない —— そのロールは 11 のサービスアカウント
//   （別プロジェクトのものを含む）が持ち、どれもが任意の利用者を名乗ってその利用者のスコープの属性の値を引けた。
//   それ以外が `user` を付けたら PERMISSION_DENIED。`user` の無い要求は従来どおり誰にも INVALID_ARGUMENT。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class AttributeValuesGrpcService(
    IVectorStore store,
    ISearchAccessResolver access,
    FusedCollections fused,
    IOptions<AttributeValuesRelayOptions> relay,
    ILogger<AttributeValuesGrpcService> logger)
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

        // 🔴 FR-04, FR-05, NFR-09, 計画 ADR-0086 決定 1・§結果 (#1636): **利用者文脈を運べる呼び出し元か。**
        // 空の key の早期 return より前に置く（信頼しない呼び出し元に「通る形」を 1 つも残さない）。
        EnsureTrustedRelay(context);

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
            await AttributeValuesEndpoint.ListAsync(store, fused, request.Key, scope, context.CancellationToken));
        return response;
    }

    // FR-04, FR-05, NFR-09, 計画 ADR-0086 決定 1・§結果, [[IADR-0417]] 追記 1 (#1636):
    // 🔴 本文の利用者文脈を信じてよいのは、それを運ぶのが**利用者の権限で動く中継者として許可集合に載った
    //   機械クライアント**だからである（ADR-0086 §結果が受け入れた依存の範囲）。
    private void EnsureTrustedRelay(ServerCallContext context)
    {
        var caller = context.GetHttpContext().User;
        if (relay.Value.TrustsUserContextFrom(caller))
            return;

        // 基数は realm の機密クライアント数で閉じる（利用者識別子・属性・key は載せない）。
        logger.LogWarning(
            "AttributeValues rejected a user context from a caller that is not a trusted relay (client={ClientId}). "
            + "Trusted relays are configured under {Section}:{Key}.",
            MachinePrincipal.ClientIdOf(caller) ?? "(unknown)", AttributeValuesRelayOptions.SectionName,
            TrustedUserContextRelay.ClientsKey);
        throw new RpcException(new Status(StatusCode.PermissionDenied,
            "この呼び出し元は利用者文脈（user）を運べません。"));
    }

    // 空の指定は「絞らない」である（REST 側の `null` と同義。判定の枝を増やさない）。
    private static Dictionary<string, List<string>>? ToNarrowing(
        IDictionary<string, NarrowTo> narrowTo)
        => narrowTo is { Count: > 0 }
            ? narrowTo.ToDictionary(kv => kv.Key, kv => kv.Value.Values.ToList(), StringComparer.OrdinalIgnoreCase)
            : null;
}
