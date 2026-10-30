using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace GraphService.Features.Graph.LinkCandidates;

// FR-17, SC-19, ADR-0034 決定 2・8・9, [[IADR-0655]] (#2343): **リンク作成の補完候補。**
//
// 🔴 **候補は作成者の read スコープ内に限る**（ADR-0034 決定 8。権限外の題名を補完に流すと、閲覧側を塞いでも
//   作成側から存在が漏れる）。絞り込みは辺の作成（`CreateGraphEdgeEndpoint`）と**同じスコープ・同じ述語**
//   （`AuthorizedNode.Authorize`）で行う —— 候補の集合は「終点として受け付けられる文書」と一致する。
// 🔴 **個人資料は呼び出し主体が所有者のものだけ**（共有されて読める他人の個人資料も出さない）。機械の主体には
//   個人資料を一切出さない（決定 9）。いずれも ABAC の述語の**後**に重ねる（狭める向きにしか働かない）。
// 🔴 **件数・総数・打ち切りの印を返さない**（決定 2）。応答は候補の配列だけである。
// 許可が無い主体には空を返す（404 にしない —— 補完は「無い」と「見えない」を区別しない）。
internal static class GraphLinkCandidatesEndpoint
{
    internal const int MinQueryLength = 2;
    internal const int MaxQueryLength = 200;
    internal const int DefaultLimit = 20;
    internal const int MaxLimit = 50;

    // 表題で一致したノードを走査する上限と 1 回の読み込み幅。打ち切ったことは返さない（件数を漏らさない）。
    internal const int ScanCap = 2_000;
    private const int BatchSize = 200;

    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/link-candidates", async (
            string? q,
            int? limit,
            IGraphAccessResolver accessResolver,
            GraphDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var term = q?.Trim() ?? string.Empty;
            var errors = new Dictionary<string, string[]>();
            if (term.Length is < MinQueryLength or > MaxQueryLength)
                errors["q"] = [$"q は {MinQueryLength}〜{MaxQueryLength} 文字で指定してください。"];
            if (limit is < 1 or > MaxLimit)
                errors["limit"] = [$"limit は 1〜{MaxLimit} で指定してください。"];
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var take = limit ?? DefaultLimit;
            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);
            if (!scope.Granted)
                return Results.Ok(Array.Empty<GraphLinkCandidateDto>());

            var caller = http.User.Identity?.Name;
            var machine = MachinePrincipal.IsMachine(http.User);
            var lowered = term.ToLowerInvariant();

            var found = new List<GraphLinkCandidateDto>(take);
            for (var skip = 0; skip < ScanCap && found.Count < take; skip += BatchSize)
            {
                var batch = await db.Documents.AsNoTracking()
                    .Where(d => d.Title.ToLower().Contains(lowered))
                    .OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.DocumentId)
                    .Skip(skip).Take(BatchSize)
                    .ToListAsync(ct);

                foreach (var authorized in AuthorizedNode.AuthorizeAll(batch, scope))
                {
                    var node = authorized.Node;
                    var isPrivateNote = GraphDocumentScope.IsPrivateNote(node.Attributes);
                    if (isPrivateNote && (machine || !IsOwnedBy(node.Attributes, caller)))
                        continue;

                    found.Add(new GraphLinkCandidateDto(node.DocumentId, node.Title, isPrivateNote));
                    if (found.Count == take)
                        break;
                }

                if (batch.Count < BatchSize)
                    break;
            }

            return Results.Ok(found);
        }).WithName("GetGraphLinkCandidates")
          .RequireAuthorization()
          .Produces<List<GraphLinkCandidateDto>>()
          .ProducesValidationProblem();
    }

    // 所有者は `owner` の属性と主体名の一致で決める。**大小も区別する**（Ordinal）—— DocumentService の
    // `DocumentBodyIntake.IsOwnedBy`（本文の書き込みの所有者判定）と同じ比較にそろえる（緩いほうへずらさない）。
    private static bool IsOwnedBy(IReadOnlyDictionary<string, string> attributes, string? caller)
        => !string.IsNullOrWhiteSpace(caller)
            && attributes.TryGetValue(PrivateNoteVisibility.OwnerKey, out var owner)
            && string.Equals(owner, caller, StringComparison.Ordinal);
}
