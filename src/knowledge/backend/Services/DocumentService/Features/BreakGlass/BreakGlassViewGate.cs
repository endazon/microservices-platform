using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.BreakGlass;

// FR-19, UC-11, SC-03, SC-19, 計画 ADR-0036 D-08, 08_data-egress-policy §経路 1, [[IADR-0610]] (#2046):
// **緊急アクセスで指名された管理者に、対象の 1 件の閲覧だけを許す判定点**（planning#841 の裁定による）。
//
// ■ どこから呼ばれるか（ここ以外から閲覧は開かない）
//   `DocumentReadUseCase.GetForViewAsync` だけが呼ぶ。そこへ来るのは SC-03 の閲覧の 3 口
//   （REST `GET /documents/{id}`・gRPC `DocumentRead.GetDocument`・gRPC `DocumentRead.GetDocumentContent`）だけである。
//   一覧・ページ・版の一覧・特定版・MCP のツール実行は `GetAsync` / `ListAsync` を通り、**ここを通らない**
//   ＝ 緊急アクセスでは開かない（一覧は描かない —— SC-19「管理者が全利用者の個人資料を閲覧する画面を描かない」）。
//   書き込み（編集・削除・共有の変更・本文の投入・露出の変更）は所有者の判定を通る別の口であり、読み取りの判定を見ない。
//   ［2026-10-24 / [[IADR-0611]]］実体は `DocumentReadUseCase.FindForViewAsync`（`GetForViewAsync` はその写し）。gRPC の 2 口は
//   呼び出し元が `allow_break_glass` を立てたときだけここへ来る（既定は来ない ＝ 前段の確認で閲覧の記録を作らない）。
//
// ■ 許す条件（すべて AND。1 つでも欠けたら許さない ＝ 通常の「無い」と同じ 404 / found=false）
//   1. 主体が名前の分かる人である（機械・名前の無い主体は不可。ADR-0034 決定 9）
//   2. 文書が個人資料である（組織文書は通常の判定だけで決まる。ここで開くものは無い）
//   3. **サーバ側の発動記録**に、この文書 × この主体（指名された管理者）の行があり、**いま**有効である
//      （発動以後・終了日時〔本人の終了か発動＋15 分の早いほう〕より前。時刻は注入した時計）
//   4. 発動したのが**現在の所有者**である（所有者が変わった後に古い同意で開かない）
//   5. 資料が論理削除されていない（ADR-0142: 論理削除の間は共有先も開けない。同じ向き）
//   6. 主体が**いま** `platform-admin` を持つ（基盤の利用者名簿で確かめる。引けない・未構成は不可 ＝ fail-closed）
//   7. 閲覧の監査ログ 1 行の保存に成功した（保存できなければ例外のまま上げる ＝ 記録の無い閲覧を作らない）
//
// 🔴 **トークン・本文の主張では判定しない**（D-08「有効性はサーバ側の発動記録で判定」）。ロールも本文の主張ではなく名簿に訊く
//   （`IApproverRoleDirectory` ＝ 計画 ADR-0088 と同じ理由）。
public sealed class BreakGlassViewGate(
    DocumentDbContext db,
    IApproverRoleDirectory roles,
    TimeProvider clock,
    IAuditLogger audit)
{
    /// <summary>
    /// 主体に、緊急アクセスとしてこの文書の閲覧を許すか。許すときは閲覧の監査ログ 1 行を保存してから true を返す。
    /// </summary>
    public async Task<bool> TryAuthorizeViewAsync(DocumentReadPrincipal principal, Document doc, string viewTarget,
        CancellationToken ct)
    {
        if (principal.PrivateNoteSubject is not { } user)
            return false;
        if (!DocumentScopes.IsPrivateNote(doc.Attributes))
            return false;

        // 指名先は正規形（小文字）で保存してある。トークンの利用者名も同じ関数で畳んで比べる（大小を無視した照合）。
        var nominee = Activate.BreakGlassNames.Normalize(user);
        var now = clock.GetUtcNow();
        var candidates = await db.BreakGlassGrants
            .Where(g => g.DocumentId == doc.Id && g.NomineeId == nominee
                && g.ActivatedAt <= now && g.ExpiresAt > now)
            .ToListAsync(ct);
        // 本人の早期終了（EndedAt）を含む判定はドメインの 1 関数で行う（述語を 2 つに分けない）。
        var grant = candidates.Where(g => g.IsActiveAt(now)).OrderByDescending(g => g.ActivatedAt).FirstOrDefault();
        if (grant is null)
            return false;

        if (!DocumentBodyIntake.IsOwnedBy(doc.Attributes, grant.OwnerId))
            return false;

        var note = await db.PrivateNotes.FindAsync([doc.Id], ct);
        if (note is null || note.IsDeleted || !string.Equals(note.OwnerId, grant.OwnerId, StringComparison.Ordinal))
            return false;

        if (await roles.GetAdminStateAsync(nominee, ct) != ApproverAdminState.Admin)
            return false;

        db.BreakGlassAuditEntries.Add(BreakGlassAuditEntry.Viewed(grant, viewTarget, now));
        await db.SaveChangesAsync(ct);
        // 構造化ログへも同じ記録の 2 つ目の出口を出す（`SyncAuditRecorder` と同じ作法）。理由・本文は載せない。
        audit.Record("break-glass.view", nominee, "granted", $"documentId={doc.Id} grantId={grant.Id} target={viewTarget}");
        return true;
    }
}
