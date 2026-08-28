using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents;

// FR-19, FR-20, UC-11, ADR-0036 D-06, ADR-0046 D-06 部品 3, IADR-0253 決定 4（段 4）:
// 文書の共有先（DocumentShare）の付与・取り消し・一覧。
//
// **変更できるのは所有者だけである**（計画: `doc.shared_with` を変更できるのは `doc.owner` を
// 満たす主体に限る）。判定は本文書き込みと同じ動的束縛 `doc.owner ∈ { ${current_user} }` を
// `DocumentBodyIntake.CanWrite` の再利用で行い、規則を 1 か所に保つ。
// **再共有不可はこの所有者限定から従う** —— 被共有者は owner ではないため、共有の追加も
// 取り消しもできない。ロールによる判定は追加しない（ADR-0036 D-07）。
//
// 🔴 **拒否の応答は 404 である。403 にしない**（ADR-0056 決定 1・[[IADR-0277]]）。
// ADR-0056 は打ち分けの軸を「**主体がその文書を読めるか**」と定め、読めない相手には
// 常に「見つからない」と答えることを課した。**本サービスは ABAC の読み取り判定を持たない**
// （AuthorizationService を呼ぶ口が無い）ため、`CanWrite` が偽のとき「読めるが書けない」
// （＝403 が許される決定 2 の側）だと**言い切れない**。判定できないものを読めると仮定せず、
// fail-closed 側へ倒す。403 を返すと**文書 ID の総当たりで実在が判別できてしまう**。
//
// 🔴 本段は**貯蔵と管理 API まで**である。共有先ベースの分岐（選言の第 3 節）を認可スコープへ
// 載せる配線は、消費側が共有記録へ到達する方式（DB per Service の越境）が未決のため別段とする。
public static class DocumentShareEndpoints
{
    public static IEndpointRouteBuilder MapDocumentShareEndpoints(this IEndpointRouteBuilder app)
    {
        // 認証は必須（主体が決まらないと動的束縛を評価できない）。ロール要求は付けない
        // （FR-19 の共有は一般利用者の操作。管理者ロール限定にすると所有者が共有できない）。
        var g = app.MapGroup("/documents/{id:guid}/shares")
            .WithTags("Documents")
            .RequireAuthorization();

        // FR-20: 共有先の一覧（所有者のみ。共有の管理は所有者の裁量に属する）。
        g.MapGet("/", async (Guid id, DocumentDbContext db, HttpContext http) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            if (!DocumentBodyIntake.CanWrite(doc.Attributes, http.User.Identity?.Name))
                return Results.NotFound();

            var shares = await db.DocumentShares
                .Where(s => s.DocumentId == id)
                .OrderBy(s => s.CreatedAt)
                .Select(s => new DocumentShareDto(s.SubjectType, s.SubjectId, s.GrantedBy, s.CreatedAt))
                .ToListAsync();
            return Results.Ok(shares);
        });

        // FR-20, ADR-0036 D-06: 共有の付与（所有者のみ。個人／グループの 2 種別）。
        g.MapPost("/", async (Guid id, CreateShareRequest req, DocumentDbContext db,
            HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(req.SubjectId)
                || !ShareSubjectType.IsValid(req.SubjectType))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["errors"] =
                    [
                        $"subjectType は {string.Join(" / ", ShareSubjectType.All)} のいずれか、"
                        + "subjectId は非空である必要があります。"
                    ]
                });

            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();

            // 所有者限定（再共有不可の実体）。認可を検証より先に見る——他人の文書に対する
            // 存在・重複の情報を返さない（拒否は 404。ADR-0056 決定 1）。
            if (!DocumentBodyIntake.CanWrite(doc.Attributes, http.User.Identity?.Name))
                return Results.NotFound();

            var exists = await db.DocumentShares.AnyAsync(s => s.DocumentId == id
                && s.SubjectType == req.SubjectType && s.SubjectId == req.SubjectId);
            if (exists)
                return Results.Conflict(new { message = "既に共有済みです。" });

            var share = DocumentShare.Create(id, req.SubjectType, req.SubjectId,
                http.User.Identity!.Name!);
            db.DocumentShares.Add(share);
            await db.SaveChangesAsync();
            return Results.Created($"/documents/{id}/shares",
                new DocumentShareDto(share.SubjectType, share.SubjectId, share.GrantedBy,
                    share.CreatedAt));
        });

        // FR-20, ADR-0036 D-06: 共有の取り消し（所有者のみ。取り消し可——ただし
        // 「既に見られた事実」は取り消せない）。
        g.MapDelete("/{subjectType}/{subjectId}", async (Guid id, string subjectType,
            string subjectId, DocumentDbContext db, HttpContext http) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            if (!DocumentBodyIntake.CanWrite(doc.Attributes, http.User.Identity?.Name))
                return Results.NotFound();

            var share = await db.DocumentShares.FirstOrDefaultAsync(s => s.DocumentId == id
                && s.SubjectType == subjectType && s.SubjectId == subjectId);
            if (share is null) return Results.NotFound();

            db.DocumentShares.Remove(share);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateShareRequest(string SubjectType, string SubjectId);

public record DocumentShareDto(string SubjectType, string SubjectId, string GrantedBy,
    DateTimeOffset CreatedAt);
