using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Features.McpClients.DisableClient;
using McpServer.Features.McpClients.EnableClient;
using McpServer.Features.McpClients.ListClients;
using McpServer.Features.McpClients.ListEffectiveTools;
using McpServer.Features.McpClients.RegisterClient;
using McpServer.Features.McpClients.ReissueSecret;
using McpServer.Features.McpClients.ReplaceAttributes;
using McpServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Logging;

namespace McpServer.Features.McpClients;

// FR-16, UC-09, SC-12: MCP クライアント登録管理スライスの合成点。**管理者限定**（SC-12「管理者限定」）。
//
// ADR-0065 決定 2: 1 ユースケースのファイルは操作フォルダへ束ねる。
// **本ファイルに残すのは、グループ（パス・タグ・認可）の構築と複数操作が共有するヘルパだけである。**
public static class McpClientEndpoints
{
    public static IEndpointRouteBuilder MapMcpClientEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/mcp-clients").WithTags("McpClients")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        g.MapListMcpClients();
        g.MapRegisterMcpClient();
        g.MapDisableMcpClient();
        g.MapEnableMcpClient();
        g.MapReplaceMcpClientAttributes();
        // ［#1845］計画 ADR-0134 決定 2 の 5: 無人の client secret の再発行（新しい値を一度だけ返す）。
        g.MapReissueMcpClientSecret();
        g.MapListEffectiveTools();

        return app;
    }

    // UC-09 / SC-12: 無効化と再有効化は**同じ 1 つのハンドラ**である（引数の真偽だけが違う）。
    // 操作フォルダごとに複製すると、片方だけ直したときに黙ってズレる。集約直下に 1 つ置く。
    //
    // 🔴 FR-16, SC-12, 計画 ADR-0123 決定 2, [[IADR-0516]] 決定 4a（2026-10-09 追記 / #1829）: 無人の行は IdP のクライアントの `enabled` へも写す
    //   （多層の防御。即時の接続拒否そのものは McpSubjectResolver が呼び出しごとに登録簿を引いて満たしている）。**順序は向きで変える**:
    //   - **無効化（閉じる）は登録簿が先、IdP が後。** 即時の接続拒否を優先する。IdP への写しが失敗しても登録簿は取り消さずに 200 を返し、
    //     ログに残す。食い違い（登録簿は無効・IdP は有効）は照合（決定 5）が `enabled_differs` として拾う。同じ操作をもう一度送れば写し直す。
    //   - **再有効化（開く）は IdP が先、登録簿が後**（決定 4 と同じ `IdpFirstWrite`）。IdP へ書けなければ 502 / 503 で登録簿を書かない。
    //     登録簿の失敗は IdP を無効へ戻す。**入口の印が無いクライアント（`abac-seeder` 等）は 400 で、どちらにも書かない。**
    //   - ［2026-10-09 / #1844］有人の行も同じく写す（有人も IdP に公開クライアントとして作るようになった。計画 ADR-0134 決定 1）。
    //     本件より前に登録簿だけへ書かれた有人の行は IdP に無い（`Absent`）ので、登録簿だけを切り替える。
    internal static async Task<IResult> SetEnabledAsync(
        string clientId, bool enabled, McpDbContext db, TimeProvider clock,
        IServiceAccountProvisioner provisioner, ILogger logger, CancellationToken ct)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client is null) return Results.NotFound();

        async Task<IResult> WriteRegistry(CancellationToken token)
        {
            client.SetEnabled(enabled, clock.GetUtcNow());
            await db.SaveChangesAsync(token);
            return Results.Ok(McpClientMapper.ToView(client));
        }

        if (enabled)
            return await IdpFirstWrite.RunAsync(
                token => provisioner.SetEnabledAsync(clientId, true, token),
                WriteRegistry, provisioner, logger, ct,
                alreadyExists: id => $"クライアント '{id}' は IdP（Keycloak）にこの画面を通らずに作られたものとして在ります。"
                                     + "入口が作っていないクライアントへは接続を開きません。");

        var result = await WriteRegistry(ct);
        await MirrorDisableAsync(clientId, provisioner, logger);
        return result;
    }

    // 無効化の IdP への写し。**登録簿は既に無効**であり、ここで何が起きても応答（200）と登録簿は変えない。
    // 🔴 要求の取り消しを伝えない（登録簿を無効にした後で止めると、写しだけが落ちる）。期限は口の HttpClient の Timeout。
    private static async Task MirrorDisableAsync(string clientId, IServiceAccountProvisioner provisioner, ILogger logger)
    {
        var id = LogSanitizer.Sanitize(clientId, 128);
        try
        {
            var written = await provisioner.SetEnabledAsync(clientId, false, CancellationToken.None);
            switch (written.Kind)
            {
                case IdpWriteKind.AlreadyExists:
                    logger.LogWarning(
                        "クライアント {ClientId} を登録簿で無効にした。IdP の同名のクライアントには入口の印が無いので、IdP の有効・無効は変えない"
                        + "（入口が作っていないクライアントへは書かない）。", id);
                    break;
                case IdpWriteKind.Absent:
                    logger.LogInformation(
                        "クライアント {ClientId} を登録簿で無効にした。IdP に同じクライアントが無いので写すものは無い。", id);
                    break;
                default:
                    logger.LogInformation("クライアント {ClientId} を登録簿と IdP の両方で無効にした。", id);
                    break;
            }
        }
        catch (IdpProvisioningException ex) when (ex.Failure == IdpProvisioningFailure.Unavailable)
        {
            logger.LogWarning(
                "クライアント {ClientId} を登録簿で無効にした（即時の接続拒否は効いている）。IdP への書き込み口が構成されていないので、"
                + "IdP のクライアントの有効・無効は写していない（McpClientProvisioning:Provider）。", id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "クライアント {ClientId} を登録簿で無効にした（即時の接続拒否は効いている）が、IdP のクライアントの有効・無効へ写せなかった。"
                + "登録簿は取り消さない。照合が enabled_differs として拾う。もう一度無効化を送れば写し直す。", id);
        }
    }

    // 🔴 FR-16, UC-09, SC-12, ADR-0034 決定 9, ADR-0062 決定 2・3:
    // **無人アカウントへの属性割当の統制は、登録と差し替えが同じ 1 つの関数を呼ぶ。**
    // 2 か所へ書くと片方だけが緩む（既存の `ValidateServiceAccountAttributes` の作法と同じ理由。
    // 計画 ADR-0062 §決定 3 も「登録だけ塞いで差し替えが緩い形」を許さない）。
    //
    // 妥当なら null、拒否するなら 400 ValidationProblem を返す。
    // **拒否理由は丸めない** —— どの値が外れたかを本文へ載せる（ADR-0062 §結果。画面が事前に
    // 示せないことの唯一の緩和策である）。
    internal static async Task<IResult?> RejectUnassignableAsync(
        string clientId,
        McpClientKind kind,
        IReadOnlyDictionary<string, string> attributes,
        IRegistrarAttributeResolver registrar,
        CancellationToken ct)
    {
        // 有人は利用者本人の属性で解決される。割り当てる属性が無いので統制の対象でもない。
        if (kind != McpClientKind.ServiceAccount) return null;

        // ADR-0024（2026-08-02 注記）/ ADR-0034 決定 9: 個人資料を読ませる属性割当の禁止。
        // **構成（公開構成のスキーマ検証）と API の両方で弾く。検証関数は 1 つを共用する。**
        var forbidden = ToolPublicationConfigValidator.ValidateServiceAccountAttributes(clientId, attributes);
        if (forbidden.Count > 0) return Problem(forbidden);

        // ADR-0062 決定 2: `clearance` / タグは登録者が持つ集合の部分集合であること。
        // 🔴 **対象の属性を 1 つも含まないなら登録者の解決を呼ばない** —— 呼ぶと、属性を持たない
        // 無人アカウントの登録まで認可サービスの可用性に従属する。
        if (!ServiceAccountAttributeSubset.Governs(attributes)) return null;

        var assignable = await registrar.ResolveAsync(ct);
        var outside = ServiceAccountAttributeSubset.Validate(clientId, attributes, assignable);
        return outside.Count > 0 ? Problem(outside) : null;
    }

    internal static IResult Problem(string message) => Problem([message]);

    // **理由をすべて返す**（先頭 1 件へ丸めない）。画面は `ApiError.details` をそのまま並べて出す。
    internal static IResult Problem(IReadOnlyList<string> messages)
        => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [.. messages] });

    // 手書きの詰め替え（ToView）とティア名の変換（TierName）は撤去した。写像は
    // `McpClientMapper.ToView`（Riok.Mapperly の生成マッパ）が持つ
    // （計画 ADR-0030 §決定 / IADR-0371 決定 3 / IADR-0393）。**このクラスに写像は残さない。**
}
