using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace RetrievalService.Features.Search.Hybrid;

// FR-03, FR-04, FR-05, FR-07, FR-17, NFR-02, NFR-09, NFR-16, UC-01, UC-02, UC-10, SC-01, SC-08,
// ADR-0004, ADR-0029, ADR-0034 決定 1, ADR-0035 決定 2, ADR-0075, 計画 ADR-0086 決定 1,
// ADR-0087 決定 2, ADR-0089 決定 1, [[IADR-0009]], [[IADR-0151]], [[IADR-0379]], [[IADR-0401]],
// [[IADR-0410]], [[IADR-0415]], [[IADR-0416]], [[IADR-0417]], [[IADR-0426]] (#1255):
// ハイブリッド検索の **east-west gRPC 面**。
//
// 🔴 **本体は持たない。** `SearchEndpoint.ExecuteAsync` を呼ぶだけであり、
// REST `POST /search` と**同じ関数**を通る（検索を 2 つにしない）。
//
// 🔴 **利用者文脈を本文で受け、スコープは自分で解決する**（`ADR-0086` 決定 1 / [[IADR-0416]]）。
// **呼び出し元が解決したスコープを受ける口は開かない**（[[IADR-0410]]）——
// 開くと、そこへ到達できる誰もが任意の scope を主張できる。
//
// 🔴 **受け取った利用者文脈は段まで引数で運ぶ**（[[IADR-0426]] 決定 2）。
// 二段検索の近傍展開が器（`IHttpContextAccessor`）から拾うと、この面では
// **呼び出し元サービスの s2s 主体**が ABAC の主体に化ける —— 例外は 1 つも起きない。
//
// 🔴 **ServiceCaller を要求する。** REST の受け口は realm の認証済み主体なら通る
// （[[IADR-0418]]）が、gRPC 面は [[IADR-0379]] 決定 4 に従い `platform-service` を要求する ——
// **権限が狭まる向き**である。**REST の口は残す**（並走中の正は REST。`ADR-0089` 決定 1）。
//
// 🔴 **「該当が無い」と「権限が無い」を区別させない**（[[IADR-0009]] / [[IADR-0151]] 決定 5）——
// どちらも**空の並び**で返る。引けなかったのは gRPC status である。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class DocumentSearchGrpcService(
    IHybridSearchService search, ISearchAccessResolver access)
    : Pb.DocumentSearch.DocumentSearchBase
{
    public override async Task<Pb.SearchResponse> Search(
        Pb.SearchRequest request, ServerCallContext context)
    {
        // 🔴 **利用者が分からないのは要求の誤りである。** deny へ畳むと、
        // 呼び出し元の配線誤りが「該当が 1 件も無い」と見分けられなくなる
        // （呼び出し元は資格情報が無ければ**呼ばない**）。`AttributeValues` と同じ姿勢。
        if (string.IsNullOrWhiteSpace(request.User?.UserId))
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "user.user_id は必須である（利用者文脈が無い呼び出しは受けない）。"));

        var response = new Pb.SearchResponse();
        if (string.IsNullOrWhiteSpace(request.Query))
            return response;

        // 🔴 **権限の根拠は自分で引く。** 本文が運ぶのは判定の**入力**であって結果ではない。
        var authoritative = await access.ResolveForUserAsync(
            request.User.UserId, request.User.UserAttributes, context.CancellationToken);

        // 🔴 **絞り込みは別項目で運ばれている**（`narrow_to`）。権限とは混ぜない。
        // **交差の規則は共有点 1 つだけ**（[[IADR-0415]] / #1340）。
        var effective = ScopeNarrowing.Resolve(authoritative, ToNarrowing(request.NarrowTo));

        var results = await SearchEndpoint.ExecuteAsync(
            search, ToDtoRequest(request), effective,
            // 🔴 **転送できる利用者の資格情報は無い**（利用者の JWT はこの面を通らない）。
            // `SearchUserContext.FromBody` がそれを型で表す。
            SearchUserContext.FromBody(request.User.UserId, request.User.UserAttributes),
            context.CancellationToken);

        response.Results.AddRange(results.Select(ToProto));
        return response;
    }

    // 🔴 **`top_k` の proto3 の未指定は `0` であり、DTO の既定は `10` である。**
    // 値を書き写さず、**位置引数を省いて DTO の既定へ委ねる** ——
    // 写すと、DTO 側の既定を変えた人が本ファイルを直し忘れる。
    private static SearchRequest ToDtoRequest(Pb.SearchRequest request)
        => request.TopK > 0
            ? new SearchRequest(request.Query, request.TopK)
            : new SearchRequest(request.Query);

    // 空の指定は「絞らない」である（REST 側の `null` と同義。判定の枝を増やさない）。
    private static Dictionary<string, List<string>>? ToNarrowing(
        IDictionary<string, Pb.NarrowTo> narrowTo)
        => narrowTo is { Count: > 0 }
            ? narrowTo.ToDictionary(kv => kv.Key, kv => kv.Value.Values.ToList(), StringComparer.OrdinalIgnoreCase)
            : null;

    // 🔴 **既定に頼らず全項目を明示的に写す**（`docs/api/east-west-grpc.md` §proto3 の「未指定」を写す）。
    // とりわけ `has_body` は DTO の既定が `true`・proto3 の既定が `false` で**向きが逆**であり、
    // 写し忘れは例外を 1 つも起こさずに**全件を「本文なし」へ**倒す。
    private static Pb.SearchResult ToProto(SearchResultDto dto)
    {
        var result = new Pb.SearchResult
        {
            ChunkId = dto.ChunkId.ToString(),
            DocumentId = dto.DocumentId.ToString(),
            DocumentTitle = dto.DocumentTitle,
            Text = dto.Text,
            Score = dto.Score,
            HasBody = dto.HasBody,
        };

        // 🔴 未設定（null）と空文字は別物である（presence で運ぶ）。
        if (dto.MarkdownUri is not null)
            result.MarkdownUri = dto.MarkdownUri;

        // 🔴 未設定は「まだ索引に無い」である。既定値で埋めない（[[IADR-0149]] 決定 3）。
        if (dto.UpdatedAt is { } updatedAt)
            result.UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt);

        foreach (var (key, value) in dto.Attributes)
            result.Attributes[key] = value;
        result.Tags.AddRange(dto.Tags);

        return result;
    }
}
