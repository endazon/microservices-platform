using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.Documents.AddTag;

// FR-05, FR-18, NFR-09, SC-03, SC-05, SC-09, ADR-0029, ADR-0036 D-07, ADR-0063 決定 1〜3,
// ADR-0065 決定 2, ADR-0075, 計画 ADR-0086 決定 1, [[IADR-0044]], [[IADR-0364]], [[IADR-0379]] 決定 5,
// [[IADR-0410]] (#1255): **文書へタグを 1 つ足す本体。**
//
// 🔴 **REST と east-west gRPC の両方がここを通る。判定器を 2 つにしない**
// （先行スライスの `DocumentReadUseCase` と同じ形。[[IADR-0402]]）。
// ハンドラ本体を写して 2 つ目の実装を作ると、**片方だけが直る形**になる ——
// 認可の選言・辞書照合・冪等・404 の一本道は**応答の意味**であって輸送の都合ではない。
//
// 🔴 **認可は「①その文書への write（所有者の動的束縛）または ②管理者ロール」の選言である**
// （`ADR-0063` 決定 3）。GraphService が同じ選言で既に判定しているが、**本サービスが最終防衛線**
// （[[IADR-0044]]）なので再判定する。
//
// 🔴 **主体とロールは引数で受け取る。`HttpContext` を引数に取らない**（計画 `ADR-0086` 決定 1）。
// east-west gRPC 経路には利用者の `HttpContext.User` が無く（載っているのは呼び出し**元サービス**の
// s2s トークンである）、承認者の身元は要求本文で運ばれる。**判定の位置は動いていない** ——
// 判定するのは依然として本サービスであり、呼び出し元の判定結果を受け取る口はここに無い。
//
// 🔴 **拒否は「見つからない」である。403 に相当する値を持たない**（`PutBody` と同じ理由。
// 本サービスは ABAC の読み取り判定を持たないため「読めるが書けない」と言い切れず、
// 403 は文書 ID の総当たりで実在を明かす）。**`NotWritable` は「所有者でも管理者でもない」と
// 「文書が無い」の両方を意味する。区別する項目を足してはならない。**
//
// **冪等**: 既に付いていれば版も進めずイベントも出さずに `Applied` を返す。
// 付けたときは版が 1 つ進み、`DocumentUpdated` を再発行する（射影が追随する）。
// **本文指紋は変わらない**ので、却下解除（ADR-0050）は発火しない。
//
// ADR-0065 決定 2 の適用: 実体は `Features/Documents/<操作>/` に置く（本クラスは 1 操作の本体である）。
public sealed class AddDocumentTagUseCase(DocumentDbContext db, IDocumentUpdatedPublisher bus)
{
    // 版履歴に残す変更メモ。**AI 提案の承認由来であることが後から読める**ようにする。
    public const string ChangeNote = "ai-suggestion-approved";

    public async Task<AddDocumentTagOutcome> ExecuteAsync(
        Guid documentId,
        string rawTagName,
        string? subject,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var name = Tag.Normalize(rawTagName);

        var doc = await db.Documents.FindAsync([documentId], ct);
        if (doc is null) return AddDocumentTagOutcome.NotWritable;

        // ★認可★ —— 副作用（辞書照合の応答を含む）より前に置く。辞書照合を先にすると、
        // 書けない主体に「そのタグは辞書に無い」という情報が返る。
        //
        //   ① `DocumentBodyIntake.CanWrite`（`doc.owner ∈ { ${current_user} }`。`PutBody` と同じ判定）
        //   ② `platform-admin`（SC-05 の管理者経路。**運用者は含めない** —— `UpdateMetadata` が
        //      `AdminOnly` であることと揃える。取り込み文書は `owner=system` なので①では誰も書けず、
        //      ②が無いと誰も承認できない）
        var canWrite = DocumentBodyIntake.CanWrite(doc.Attributes, subject);
        if (!canWrite && !isAdmin)
            return AddDocumentTagOutcome.NotWritable;

        // 🔴 **辞書に無い名前は却下**（SC-05「既定タグ辞書に整合」は経路を問わない不変条件。
        // `ADR-0063` 決定 2）。識別子化した以上、辞書に無い名前は物理的に保存できない。
        var (ids, unknown) = await TagResolver.ToIdsAsync(db, [name], ct);
        if (unknown.Count > 0) return AddDocumentTagOutcome.Unknown(unknown);

        var names = await TagResolver.NamesAsync(db, ct);
        if (!doc.AddTag(ids[0], ChangeNote))
            return AddDocumentTagOutcome.Ok(DocumentEndpoints.ToDto(doc, names));

        await db.SaveChangesAsync(ct);
        await DocumentEndpoints.PublishUpdatedAsync(bus, db, doc, names, ct);
        return AddDocumentTagOutcome.Ok(DocumentEndpoints.ToDto(doc, names));
    }
}

// FR-18, SC-05, ADR-0063 決定 2・3, [[IADR-0410]] (#1255): タグ反映の結果。
// REST の状態コード（200 / 400 / 404）と 1:1 であり、gRPC の `TagWriteResult` とも 1:1 である。
public sealed record AddDocumentTagOutcome(
    AddDocumentTagStatus Status,
    DocumentDto? Document = null,
    List<string>? UnknownTags = null)
{
    public static AddDocumentTagOutcome NotWritable { get; } = new(AddDocumentTagStatus.NotWritable);

    public static AddDocumentTagOutcome Ok(DocumentDto document) =>
        new(AddDocumentTagStatus.Applied, document);

    public static AddDocumentTagOutcome Unknown(List<string> unknown) =>
        new(AddDocumentTagStatus.UnknownTag, UnknownTags: unknown);
}

public enum AddDocumentTagStatus
{
    // 🔴 **既定の 0 を「成功」にしない**（写し漏れが成功に化けるのが最悪である）。
    NotWritable = 0,
    Applied = 1,
    UnknownTag = 2,
}
