using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;

namespace AuthorizationService.Features.Users.DepartmentSync;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0115 決定 3, ADR-0088 決定 1, [[IADR-0473]] (#1573):
// **利用者属性 `department` を部門グループの所属に合わせる**（検知して直す形）。
//
// ■ なぜここ（AuthorizationService の定期処理）なのか —— 3 案の比較は [[IADR-0473]]
//   - ABAC が読む利用者属性は、本サービスが IdP から引き直す属性である（ADR-0088 決定 1）。トークンのクレームを
//     グループから導くマッパーでは、本サービスが読む属性は変わらない。
//   - realm の reconcile Job は人間の利用者の属性・グループを「実行時所有（触らない）」とする境界を持ち（IADR-0369 決定 2）、
//     client の secret を宣言へ戻す。そこへ利用者属性の書き込みを足すと境界が崩れる。
//   - 本サービスは既に realm を読み書きする主体（`identity-admin`）を 1 つだけ持つ。主体を増やさずに済む。
//
// ■ 🔴 **opt-in である**（`DepartmentAttributeSync:Mode` の既定は `Off`）。稼働 realm は AST の PoC と共有しており、
//   構成で明示するまで IdP へ 1 回も問い合わせない。`Report` は検知して記録するだけで書かない。`Fix` だけが書く。
//
// ■ 🔴 **直すのは「ちょうど 1 つの部門グループに属する人」の属性 `department` の 1 キーだけ**である。
//   グループは変えない（逆向きに直さない）。0 個・2 個以上は上書きしない。他の属性・ロール・クレーム・マッパー・
//   クライアントには触れない。部門グループに属さないサービスアカウント（AST のクライアントを含む）は対象に現れない。
//
// ■ 冪等: 直した後にもう一度回すと、全員が InSync（または Unresolved）になり書き込みは 0 件である。
public sealed class DepartmentAttributeSync(IIdentityAdminClient identity, ILogger<DepartmentAttributeSync> logger)
{
    /// <summary>1 周分の結果。<see cref="RootFound"/> が false なら `/department` グループが realm に無い（何もしない）。</summary>
    public sealed record Outcome(
        bool RootFound,
        IReadOnlyList<DepartmentAttributeFinding> Findings,
        int Corrected)
    {
        public int InSync => Findings.Count(f => f.Verdict == DepartmentAttributeVerdict.InSync);
        public int Mismatched => Findings.Count(f => f.Verdict == DepartmentAttributeVerdict.Mismatch);
        public int Unresolved => Findings.Count(f => f.Verdict == DepartmentAttributeVerdict.Unresolved);
    }

    public async Task<Outcome> RunAsync(DepartmentAttributeSyncMode mode, CancellationToken ct)
    {
        if (mode == DepartmentAttributeSyncMode.Off) return new Outcome(false, [], 0);

        var root = await identity.FindGroupByPathAsync(
            DepartmentAttributeReconciliation.DepartmentGroupRoot.TrimEnd('/'), ct);
        if (root is null)
        {
            logger.LogWarning(
                "部門の同期: realm に /department グループが無い。部門グループの所属から属性を合わせられない（何もしない）。");
            return new Outcome(false, [], 0);
        }

        var (codesByUser, currentByUser) = await CollectAsync(root, ct);
        var findings = DepartmentAttributeReconciliation.Plan(codesByUser, currentByUser);

        var corrected = 0;
        foreach (var finding in findings)
        {
            switch (finding.Verdict)
            {
                case DepartmentAttributeVerdict.Mismatch when mode == DepartmentAttributeSyncMode.Fix:
                    // 🔴 **グループのコードへ直す。** 書けたことは IdP 実装が読み直して確かめる（捨てられたら例外）。
                    var updated = await identity.SetDepartmentAttributeAsync(finding.UserId, finding.Expected!, ct);
                    if (updated is null)
                    {
                        logger.LogWarning(
                            "部門の同期: 利用者 {UserId} は直す前に居なくなった（削除された）。飛ばす。", finding.UserId);
                        continue;
                    }
                    corrected++;
                    logger.LogInformation(
                        "部門の同期: 利用者 {UserId} の属性 department を部門グループに合わせて直した（{Current} → {Expected}）。",
                        finding.UserId, Printable(finding.Current), finding.Expected);
                    break;

                case DepartmentAttributeVerdict.Mismatch:
                    logger.LogInformation(
                        "部門の同期（Report）: 利用者 {UserId} の属性 department が部門グループと食い違う（{Current} ≠ {Expected}）。書き込まない。",
                        finding.UserId, Printable(finding.Current), finding.Expected);
                    break;

                case DepartmentAttributeVerdict.Unresolved:
                    logger.LogInformation(
                        "部門の同期: 利用者 {UserId} は部門グループに {Count} 個属する（{Codes}）。どれに合わせるか決められないので属性を変えない。",
                        finding.UserId, finding.Codes.Count, string.Join(",", finding.Codes));
                    break;
            }
        }

        var outcome = new Outcome(true, findings, corrected);
        logger.LogInformation(
            "部門の同期（{Mode}）: 一致 {InSync} / 食い違い {Mismatched} / 直した {Corrected} / 未解決（複数所属）{Unresolved}。",
            mode, outcome.InSync, outcome.Mismatched, outcome.Corrected, outcome.Unresolved);
        return outcome;
    }

    // `/department` の木を辿り、利用者ごとに所属する部門コード（入れ子は上位に畳む）と現在の属性を集める。
    // 🔴 部門コードはグループの**パス**から取る（名前では取らない。`/teams/sales` と `/department/sales` を混ぜない）。
    private async Task<(Dictionary<string, IReadOnlySet<string>>, Dictionary<string, string?>)> CollectAsync(
        IdentityGroup root, CancellationToken ct)
    {
        var codes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var current = new Dictionary<string, string?>(StringComparer.Ordinal);

        var pending = new Queue<IdentityGroup>(await identity.ListSubGroupsAsync(root.Id, ct));
        while (pending.Count > 0)
        {
            var group = pending.Dequeue();
            var code = DepartmentAttributeReconciliation.CodeOf(group.Path);
            if (code is null) continue;

            foreach (var member in await identity.ListGroupMembersAsync(group.Id, ct))
            {
                if (!codes.TryGetValue(member.Id, out var set))
                    codes[member.Id] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(code);
                current[member.Id] = member.Attributes.TryGetValue(DepartmentAttributeReconciliation.AttributeKey, out var v)
                    ? v
                    : null;
            }

            foreach (var child in await identity.ListSubGroupsAsync(group.Id, ct))
                pending.Enqueue(child);
        }

        return (codes.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal), current);
    }

    // 属性値は管理者が入れた文字列である。ログ行を割らないよう改行・制御文字を落とす（無ければ「なし」）。
    private static string Printable(string? value)
        => value is null ? "(なし)" : new string([.. value.Where(c => !char.IsControl(c))]);
}

// FR-05, SC-17, [[IADR-0473]] (#1573): 同期の動作。**既定は Off**（opt-in）。
public enum DepartmentAttributeSyncMode
{
    /// <summary>何もしない（IdP へ問い合わせもしない）。既定。</summary>
    Off,

    /// <summary>食い違いを検知して記録するだけ（IdP へ書かない）。</summary>
    Report,

    /// <summary>食い違いを検知し、属性を部門グループのコードへ直す。</summary>
    Fix,
}
