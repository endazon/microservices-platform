using FluentValidation;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.AddTag;

// FR-18, SC-03, SC-05, SC-09, ADR-0063 決定 1〜3, ADR-0036 D-07, IADR-0364 (#1187):
// **文書へタグを 1 つ足す**（AI のタグ提案を承認したときの反映先）。
//
// 🔴 **認可は「①その文書への write（所有者の動的束縛）または ②管理者ロール」の選言である**
// （ADR-0063 決定 3）。GraphService が同じ選言で既に判定しているが、**本サービスが最終防衛線**
// （[[IADR-0044]]）なので再判定する。呼び出しは承認者本人の資格情報で来る（決定 3
// 「サービスが利用者に代わって書く形は採らない」）。
//
//   ① `DocumentBodyIntake.CanWrite`（`doc.owner ∈ { ${current_user} }`。`PutBody` と同じ判定）
//   ② `platform-admin`（SC-05 の管理者経路。**運用者は含めない** —— `UpdateMetadata` が `AdminOnly` である
//      ことと揃える。取り込み文書は `owner=system` なので①では誰も書けず、②が無いと誰も承認できない）
//
// 🔴 **拒否は 404 である。403 にしない**（`PutBody` と同じ理由。本サービスは ABAC の読み取り判定を
// 持たないため「読めるが書けない」と言い切れず、403 は文書 ID の総当たりで実在を明かす）。
//
// 🔴 **辞書に無い名前は 400**（SC-05「既定タグ辞書に整合」は経路を問わない不変条件。ADR-0063 決定 2）。
// 識別子化した以上、辞書に無い名前は物理的に保存できない（`TagResolver.ToIdsAsync` が権威）。
//
// **冪等**: 既に付いていれば 200 を返すだけで、版も進めずイベントも出さない。
// 付けたときは版が 1 つ進み、`DocumentUpdated` を再発行する（射影が追随する）。
// **本文指紋は変わらない**ので、却下解除（ADR-0050）は発火しない。
internal static class AddDocumentTagEndpoint
{
    internal static void Map(RouteGroupBuilder tagReflection)
    {
        tagReflection.MapPost("/{id:guid}/tags", async (Guid id, AddDocumentTagRequest req,
            IValidator<AddDocumentTagRequest> validator,
            AddDocumentTagUseCase tags, HttpContext http, CancellationToken ct) =>
        {
            // FR-18, SC-09 / 計画 ADR-0030 §決定 / IADR-0371 決定 2 / [[IADR-0398]] 決定 1:
            // タグ名は必須（正規化後に空なら不可）。規則は `AddDocumentTagValidator` が持つ。
            //
            // 🔴 **この位置（取得・認可より前）を動かしてはならない。** 移送前もそうだった ——
            // 空のタグ名は文書の存在も認可も見ずに 400 である（後ろへ動かすと 404 に化ける）。
            // **辞書照合（`UnknownTagsProblem`）は逆に認可の後ろ**であり、こちらとは別物である。
            //
            // 🔴 **検証は輸送の縁に残す。** 器（`Results.ValidationProblem`）が輸送ごとに違うためで、
            // **規則そのものは 1 つ**（同じ `IValidator<AddDocumentTagRequest>` を gRPC 面も引く）。
            var gate = validator.Validate(req);
            if (!gate.IsValid) return ValidationProblems.FirstViolation(gate);

            // FR-05, FR-18, 計画 ADR-0086 決定 1, [[IADR-0410]] (#1255):
            // 🔴 **本体は `AddDocumentTagUseCase` である。REST と east-west gRPC が同じ関数を通る。**
            // 主体（所有者束縛）と管理者ロールは**ここで**検証済みの `HttpContext.User` から取り、
            // gRPC 面は要求本文から取る —— **判定そのものは本体の中で 1 度だけ行われる。**
            var outcome = await tags.ExecuteAsync(
                id, req.Name ?? string.Empty,
                http.User.Identity?.Name,
                http.User.IsInRole(PlatformAuthPolicies.AdminRole),
                ct);

            return outcome.Status switch
            {
                // 🔴 **拒否は 404 である。403 にしない**（`PutBody` と同じ理由。本サービスは ABAC の
                // 読み取り判定を持たないため「読めるが書けない」と言い切れず、403 は文書 ID の
                // 総当たりで実在を明かす）。**「文書が無い」と同じ一本道である。**
                AddDocumentTagStatus.NotWritable => Results.NotFound(),
                AddDocumentTagStatus.UnknownTag =>
                    DocumentEndpoints.UnknownTagsProblem(outcome.UnknownTags ?? []),
                _ => Results.Ok(outcome.Document),
            };
        }).WithName("AddDocumentTag").Produces<DocumentDto>();
    }
}
