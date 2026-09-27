using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents.ContentAbac;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace DocumentService.Features.Documents;

// FR-19, NFR-09, 計画 ADR-0119 決定 3, ADR-0036 D-05・D-06・D-08, ADR-0034 決定 9, ADR-0054, ADR-0056 (#1614):
// **文書台帳の読み取りの可視性の唯一の判定点。** REST の読み取り 5 口と gRPC `DocumentRead` の 4 rpc は
// すべて `DocumentReadUseCase` を経てここを通る（判定器を 2 つにしない）。
//
// ■ 規則（計画 `07_abac-attribute-model` の `read` 規則のうち、個人資料の分岐）
//   - **組織文書**（`doc_scope` が `private-note` でない。キー欠落を含む）: 認証済みの全主体に返す。
//     ［2026-09-28 更新 / #1615］内容の ABAC は**門（`IContentAbacGate`）が開いたときだけ**下の「開いた枝」で効く。
//     門が閉じている間（既定 Off・未確認・数えられない）は、この「閉じた枝」のまま 1 ビットも変えない。
//   - **個人資料**: 名前の分かる**利用者**のうち、次のいずれかを満たす者にだけ返す（OR）。
//       1. 所有者（`DocumentBodyIntake.IsOwnedBy` —— 本文の書き込みと同じ比較）
//       2. 利用者の共有先（主体 ∈ 共有台帳の `SubjectId`）
//       3. グループの共有先 —— **認可サービスへ問う**（所属は認可サービスが IdP から引く）。許可の根拠は
//          BFF・検索・グラフと同じ述語（`AttributeFilterMatch.MatchesAll` ∧ `PrivateNoteVisibility.BranchMayGrant`）
//     機械の主体には一律に返さない（ADR-0034 決定 9）。管理者ロールも特別扱いしない（ADR-0036 D-08）。
//
// 🔴 **問い合わせは 3 の分岐に来たときだけ、要求ごとに高々 1 回**（利用者ごとに memo。本クラスは要求の寿命）。
//   所有者・利用者の共有先・組織文書だけの読み取りでは 1 度も問わない ―― 認可サービスの不調で
//   自分の資料や組織文書が読めなくなることはない。
// 🔴 **引けない・未構成・許可なしは「読めない」**（fail-closed）。ADR-0036 D-14（判定をキャッシュするなら
//   主体をキーに含める）は memo のキーを利用者名にすることで満たす。
//
// ── FR-05, NFR-09, 計画 ADR-0121 決定 2・4・5, ADR-0119 決定 3, ADR-0036 D-01・D-08, [[IADR-0481]] 決定 6 (#1615) ──
// ■ **開いた枝（内容の ABAC）**: 判定は認可サービスの `read` の分岐ただ 1 つで行う（判定器を DocumentService に残さない ——
//   ADR-0121 決定 5）。所有者は所有者の read ポリシー、利用者共有は `${current_user}`、グループ共有は `${current_groups}` の
//   束縛として認可サービスが答える。組織文書にも同じ分岐を当てる（機密・部門・ライフサイクル）。述語は BFF・検索・グラフと同じ
//   `AttributeFilterMatch.MatchesAll` ∧ `PrivateNoteVisibility.BranchMayGrant`（静的属性の分岐は個人資料を開かない ＝ D-08）。
//   ABAC の外の規則として、機械の主体は個人資料を読まない（ADR-0034 決定 9）。主体名が決まらない・分岐が引けない・許可なし・
//   未構成は**何も読めない**（fail-closed。認可サービスが落ちたら自分の資料も読めないことは ADR-0121 決定 5 が受け入れた）。
// ■ 🔴 **門は要求の中で最初に判定するときに 1 度だけ読み、以後はその値に固定する。** 一覧の途中で門が開いても、1 つの応答に
//   旧判定と新判定が混ざらない（門は開く向きにしか動かない〔ラッチ〕ので、次の要求から開く）。
// ■ 一覧の問い合わせは件数に比例しない —— 1 要求の主体は 1 人で、分岐は主体名で memo する（要求ごとに高々 1 回）。
public sealed class DocumentReadAccess(IDocumentReadScopeSource scopes, IContentAbacGate gate)
{
    private readonly Dictionary<string, IReadOnlyList<IReadOnlyList<AttributeFilter>>?> _branches =
        new(StringComparer.Ordinal);

    private bool? _contentAbac;

    /// <summary>
    /// この要求で内容の ABAC が効いているか（門の状態を最初に読んだ値。以後は変わらない）。#1615。
    /// </summary>
    public bool ContentAbacEnabled => _contentAbac ??= gate.IsOpen;

    /// <summary>
    /// 主体がこの文書を読めるか。<paramref name="sharedWith"/> は共有台帳の `SubjectId` の集合
    /// （`DocumentEndpoints.ResolveSharedWithAsync` の値。共有なしは null / 空）。
    /// </summary>
    public Task<bool> CanReadAsync(DocumentReadPrincipal principal, Document doc,
        IReadOnlyCollection<string>? sharedWith, CancellationToken ct)
        => ContentAbacEnabled
            ? CanReadByContentAbacAsync(principal, doc, sharedWith, ct)
            : CanReadPrivateNoteRuleAsync(principal, doc, sharedWith, ct);

    // 門が開いた枝（#1615）。判定は認可サービスの分岐だけで行う。
    private async Task<bool> CanReadByContentAbacAsync(DocumentReadPrincipal principal, Document doc,
        IReadOnlyCollection<string>? sharedWith, CancellationToken ct)
    {
        // ADR-0034 決定 9: 機械の主体は個人資料を一律に読まない（分岐が一致しても）。
        if (principal.IsMachine && DocumentScopes.IsPrivateNote(doc.Attributes))
            return false;

        if (principal.AbacSubject is not { } subject)
            return false;

        var branches = await ResolveBranchesAsync(subject, ct);
        if (branches is null)
            return false;

        return Grants(branches, doc, sharedWith);
    }

    // 門が閉じている枝（#1614 のまま。1 ビットも変えない）。
    private async Task<bool> CanReadPrivateNoteRuleAsync(DocumentReadPrincipal principal, Document doc,
        IReadOnlyCollection<string>? sharedWith, CancellationToken ct)
    {
        if (!DocumentScopes.IsPrivateNote(doc.Attributes))
            return true;

        if (principal.PrivateNoteSubject is not { } subject)
            return false;

        if (DocumentBodyIntake.IsOwnedBy(doc.Attributes, subject))
            return true;

        // 共有が 1 件も無ければ、所有者でない限り誰にも返らない（認可サービスへ問うまでもない）。
        if (sharedWith is not { Count: > 0 })
            return false;

        if (sharedWith.Contains(subject, StringComparer.Ordinal))
            return true;

        var branches = await ResolveBranchesAsync(subject, ct);
        if (branches is null)
            return false;

        return Grants(branches, doc, sharedWith);
    }

    // 共有先を `shared_with` の集合値属性として重ねた像で評価する（BFF の `AuthzView` と同じ像）。
    private static bool Grants(IReadOnlyList<IReadOnlyList<AttributeFilter>> branches, Document doc,
        IReadOnlyCollection<string>? sharedWith)
    {
        var view = DocumentAttributeEncoding.WithSharedWith(doc.Attributes, sharedWith);
        return branches.Any(b =>
            AttributeFilterMatch.MatchesAll(view, b)
            && PrivateNoteVisibility.BranchMayGrant(view, b));
    }

    private async Task<IReadOnlyList<IReadOnlyList<AttributeFilter>>?> ResolveBranchesAsync(
        string subject, CancellationToken ct)
    {
        if (_branches.TryGetValue(subject, out var cached))
            return cached;

        var resolved = await scopes.ResolveReadBranchesAsync(subject, ct);
        _branches[subject] = resolved;
        return resolved;
    }
}
