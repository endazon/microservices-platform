using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents;

// FR-06, UC-03, SC-03, SC-05, NFR-09, ADR-0029, ADR-0065 決定 2, ADR-0075, IADR-0012,
// [[IADR-0379]] 決定 5, [[IADR-0402]] (#1255): 文書台帳の**読み取り 4 口の本体**。
//
// 🔴 **REST と gRPC の両方がここを通る。判定器を 2 つにしない。**
// east-west gRPC の面（`DocumentReadGrpcService`）を足すときに、ハンドラ本体を写して
// 2 つ目の実装を作ると、片方だけが直る形になる（並び順・タグ名の解決・不在の扱いは
// **応答の意味**であって輸送の都合ではない）。先行スライスの `EmbedUseCase` /
// `CompletionUseCase`（[[IADR-0397]] / [[IADR-0400]]）と同じ形である。
//
// 🔴 **ここに認可は無い。** 読み取りの ABAC は呼び出し元（BFF）の `BffScopeResolver` ＋
// `IsManageable` ただ 1 つが実施点である（[[IADR-0041]] / [[IADR-0045]]、`IADR-0012`）。
// REST の読み取り group はロールで塞いでおらず（SC-03 の一般利用者の閲覧）、
// gRPC の面は `ServiceCaller` を要求する —— **どちらの門もこのクラスの外**にある。
//
// 🔴 **「無い」は `null` で返す。例外にしない。** 呼び出し側（REST は 404、gRPC は
// `found=false`）が意味を与える。存在秘匿（`ADR-0056` / [[IADR-0009]]）の実施は
// さらに外（BFF）である。
//
// ADR-0065 決定 2 の適用: 実体は `Features/Documents/<操作>/` に置くのが原則だが、
// **本クラスは 4 操作が共有する**ため合成点と同じ階層に置く（`ToDto` / `ToVersionDto` /
// `PublishUpdatedAsync` と同じ扱い）。**各操作フォルダへ複写しない。**
public sealed class DocumentReadUseCase(DocumentDbContext db)
{
    // FR-06, UC-03: 一覧（更新の新しい順）。
    public async Task<List<DocumentDto>> ListAsync(CancellationToken ct = default)
    {
        var names = await TagResolver.NamesAsync(db);
        var docs = await db.Documents
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);
        return docs.Select(d => DocumentEndpoints.ToDto(d, names)).ToList();
    }

    // FR-06, UC-03: 1 件の取得。台帳に無ければ null。
    public async Task<DocumentDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var doc = await db.Documents.FindAsync([id], ct);
        return doc is null ? null : DocumentEndpoints.ToDto(doc, await TagResolver.NamesAsync(db));
    }

    // FR-06, UC-03: 版履歴一覧（新しい順）。
    // 🔴 **「文書が無い」と「版が無い」を分ける。** 前者は null（REST は 404）、
    // 後者は空リスト（REST は 200 の `[]`）である。現行の `ListDocumentVersionsEndpoint` が
    // `AnyAsync` を先に引いているのはこの区別のためであり、畳んではならない。
    public async Task<List<DocumentVersionDto>?> ListVersionsAsync(Guid id, CancellationToken ct = default)
    {
        var exists = await db.Documents.AnyAsync(d => d.Id == id, ct);
        if (!exists) return null;

        var names = await TagResolver.NamesAsync(db);
        var versions = await db.DocumentVersions
            .Where(v => v.DocumentId == id)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);
        return versions.Select(v => DocumentEndpoints.ToVersionDto(v, names)).ToList();
    }

    // FR-06, UC-03: 特定版の取得。
    // 🔴 **文書の不在と版の不在を区別しない**（現行どおり。REST もどちらも 404 である）。
    public async Task<DocumentVersionDto?> GetVersionAsync(Guid id, int version, CancellationToken ct = default)
    {
        var snapshot = await db.DocumentVersions
            .FirstOrDefaultAsync(v => v.DocumentId == id && v.Version == version, ct);
        return snapshot is null
            ? null
            : DocumentEndpoints.ToVersionDto(snapshot, await TagResolver.NamesAsync(db));
    }
}
