using McpServer.Domain.Ports;

namespace McpServer.Features.McpClients;

// FR-16, SC-12, 計画 ADR-0123 決定 2・3, [[IADR-0516]] 決定 4 (#1786): **検証 → IdP → 登録簿** の順で書き、
// 登録簿への書き込みが失敗したら IdP を書く前へ戻す（補償）。登録と差し替えの両方がこの 1 つを通る。
//
// ■ 🔴 **検証はこの関数の前で終わっている**（`McpClientEndpoints.RejectUnassignableAsync`）。ここへ来た要求だけが IdP へ書かれる。
// ■ 🔴 **IdP を先に書く理由**: 登録簿は IdP へ書いた値の写しである（ADR-0123 決定 2）。登録簿を先に書くと、IdP の書き込みが
//   失敗した間、判定に効かない値が「割り当て済み」として画面に出る。IdP を先に書けば、登録簿に在る値は IdP にも在る。
// ■ 🔴 **IdP への書き込みと補償には要求の取り消しを伝えない**（PR #1816 監査 🟡-1）。書きかけで止めると孤児が残る。
//   期限は口の HttpClient の Timeout が持つ。登録簿への書き込みが取り消し・時間切れで止まったときも補償する。
// ■ 登録簿への書き込みが例外を投げたときだけでなく、**失敗の結果（4xx / 5xx）を返したときも**補償する。
// ■ 補償が失敗したら、元の失敗を投げる（補償の失敗は口がログに残す）。残った食い違いは照合（IADR-0516 決定 5）が拾う。
internal static class IdpFirstWrite
{
    public static async Task<IResult> RunAsync(
        Func<CancellationToken, Task<IdpWrite>> writeIdp,
        Func<CancellationToken, Task<IResult>> writeRegistry,
        IServiceAccountProvisioner provisioner,
        ILogger logger,
        CancellationToken ct)
    {
        // 書き始める前の取り消しだけは受ける。
        ct.ThrowIfCancellationRequested();

        IdpWrite written;
        try
        {
            written = await writeIdp(CancellationToken.None);
        }
        catch (IdpProvisioningException ex)
        {
            return ex.Failure == IdpProvisioningFailure.Unavailable
                ? Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "IdP への書き込み口が構成されていない")
                : Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway,
                    title: "IdP へ書けなかった（登録簿にも書いていない）");
        }

        // 🔴 入口が作っていないクライアントへは属性を書かない（ADR-0123 決定 2）。IdP には何も書いていない。
        if (written.Kind == IdpWriteKind.AlreadyExists)
            return McpClientEndpoints.Problem(
                $"クライアント '{written.ClientId}' は IdP（Keycloak）に既にあります。"
                + "この画面を通らずに作られたクライアントへは属性を書きません。");

        IResult result;
        try
        {
            result = await writeRegistry(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "登録簿への書き込みに失敗した。IdP への書き込みを取り消す（{Kind}）。", written.Kind);
            await UndoAsync(provisioner, written, logger);
            throw;
        }

        if (result is IStatusCodeHttpResult { StatusCode: >= 400 } failed)
        {
            logger.LogWarning("登録簿への書き込みが失敗の結果（{Status}）を返した。IdP への書き込みを取り消す（{Kind}）。",
                failed.StatusCode, written.Kind);
            await UndoAsync(provisioner, written, logger);
        }
        return result;
    }

    private static async Task UndoAsync(IServiceAccountProvisioner provisioner, IdpWrite written, ILogger logger)
    {
        try
        {
            await provisioner.UndoAsync(written, CancellationToken.None);
        }
        catch (Exception undo)
        {
            logger.LogError(undo, "IdP への書き込みの取り消しに失敗した。登録簿と IdP が食い違っている（{Kind}）。", written.Kind);
        }
    }
}
