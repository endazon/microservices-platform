using Microsoft.AspNetCore.Authorization;
using Platform.Bff.Foundation.Secrets;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Bff.Foundation.Endpoints;

// SC-22, FR-05, NFR-18, ADR-0095 決定 1・3・4, ADR-0042 決定 2, IADR-0433, IADR-0453 (#1411):
// 秘密情報・接続設定の管理。画面 → BFF → Vault（KV v2）の「投入」の経路である。
// 供給（Vault → ESO → Secret）は別の主体が読み取り専用で行い、ここは触れない（IADR-0433 決定 5）。
//
// 🔴 **値を読み出す口を作らない。** `GET /bff/secrets/{item}` は存在しない。一覧は allowlist と
// metadata（版・時刻。値を持たない）から作る（IADR-0433 決定 7）。
// 🔴 **値を監査・ログ・応答へ出さない。** 長さ・ハッシュ・先頭数文字も出さない（IADR-0433 決定 6）。
// 🔴 **allowlist 外は 400。** 存在秘匿の対象ではなく「入力が不正」である（404 にしない）。
//
// 認可: 群は `RequireAuthorization()`（未認証 401）。**ロールはハンドラ内で評価する** ——
// 群に `RequireAuthorization(policy)` を付けるとミドルウェアが先に 403 を返し、
// **拒否が監査に残らない**（IADR-0433 決定 6「denied も記録する」）。存在は秘匿しない（403）。
public static class SecretItemBffEndpoints
{
    public const string ListAction = "secret.item.list";
    public const string UpdateAction = "secret.item.update";

    /// <summary>値の上限（文字数）。PEM など大きな値は対象外（`deferred[]`）であり、API キー・webhook には十分。</summary>
    public const int MaxValueLength = 8192;

    /// <summary>更新の理由の上限（文字数）。監査ログの 1 行に載る。</summary>
    public const int MaxReasonLength = 500;

    private const string ProblemTypePrefix = "urn:microservices-platform:secret-items:";

    public static IEndpointRouteBuilder MapSecretItemBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/secrets")
            .WithTags("SecretItems BFF")
            .RequireAuthorization();

        // SC-22 主要素 1・3: 項目の一覧（値の列は無い）。状態は KV 単位の 3 値（IADR-0453 決定 4）。
        g.MapGet("", async (
            HttpContext http,
            IAuthorizationService authz,
            IAuditLogger audit,
            SecretItemCatalog catalog,
            IVaultKvClient vault,
            ISecretWriteRecordStore records,
            CancellationToken ct) =>
        {
            var denied = await DenyUnlessWriterAsync(http, authz, audit, ListAction, null);
            if (denied is not null)
                return denied;

            var subject = SubjectOf(http);
            var unavailable = await VaultUnavailableAsync(vault, audit, ListAction, subject, null, ct);
            if (unavailable is not null)
                return unavailable;

            var rows = new List<SecretItemStatusDto>(catalog.Items.Count);
            foreach (var definition in catalog.Items)
            {
                var metadata = await vault.ReadMetadataAsync(catalog.VaultMount, definition.VaultPath, ct);
                var present = metadata.State == VaultMetadataState.Present;

                // IADR-0453 決定 3: 最終更新者は「BFF が書いた版」と現在版が一致するときだけ出す。
                string? updatedBy = null;
                if (present && metadata.CurrentVersion is int current)
                {
                    var record = await records.GetAsync(definition.Item, ct);
                    if (record?.Version == current)
                        updatedBy = record.UpdatedBy;
                }

                rows.Add(new SecretItemStatusDto(
                    definition.Item,
                    definition.VaultPath,
                    [.. definition.Properties],
                    StatusOf(metadata.State),
                    present ? metadata.CurrentVersion : null,
                    present ? metadata.CurrentVersionCreatedAt : null,
                    updatedBy));
            }

            // IADR-0453 決定 5: **1 件も取れない**なら保管先に届いていない（保持中のトークンで
            // ログイン確認を飛ばした後に Vault が落ちた場合もここへ来る）。全行「取得できない」の 200 ではなく 503 にする。
            if (rows.Count > 0 && rows.TrueForAll(r => r.Status == "unavailable"))
            {
                audit.Record(ListAction, subject, "failed", "reason=vault-unavailable");
                return UnavailableProblem();
            }

            audit.Record(ListAction, subject, "granted", $"items={rows.Count}");
            return Results.Ok(rows);
        }).WithName("BffSecretItemsList")
          .Produces<List<SecretItemStatusDto>>()
          .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // SC-22 主要素 2・入力/バリデーション: 1 回に 1 プロパティだけを書く（IADR-0453 決定 2）。
        g.MapPut("/{item}", async (
            string item,
            UpdateSecretItemRequest? body,
            HttpContext http,
            IAuthorizationService authz,
            IAuditLogger audit,
            SecretItemCatalog catalog,
            IVaultKvClient vault,
            ISecretWriteRecordStore records,
            CancellationToken ct) =>
        {
            var denied = await DenyUnlessWriterAsync(http, authz, audit, UpdateAction, item);
            if (denied is not null)
                return denied;

            var subject = SubjectOf(http);
            var definition = catalog.Find(item);
            if (definition is null)
            {
                audit.Record(UpdateAction, subject, "denied", $"item={Clip(item)} reason=not-in-allowlist");
                return Invalid("item", "この項目は画面から投入できる項目の一覧にありません。");
            }

            var property = body?.Property;
            if (property is null || !definition.Properties.Contains(property, StringComparer.Ordinal))
            {
                // 🔴 `notWritable`（構成・realm と対の秘密）も同じ扱いで拒む。allowlist の型が持たないので
                // 「書けるプロパティに無い」で閉じる。
                audit.Record(UpdateAction, subject, "denied",
                    $"item={definition.Item} property={Clip(property)} reason=property-not-writable");
                return Invalid("property", "このプロパティは画面から書き込めません。");
            }

            // 🔴 値そのものも、その長さも、ここから先のどこにも出さない。
            if (string.IsNullOrEmpty(body!.Value) || body.Value.Length > MaxValueLength)
            {
                audit.Record(UpdateAction, subject, "denied",
                    $"item={definition.Item} property={property} reason=invalid-value");
                return Invalid("value", $"値を入力してください（{MaxValueLength} 文字以内）。");
            }

            var reason = body.Reason?.Trim();
            if (reason is { Length: > MaxReasonLength })
            {
                audit.Record(UpdateAction, subject, "denied",
                    $"item={definition.Item} property={property} reason=invalid-reason");
                return Invalid("reason", $"更新の理由は {MaxReasonLength} 文字以内で入力してください。");
            }

            var target = $"item={definition.Item} property={property}";
            var unavailable = await VaultUnavailableAsync(vault, audit, UpdateAction, subject, target, ct, probeLogin: false);
            if (unavailable is not null)
                return unavailable;

            var written = await vault.WritePropertyAsync(catalog.VaultMount, definition.VaultPath, property, body.Value, ct);
            switch (written.Outcome)
            {
                case VaultWriteOutcome.Written:
                    break;
                case VaultWriteOutcome.Rejected:
                    audit.Record(UpdateAction, subject, "failed", $"{target} reason=vault-rejected");
                    return Results.Problem(
                        statusCode: StatusCodes.Status502BadGateway,
                        type: ProblemTypePrefix + "vault-rejected",
                        title: "秘密情報の保管先（Vault）が書き込みを受け付けませんでした。");
                case VaultWriteOutcome.NotConfigured:
                    audit.Record(UpdateAction, subject, "failed", $"{target} reason=vault-not-configured");
                    return NotConfiguredProblem();
                default:
                    audit.Record(UpdateAction, subject, "failed", $"{target} reason=vault-unavailable");
                    return UnavailableProblem();
            }

            await records.SaveAsync(definition.Item,
                new SecretWriteRecord(written.Version, subject, property, written.UpdatedAt), ct);

            var detail = $"{target} version={written.Version}";
            if (!string.IsNullOrEmpty(reason))
                detail += $" reason={reason}";
            audit.Record(UpdateAction, subject, "granted", detail);

            return Results.Ok(new SecretItemWriteResultDto(definition.Item, property, written.Version, written.UpdatedAt));
        }).WithName("BffSecretItemsUpdate")
          .Produces<SecretItemWriteResultDto>()
          .ProducesValidationProblem()
          .ProducesProblem(StatusCodes.Status502BadGateway)
          .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    // 運用者・システム管理者か（`SecretItemWriter`）。拒否は監査へ `denied` を残して 403。
    // 権限ありは null を返して続行する。
    private static async Task<IResult?> DenyUnlessWriterAsync(
        HttpContext http, IAuthorizationService authz, IAuditLogger audit, string action, string? item)
    {
        var authorized =
            (await authz.AuthorizeAsync(http.User, PlatformAuthPolicies.SecretItemWriter)).Succeeded;
        if (authorized)
            return null;

        var detail = item is null ? "reason=forbidden" : $"item={Clip(item)} reason=forbidden";
        audit.Record(action, SubjectOf(http), "denied", detail);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    // IADR-0453 決定 5: Vault を配備していない構成・ログインできない状況を 503 で見せる。
    private static async Task<IResult?> VaultUnavailableAsync(
        IVaultKvClient vault, IAuditLogger audit, string action, string subject, string? target,
        CancellationToken ct, bool probeLogin = true)
    {
        var prefix = target is null ? string.Empty : target + " ";
        if (!vault.IsConfigured)
        {
            audit.Record(action, subject, "failed", prefix + "reason=vault-not-configured");
            return NotConfiguredProblem();
        }

        if (probeLogin && !await vault.CanAuthenticateAsync(ct))
        {
            audit.Record(action, subject, "failed", prefix + "reason=vault-unavailable");
            return UnavailableProblem();
        }

        return null;
    }

    private static IResult NotConfiguredProblem() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        type: ProblemTypePrefix + "vault-not-configured",
        title: "秘密情報の保管先（Vault）が構成されていません。");

    private static IResult UnavailableProblem() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        type: ProblemTypePrefix + "vault-unavailable",
        title: "秘密情報の保管先（Vault）に接続できません。");

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static string StatusOf(VaultMetadataState state) => state switch
    {
        VaultMetadataState.Present => "set",
        VaultMetadataState.Absent => "notSet",
        _ => "unavailable",
    };

    private static string SubjectOf(HttpContext http) => http.User.Identity?.Name ?? "unknown";

    // 利用者が送った識別子（項目名・プロパティ名）を監査へ載せるときの上限。改行等の除去は AuditLogger が行う。
    private static string Clip(string? text) =>
        text is null ? "(none)" : text.Length <= 100 ? text : text[..100];
}
