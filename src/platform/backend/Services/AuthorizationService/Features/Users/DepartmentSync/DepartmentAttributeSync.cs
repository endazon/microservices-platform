using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;

namespace AuthorizationService.Features.Users.DepartmentSync;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0115 決定 3, ADR-0116 決定 2, ADR-0088 決定 1, [[IADR-0473]] (#1573, #1609):
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
// ■ 🔴 **書くのは属性 `department` の 1 キーだけ**である。グループは変えない（逆向きに直さない）。
//   - 部門グループにちょうど 1 つ属する人: グループのコードへ直す。
//   - ［2026-09-27 / #1609・計画 ADR-0116 決定 2］部門グループに 1 つも属さない人: 属性が残っていれば**消す**。
//   - 2 個以上属する人: 上書きしない・消さない（ADR-0116 決定 2 の対象外）。
//   他の属性・ロール・クレーム・マッパー・クライアントには触れない。**サービスアカウント（AST のクライアントを含む）は消さない。**
//
// ■ ［2026-09-27 / #1609］🔴 **「0 個」は全利用者の列挙を最後まで読めた周期にしか言わない**（原則 A: 不明は「無い」ではない）。
//   部門グループの所属者は `/department` の木から集まるが、グループから外れた人はそこに現れない。見つけるには全利用者の列挙が
//   要る（ADR-0116 実測 4）。列挙がページの途中で失敗した・打ち切られた周期は、**0 個の人を 1 人も消さない**
//   （列挙に現れなかった人は「居ない」ではなく「読めなかった」）。未完了は計器 `department_sync.enumeration_incomplete.total` と
//   Error ログで知らせ、1 つ属する人の是正は続ける（[[IADR-0413]] 決定 5 と同型の「黙った打ち切り」を持ち込まない）。
//   **消す直前にその人の所属を個別に読み直し**、部門グループが見つかれば消さない（所属者のページ送りで飛んだ人を 0 個と読まない）。
//
// ■ ［2026-09-26 / #1573 監査］🔴 **SC-17 の操作との競合**: Keycloak の利用者更新は表現全体の PUT で条件付き更新が無い。
//   計画の読み取りと書き込みの間に SC-17 の無効化（`enabled=false` ＋ 保持起点）が入ると、古い表現で上書きし得る。
//   書く直前に読み直し、有効状態・部門以外の属性が変わっていれば見送る（`DepartmentWriteOutcome.Changed`）。
//   **残る窓はその読み直しから PUT までの 1 往復**であり、ゼロにはできない（運用仕様書に明記）。消す書き込みも同じ規則である。
//
// ■ 1 人の失敗（例外）は数えて続ける。失敗数はログ（Warning）と計器 `DepartmentAttributeSyncMetrics` に出す。
//
// ■ 冪等: 直した・消した後にもう一度回すと、全員が InSync（または Unresolved）になり書き込みは 0 件である。
public sealed class DepartmentAttributeSync(
    IIdentityAdminClient identity, DepartmentAttributeSyncMetrics metrics, ILogger<DepartmentAttributeSync> logger)
{
    /// <summary>
    /// 1 周分の結果。<see cref="RootFound"/> が false なら `/department` グループが realm に無い（何もしない）。
    /// <see cref="SkippedChanged"/> は書く直前に利用者が変わっていたので見送った人数、<see cref="Failed"/> は書き込みが例外になった人数、
    /// <see cref="Cleared"/> は部門グループ 0 個で属性を消した人数。
    /// <see cref="EnumerationComplete"/> が false の周期は、0 個の人を計画に入れていない（誰も消していない）。
    /// </summary>
    public sealed record Outcome(
        bool RootFound,
        IReadOnlyList<DepartmentAttributeFinding> Findings,
        int Corrected,
        int SkippedChanged = 0,
        int Failed = 0,
        int Cleared = 0,
        bool EnumerationComplete = false)
    {
        public int InSync => Findings.Count(f => f.Verdict == DepartmentAttributeVerdict.InSync);
        public int Mismatched => Findings.Count(f => f.Verdict == DepartmentAttributeVerdict.Mismatch);
        public int Unresolved => Findings.Count(f => f.Verdict == DepartmentAttributeVerdict.Unresolved);
        public int Orphaned => Findings.Count(f => f.Verdict == DepartmentAttributeVerdict.Orphaned);
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
            metrics.RecordCycle("aborted");
            return new Outcome(false, [], 0);
        }

        var (codesByUser, currentByUser, observed) = await CollectAsync(root, ct);
        var enumerationComplete = await AddUsersWithoutDepartmentGroupAsync(codesByUser, currentByUser, observed, ct);
        var findings = DepartmentAttributeReconciliation.Plan(
            codesByUser.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal),
            currentByUser);

        int corrected = 0, cleared = 0, skipped = 0, failed = 0, notFound = 0;
        foreach (var finding in findings)
        {
            switch (finding.Verdict)
            {
                case DepartmentAttributeVerdict.Mismatch when mode == DepartmentAttributeSyncMode.Fix:
                case DepartmentAttributeVerdict.Orphaned when mode == DepartmentAttributeSyncMode.Fix:
                    // ［2026-09-26 / #1573 監査］🔴 **1 人の失敗で周期を止めない。** 例外（IdP の一時障害・属性が
                    // 捨てられた等）は利用者ごとに捕まえて数え、残りの人は続ける。失敗数はログと計器に出す。
                    try
                    {
                        var clearing = finding.Verdict == DepartmentAttributeVerdict.Orphaned;
                        var result = clearing
                            ? await ClearAsync(finding, observed[finding.UserId], ct)
                            // 🔴 **グループのコードへ直す。** 計画の読み取りの像を渡し、書く直前に変わっていれば IdP 実装が見送る。
                            : await identity.SetDepartmentAttributeAsync(
                                finding.UserId, finding.Expected!, observed[finding.UserId], ct);
                        switch (result.Outcome)
                        {
                            case DepartmentWriteOutcome.Applied when clearing:
                                cleared++;
                                logger.LogInformation(
                                    "部門の同期: 利用者 {UserId} は部門グループに 1 つも属さないので属性 department を消した（{Current} → なし）。",
                                    finding.UserId, Printable(finding.Current));
                                break;
                            case DepartmentWriteOutcome.Applied:
                                corrected++;
                                logger.LogInformation(
                                    "部門の同期: 利用者 {UserId} の属性 department を部門グループに合わせて直した（{Current} → {Expected}）。",
                                    finding.UserId, Printable(finding.Current), finding.Expected);
                                break;
                            case DepartmentWriteOutcome.Changed:
                                skipped++;
                                logger.LogInformation(
                                    "部門の同期: 利用者 {UserId} は読み取り後に有効状態・他の属性・部門グループの所属が変わった（管理画面の操作等）。"
                                    + "上書きしないよう今回は見送り、次の周期で読み直す。",
                                    finding.UserId);
                                break;
                            default:
                                notFound++;
                                logger.LogWarning(
                                    "部門の同期: 利用者 {UserId} は直す前に居なくなった（削除された）。飛ばす。", finding.UserId);
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        logger.LogError(ex,
                            "部門の同期: 利用者 {UserId} の属性 department を直せなかった。他の利用者は続ける。", finding.UserId);
                    }
                    break;

                case DepartmentAttributeVerdict.Mismatch:
                    logger.LogInformation(
                        "部門の同期（Report）: 利用者 {UserId} の属性 department が部門グループと食い違う（{Current} ≠ {Expected}）。書き込まない。",
                        finding.UserId, Printable(finding.Current), finding.Expected);
                    break;

                case DepartmentAttributeVerdict.Orphaned:
                    logger.LogInformation(
                        "部門の同期（Report）: 利用者 {UserId} は部門グループに 1 つも属さないのに属性 department（{Current}）を持つ。書き込まない。",
                        finding.UserId, Printable(finding.Current));
                    break;

                case DepartmentAttributeVerdict.Unresolved:
                    logger.LogInformation(
                        "部門の同期: 利用者 {UserId} は部門グループに {Count} 個属する（{Codes}）。どれに合わせるか決められないので属性を変えない。",
                        finding.UserId, finding.Codes.Count, string.Join(",", finding.Codes));
                    break;
            }
        }

        metrics.RecordUsers("corrected", corrected);
        metrics.RecordUsers("cleared", cleared);
        metrics.RecordUsers("skipped_changed", skipped);
        metrics.RecordUsers("failed", failed);
        metrics.RecordUsers("not_found", notFound);
        // ［2026-09-26 / #1573 監査］🔴 **直そうとした全員が「変わった」で見送られた周期は、別の結末として出す。**
        // 計画の読み取り（`/groups/{id}/members`・`/users`）と書く直前の読み直し（`/users/{id}`）で属性のキー集合が食い違う realm では、
        // 毎回 `Changed` になり、`Fix` が**黙って誰も直さない**。偶然の競合では全員が見送られることはまず無い。
        var attempted = corrected + cleared + skipped + failed + notFound;
        var allSkipped = attempted > 0 && skipped == attempted;
        if (allSkipped)
            logger.LogWarning(
                "部門の同期: 直そうとした {Count} 人すべてが「読み取り後に変わった」として見送られた。"
                + "所属者の一覧と利用者の個別取得で属性の見え方が違う可能性がある（Fix が何も直せていない）。"
                + "運用仕様書の「試験利用者 1 人での確認」を行うこと。",
                attempted);
        metrics.RecordCycle(allSkipped ? "all_skipped_changed" : failed > 0 ? "completed_with_failures" : "completed");

        var outcome = new Outcome(true, findings, corrected, skipped, failed, cleared, enumerationComplete);
        var summary =
            "部門の同期（{Mode}）: 一致 {InSync} / 食い違い {Mismatched} / 直した {Corrected} / 部門グループなし {Orphaned} / "
            + "消した {Cleared} / 見送り（変更あり）{Skipped} / 失敗 {Failed} / 未解決（複数所属）{Unresolved} / 全利用者の列挙 {Enumeration}。";
        var enumeration = enumerationComplete ? "完了" : "未完了（消去なし）";
        if (failed > 0 || !enumerationComplete)
            logger.LogWarning(summary, mode, outcome.InSync, outcome.Mismatched, corrected, outcome.Orphaned, cleared, skipped,
                failed, outcome.Unresolved, enumeration);
        else
            logger.LogInformation(summary, mode, outcome.InSync, outcome.Mismatched, corrected, outcome.Orphaned, cleared, skipped,
                failed, outcome.Unresolved, enumeration);
        return outcome;
    }

    // ［2026-09-27 / #1609］🔴 **消す直前に、その人の所属を個別に読み直す。** 部門グループが 1 つでも見つかれば消さない
    // （`Changed` として見送る）。所属者の一覧はページ送り（offset）であり、並行した所属の変更でページの境目の人が
    // 1 人飛ぶことがある。飛んだ人を「0 個」と読んで消さないための歯止めである（消す向きの誤りは、その人から部門の資料を奪う）。
    private async Task<DepartmentWriteResult> ClearAsync(
        DepartmentAttributeFinding finding, IdentityUser observed, CancellationToken ct)
    {
        var groups = await identity.GetUserGroupsAsync(finding.UserId, ct);
        if (groups.Any(g => DepartmentAttributeReconciliation.CodeOf(g.Path) is not null))
            return DepartmentWriteResult.Changed;
        return await identity.ClearDepartmentAttributeAsync(finding.UserId, observed, ct);
    }

    // ［2026-09-27 / #1609・計画 ADR-0116 決定 2］全利用者を列挙し、部門グループの所属者に現れなかった人のうち
    // 属性 `department` を持つ人を「0 個」として計画へ足す。**列挙を最後まで読めたときだけ足す**（戻り値 true）。
    //
    // 🔴 **列挙が失敗した・打ち切られたら 1 人も足さない。** 部分的な列挙から「0 個」を推定しない（原則 A）。
    // 1 つ属する人の是正はこの結果に依らない（所属者は木から集めてある）ので、周期は止めない。
    // 取り消し（ホストの停止）だけは周期ごと中断する（利用者の失敗や列挙の失敗として数えない）。
    // サービスアカウント（利用者名の接頭辞）は足さない —— 機械の主体の属性は所属から何も言えない。
    private async Task<bool> AddUsersWithoutDepartmentGroupAsync(
        Dictionary<string, HashSet<string>> codes,
        Dictionary<string, string?> current,
        Dictionary<string, IdentityUser> observed,
        CancellationToken ct)
    {
        UserEnumeration enumeration;
        try
        {
            enumeration = await identity.ListAllUsersAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordEnumerationIncomplete("page_failed");
            logger.LogError(ex,
                "部門の同期: 全利用者の列挙が途中で失敗した。部門グループから外れた利用者を判定できないため、"
                + "この周期では誰の属性 department も消さない（1 つ属する人の是正は続ける）。");
            return false;
        }

        if (!enumeration.Complete)
        {
            metrics.RecordEnumerationIncomplete("truncated");
            logger.LogError(
                "部門の同期: 全利用者の列挙が打ち切られた（{Count} 人まで読んだ）。部門グループから外れた利用者を判定できないため、"
                + "この周期では誰の属性 department も消さない（1 つ属する人の是正は続ける）。",
                enumeration.Users.Count);
            return false;
        }

        foreach (var user in enumeration.Users)
        {
            if (string.IsNullOrEmpty(user.Id) || codes.ContainsKey(user.Id)) continue;
            if (DepartmentAttributeReconciliation.IsServiceAccount(user.Username)) continue;
            if (!user.Attributes.TryGetValue(DepartmentAttributeReconciliation.AttributeKey, out var department)) continue;

            codes[user.Id] = new HashSet<string>(StringComparer.Ordinal);
            current[user.Id] = department;
            observed[user.Id] = user;
        }
        return true;
    }

    // `/department` の木を辿り、利用者ごとに所属する部門コード（入れ子は上位に畳む）と現在の属性を集める。
    // 🔴 部門コードはグループの**パス**から取る（名前では取らない。`/teams/sales` と `/department/sales` を混ぜない）。
    // 所属者の像（`observed`）も返す —— 書く直前に「読み取りから変わっていないか」を IdP 実装が確かめる基準である。
    private async Task<(Dictionary<string, HashSet<string>>, Dictionary<string, string?>, Dictionary<string, IdentityUser>)>
        CollectAsync(IdentityGroup root, CancellationToken ct)
    {
        var codes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var current = new Dictionary<string, string?>(StringComparer.Ordinal);
        var observed = new Dictionary<string, IdentityUser>(StringComparer.Ordinal);

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
                observed[member.Id] = member;
                current[member.Id] = member.Attributes.TryGetValue(DepartmentAttributeReconciliation.AttributeKey, out var v)
                    ? v
                    : null;
            }

            foreach (var child in await identity.ListSubGroupsAsync(group.Id, ct))
                pending.Enqueue(child);
        }

        return (codes, current, observed);
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

    /// <summary>食い違いを検知し、属性を部門グループのコードへ直す（部門グループ 0 個の人の属性は消す）。</summary>
    Fix,
}
