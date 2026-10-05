using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Platform.Bff.Foundation.Secrets;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Ports.Secrets;

namespace Platform.Bff.Foundation.Endpoints;

// SC-22, SC-06, NFR-18, 計画 ADR-0126 決定 1〜4, ADR-0095 決定 3（補完）, IADR-0501 (#458 段 S2):
// SC-22 の**群**（実行時に成員が増える項目の集まり。いまは「データソースの資格情報」の 1 つ）。
//
// ■ 書き込みの射程は 2 つの層で限る（ADR-0126 決定 2）
//   - **専用接頭辞（権限の層）**: BFF の Vault の policy（`policy-bff-secret-group-write.hcl`）は `<接頭辞>/+` の
//     1 セグメントに `create`・`patch` だけを持つ。群を通じて基盤の秘密（`msp/*`）へは書けない。
//   - **登録済みの成員（このコード）**: 書く前に可変ユニットのポート（`ISecretItemGroupSource`）で成員を引き、
//     **成員に無い ID へは書かない（404。Vault へ届かない）**。成員 ID は 1 セグメントの形（小文字英数とハイフン）に限る ——
//     `/`・`..`・空白を Vault のパスへ入れない（policy の `+` に頼る前にここで止める）。
// ■ ロール（ADR-0126 決定 3）: **書き込みは管理者だけ**（`AdminOnly`）。一覧は静的な項目と同じく運用者・システム管理者。
//   ロールはハンドラ内で評価し、拒否を監査に残す（静的な項目と同じ理由。IADR-0433 決定 6）。
// ■ 🔴 **値を読み出す口を作らない・値を出さない**（静的な項目と同じ。監査・ログ・応答のどこにも値・長さを出さない）。
// ■ 供給元（ADR-0126 決定 4。ADR-0110 決定 1・3 の部分改定）: ExternalSecret の有無で判定しない。成員の設定が群の参照を持つ
//   （または値を持たない）なら `screen`、画面以外の値（平文など）を持つなら `git`（表示「画面以外」）。
//   **同期依頼も再起動の確認も無い**（消費側は次の同期の開始時に Vault から読む。IADR-0493）。
public static partial class SecretItemGroupBffEndpoints
{
    public const string ListAction = "secret.group.list";
    public const string UpdateAction = "secret.group.update";

    /// <summary>
    /// 書き込み後の参照の配置（IADR-0501 決定 3）。**書き込みの監査行とは別の行**で残す ——
    /// 配置の失敗は書き込みの失敗ではないため、`secret.group.update` の outcome を汚さない。
    /// </summary>
    public const string ReferenceAction = "secret.group.reference";

    public static IEndpointRouteBuilder MapSecretItemGroupBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/secrets/groups")
            .WithTags("SecretItems BFF")
            .RequireAuthorization();

        // ADR-0126 決定 1・3・4: 群の成員の一覧。運用者・システム管理者が閲覧できる（値の列は無い）。
        g.MapGet("/{group}", async (
            string group,
            HttpContext http,
            IAuthorizationService authz,
            IAuditLogger audit,
            SecretItemCatalog catalog,
            IVaultKvClient vault,
            ISecretWriteRecordStore records,
            IEnumerable<ISecretItemGroupSource> sources,
            CancellationToken ct) =>
        {
            var denied = await DenyUnlessReaderAsync(http, authz, audit, ListAction, group);
            if (denied is not null)
                return denied;

            var subject = SecretItemBffEndpoints.SubjectOf(http);
            var (definition, source, rejected) = Resolve(group, catalog, sources, audit, ListAction, subject);
            if (rejected is not null)
                return rejected;

            var unavailable = await SecretItemBffEndpoints.VaultUnavailableAsync(
                vault, audit, ListAction, subject, $"group={definition!.Group}", ct);
            if (unavailable is not null)
                return unavailable;

            // 🔴 **成員が取れないときは空の群に縮退させない**（「登録が無い」と「取れない」は別の意味。502）。
            var members = await source!.ListMembersAsync(http, ct);
            if (members is null)
            {
                audit.Record(ListAction, subject, "failed", $"group={definition.Group} reason=members-unavailable");
                return MembersUnavailableProblem();
            }

            var rows = new List<SecretItemGroupMemberStatusDto>(members.Count);
            foreach (var member in members)
            {
                // 形の崩れた成員 ID は Vault へ問い合わせない（パスに入れない）。一覧からも落とす。
                if (!IsMemberId(member.MemberId))
                    continue;

                var path = definition.VaultPathOf(member.MemberId);
                var metadata = await vault.ReadMetadataAsync(catalog.VaultMount, path, ct);
                var present = metadata.State == VaultMetadataState.Present;

                // IADR-0453 決定 3・IADR-0454 決定 3: 最終更新者は「BFF が書いた版」と現在版が版と作成時刻で一致するときだけ。
                string? updatedBy = null;
                if (present && metadata.CurrentVersion is int current)
                {
                    var record = await records.GetAsync(definition.RecordKeyOf(member.MemberId), ct);
                    if (record is not null
                        && record.Version == current
                        && metadata.CurrentVersionCreatedAt is { } createdAt
                        && record.UpdatedAt == createdAt)
                        updatedBy = record.UpdatedBy;
                }

                rows.Add(new SecretItemGroupMemberStatusDto(
                    member.MemberId,
                    member.DisplayName,
                    member.Kind,
                    path,
                    // ADR-0126 決定 1: 群の項目は種別「入力」（マスク入力・確認入力 2 度）。秘密として扱う。
                    [.. member.Properties.Select(p => new SecretItemPropertyDto(p.Name, SecretItemCatalog.KindValue, true))],
                    SecretItemBffEndpoints.StatusOf(metadata.State),
                    present ? metadata.CurrentVersion : null,
                    present ? metadata.CurrentVersionCreatedAt : null,
                    updatedBy,
                    SupplySourceOf(member.Properties.Select(p => p.Supply))));
            }

            // IADR-0453 決定 5 と同じ: **1 件も取れない**なら保管先に届いていない（全行「取得できない」の 200 にしない）。
            if (rows.Count > 0 && rows.TrueForAll(r => r.Status == "unavailable"))
            {
                audit.Record(ListAction, subject, "failed", $"group={definition.Group} reason=vault-unavailable");
                return SecretItemBffEndpoints.UnavailableProblem();
            }

            // ADR-0126 決定 3: 書けるのは管理者だけ。画面が更新の操作を出し分けるために返す（実効境界は PUT 側）。
            // 🔴 これは**閲覧の可否ではない**（ここまで来た運用者にも一覧は返る）。認可の検査器（`check-bff-authz-docs.js`）は
            // `AuthorizeAsync(…, PlatformAuthPolicies.X)` を端点の実効ロールとして読むので、表示用の判定はポリシー名を変数で渡す。
            var writable = await CanWriteGroupAsync(http, authz);

            audit.Record(ListAction, subject, "granted", $"group={definition.Group} members={rows.Count}");
            return Results.Ok(new SecretItemGroupDto(definition.Group, writable, rows));
        }).WithName("BffSecretItemGroupList")
          .Produces<SecretItemGroupDto>()
          .ProducesValidationProblem()
          .ProducesProblem(StatusCodes.Status502BadGateway)
          .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // ADR-0126 決定 2・3: 群の成員 1 件の 1 プロパティを書く。**管理者だけ。登録済みの成員だけ。**
        // 🔴 本文は暗黙バインドしない・`.Accepts<T>` を付けない（静的な項目と同じ理由。IADR-0454 決定 2）。
        g.MapPut("/{group}/{memberId}", async (
            string group,
            string memberId,
            HttpContext http,
            IAuthorizationService authz,
            IAuditLogger audit,
            SecretItemCatalog catalog,
            IVaultKvClient vault,
            ISecretWriteRecordStore records,
            IEnumerable<ISecretItemGroupSource> sources,
            IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
            CancellationToken ct) =>
        {
            var clippedTarget = $"group={SecretItemBffEndpoints.Clip(group)} member={SecretItemBffEndpoints.Clip(memberId)}";
            var denied = await DenyUnlessGroupWriterAsync(http, authz, audit, UpdateAction, group);
            if (denied is not null)
                return denied;

            var subject = SecretItemBffEndpoints.SubjectOf(http);
            var (definition, source, rejected) = Resolve(group, catalog, sources, audit, UpdateAction, subject);
            if (rejected is not null)
                return rejected;

            // ADR-0126 決定 2: **登録済みの成員だけ**へ書く。形が崩れた ID は成員に問い合わせるまでもなく未登録と同じ扱い。
            if (!IsMemberId(memberId))
            {
                audit.Record(UpdateAction, subject, "denied", $"{clippedTarget} reason=not-registered");
                return Results.NotFound();
            }

            var members = await source!.ListMembersAsync(http, ct);
            if (members is null)
            {
                audit.Record(UpdateAction, subject, "failed", $"{clippedTarget} reason=members-unavailable");
                return MembersUnavailableProblem();
            }

            var member = members.FirstOrDefault(m => string.Equals(m.MemberId, memberId, StringComparison.Ordinal));
            if (member is null)
            {
                audit.Record(UpdateAction, subject, "denied", $"{clippedTarget} reason=not-registered");
                return Results.NotFound();
            }

            var target = $"group={definition!.Group} member={member.MemberId}";
            var (body, bodyRejected) = await SecretItemBffEndpoints.ReadUpdateBodyAsync(
                http.Request, jsonOptions.Value.SerializerOptions, audit, subject, target, ct, UpdateAction);
            if (bodyRejected is not null)
                return bodyRejected;

            var property = body!.Property;
            var memberProperty = member.Properties.FirstOrDefault(p => string.Equals(p.Name, property, StringComparison.Ordinal));
            if (memberProperty is null)
            {
                audit.Record(UpdateAction, subject, "denied",
                    $"{target} property={SecretItemBffEndpoints.Clip(property)} reason=property-not-writable");
                return SecretItemBffEndpoints.Invalid("property", "このプロパティは画面から書き込めません。");
            }

            // 🔴 値そのものも、その長さも、ここから先のどこにも出さない。
            if (string.IsNullOrEmpty(body.Value) || body.Value.Length > SecretItemBffEndpoints.MaxValueLength)
            {
                audit.Record(UpdateAction, subject, "denied", $"{target} property={property} reason=invalid-value");
                return SecretItemBffEndpoints.Invalid(
                    "value", $"値を入力してください（{SecretItemBffEndpoints.MaxValueLength} 文字以内）。");
            }

            var reason = body.Reason?.Trim();
            if (reason is { Length: > SecretItemBffEndpoints.MaxReasonLength })
            {
                audit.Record(UpdateAction, subject, "denied", $"{target} property={property} reason=invalid-reason");
                return SecretItemBffEndpoints.Invalid(
                    "reason", $"更新の理由は {SecretItemBffEndpoints.MaxReasonLength} 文字以内で入力してください。");
            }

            var propertyTarget = $"{target} property={property}";
            var unavailable = await SecretItemBffEndpoints.VaultUnavailableAsync(
                vault, audit, UpdateAction, subject, propertyTarget, ct, probeLogin: false);
            if (unavailable is not null)
                return unavailable;

            // 窓（作業仕様書 20261003_458 §S2・S3 の窓）: **Vault が先、参照が後。** 逆にすると、書き込みが失敗したときに
            // 値の無い Vault を指す参照が残り、次の同期が `not-found` で止まる。
            var written = await vault.WritePropertyAsync(
                catalog.VaultMount, definition.VaultPathOf(member.MemberId), property, body.Value, ct);
            var writeFailure = SecretItemBffEndpoints.VaultWriteFailure(written.Outcome, audit, UpdateAction, subject, propertyTarget);
            if (writeFailure is not null)
                return writeFailure;

            await records.SaveAsync(definition.RecordKeyOf(member.MemberId),
                new SecretWriteRecord(written.Version, subject, property, written.UpdatedAt), ct);

            var detail = $"{propertyTarget} version={written.Version}";
            if (!string.IsNullOrEmpty(reason))
                detail += $" reason={SecretItemBffEndpoints.QuoteForAudit(reason)}";
            audit.Record(UpdateAction, subject, "granted", detail);

            // ADR-0126 決定 4, IADR-0501 決定 3: 値を持たないプロパティにだけ参照を置く（平文は置き換えない＝移送は段 S4）。
            // 🔴 置けなくても 200 のまま（書き込みは成立している）。結果は供給元と別の監査行に写す。
            var supply = await source.EnsureReferenceAsync(http, member.MemberId, property, ct);
            audit.Record(ReferenceAction, subject, supply is null ? "failed" : "granted",
                supply is null ? $"{propertyTarget} reason=reference-not-placed" : $"{propertyTarget} supply={supply}");

            return Results.Ok(new SecretItemGroupWriteResultDto(
                definition.Group, member.MemberId, property, written.Version, written.UpdatedAt,
                supply is { } placed ? SupplySourceOf([placed]) : SecretItemSupplySources.Unknown));
        }).WithName("BffSecretItemGroupUpdate")
          .Produces<SecretItemGroupWriteResultDto>()
          .ProducesValidationProblem()
          .ProducesProblem(StatusCodes.Status404NotFound)
          .ProducesProblem(StatusCodes.Status409Conflict)
          .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
          .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
          .ProducesProblem(StatusCodes.Status502BadGateway)
          .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    // 群の宣言（`groups[]`）と、その群の成員を供給するポート。宣言に無い群は 400（allowlist 外。静的な項目と同じ）、
    // 宣言はあるが供給するユニットが組み込まれていなければ 503（構成の欠落）。
    private static (SecretItemGroupDefinition? Definition, ISecretItemGroupSource? Source, IResult? Rejected) Resolve(
        string group, SecretItemCatalog catalog, IEnumerable<ISecretItemGroupSource> sources,
        IAuditLogger audit, string action, string subject)
    {
        var definition = catalog.FindGroup(group);
        if (definition is null)
        {
            audit.Record(action, subject, "denied", $"group={SecretItemBffEndpoints.Clip(group)} reason=not-in-allowlist");
            return (null, null, SecretItemBffEndpoints.Invalid("group", "この群は画面から投入できる項目の一覧にありません。"));
        }

        var source = sources.FirstOrDefault(s => string.Equals(s.Group, definition.Group, StringComparison.Ordinal));
        if (source is null)
        {
            audit.Record(action, subject, "failed", $"group={definition.Group} reason=group-source-missing");
            return (definition, null, Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: SecretItemBffEndpoints.ProblemTypePrefix + "group-source-missing",
                title: "この群の成員を供給する機能が組み込まれていません。"));
        }

        return (definition, source, null);
    }

    // 一覧（閲覧）: 運用者・システム管理者（静的な項目と同じ `SecretItemWriter`）。拒否は監査に残して 403。
    private static async Task<IResult?> DenyUnlessReaderAsync(
        HttpContext http, IAuthorizationService authz, IAuditLogger audit, string action, string group)
    {
        if ((await authz.AuthorizeAsync(http.User, PlatformAuthPolicies.SecretItemWriter)).Succeeded)
            return null;
        return SecretItemBffEndpoints.Forbidden(http, audit, action, group);
    }

    // ADR-0126 決定 3: 群の書き込みは**管理者だけ**（`AdminOnly`）。運用者も 403 で、拒否は監査に残る（本文を読む前）。
    private static async Task<IResult?> DenyUnlessGroupWriterAsync(
        HttpContext http, IAuthorizationService authz, IAuditLogger audit, string action, string group)
    {
        if ((await authz.AuthorizeAsync(http.User, PlatformAuthPolicies.AdminOnly)).Succeeded)
            return null;
        return SecretItemBffEndpoints.Forbidden(http, audit, action, group);
    }

    // 画面の出し分け用（`writable`）。書き込みの判定（`DenyUnlessGroupWriterAsync`）と同じポリシーを引く。
    private static async Task<bool> CanWriteGroupAsync(HttpContext http, IAuthorizationService authz)
    {
        var writerPolicy = PlatformAuthPolicies.AdminOnly;
        return (await authz.AuthorizeAsync(http.User, writerPolicy)).Succeeded;
    }

    // ADR-0126 決定 4: 🔴 **どれか 1 つでも画面以外の値を持てば「画面以外」**（書いても、その値は使われない）。
    // それ以外（参照あり・値なし）は「画面」—— 値なしは書けば参照が置かれて次の同期から効く。
    internal static string SupplySourceOf(IEnumerable<SecretItemGroupSupply> supplies) =>
        supplies.Any(s => s == SecretItemGroupSupply.OtherSource)
            ? SecretItemSupplySources.Git
            : SecretItemSupplySources.Screen;

    // 成員 ID は Vault のパスの 1 セグメントになる。小文字英数とハイフンだけ（データソース ID の `D` 形式を含む）。
    internal static bool IsMemberId(string? memberId) => memberId is not null && MemberIdPattern().IsMatch(memberId);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex MemberIdPattern();

    private static IResult MembersUnavailableProblem() => Results.Problem(
        statusCode: StatusCodes.Status502BadGateway,
        type: SecretItemBffEndpoints.ProblemTypePrefix + "members-unavailable",
        title: "群の成員（登録済みの対象）を取得できません。");
}
