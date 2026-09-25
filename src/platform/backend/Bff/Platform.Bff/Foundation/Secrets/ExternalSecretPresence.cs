using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Platform.Bff.Foundation.Secrets;

// SC-22 主要素 1, NFR-18, ADR-0104 決定 1・2, IADR-0460 決定 1 (#1502): 項目の「いま効いている供給元」を配備の結果から読む。
//
// ADR-0104 決定 2 は「供給元は画面が推測しない。配備時のスイッチが決めた事実を出す」と定めた。
// 画面の経路（Vault → ESO → Secret）が効いているのは、**項目の同期先 ExternalSecret がクラスタに在るとき**だけである
// —— AST の `ast-secrets` は `externalSecrets.enabled && appSecrets.enabled` のときだけ描画され、Discord ID の読み先を
// Secret へ切り替えるのも同じ述語である（AST の IADR-0341 決定 2・3）。無ければ値は配備時の設定（Helm values・配備スクリプト）から来る。
// したがって `get` の応答は、配備時のスイッチの**事実そのもの**である（推測でも、BFF の構成値の写しでもない）。
//
// 🔴 **3 値を畳まない。** 200 → `Present`、404 → `Absent`、それ以外（未構成・拒否・障害・不達）→ `Unknown`。
// 不明を「在る」にも「無い」にも倒すと、画面が「書けば効く」「書いても効かない」のどちらかを根拠なく言うことになる。
// 🔴 **権限は増やさない。** 使うのは同期依頼と同じ Role（`resourceNames` 限定の `get` / `patch`。IADR-0456 決定 4）の `get` であり、
// ExternalSecret の本文は読み捨てる（Secret にも Vault にも触れない）。構成・名乗り・TLS も同期依頼と同じものを使う。

/// <summary>項目の同期先 ExternalSecret がクラスタに在るか。</summary>
public enum ExternalSecretPresence
{
    /// <summary>在る（画面の経路が効いている）。</summary>
    Present,

    /// <summary>無い（404。画面で書いた値は届かない）。</summary>
    Absent,

    /// <summary>判定できない（未構成・RBAC の拒否・障害・不達）。</summary>
    Unknown,
}

public interface IExternalSecretPresenceReader
{
    Task<ExternalSecretPresence> ReadAsync(ExternalSecretReference target, CancellationToken ct);
}

public sealed class ExternalSecretPresenceReader(
    IHttpClientFactory httpClientFactory,
    IOptions<ExternalSecretSyncOptions> options,
    IServiceAccountTokenReader tokenReader,
    ILogger<ExternalSecretPresenceReader> logger) : IExternalSecretPresenceReader
{
    public async Task<ExternalSecretPresence> ReadAsync(ExternalSecretReference target, CancellationToken ct)
    {
        var o = options.Value;
        var server = o.Enabled ? o.ResolveApiServer() : null;
        if (server is null)
            return ExternalSecretPresence.Unknown;

        string token;
        try
        {
            token = await tokenReader.ReadAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("ServiceAccount トークンを読めない（供給元を判定できない）: {ExceptionType}", ex.GetType().Name);
            return ExternalSecretPresence.Unknown;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{server}/apis/external-secrets.io/v1/namespaces/{Uri.EscapeDataString(target.Namespace)}/externalsecrets/{Uri.EscapeDataString(target.Name)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await httpClientFactory.CreateClient(ExternalSecretSyncRequester.ClientName).SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return ExternalSecretPresence.Present;
            if (response.StatusCode == HttpStatusCode.NotFound)
                return ExternalSecretPresence.Absent;

            logger.LogWarning("ExternalSecret の有無を判定できない: {Namespace}/{Name} {Status}",
                target.Namespace, target.Name, (int)response.StatusCode);
            return ExternalSecretPresence.Unknown;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("ExternalSecret の有無を判定できない: {Namespace}/{Name} {ExceptionType}",
                target.Namespace, target.Name, ex.GetType().Name);
            return ExternalSecretPresence.Unknown;
        }
    }
}
