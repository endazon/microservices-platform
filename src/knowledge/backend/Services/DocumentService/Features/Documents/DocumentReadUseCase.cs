using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents;

// FR-06, UC-03, SC-03, SC-05, NFR-09, ADR-0029, ADR-0065 決定 2, ADR-0075, IADR-0012,
// [[IADR-0379]] 決定 5, [[IADR-0402]] (#1255), ADR-0119 決定 3 (#1614): 文書台帳の**読み取り 4 口の本体**。
//
// 🔴 **REST と gRPC の両方がここを通る。判定器を 2 つにしない。**
// east-west gRPC の面（`DocumentReadGrpcService`）を足すときに、ハンドラ本体を写して
// 2 つ目の実装を作ると、片方だけが直る形になる（並び順・タグ名の解決・不在の扱いは
// **応答の意味**であって輸送の都合ではない）。先行スライスの `EmbedUseCase` /
// `CompletionUseCase`（[[IADR-0397]] / [[IADR-0400]]）と同じ形である。
//
// ［2026-09-27 更新 / #1614］🔴 **個人資料の可視性はここで判定する**（計画 ADR-0119 決定 3）。
// すべての操作は**主体**（`DocumentReadPrincipal`）を受け取り、`DocumentReadAccess` で読めるかを決める。
// 読めない文書は一覧から除き（件数にも含めない）、個別は「無い」と同じ `null` を返す（ADR-0056 の存在秘匿）。
// **組織文書の内容の ABAC はまだ BFF の `BffScopeResolver` が実施点である**（#1615 で `DocumentReadAccess` に入る）。
// 認証の門（REST の読み取り群の `RequireAuthorization()`・gRPC の `ServiceCaller`）は**このクラスの外**にある。
//
// 🔴 **「無い」は `null` で返す。例外にしない。** 呼び出し側（REST は 404、gRPC は
// `found=false`）が意味を与える。
//
// ADR-0065 決定 2 の適用: 実体は `Features/Documents/<操作>/` に置くのが原則だが、
// **本クラスは 4 操作が共有する**ため合成点と同じ階層に置く（`ToDto` / `ToVersionDto` /
// `PublishUpdatedAsync` と同じ扱い）。**各操作フォルダへ複写しない。**
public sealed class DocumentReadUseCase(DocumentDbContext db, DocumentReadAccess access)
{
    // FR-06, UC-03: 一覧（更新の新しい順）。読めない文書は除く（#1614）。
    //
    // FR-19, [[IADR-0447]] (#1447): 共有先は**1 クエリで引いて分配する**（`ResolveSharedWithAsync`
    // の束の口）。🔴 **文書ごとに引かない** —— 一覧の応答が文書数に比例して遅くなる
    // （`PrivateNoteEnrichment` が共有の件数で採っているのと同じ規律）。
    public async Task<List<DocumentDto>> ListAsync(DocumentReadPrincipal principal, CancellationToken ct = default)
    {
        var names = await TagResolver.NamesAsync(db);
        var docs = await db.Documents
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);
        var shares = await DocumentEndpoints.ResolveSharedWithAsync(
            db, docs.Select(d => d.Id).ToList(), ct);

        var visible = new List<DocumentDto>(docs.Count);
        foreach (var d in docs)
        {
            var sharedWith = shares.GetValueOrDefault(d.Id);
            if (await access.CanReadAsync(principal, d, sharedWith, ct))
                visible.Add(DocumentEndpoints.ToDto(d, names, sharedWith));
        }
        return visible;
    }

    // FR-06, UC-03: 1 件の取得。台帳に無い・読めないは同じ null（#1614。ADR-0056）。
    public async Task<DocumentDto?> GetAsync(DocumentReadPrincipal principal, Guid id, CancellationToken ct = default)
    {
        var doc = await FindReadableAsync(principal, id, ct);
        return doc is null
            ? null
            : DocumentEndpoints.ToDto(doc.Value.Doc, await TagResolver.NamesAsync(db), doc.Value.SharedWith);
    }

    // FR-06, UC-03: 版履歴一覧（新しい順）。
    // 🔴 **「文書が無い」と「版が無い」を分ける。** 前者は null（REST は 404）、
    // 後者は空リスト（REST は 200 の `[]`）である。**読めない文書は「文書が無い」と同じ null**（#1614）。
    public async Task<List<DocumentVersionDto>?> ListVersionsAsync(DocumentReadPrincipal principal, Guid id,
        CancellationToken ct = default)
    {
        if (await FindReadableAsync(principal, id, ct) is null) return null;

        var names = await TagResolver.NamesAsync(db);
        var versions = await db.DocumentVersions
            .Where(v => v.DocumentId == id)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);
        return versions.Select(v => DocumentEndpoints.ToVersionDto(v, names)).ToList();
    }

    // FR-06, UC-03: 特定版の取得。
    // 🔴 **文書の不在・版の不在・読めないを区別しない**（REST はどれも 404）。
    // 🔴 **可視性は現在の文書で判定する**（版のスナップショットの属性では判定しない。`doc_scope` は不変 ——
    //   ADR-0058。所有者・共有先は現在の値が正である）。
    public async Task<DocumentVersionDto?> GetVersionAsync(DocumentReadPrincipal principal, Guid id, int version,
        CancellationToken ct = default)
    {
        if (await FindReadableAsync(principal, id, ct) is null) return null;

        var snapshot = await db.DocumentVersions
            .FirstOrDefaultAsync(v => v.DocumentId == id && v.Version == version, ct);
        return snapshot is null
            ? null
            : DocumentEndpoints.ToVersionDto(snapshot, await TagResolver.NamesAsync(db));
    }

    // 個別の 3 操作が共有する「在って、読める」の判定（#1614）。共有先は応答にも使うので一緒に返す。
    private async Task<(Document Doc, List<string> SharedWith)?> FindReadableAsync(
        DocumentReadPrincipal principal, Guid id, CancellationToken ct)
    {
        var doc = await db.Documents.FindAsync([id], ct);
        if (doc is null) return null;

        var sharedWith = await DocumentEndpoints.ResolveSharedWithAsync(db, doc.Id, ct);
        return await access.CanReadAsync(principal, doc, sharedWith, ct) ? (doc, sharedWith) : null;
    }
}
