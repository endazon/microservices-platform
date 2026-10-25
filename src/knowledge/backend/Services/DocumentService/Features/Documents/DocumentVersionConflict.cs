using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocumentService.Features.Documents;

// FR-06, UC-03, SC-05, [[IADR-0597]] (#2103): 版の競合（lost update）を 409 `version_conflict` へ写す唯一の点。
// 写す口は `Update`・`UpdateMetadata`・`Delete`・個人資料の露出トグル（`SetExposure`）の 4 つ（決定 3・4）。
//
// 期待版の比較（`ExpectedVersion != doc.Version`）は**読んだ時点の**比較であり、比較と保存の間に別の更新が入る窓を
// 塞がない。窓は保存の側で塞ぐ —— `Document.Version` は並行トークンであり（`DocumentDbContext`）、
// `UPDATE … WHERE "Id" = @id AND "Version" = @読んだ版` が 0 行なら EF は `DbUpdateConcurrencyException` を投げる。
//
// 🔴 **版テーブルの一意索引違反も同じ競合として読む。** 同じ版を前提にした 2 つの保存は、どちらも
// `DocumentVersions (DocumentId, Version)` に同じ番号の行を足す。EF が 1 つのバッチに積む順によっては、
// 後から確定する側は文書行の 0 行より先にこの一意索引で 23505 になる（実 PostgreSQL の実測ではこちらが先に出る。
// どちらが先かは EF の命令の並べ方で決まり、契約にできない）。両方を拾わないと、片方の順でだけ 500 へ戻る。
// **他の一意索引違反は拾わない**（制約名で絞る。競合でない不正を 409 に化けさせない）。
internal static class DocumentVersionConflict
{
    // 版テーブルの一意索引の名前（マイグレーション `AddDocumentVersions` が付けた名前）。
    internal const string VersionUniqueIndex = "IX_DocumentVersions_DocumentId_Version";

    internal static bool Is(DbUpdateException ex)
        => ex is DbUpdateConcurrencyException
           || ex.InnerException is PostgresException
           {
               SqlState: PostgresErrorCodes.UniqueViolation,
               ConstraintName: VersionUniqueIndex,
           };

    // 期待版の比較で弾いたとき（保存より前）と、保存で負けたときの応答は**同じ形**である
    // （画面は 409 の本文の形で分岐しない。`expectedVersion` は要求に無ければ null）。
    internal static IResult Problem(int? expectedVersion, int currentVersion)
        => Results.Conflict(new
        {
            error = "version_conflict",
            expectedVersion,
            currentVersion,
        });

    // FR-02, IADR-0637 (#2150): 複数の文書を 1 回で確定する口（個人資料の完全削除など）が負けたときの応答。
    // 確定は全体が巻き戻っている（部分の結果は無い）。どの文書で負けたかは返さない（呼び出し側は全体をやり直す）。
    internal static IResult ProblemForBatch() => Results.Conflict(new { error = "version_conflict" });

    // 保存で負けた後の応答。**現在版は読み直す**（追跡中の実体は自分が進めた版を持っており、勝った側の版ではない）。
    // 負けた保存は暗黙のトランザクションごと巻き戻っている（版の行も残らない）。
    // 勝った側が文書を消していたら 404（期待版の比較より前に不存在で返すのと同じ）。
    internal static async Task<IResult> AfterLostSaveAsync(DocumentDbContext db, Guid id,
        int? expectedVersion, CancellationToken ct)
    {
        var current = await db.Documents.AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => (int?)d.Version)
            .FirstOrDefaultAsync(ct);
        return current is { } v ? Problem(expectedVersion, v) : Results.NotFound();
    }
}
