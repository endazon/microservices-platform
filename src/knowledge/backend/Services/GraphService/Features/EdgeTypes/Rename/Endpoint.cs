using FluentValidation;
using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Kernel;

namespace GraphService.Features.EdgeTypes.Rename;

// FR-17, SC-09, ADR-0033 決定 9: 改名。
// 🔴 **識別子は変えない。** 辺は識別子を参照しているため、既存の辺は 1 行も書き換わらずに
// 新しい名前へ追随する。ここで Id を振り直すと既存の参照が全部切れる。
internal static class RenameEdgeTypeEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPut("/{id:guid}", async (Guid id, RenameEdgeTypeRequest req,
            IValidator<RenameEdgeTypeRequest> validator, GraphDbContext db,
            CancellationToken ct) =>
        {
            var type = await db.EdgeTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (type is null) return Results.NotFound();

            // 🔴 **検証はここに置く。ハンドラの先頭へ上げてはならない。**
            // 移送前も型を引いた後に空名を弾いていた —— **不存在の型 ID への空名改名は 404** であり、
            // 先頭へ上げると 400 に化ける。`IValidator<T>` が引数にあることは順序の証拠にならない
            // （引数は解決であって実行ではない。IADR-0395 決定 2）。
            //
            // FR-17, SC-09 / IADR-0371 決定 2・4 / IADR-0395: 検証の失敗を Kernel の `Result` で表し、
            // **HTTP への写像は 1 度だけ行う**（計画 ADR-0030 §決定 / ADR-0041 §結果）。
            var gate = Validate(validator, req);
            if (gate.IsFailure)
                return Results.BadRequest(new { error = gate.Error.Message });

            var name = EdgeType.Normalize(req.Name ?? string.Empty);

            // 同じ名前への改名（実質の no-op）を 409 にしても管理者は何も直せない。
            if (!string.Equals(name, type.Name, StringComparison.OrdinalIgnoreCase)
                && await EdgeTypeEndpoints.ExistsAsync(db, name, ct))
                return EdgeTypeEndpoints.Conflict(name);

            type.Rename(name);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return EdgeTypeEndpoints.Conflict(name);
            }

            var usage = await EdgeTypeEndpoints.UsageOfAsync(db, type.Id, ct);
            return Results.Ok(new EdgeTypeDto(
                type.Id, type.Name, type.Layer, type.IsSymmetric, type.IsSeed, usage));
        }).WithName("RenameEdgeType").Produces<EdgeTypeDto>();
    }

    // FR-17 / IADR-0371 決定 2: 入力規則の判定。**規則そのものは `RenameEdgeTypeValidator` が持つ。**
    // 規則は 1 本だが、`Errors[0]` を採る形は他の端点と揃える（規則が増えたときに
    // 「宣言順が応答の契約」という読み方がそのまま効く）。
    private static Result Validate(IValidator<RenameEdgeTypeRequest> validator,
        RenameEdgeTypeRequest req)
    {
        var result = validator.Validate(req);
        return result.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation(
                "graph.edge-type.rename.invalid", result.Errors[0].ErrorMessage));
    }
}
