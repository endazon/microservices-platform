using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;
using AuthorizationService.Features.Authz;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Features.Users.Department;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0116 決定 1, ADR-0115 決定 3, [[IADR-0473]] (#1610):
// **SC-17 の部門欄 ＝ 部門グループの所属の変更。** 利用者属性 `department` は書かない（部門の同期が追いつく）。
//
// ■ 読む・書くのは IdP（`IIdentityAdminClient`）の 5 つの口だけである: `FindByIdAsync`（実在と属性）・`GetUserGroupsAsync`（所属）・
//   `FindGroupByPathAsync`（目的のグループ）・`JoinGroupAsync` / `LeaveGroupAsync`（所属の変更）。選択肢は属性辞書と同じ読み取り
//   （`AttributeDictionary.ReadDepartmentDomainAsync`。IADR-0477）。🔴 **属性を書く口（差し替え・部門の書き込み・消去）は呼ばない。**
//
// ■ 🔴 **先に入れてから外す。** 途中で失敗しても部門グループ 0 個にならない向きである（原則 A: 黙って部門を失わせない）。
//   外す途中で失敗したら補償する —— 外したグループ（と失敗したグループ）へ入れ直し、**入れ直しがすべて成功したときだけ**
//   入れた目的のグループから外す。入れ直しが 1 つでも失敗したら目的のグループは残す（0 個にしない）。結果はいまの所属つきで返す。
// ■ 2 個以上の部門グループに属する人は変えない（`DepartmentMembershipPlan` の注記）。
public sealed class UserDepartmentService(
    IIdentityAdminClient identity, AttributeDictionary dictionary, ILogger<UserDepartmentService> logger)
{
    /// <summary>SC-17: 利用者の部門（所属・属性・選択肢）を読む。</summary>
    public async Task<UserDepartmentOutcome> ReadAsync(string userId, CancellationToken ct)
    {
        var (user, unavailable) = await FindUserAsync(userId, ct);
        if (unavailable) return UserDepartmentOutcome.RealmUnavailable();
        if (user is null) return UserDepartmentOutcome.NotFound();

        var domain = await dictionary.ReadDepartmentDomainAsync(ct);
        if (!domain.Known) return UserDepartmentOutcome.RealmUnavailable();

        var groups = await ReadGroupsOrNullAsync(userId, ct);
        if (groups is null) return UserDepartmentOutcome.RealmUnavailable();
        return UserDepartmentOutcome.Ok(View(user, groups, domain));
    }

    /// <summary>
    /// SC-17: 部門を変える（<paramref name="requested"/> が null・空なら部門なし）。部門グループの所属だけを変える。
    /// </summary>
    public async Task<UserDepartmentOutcome> ChangeAsync(string userId, string? requested, CancellationToken ct)
    {
        var targetCode = string.IsNullOrWhiteSpace(requested) ? null : requested;

        var (user, unavailable) = await FindUserAsync(userId, ct);
        if (unavailable) return UserDepartmentOutcome.RealmUnavailable();
        if (user is null) return UserDepartmentOutcome.NotFound();

        var domain = await dictionary.ReadDepartmentDomainAsync(ct);
        if (!domain.Known) return UserDepartmentOutcome.RealmUnavailable();

        // 値域は realm の部門グループのコード（`/department` の直下の子）。序数一致（部門コードは大小文字を区別する）。
        string? targetGroupId = null;
        if (targetCode is not null)
        {
            if (!domain.Codes.Contains(targetCode, StringComparer.Ordinal))
            {
                return UserDepartmentOutcome.Invalid(
                    $"部門 '{targetCode}' は realm の部門グループにありません"
                    + $"（部門グループ: {(domain.Codes.Count == 0 ? "なし" : string.Join(", ", domain.Codes))}）。");
            }

            IdentityGroup? target;
            try
            {
                target = await identity.FindGroupByPathAsync(
                    DepartmentAttributeReconciliation.DepartmentGroupRoot + targetCode, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "SC-17 の部門: 目的の部門グループを引けなかった。何も変えない。");
                return UserDepartmentOutcome.RealmUnavailable();
            }
            if (target is null)
                return UserDepartmentOutcome.Invalid($"部門グループ '/department/{targetCode}' が見つかりません（削除された可能性）。");
            targetGroupId = target.Id;
        }

        var before = await ReadGroupsOrNullAsync(userId, ct);
        if (before is null) return UserDepartmentOutcome.RealmUnavailable();

        var plan = DepartmentMembershipPlan.Plan(before, targetCode, targetGroupId);
        switch (plan.Verdict)
        {
            case DepartmentMembershipVerdict.MultipleDepartments:
                return UserDepartmentOutcome.Conflict(
                    $"この利用者は複数の部門グループ（{string.Join(", ", plan.CurrentCodes)}）に属しているため、この画面では部門を変えません。"
                    + "部門は 1 つです。Keycloak で所属を 1 つにしてから変えてください。");
            case DepartmentMembershipVerdict.Unchanged:
                return UserDepartmentOutcome.Ok(View(user, before, domain));
        }

        var failure = await ApplyAsync(userId, plan);
        if (failure is not null) return failure;

        // 書いた後に読み直し、期待どおりか確かめる（並行した所属の変更と競合したら、黙って成功と言わない）。
        var after = await ReadGroupsOrNullAsync(userId, ct);
        if (after is null)
        {
            return UserDepartmentOutcome.Failed(
                "部門グループの所属を変えましたが、読み直せませんでした。画面を読み込み直して確かめてください。");
        }
        var afterCodes = DepartmentMembershipPlan.CodesOf(after);
        var expected = targetCode is null ? [] : new[] { targetCode };
        if (!afterCodes.SequenceEqual(expected, StringComparer.Ordinal))
        {
            return UserDepartmentOutcome.Conflict(
                $"部門グループの所属を変えましたが、読み直すと {Describe(afterCodes)} でした（他の変更と重なった可能性があります）。");
        }

        logger.LogInformation(
            "SC-17 の部門: 利用者 {UserId} の部門グループを {Before} から {After} へ変えた（属性 department は書かない。部門の同期が追いつく）。",
            Sanitize(userId), string.Join(",", plan.CurrentCodes), targetCode ?? "(なし)");
        var (reloaded, _) = await FindUserAsync(userId, ct);
        return UserDepartmentOutcome.Ok(View(reloaded ?? user, after, domain));
    }

    // 利用者を引く。引けなかった（例外）は「居ない」と混ぜない（`unavailable`）。
    private async Task<(IdentityUser? User, bool Unavailable)> FindUserAsync(string userId, CancellationToken ct)
    {
        try
        {
            return (await identity.FindByIdAsync(userId, ct), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "SC-17 の部門: 利用者 {UserId} を引けなかった。", Sanitize(userId));
            return (null, true);
        }
    }

    // 🔴 先に入れてから外す。外す途中の失敗は補償する（型の注記）。成功なら null。
    // 🔴 **書き込みの途中は要求の取り消しで止めない**（`CancellationToken.None`）。入れた後・外す前に止めると、2 つの部門グループに
    // 属したまま残る。各往復は HTTP クライアントの時間切れで必ず終わる（時間切れは失敗として補償する）。
    private async Task<UserDepartmentOutcome?> ApplyAsync(string userId, DepartmentMembershipChange plan)
    {
        var ct = CancellationToken.None;
        if (plan.JoinGroupId is { } join)
        {
            try
            {
                // 居ない（404）は、利用者か目的のグループが読み取りの後に消えた。まだ何も変えていない。
                if (!await identity.JoinGroupAsync(userId, join, ct))
                    return UserDepartmentOutcome.Conflict("利用者か部門グループが見つかりません（削除された可能性）。部門は変えていません。");
            }
            catch (Exception ex)
            {
                // 入ったかどうか分からない（時間切れの後に反映されていることがある）ので、外しておく（冪等）。
                // この時点ではまだ何も外していないので、外しても 0 個にはならない（元の所属は残っている）。
                logger.LogError(ex, "SC-17 の部門: 利用者 {UserId} を部門グループへ入れられなかった。元に戻す。", Sanitize(userId));
                var undone = await TryAsync(() => identity.LeaveGroupAsync(userId, join, ct));
                return await PartialFailureAsync(userId, restored: undone, ct);
            }
        }

        var left = new List<string>();
        foreach (var leave in plan.LeaveGroupIds)
        {
            try
            {
                // 居ない（404）は、そのグループ（か利用者）が消えた ＝ 所属はもう無い。外れたものとして続ける
                // （利用者が消えた場合は、後の読み直しが失敗として返す）。
                await identity.LeaveGroupAsync(userId, leave, ct);
                left.Add(leave);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SC-17 の部門: 利用者 {UserId} を元の部門グループから外せなかった。元に戻す。", Sanitize(userId));
                // 失敗した 1 つも入れ直す（外れたかどうか分からない。入れるのは冪等）。
                left.Add(leave);
                var rejoined = true;
                foreach (var groupId in left)
                    rejoined &= await TryAsync(() => identity.JoinGroupAsync(userId, groupId, ct));

                // 🔴 **入れ直しがすべて成功したときだけ、目的のグループから外す**（0 個にしない）。
                var restored = rejoined;
                if (rejoined && plan.JoinGroupId is { } joined)
                    restored = await TryAsync(() => identity.LeaveGroupAsync(userId, joined, ct));
                return await PartialFailureAsync(userId, restored, ct);
            }
        }
        return null;
    }

    private async Task<UserDepartmentOutcome> PartialFailureAsync(string userId, bool restored, CancellationToken ct)
    {
        var now = await ReadGroupsOrNullAsync(userId, ct);
        var state = now is null ? "読み直せませんでした" : Describe(DepartmentMembershipPlan.CodesOf(now));
        if (!restored)
        {
            logger.LogError(
                "SC-17 の部門: 利用者 {UserId} の部門グループの変更が途中で失敗し、元に戻せなかった（いまの部門グループ: {State}）。Keycloak で確かめること。",
                Sanitize(userId), state);
        }
        return UserDepartmentOutcome.Failed(restored
            ? $"部門グループの所属を変えられませんでした。元に戻しました（いまの部門グループ: {state}）。もう一度保存してください。"
            : $"部門グループの所属の変更が途中で失敗し、元に戻せませんでした（いまの部門グループ: {state}）。"
              + "部門グループを 1 つも持たない状態にはしていません。Keycloak で所属を確かめてください。");
    }

    private async Task<IReadOnlyList<IdentityGroup>?> ReadGroupsOrNullAsync(string userId, CancellationToken ct)
    {
        try
        {
            return await identity.GetUserGroupsAsync(userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "SC-17 の部門: 利用者 {UserId} の所属を読めなかった。", Sanitize(userId));
            return null;
        }
    }

    private async Task<bool> TryAsync(Func<Task<bool>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SC-17 の部門: 補償の操作が失敗した。");
            return false;
        }
    }

    private static UserDepartmentDto View(
        IdentityUser user, IReadOnlyList<IdentityGroup> groups, DepartmentDomainReading domain)
        => new(
            [.. DepartmentMembershipPlan.CodesOf(groups)],
            user.Attributes.TryGetValue(DepartmentAttributes.Key, out var attribute) ? attribute : null,
            [.. domain.Codes]);

    private static string Describe(IReadOnlyList<string> codes) => codes.Count == 0 ? "なし" : string.Join(", ", codes);

    // ログへ出す IdP 内部 ID から制御文字を落とす（要求の経路の値。ログ偽装を防ぐ）。
    private static string Sanitize(string value) => new([.. value.Where(c => !char.IsControl(c))]);
}

/// <summary>部門の読み取り・変更の結末（端点が状態コードへ写す）。</summary>
public sealed record UserDepartmentOutcome(UserDepartmentOutcomeKind Kind, UserDepartmentDto? Value, string? Message)
{
    public static UserDepartmentOutcome Ok(UserDepartmentDto value) => new(UserDepartmentOutcomeKind.Ok, value, null);
    public static UserDepartmentOutcome NotFound() => new(UserDepartmentOutcomeKind.NotFound, null, null);
    public static UserDepartmentOutcome Invalid(string message) => new(UserDepartmentOutcomeKind.Invalid, null, message);
    public static UserDepartmentOutcome Conflict(string message) => new(UserDepartmentOutcomeKind.Conflict, null, message);
    public static UserDepartmentOutcome Failed(string message) => new(UserDepartmentOutcomeKind.Failed, null, message);

    public static UserDepartmentOutcome RealmUnavailable()
        => new(UserDepartmentOutcomeKind.RealmUnavailable, null,
            "realm の部門グループを読めません。部門は変えていません。時間をおいて開き直してください。");
}

public enum UserDepartmentOutcomeKind
{
    /// <summary>200。</summary>
    Ok,

    /// <summary>404（利用者が居ない）。</summary>
    NotFound,

    /// <summary>400（realm の部門グループに無いコード）。</summary>
    Invalid,

    /// <summary>409（2 個以上の部門グループ・書いた後の読み直しが期待と違う）。何も書いていないか、書いた結果が期待と違う。</summary>
    Conflict,

    /// <summary>502（所属の変更が途中で失敗した。補償の結果とともに返す）。</summary>
    Failed,

    /// <summary>503（realm を読めない。何も変えていない）。</summary>
    RealmUnavailable,
}
