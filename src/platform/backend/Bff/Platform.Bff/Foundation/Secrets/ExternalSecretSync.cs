using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Platform.Bff.Foundation.Secrets;

// SC-22, NFR-18, ADR-0095 決定 3, IADR-0456 決定 4 (#1477): 書き込み後に ExternalSecret へ即時同期を依頼する。
//
// ESO の同期間隔（`refreshInterval: 1h`）を待たずに Secret へ反映させるため、書き込みが成功した項目の ExternalSecret に
// `force-sync: <unix 秒>` の注釈を merge-patch する（ESO は注釈の値が変わると同期し直す）。
//
// 🔴 **BFF は ESO の権限を持たない。** 触れるのは ExternalSecret の注釈だけで、Secret も Vault の読み取りも触らない
// （ADR-0095 決定 3「ESO は読み取り専用の同期を維持する」は変わらない。同期するのは ESO 自身である）。
// 🔴 **RBAC は `resourceNames` で items[] の ExternalSecret に限る**（helm と deploy/local/vault/eso/ の Role。
// 名前集合の一致は Platform.Bff.Tests の SecretItemExternalSecretRbacTests が固定する）。
// 🔴 **依頼の失敗は書き込みの失敗にしない。** 呼び出し側は結果を応答の `syncRequested` と監査に写すだけである。

/// <summary>ExternalSecret への同期依頼の構成（`ExternalSecretSync:*`）。</summary>
public sealed class ExternalSecretSyncOptions
{
    public const string SectionName = "ExternalSecretSync";

    /// <summary>同期を依頼するか。**既定 false**（ESO を配備しない構成・クラスタ外では依頼しない）。</summary>
    public bool Enabled { get; set; }

    /// <summary>API サーバの URL の上書き。空なら Pod の `KUBERNETES_SERVICE_HOST` / `KUBERNETES_SERVICE_PORT` から作る。</summary>
    public string? ApiServer { get; set; }

    /// <summary>API サーバの証明書を検証する CA（Pod の ServiceAccount の既定位置）。</summary>
    public string CaCertificatePath { get; set; } = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

    /// <summary>1 回の依頼の上限秒数（書き込みの応答を長く待たせない）。</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>API サーバの URL。決められなければ null（＝構成されていない）。</summary>
    public string? ResolveApiServer()
    {
        if (!string.IsNullOrWhiteSpace(ApiServer))
            return ApiServer.TrimEnd('/');

        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        if (string.IsNullOrWhiteSpace(host))
            return null;
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT");
        var authority = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return $"https://{authority}:{(string.IsNullOrWhiteSpace(port) ? "443" : port)}";
    }
}

public enum ExternalSecretSyncOutcome
{
    /// <summary>注釈を付けた（ESO が同期し直す）。</summary>
    Requested,

    /// <summary>依頼しない構成（無効化・クラスタ外）。</summary>
    NotConfigured,

    /// <summary>依頼したが通らなかった（RBAC の拒否・不在・障害・不達）。</summary>
    Failed,
}

public interface IExternalSecretSyncRequester
{
    Task<ExternalSecretSyncOutcome> RequestSyncAsync(ExternalSecretReference target, CancellationToken ct);
}

public sealed class ExternalSecretSyncRequester(
    IHttpClientFactory httpClientFactory,
    IOptions<ExternalSecretSyncOptions> options,
    IServiceAccountTokenReader tokenReader,
    TimeProvider time,
    ILogger<ExternalSecretSyncRequester> logger) : IExternalSecretSyncRequester
{
    public const string ClientName = "KubernetesApi";

    /// <summary>ESO が即時同期の合図として見る注釈のキー（ClusterExternalSecret では別名だが、ここでは使わない）。</summary>
    public const string ForceSyncAnnotation = "force-sync";

    /// <summary>CRD の部分更新が受け付ける Content-Type（strategic merge patch は CRD に効かない）。</summary>
    public const string MergePatchContentType = "application/merge-patch+json";

    public async Task<ExternalSecretSyncOutcome> RequestSyncAsync(ExternalSecretReference target, CancellationToken ct)
    {
        var o = options.Value;
        var server = o.Enabled ? o.ResolveApiServer() : null;
        if (server is null)
            return ExternalSecretSyncOutcome.NotConfigured;

        string token;
        try
        {
            token = await tokenReader.ReadAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("ServiceAccount トークンを読めない（ExternalSecret の同期を依頼できない）: {ExceptionType}", ex.GetType().Name);
            return ExternalSecretSyncOutcome.Failed;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch,
                $"{server}/apis/external-secrets.io/v1/namespaces/{Uri.EscapeDataString(target.Namespace)}/externalsecrets/{Uri.EscapeDataString(target.Name)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var body = new Dictionary<string, object>
            {
                ["metadata"] = new Dictionary<string, object>
                {
                    ["annotations"] = new Dictionary<string, string>
                    {
                        [ForceSyncAnnotation] = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    },
                },
            };
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(MergePatchContentType);

            using var response = await httpClientFactory.CreateClient(ClientName).SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return ExternalSecretSyncOutcome.Requested;

            logger.LogWarning("ExternalSecret の同期を依頼できない: {Namespace}/{Name} {Status}",
                target.Namespace, target.Name, (int)response.StatusCode);
            return ExternalSecretSyncOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("ExternalSecret の同期を依頼できない: {Namespace}/{Name} {ExceptionType}",
                target.Namespace, target.Name, ex.GetType().Name);
            return ExternalSecretSyncOutcome.Failed;
        }
    }

    /// <summary>
    /// API サーバへの接続。証明書は Pod の ServiceAccount の CA で検証する（クラスタの CA はシステムの信頼ストアに無い）。
    /// 🔴 **検証を外さない。** CA が読めなければシステムの信頼ストアのまま（＝通常は検証に失敗し `Failed` に倒れる）。
    /// </summary>
    internal static HttpMessageHandler CreateApiServerHandler(ExternalSecretSyncOptions options)
    {
        var handler = new SocketsHttpHandler();
        if (!File.Exists(options.CaCertificatePath))
            return handler;

        var authorities = new X509Certificate2Collection();
        authorities.ImportFromPemFile(options.CaCertificatePath);
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            // 名前の不一致・証明書の欠落は CA に依らず拒む。連鎖の誤りだけを下で CA に照らして判定し直す。
            if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
                return false;

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var leaf = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return chain.Build(leaf);
        };
        return handler;
    }
}
