using DocumentService.Domain;
using DocumentService.Domain.Ports;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace DocumentService.Features.Documents;

// FR-19, NFR-09, 計画 ADR-0119 決定 3, ADR-0036 D-05・D-06・D-08, ADR-0034 決定 9, ADR-0054, ADR-0056 (#1614):
// **文書台帳の読み取りの可視性の唯一の判定点。** REST の読み取り 5 口と gRPC `DocumentRead` の 4 rpc は
// すべて `DocumentReadUseCase` を経てここを通る（判定器を 2 つにしない）。
//
// ■ 規則（計画 `07_abac-attribute-model` の `read` 規則のうち、個人資料の分岐）
//   - **組織文書**（`doc_scope` が `private-note` でない。キー欠落を含む）: 認証済みの全主体に返す。
//     🔴 **内容の ABAC（機密・部門・ライフサイクル）はまだここに無い**（#1615）。その間の実施点は BFF の
//     `BffScopeResolver` のままである（ADR-0119 決定 4 の暫定手段）。#1615 は下の `ResolveBranchesAsync` の結果を
//     組織文書にも当てることで入る —— 判定の点を増やさない。
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
public sealed class DocumentReadAccess(IDocumentReadScopeSource scopes)
{
    private readonly Dictionary<string, IReadOnlyList<IReadOnlyList<AttributeFilter>>?> _branches =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 主体がこの文書を読めるか。<paramref name="sharedWith"/> は共有台帳の `SubjectId` の集合
    /// （`DocumentEndpoints.ResolveSharedWithAsync` の値。共有なしは null / 空）。
    /// </summary>
    public async Task<bool> CanReadAsync(DocumentReadPrincipal principal, Document doc,
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

        // 共有先を `shared_with` の集合値属性として重ねた像で評価する（BFF の `AuthzView` と同じ像）。
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
