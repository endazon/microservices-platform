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
// ■ ［2026-09-26 / #1573 監査］🔴 **SC-17 の操作との競合**: Keycloak の利用者更新は表現全体の PUT で条件付き更新が無い。
//   計画の読み取りと書き込みの間に SC-17 の無効化（`enabled=false` ＋ 保持起点）が入ると、古い表現で上書きし得る。
//   書く直前に読み直し、有効状態・部門以外の属性が変わっていれば見送る（`DepartmentWriteOutcome.Changed`）。
//   **残る窓はその読み直しから PUT までの 1 往復**であり、ゼロにはできない（運用仕様書に明記）。
//
// ■ 1 人の失敗（例外）は数えて続ける。失敗数はログ（Warning）と計器 `DepartmentAttributeSyncMetrics` に出す。
//
// ■ 冪等: 直した後にもう一度回すと、全員が InSync（または Unresolved）になり書き込みは 0 件である。
public sealed class DepartmentAttributeSync(
    IIdentityAdminClient identity, DepartmentAttributeSyncMetrics metrics, ILogger<DepartmentAttributeSync> logger)
{
    /// <summary>
    /// 1 周分の結果。<see cref="RootFound"/> が false なら `/department` グループが realm に無い（何もしない）。
    /// <see cref="SkippedChanged"/> は書く直前に利用者が変わっていたので見送った人数、<see cref="Failed"/> は書き込みが例外になった人数。
    /// </summary>
    public sealed record Outcome(
        bool RootFound,
        IReadOnlyList<DepartmentAttributeFinding> Findings,
        int Corrected,
        int SkippedChanged = 0,
        int Failed = 0)
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
            metrics.RecordCycle("aborted");
            return new Outcome(false, [], 0);
        }

        var (codesByUser, currentByUser, observed) = await CollectAsync(root, ct);
        var findings = DepartmentAttributeReconciliation.Plan(codesByUser, currentByUser);

        int corrected = 0, skipped = 0, failed = 0, notFound = 0;
        foreach (var finding in findings)
        {
            switch (finding.Verdict)
            {
                case DepartmentAttributeVerdict.Mismatch when mode == DepartmentAttributeSyncMode.Fix:
                    // ［2026-09-26 / #1573 監査］🔴 **1 人の失敗で周期を止めない。** 例外（IdP の一時障害・属性が
                    // 捨てられた等）は利用者ごとに捕まえて数え、残りの人は続ける。失敗数はログと計器に出す。
                    try
                    {
                        // 🔴 **グループのコードへ直す。** 計画の読み取りの像を渡し、書く直前に変わっていれば IdP 実装が見送る。
                        var result = await identity.SetDepartmentAttributeAsync(
                            finding.UserId, finding.Expected!, observed[finding.UserId], ct);
                        switch (result.Outcome)
                        {
                            case DepartmentWriteOutcome.Applied:
                                corrected++;
                                logger.LogInformation(
                                    "部門の同期: 利用者 {UserId} の属性 department を部門グループに合わせて直した（{Current} → {Expected}）。",
                                    finding.UserId, Printable(finding.Current), finding.Expected);
                                break;
                            case DepartmentWriteOutcome.Changed:
                                skipped++;
                                logger.LogInformation(
                                    "部門の同期: 利用者 {UserId} は読み取り後に有効状態か他の属性が変わった（管理画面の操作等）。"
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

                case DepartmentAttributeVerdict.Unresolved:
                    logger.LogInformation(
                        "部門の同期: 利用者 {UserId} は部門グループに {Count} 個属する（{Codes}）。どれに合わせるか決められないので属性を変えない。",
                        finding.UserId, finding.Codes.Count, string.Join(",", finding.Codes));
                    break;
            }
        }

        metrics.RecordUsers("corrected", corrected);
        metrics.RecordUsers("skipped_changed", skipped);
        metrics.RecordUsers("failed", failed);
        metrics.RecordUsers("not_found", notFound);
        // ［2026-09-26 / #1573 監査］🔴 **直そうとした全員が「変わった」で見送られた周期は、別の結末として出す。**
        // 計画の読み取り（`/groups/{id}/members`）と書く直前の読み直し（`/users/{id}`）で属性のキー集合が食い違う realm では、
        // 毎回 `Changed` になり、`Fix` が**黙って誰も直さない**。偶然の競合では全員が見送られることはまず無い。
        var attempted = corrected + skipped + failed + notFound;
        var allSkipped = attempted > 0 && skipped == attempted;
        if (allSkipped)
            logger.LogWarning(
                "部門の同期: 直そうとした {Count} 人すべてが「読み取り後に変わった」として見送られた。"
                + "所属者の一覧と利用者の個別取得で属性の見え方が違う可能性がある（Fix が何も直せていない）。"
                + "運用仕様書の「試験利用者 1 人での確認」を行うこと。",
                attempted);
        metrics.RecordCycle(allSkipped ? "all_skipped_changed" : failed > 0 ? "completed_with_failures" : "completed");

        var outcome = new Outcome(true, findings, corrected, skipped, failed);
        var summary =
            "部門の同期（{Mode}）: 一致 {InSync} / 食い違い {Mismatched} / 直した {Corrected} / 見送り（変更あり）{Skipped} / "
            + "失敗 {Failed} / 未解決（複数所属）{Unresolved}。";
        if (failed > 0)
            logger.LogWarning(summary, mode, outcome.InSync, outcome.Mismatched, corrected, skipped, failed, outcome.Unresolved);
        else
            logger.LogInformation(summary, mode, outcome.InSync, outcome.Mismatched, corrected, skipped, failed, outcome.Unresolved);
        return outcome;
    }

    // `/department` の木を辿り、利用者ごとに所属する部門コード（入れ子は上位に畳む）と現在の属性を集める。
    // 🔴 部門コードはグループの**パス**から取る（名前では取らない。`/teams/sales` と `/department/sales` を混ぜない）。
    // 所属者の像（`observed`）も返す —— 書く直前に「読み取りから変わっていないか」を IdP 実装が確かめる基準である。
    private async Task<(Dictionary<string, IReadOnlySet<string>>, Dictionary<string, string?>, Dictionary<string, IdentityUser>)>
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

        return (codes.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal),
            current, observed);
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
