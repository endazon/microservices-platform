using Grpc.Core;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using Knowledge.Contracts.Grpc.Ingestion.V1;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace IngestionService.Features.Indexing.GetIndexStatuses;

// FR-06, FR-02, SC-05, NFR-09, NFR-16, ADR-0029, ADR-0075, ADR-0127, [[IADR-0009]], [[IADR-0379]], [[IADR-0401]],
// [[IADR-0536]], [[IADR-0565]] (#1993): 文書の**索引の状態**を文書 ID の集合から返す east-west gRPC 面。
//
// 🔴 **ServiceCaller を要求する**（[[IADR-0379]] 決定 4。`platform-service`）。利用者のトークンでは通らない。
//
// 🔴 **利用者の文脈を受け取らない。ABAC は呼び出し側（BFF）で済んでいる**（[[IADR-0565]] 決定 3）。
// 呼び出し元は文書管理の一覧の 1 ページ分の、ABAC を通った ID だけを渡す。面が返すのは渡された ID の状態だけで、
// 表題・属性・件数・点の中身は返さない（呼び出し元が要らないものを面へ出さない。[[IADR-0401]] 決定 2）。
//
// 🔴 **「引けなかった」を状態で表さない**（[[IADR-0401]] 決定 5）。索引（Qdrant）が引けなければ `UNAVAILABLE` であり、
// 「未索引」へ畳まない —— 畳むと索引の障害が「全部未索引」に見える。詳細（Qdrant の応答）は status に載せない。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class DocumentIndexStatusGrpcService(
    IIngestionVectorStore store,
    ILogger<DocumentIndexStatusGrpcService> logger)
    : DocumentIndexStatus.DocumentIndexStatusBase
{
    public override async Task<GetIndexStatusesResponse> GetStatuses(
        GetIndexStatusesRequest request, ServerCallContext context)
    {
        var ids = Parse(request);
        var response = new GetIndexStatusesResponse();
        if (ids.Count == 0)
            return response;

        DocumentIndexPresence presence;
        try
        {
            presence = await store.GetIndexPresenceAsync(ids, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Failed to read index presence for {Count} documents from the search index; answering UNAVAILABLE",
                ids.Count);
            throw new RpcException(new Status(StatusCode.Unavailable, "search index is unavailable"));
        }

        foreach (var id in ids)
        {
            response.States.Add(new DocumentIndexState
            {
                DocumentId = id.ToString(),
                Status = ToProto(DocumentIndexStatusRules.Classify(id, presence)),
            });
        }
        return response;
    }

    // [[IADR-0565]] 決定 2: 上限超・UUID でない ID は INVALID_ARGUMENT（索引を呼ばない）。重複は最初の位置に 1 つ。
    internal static IReadOnlyList<Guid> Parse(GetIndexStatusesRequest request)
    {
        if (request.DocumentIds.Count > DocumentIndexStatusRules.MaxDocuments)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"document_ids accepts at most {DocumentIndexStatusRules.MaxDocuments} ids"));

        var seen = new HashSet<Guid>();
        var ids = new List<Guid>(request.DocumentIds.Count);
        foreach (var raw in request.DocumentIds)
        {
            if (!Guid.TryParse(raw, out var id))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "document_ids must be UUIDs"));
            if (seen.Add(id))
                ids.Add(id);
        }
        return ids;
    }

    internal static IndexStatus ToProto(IndexState state) => state switch
    {
        IndexState.Indexed => IndexStatus.Indexed,
        IndexState.EmbeddingPending => IndexStatus.EmbeddingPending,
        IndexState.NotIndexed => IndexStatus.NotIndexed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}
