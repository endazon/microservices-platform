using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.EdgeTypes;

// FR-17, SC-09, SC-10, ADR-0033 決定 3・9, IADR-0242 決定 7・8: 辺の型辞書。
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

        // FR-17, SC-09, SC-10: 使用件数つきの一覧。
        // **SC-10 の型別使用件数はこの 1 本が供給する。** 別経路を作ると数えの二重化が起きる。
        read.MapGet("/", async (GraphDbContext db, CancellationToken ct) =>
            Results.Ok(await LoadWithUsageAsync(db, ct)))
            .WithName("ListEdgeTypes").Produces<List<EdgeTypeDto>>();

        // FR-17, SC-18 (#962): **描画用カタログ。認証のみでロールを問わない。**
        //
        // 🔴 **上の一覧を一般利用者へ開けてはならない。** `UsageCount` は全辺を数えており
        // ABAC で絞られていない。ホップごと ABAC（IADR-0242）が個々のノード・辺を隠しているのに、
        // **集計値が総量を漏らす**。だから件数を含まない別の口を置く。
        //
        // 🔴 **ロール要求を足さないこと。** SC-18 は一般利用者の画面であり、辺の型名が引けないと
        // 辺の描き分けも型フィルタも描けない（グラフ応答が返すのは `EdgeTypeId` だけである）。
        app.MapGet("/graph/edge-types/catalog", async (GraphDbContext db, CancellationToken ct) =>
            Results.Ok(await LoadCatalogAsync(db, ct)))
            .WithTags("EdgeTypes").RequireAuthorization()
            .WithName("ListEdgeTypeCatalog").Produces<List<EdgeTypeCatalogItemDto>>();

        // FR-17, SC-09: 追加。**同名は 409**（正規化後の名前で比較する）。
        write.MapPost("/", async (CreateEdgeTypeRequest req, GraphDbContext db, CancellationToken ct) =>
        {
            var name = EdgeType.Normalize(req.Name ?? string.Empty);
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { error = "name_required" });
            if (!EdgeTypeLayer.IsValid(req.Layer ?? string.Empty))
                return Results.BadRequest(new { error = "invalid_layer" });

            if (await ExistsAsync(db, name, ct))
                return Conflict(name);

            var type = EdgeType.Create(name, req.Layer!, req.IsSymmetric);
            db.EdgeTypes.Add(type);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // 事前確認と保存の間に別の要求が同名を入れた（race）。一意制約違反を
                // **素の 500 にせず 409 へ変換する** —— 契約は「重複は 409」である。
                db.Entry(type).State = EntityState.Detached;
                if (await ExistsAsync(db, name, ct))
                    return Conflict(name);
                throw;
            }

            return Results.Created($"/graph/edge-types/{type.Id}",
                new EdgeTypeDto(type.Id, type.Name, type.Layer, type.IsSymmetric, type.IsSeed, 0));
        }).WithName("CreateEdgeType").Produces<EdgeTypeDto>(StatusCodes.Status201Created);

        // FR-17, SC-09, ADR-0033 決定 9: 改名。
        // 🔴 **識別子は変えない。** 辺は識別子を参照しているため、既存の辺は 1 行も書き換わらずに
        // 新しい名前へ追随する。ここで Id を振り直すと既存の参照が全部切れる。
        write.MapPut("/{id:guid}", async (Guid id, RenameEdgeTypeRequest req, GraphDbContext db,
            CancellationToken ct) =>
        {
            var type = await db.EdgeTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (type is null) return Results.NotFound();

            var name = EdgeType.Normalize(req.Name ?? string.Empty);
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { error = "name_required" });

            // 同じ名前への改名（実質の no-op）を 409 にしても管理者は何も直せない。
            if (!string.Equals(name, type.Name, StringComparison.OrdinalIgnoreCase)
                && await ExistsAsync(db, name, ct))
                return Conflict(name);

            type.Rename(name);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return Conflict(name);
            }

            var usage = await UsageOfAsync(db, type.Id, ct);
            return Results.Ok(new EdgeTypeDto(
                type.Id, type.Name, type.Layer, type.IsSymmetric, type.IsSeed, usage));
        }).WithName("RenameEdgeType").Produces<EdgeTypeDto>();

        // FR-17, SC-09, ADR-0033 決定 9: 削除。**参照が 1 件でもあれば拒否する。**
        //
        // 🔴 **数え方は一覧と同一でなければならない。** 一覧が 0 件と表示したのに削除が 409 を
        // 返す（あるいはその逆）と、管理者は辞書を信用できなくなる。同じ `UsageOfAsync` を使う。
        //
        // DB 層にも `ON DELETE RESTRICT` があり、これが最後の防壁になる。**その例外を素の 500 で
        // 漏らさない** —— アプリ層の事前カウントをすり抜けた競合も 409 で返す。
        write.MapDelete("/{id:guid}", async (Guid id, GraphDbContext db, CancellationToken ct) =>
        {
            var type = await db.EdgeTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (type is null) return Results.NotFound();

            var usage = await UsageOfAsync(db, id, ct);
            if (usage > 0)
                return Results.Conflict(new EdgeTypeInUseResponse(
                    "edge_type_in_use",
                    $"型「{type.Name}」は {usage} 本の辺で使われているため削除できません。",
                    usage));

            db.EdgeTypes.Remove(type);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // ON DELETE RESTRICT に弾かれた（事前カウントと削除の間に辺が張られた）。
                var now = await UsageOfAsync(db, id, ct);
                return Results.Conflict(new EdgeTypeInUseResponse(
                    "edge_type_in_use",
                    $"型「{type.Name}」は {now} 本の辺で使われているため削除できません。",
                    now));
            }

            return Results.NoContent();
        }).WithName("DeleteEdgeType").Produces(StatusCodes.Status204NoContent);

        return app;
    }

    private static IResult Conflict(string name)
        => Results.Conflict(new { error = "edge_type_exists", message = $"型「{name}」は既に辞書にあります。" });

    // 正規化後の名前で、大文字小文字を無視して突き合わせる。
    // DB の一意索引は素の `name` に対するものなので、**サービス層のこの検査が先に効く**。
    private static async Task<bool> ExistsAsync(GraphDbContext db, string name, CancellationToken ct)
        => await db.EdgeTypes.AnyAsync(t => t.Name.ToLower() == name.ToLower(), ct);

    // FR-17, SC-09, SC-10: その型を参照している辺の数。
    // **外部キーの列を数えるだけ**（`ix_edges_type` が効く）。一覧・改名・削除がこの 1 つを共有する。
    private static async Task<int> UsageOfAsync(GraphDbContext db, Guid typeId, CancellationToken ct)
        => await db.Edges.CountAsync(e => e.EdgeTypeId == typeId, ct);

    // FR-17, SC-18 (#962): 描画用カタログ。**辺を一切参照しない** ——
    // 参照しないことが、集計値を漏らさないことの構造的な保証である。
    private static async Task<List<EdgeTypeCatalogItemDto>> LoadCatalogAsync(
        GraphDbContext db, CancellationToken ct)
        => await db.EdgeTypes.AsNoTracking()
            .OrderBy(t => t.Name)
            // FR-04, ADR-0035 決定 2 (#970): `Weight` は二段検索の再ランクが引く（#947a が入れた値の公開面）。
            .Select(t => new EdgeTypeCatalogItemDto(t.Id, t.Name, t.Layer, t.IsSymmetric, t.Weight))
            .ToListAsync(ct);

    private static async Task<List<EdgeTypeDto>> LoadWithUsageAsync(GraphDbContext db, CancellationToken ct)
    {
        var types = await db.EdgeTypes.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        if (types.Count == 0) return [];

        // 型ごとの件数を 1 クエリで集計する（型の数だけ COUNT を投げない）。
        var usage = await db.Edges.AsNoTracking()
            .GroupBy(e => e.EdgeTypeId)
            .Select(g => new { TypeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TypeId, x => x.Count, ct);

        return types
            .Select(t => new EdgeTypeDto(
                t.Id, t.Name, t.Layer, t.IsSymmetric, t.IsSeed, usage.GetValueOrDefault(t.Id)))
            .ToList();
    }
}
