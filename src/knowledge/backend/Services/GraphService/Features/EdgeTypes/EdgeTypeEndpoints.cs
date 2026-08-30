using GraphService.Features.EdgeTypes.Catalog;
using GraphService.Features.EdgeTypes.Create;
using GraphService.Features.EdgeTypes.Delete;
using GraphService.Features.EdgeTypes.List;
using GraphService.Features.EdgeTypes.Rename;
using GraphService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.EdgeTypes;

// FR-17, SC-09, SC-10, ADR-0033 決定 3・9, IADR-0242 決定 7・8: 辺の型辞書の合成点。
//
// ADR-0065 決定 2: 各ユースケースの実体は `Features/EdgeTypes/<操作>/` に居る。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— 2 つの route group
// （読みの下限・書きの下限）、409 の形、同名判定、使用件数の数え方。
//
// **DocumentService のタグ辞書と同じ契約**（重複は 409 / 改名は識別子不変 / 参照ありは削除拒否 ＋
// 使用件数 / 一覧と削除で同一の母集合）だが、**共有ヘルパは作らない**。共通化できるのは応答形の
// 数行だけで、数え方が根本的に違う（あちらは jsonb をメモリで集計、こちらは外部キーの COUNT）。
// 計画外の抽象化を避ける。
public static class EdgeTypeEndpoints
{
    public static IEndpointRouteBuilder MapEdgeTypeEndpoints(this IEndpointRouteBuilder app)
    {
        // 読みは運用者・管理者（SC-10 の型別使用件数の表示に要る）。
        var read = app.MapGroup("/graph/edge-types").WithTags("EdgeTypes")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole));

        // 書きは管理者のみ（SC-09 の値集合の管理）。
        var write = app.MapGroup("/graph/edge-types").WithTags("EdgeTypes")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        ListEdgeTypesEndpoint.Map(read);
        // 🔴 カタログだけは group に属さない（ロール要求を持たない別の口である。下の注記を参照）。
        ListEdgeTypeCatalogEndpoint.Map(app);
        CreateEdgeTypeEndpoint.Map(write);
        RenameEdgeTypeEndpoint.Map(write);
        DeleteEdgeTypeEndpoint.Map(write);

        return app;
    }

    internal static IResult Conflict(string name)
        => Results.Conflict(new { error = "edge_type_exists", message = $"型「{name}」は既に辞書にあります。" });

    // 正規化後の名前で、大文字小文字を無視して突き合わせる。
    // DB の一意索引は素の `name` に対するものなので、**サービス層のこの検査が先に効く**。
    internal static async Task<bool> ExistsAsync(GraphDbContext db, string name, CancellationToken ct)
        => await db.EdgeTypes.AnyAsync(t => t.Name.ToLower() == name.ToLower(), ct);

    // FR-17, SC-09, SC-10: その型を参照している辺の数。
    // **外部キーの列を数えるだけ**（`ix_edges_type` が効く）。一覧・改名・削除がこの 1 つを共有する。
    internal static async Task<int> UsageOfAsync(GraphDbContext db, Guid typeId, CancellationToken ct)
        => await db.Edges.CountAsync(e => e.EdgeTypeId == typeId, ct);
}
