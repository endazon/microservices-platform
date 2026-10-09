using FluentValidation;
using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace McpServer.Features.McpClients.RegisterClient;

// FR-16, UC-09 基本フロー 1, SC-12: クライアント登録（有人 / サービスアカウント）。
// ［2026-10-09 / #1844］有人は Keycloak の公開クライアント、無人は機密クライアントとして IdP へ作ってから登録簿へ書く（計画 ADR-0134 決定 1）。
public static class RegisterMcpClientEndpoint
{
    public static IEndpointRouteBuilder MapRegisterMcpClient(this IEndpointRouteBuilder app)
    {
        app.MapPost("", async (
            RegisterMcpClientRequest req, IValidator<RegisterMcpClientRequest> validator,
            McpDbContext db, TimeProvider clock,
            IRegistrarAttributeResolver registrar, IServiceAccountProvisioner provisioner,
            ILoggerFactory loggers, IAuditLogger audit, HttpContext http, CancellationToken ct) =>
        {
            // 🔴 FR-16, SC-12, 計画 ADR-0134 決定 2 の 3・フォローアップ 5（#1845）: 結果（拒否・未構成・失敗を含む）を監査に残す。
            // 値（secret）は渡さない。無人の登録が通ったら「secret を発行した」も別の行で残す。
            var result = await McpClientAudit.RecordAsync(audit, http.User, McpClientAudit.RegisterAction,
                McpClientAudit.Detail(req.ClientId, req.Kind, req.Attributes),
                () => RegisterAsync(req, validator, db, clock, registrar, provisioner, loggers, http, ct));
            if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status201Created }
                && TryParseKind(req.Kind, out var registered) && registered == McpClientKind.ServiceAccount)
                McpClientAudit.RecordIssued(audit, http.User, McpClientAudit.SecretIssueAction, req.ClientId);
            return result;
        });

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterMcpClientRequest req, IValidator<RegisterMcpClientRequest> validator,
        McpDbContext db, TimeProvider clock,
        IRegistrarAttributeResolver registrar, IServiceAccountProvisioner provisioner,
        ILoggerFactory loggers, HttpContext http, CancellationToken ct)
    {
        // FR-16, UC-09 / 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
        // [[IADR-0398]] 決定 1 (b)・5: `clientId` → `kind` → `egressTier` の順で検査する。
        // 規則は `RegisterMcpClientValidator` が持ち、**鍵（`request`）は sink が持つ**ので
        // 端点は先頭 1 件の**本文だけ**を渡す（移送前は最初のガード節でここから返っていた）。
        // 🔴 **この呼び出しは認可（管理者限定）の後ろ・`RejectUnassignableAsync` と
        // 重複照会（`AnyAsync`）の前**でなければならない。
        var gate = validator.Validate(req);
        if (!gate.IsValid) return McpClientEndpoints.Problem(gate.Errors[0].ErrorMessage);

        // 🔴 解析は検証通過後に、**検証器と同じ関数**で行う（[[IADR-0398]] 決定 5）。
        // 対応表を 2 つ持つと「検証は通るが解析で落ちる」形が生まれる。
        _ = TryParseKind(req.Kind, out var kind);
        _ = TryParseTier(req.EgressTier, out var tier);

        var attributes = req.Attributes ?? [];

        // 🔴 ADR-0024（2026-08-02 注記）/ ADR-0034 決定 9 / ADR-0062 決定 2・3:
        // 無人アカウントへの属性割当の統制（個人資料の禁止 ＋ 登録者の集合の部分集合）は
        // **差し替え経路と同じ 1 つの関数**が行う（McpClientEndpoints）。
        var rejected = await McpClientEndpoints.RejectUnassignableAsync(
            req.ClientId, kind, attributes, registrar, ct);
        if (rejected is not null) return rejected;

        if (await db.Clients.AnyAsync(c => c.ClientId == req.ClientId, ct))
            return McpClientEndpoints.Problem($"クライアント '{req.ClientId}' は既に登録されています。");

        var client = McpClient.Register(
            req.ClientId, req.DisplayName, kind, attributes, tier, clock.GetUtcNow());

        async Task<IResult> WriteRegistry(CancellationToken token)
        {
            // 🔴 FR-16, SC-12, 計画 ADR-0134 決定 2 の 1, [[IADR-0516]]（#1845）: 無人は**登録簿へ書く前に** IdP の secret を読む。
            // 読めなければ失敗の結果（502）を返す —— `IdpFirstWrite` がそれを見て作ったクライアントを消す（補償。登録簿にも書かない）。
            // secret は登録簿へ書かず、この応答の本文にだけ載せる（プラットフォームは値を保存しない）。
            ClientSecret? secret = null;
            if (kind == McpClientKind.ServiceAccount)
            {
                var read = await ReadIssuedSecretAsync(provisioner, req.ClientId);
                if (read.Secret is null) return read.Failure!;
                secret = read.Secret;
            }

            db.Clients.Add(client);
            await db.SaveChangesAsync(token);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/mcp-clients/{client.ClientId}",
                McpClientRegistrationView.From(McpClientMapper.ToView(client), secret));
        }

        var logger = loggers.CreateLogger(typeof(RegisterMcpClientEndpoint));

        // 🔴 FR-16, SC-12, 計画 ADR-0134 決定 1・フォローアップ 1, [[IADR-0516]]（2026-10-09 追記 / #1844）: 有人も **検証 → IdP → 登録簿**。
        // IdP には公開クライアント（PKCE S256・リダイレクト URI の完全一致・audience を MCP サーバーに限る）を作る。順序・補償・入口の印は
        // 無人と同じ（`IdpFirstWrite`）。口が構成されていなければ 503 で登録簿にも書かない（IADR-0516 決定 3 の既知の逸脱を解いた）。
        if (kind == McpClientKind.Interactive)
            return await IdpFirstWrite.RunAsync(
                token => provisioner.CreatePublicClientAsync(req.ClientId, req.DisplayName, req.RedirectUris ?? [], token),
                WriteRegistry, provisioner, logger, ct,
                alreadyExists: id => $"クライアント '{id}' は IdP（Keycloak）に既にあります。"
                                     + "この画面を通らずに作られたクライアントを有人の MCP クライアントとして登録しません。");

        // 🔴 FR-16, SC-12, 計画 ADR-0123 決定 2・3, [[IADR-0516]] 決定 4 (#1786): 無人は **検証（上で済んだ）→ IdP → 登録簿**。
        // 登録簿は IdP へ書いた値の写しである。IdP へ書けなければ登録簿にも書かない。
        return await IdpFirstWrite.RunAsync(
            token => provisioner.CreateAsync(req.ClientId, req.DisplayName, attributes, token),
            WriteRegistry, provisioner, logger, ct);
    }

    // 作ったばかりの機密クライアントの secret を読む。読めない（失敗・印なし等）は 502 の結果へ写す（補償は呼び出し元の `IdpFirstWrite`）。
    // 🔴 要求の取り消しは伝えない（作ったクライアントを、応答を組み立てる段で孤児にしない）。
    private static async Task<(ClientSecret? Secret, IResult? Failure)> ReadIssuedSecretAsync(
        IServiceAccountProvisioner provisioner, string clientId)
    {
        try
        {
            var read = await provisioner.ReadClientSecretAsync(clientId, CancellationToken.None);
            if (read is { Outcome: ClientSecretOutcome.Issued, Secret: { } secret }) return (secret, null);
            return (null, Results.Problem(
                $"作ったクライアント '{clientId}' の client secret を IdP（Keycloak）から読めない（{read.Outcome}）。作ったクライアントは取り消す。",
                statusCode: StatusCodes.Status502BadGateway, title: "IdP から client secret を読めなかった（登録簿にも書いていない）"));
        }
        catch (IdpProvisioningException ex)
        {
            return (null, Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway,
                title: "IdP から client secret を読めなかった（登録簿にも書いていない）"));
        }
    }

    // 🔴 [[IADR-0398]] 決定 5: 検証器（`RegisterMcpClientValidator`）と端点が**同じ 1 つ**を呼ぶため
    // `internal` である。片方だけ変えると「検証は通るが解析で落ちる」（またはその逆）になる。
    internal static bool TryParseKind(string? value, out McpClientKind kind)
    {
        kind = McpClientKind.Interactive;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "interactive": kind = McpClientKind.Interactive; return true;
            case "service-account": kind = McpClientKind.ServiceAccount; return true;
            default: return false;
        }
    }

    // 🔴 同上。**未指定（null / 空白）は有効**であり、最も低い保護水準へ倒す ——
    // 検証器が `NotEmpty()` を使うとこの分岐が消えて `egressTier` 省略が 400 に化ける。
    internal static bool TryParseTier(string? value, out EgressTier tier)
    {
        // 未指定は最も低い保護水準（＝本文を出しにくい側）へ倒す。08_data-egress-policy §基本原則。
        tier = EgressTier.StandardExternal;
        if (string.IsNullOrWhiteSpace(value)) return true;
        switch (value.Trim().ToLowerInvariant())
        {
            case "self-hosted": tier = EgressTier.SelfHosted; return true;
            case "protected-external": tier = EgressTier.ProtectedExternal; return true;
            case "standard-external": tier = EgressTier.StandardExternal; return true;
            default: return false;
        }
    }
}
