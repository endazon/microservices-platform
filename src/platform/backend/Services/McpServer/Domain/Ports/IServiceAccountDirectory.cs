namespace McpServer.Domain.Ports;

// FR-16, SC-12, 計画 ADR-0123 決定 2（登録簿は IdP へ書いた値の写し。食い違いは検知して知らせる）, [[IADR-0516]] 決定 5 (#1818):
// **登録簿と IdP の照合が読むための口。** IdP（Keycloak）のクライアントの一覧（入口の印の有無）と、
// サービスアカウント `service-account-<client>` の利用者属性を読む。
//
// ■ 🔴 **読むだけで、書き込みを持たない。** 照合は検知して知らせるだけであり（IADR-0516 決定 5）、IdP にも登録簿にも書かない。
//   書き込みの口（<see cref="IServiceAccountProvisioner"/>）と別の型にして、照合が書けないことを型で保つ。
// ■ 属性は**認可サービスが判定のたびに引き直すのと同じ照会**（利用者名の完全一致）で読む（ADR-0088 決定 1・IADR-0516 決定 6）。
//   判定に効いている値と比べるためである。
// ■ 読み取りなので、要求の取り消しは伝えてよい（書き込みと違い、途中で止めても孤児は生まれない）。
// ■ 失敗（到達不能・時間切れ・形の違う応答・口が構成されていない）は <see cref="IdpProvisioningException"/> で表す。
public interface IServiceAccountDirectory
{
    /// <summary>IdP の全クライアント（clientId と入口の印の有無）。読み切れなければ例外。</summary>
    Task<IReadOnlyList<IdpClientEntry>> ListClientsAsync(CancellationToken ct);

    /// <summary>
    /// クライアントのサービスアカウントの利用者属性（集合値は <c>,</c> で連ねた 1 つの値。IADR-0385）。
    /// 認可サービスと同じ照会で利用者が引けなければ null（判定ではその主体は名簿に居ない＝拒否になる）。
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> ReadServiceAccountAttributesAsync(string clientId, CancellationToken ct);
}

/// <summary>
/// IdP のクライアント 1 件。<paramref name="Managed"/> は入口の印（`msp.mcp-client.managed-by=mcp-server`）があること。
/// <paramref name="Enabled"/> はクライアントの <c>enabled</c>（［2026-10-09 / #1829］照合が登録簿の有効・無効と比べる。IADR-0516 決定 4a）。
/// </summary>
public sealed record IdpClientEntry(string ClientId, bool Managed, bool Enabled = true);
