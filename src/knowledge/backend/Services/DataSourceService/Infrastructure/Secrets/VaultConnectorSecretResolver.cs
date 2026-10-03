using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using Microsoft.Extensions.Options;

namespace DataSourceService.Infrastructure.Secrets;

// FR-01, UC-04, NFR-18, 09_datasource-connectors（fixed）§認証・秘匿情報, [[IADR-0495]] 決定 1・2・4 (#458 段 S1):
// `vault:<path>#<key>` を Vault の KV v2 から**読む**解決器。素の HttpClient で k8s auth と KV v2 の GET だけを話す
// （VaultSharp を入れない。理由は [[IADR-0495]] 決定 2）。
//
// ■ 合成（移送期間）
//   - 平文（`vault:` で始まらない値）→ `PlaintextPassthroughConnectorSecretResolver` へ委ね、そのまま返す（段 S4 まで）。
//   - `vault:` で始まる値 → 形と**専用接頭辞**（`datasource/`）を確かめてから Vault を引く。
//   🔴 **Vault の失敗で平文へ倒さない。** 参照の行は参照としてしか解決しない（fail-closed）。
//
// ■ 専用接頭辞（[[IADR-0495]] 決定 1）
//   参照のパスは KV マウントからの相対で `datasource/<…>` に限る。ESO の policy（`secret/data/msp/*`）の外であり、
//   datasource-service の role の policy もこの接頭辞の `read` だけである。接頭辞の外・`.` / `..` / 空のセグメントを持つ参照は
//   **Vault へ送らずに** `MalformedReference` で止める（policy が拒むはずの要求を、そもそも出さない）。
//
// ■ 失敗の写像（`ConnectorSecretFailure`。[[IADR-0493]] 決定 3 の列挙を増やさない）
//   404（パス無し・現在版の削除・破棄）/ キー無し / 値が文字列でない → `NotFound`、値が空白 → `Empty`、
//   ログイン不能・403（取り直し後も）・5xx・不達・時間切れ・応答の解釈不能 → `Unreachable`。
//
// ■ 🔴 ログ（[[IADR-0493]] 決定 3 と同じ規律）
//   出すのは HTTP の状態コードと例外の**型名**だけ。値・参照のパス・キー名・例外オブジェクト・例外文を出さない
//   （例外文は URL＝パスを運び得る）。参照を同期のどの項目が持つかは呼び出し側（同期サービス）が番号で記録する。
public sealed class VaultConnectorSecretResolver(
    IHttpClientFactory httpClientFactory,
    IOptions<VaultConnectorSecretOptions> options,
    IServiceAccountTokenReader tokenReader,
    TimeProvider time,
    ILogger<VaultConnectorSecretResolver> logger) : IConnectorSecretResolver
{
    public const string ClientName = "ConnectorSecretVault";

    /// <summary>参照のパスが置かれる専用接頭辞（KV マウントからの相対）。policy の path と対で固定する。</summary>
    public const string PathPrefix = "datasource/";

    // リース満了のこれだけ手前で取り直す（満了ちょうどで 403 を踏まないため。BFF の VaultKvClient と同じ）。
    private static readonly TimeSpan RenewMargin = TimeSpan.FromSeconds(30);

    private readonly PlaintextPassthroughConnectorSecretResolver _plaintext = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    public async Task<ConnectorSecretResolution> ResolveAsync(string configuredValue, CancellationToken ct)
    {
        // 移送期間の前の端（[[IADR-0493]] 決定 2）。平文は素通しの解決器へ委ねる。
        if (!ConnectorSecretReference.LooksLikeReference(configuredValue))
            return await _plaintext.ResolveAsync(configuredValue, ct);

        if (!ConnectorSecretReference.TryParse(configuredValue, out var reference) || !IsUnderDedicatedPrefix(reference!.Path))
            return ConnectorSecretResolution.Failed(ConnectorSecretFailure.MalformedReference);

        try
        {
            using var response = await SendWithTokenAsync(reference.Path, ct);
            if (response is null)
                return ConnectorSecretResolution.Failed(ConnectorSecretFailure.Unreachable);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return ConnectorSecretResolution.Failed(ConnectorSecretFailure.NotFound);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Vault がコネクタの資格情報の読み取りを受け付けない: {Status}", (int)response.StatusCode);
                return ConnectorSecretResolution.Failed(ConnectorSecretFailure.Unreachable);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return Interpret(document.RootElement, reference.Key);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            // 🔴 例外オブジェクトも例外文も渡さない（URL＝参照のパスを運び得る）。型名だけ。
            logger.LogWarning("Vault からコネクタの資格情報を読めない: {ExceptionType}", ex.GetType().Name);
            return ConnectorSecretResolution.Failed(ConnectorSecretFailure.Unreachable);
        }
    }

    // `datasource/<…>` だけを通す。Vault のパスは大文字小文字を区別するので接頭辞の比較も区別する。
    internal static bool IsUnderDedicatedPrefix(string path)
    {
        if (!path.StartsWith(PathPrefix, StringComparison.Ordinal) || path.Length == PathPrefix.Length)
            return false;

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
        }

        return true;
    }

    // KV v2 の読み取りの応答 `{"data":{"data":{…},"metadata":{…}}}` から 1 キーを取り出す。
    private static ConnectorSecretResolution Interpret(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var envelope)
            || envelope.ValueKind != JsonValueKind.Object)
        {
            return ConnectorSecretResolution.Failed(ConnectorSecretFailure.Unreachable);
        }

        // 現在版が削除・破棄されていると、data.data は null になる（Vault は通常 404 で返すが、200 でも同じ扱いにする）。
        if (envelope.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            var deleted = metadata.TryGetProperty("deletion_time", out var deletion)
                          && deletion.ValueKind == JsonValueKind.String
                          && !string.IsNullOrEmpty(deletion.GetString());
            var destroyed = metadata.TryGetProperty("destroyed", out var d) && d.ValueKind == JsonValueKind.True;
            if (deleted || destroyed)
                return ConnectorSecretResolution.Failed(ConnectorSecretFailure.NotFound);
        }

        if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return ConnectorSecretResolution.Failed(ConnectorSecretFailure.NotFound);

        if (!data.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            return ConnectorSecretResolution.Failed(ConnectorSecretFailure.NotFound);

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text)
            ? ConnectorSecretResolution.Failed(ConnectorSecretFailure.Empty)
            : ConnectorSecretResolution.Resolved(text);
    }

    // トークン付きで GET を送る。403 のときだけトークンを取り直して 1 度やり直す（リースが先に切れた場合）。
    // ログインできなければ null。
    private async Task<HttpResponseMessage?> SendWithTokenAsync(string path, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        var uri = $"v1/{EscapePath(options.Value.KvMount)}/data/{EscapePath(path)}";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetTokenAsync(forceRefresh: attempt > 0, ct);
            if (token is null) return null;

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("X-Vault-Token", token);
            var response = await client.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Forbidden || attempt > 0) return response;
            response.Dispose();
        }

        return null;
    }

    private async Task<string?> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token is not null && time.GetUtcNow() < _tokenExpiresAt)
                return _token;

            _token = null;
            string jwt;
            try
            {
                jwt = await tokenReader.ReadAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("ServiceAccount トークンを読めない（Vault へログインできない）: {ExceptionType}", ex.GetType().Name);
                return null;
            }

            var o = options.Value;
            using var response = await httpClientFactory.CreateClient(ClientName).PostAsJsonAsync(
                $"v1/auth/{EscapePath(o.AuthMount)}/login", new { role = o.Role, jwt }, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Vault の k8s auth ログインに失敗: {Status}", (int)response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var auth = document.RootElement.GetProperty("auth");
            var token = auth.GetProperty("client_token").GetString();
            if (string.IsNullOrEmpty(token)) return null;

            var lease = TimeSpan.FromSeconds(auth.TryGetProperty("lease_duration", out var d) ? d.GetInt32() : 0);
            _token = token;
            _tokenExpiresAt = time.GetUtcNow() + (lease > RenewMargin * 2 ? lease - RenewMargin : lease);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    // 呼び出し側の ct による取り消しだけは外へ出す（[[IADR-0493]] 決定 3。#1604 と同じ）。それ以外は Unreachable へ畳む。
    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);
}
