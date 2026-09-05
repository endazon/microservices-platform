using FluentValidation;
using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Kernel;

namespace GraphService.Features.EdgeTypes.Create;

// FR-17, SC-09: 追加。**同名は 409**（正規化後の名前で比較する）。
internal static class CreateEdgeTypeEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost("/", async (CreateEdgeTypeRequest req, IValidator<CreateEdgeTypeRequest> validator,
            GraphDbContext db, CancellationToken ct) =>
        {
            // FR-17, SC-09 / IADR-0371 決定 2・4 / IADR-0395: 入力検証（FluentValidation）の失敗を
            // Kernel の `Result` で表し、**HTTP への写像は 1 度だけ行う**
            // （計画 ADR-0030 §決定「ProblemDetails 変換は API 層」/ ADR-0041 §結果）。
            // **判定の位置は移送前のガード節と同じ**（重複検査より前）であり、
            // 状態コードも本文も変わらない。
            var gate = Validate(validator, req);
            if (gate.IsFailure)
                return Results.BadRequest(new { error = gate.Error.Message });

            var name = EdgeType.Normalize(req.Name ?? string.Empty);
            if (await EdgeTypeEndpoints.ExistsAsync(db, name, ct))
                return EdgeTypeEndpoints.Conflict(name);

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
                if (await EdgeTypeEndpoints.ExistsAsync(db, name, ct))
                    return EdgeTypeEndpoints.Conflict(name);
                throw;
            }

            return Results.Created($"/graph/edge-types/{type.Id}",
                new EdgeTypeDto(type.Id, type.Name, type.Layer, type.IsSymmetric, type.IsSeed, 0));
        }).WithName("CreateEdgeType").Produces<EdgeTypeDto>(StatusCodes.Status201Created);
    }

    // FR-17 / IADR-0371 決定 2: 入力規則の判定。**規則そのものは `CreateEdgeTypeValidator` が持つ。**
    //
    // 🔴 **`Errors[0]` を採る。** FluentValidation は既定で全規則を走らせるため、
    // 移送前の「最初の違反で 400 を返す」と同じ本文にするには最初の失敗を採るしかない。
    // 規則の宣言順が応答の契約の一部になっている（同 Validator のコメントを参照）。
    private static Result Validate(IValidator<CreateEdgeTypeRequest> validator,
        CreateEdgeTypeRequest req)
    {
        var result = validator.Validate(req);
        return result.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation(
                "graph.edge-type.create.invalid", result.Errors[0].ErrorMessage));
    }
}
