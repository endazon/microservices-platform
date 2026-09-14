using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Platform.Bff.Foundation.Secrets;

// SC-22, NFR-18, ADR-0095 決定 3, IADR-0096, IADR-0433 決定 1・2・4, IADR-0453 決定 4・5・7 (#1411):
// BFF が Vault の KV v2 へ**項目ごとに**書くためのクライアント。
//
// 🔴 **このクライアントは値を読まない。** 呼ぶのは metadata（版・作成時刻。値を持たない）の読み取りと、
// data への部分更新（`PATCH`）と、KV が無いときだけの作成（`POST` ＋ `cas=0`）の 3 つだけである。
// 権限の側でも data の `read` は与えていない（`deploy/local/vault/eso/policy-bff-secret-write.hcl`）。
//
// 🔴 **値をログ・例外メッセージへ出さない。** 要求本文をログに書く経路を持たない
// （`IHttpClientFactory` の既定ログは URL と状態コードだけで、URL に値は載らない）。

/// <summary>Vault への接続構成（`Vault:*`）。</summary>
public sealed class VaultOptions
{
    public const string SectionName = "Vault";

    /// <summary>
    /// Vault の URL（例 `http://vault.platform-infra.svc.cluster.local:8200`）。
    /// **空なら Vault を配備していない構成**として扱い、SC-22 の端点は 503 を返す（IADR-0453 決定 5）。
    /// </summary>
    public string? Address { get; set; }

    /// <summary>k8s auth のロール名（IADR-0433 決定 4）。</summary>
    public string Role { get; set; } = "bff-secret-writer";

    /// <summary>k8s auth のマウント名。</summary>
    public string AuthMount { get; set; } = "kubernetes";

    /// <summary>Pod の ServiceAccount トークンの置き場（projected token の既定位置）。</summary>
    public string ServiceAccountTokenPath { get; set; } = "/var/run/secrets/kubernetes.io/serviceaccount/token";

    /// <summary>1 回の HTTP 呼び出しの上限秒数。</summary>
    public int TimeoutSeconds { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Address);
}

/// <summary>Pod の ServiceAccount トークンを読む口（テストが差し替える）。</summary>
public interface IServiceAccountTokenReader
{
    Task<string> ReadAsync(CancellationToken ct);
}

public sealed class FileServiceAccountTokenReader(IOptions<VaultOptions> options) : IServiceAccountTokenReader
{
    public async Task<string> ReadAsync(CancellationToken ct) =>
        (await File.ReadAllTextAsync(options.Value.ServiceAccountTokenPath, ct)).Trim();
}

public enum VaultMetadataState
{
    /// <summary>KV に削除されていない現在版がある。</summary>
    Present,

    /// <summary>KV が無い（404）か、`current_version` が 0。</summary>
    Absent,

    /// <summary>取れない（403・5xx・不達・解釈不能）。</summary>
    Unavailable,

    /// <summary>
    /// metadata は在り、現在版が削除（`deletion_time`）または破棄（`destroyed`）されている（IADR-0454 決定 1）。
    /// 一覧では `Absent` と同じく未設定と出すが、**書き込みでは区別する** —— この KV へは `cas=0` で作れない。
    /// </summary>
    Deleted,
}

public sealed record VaultMetadata(VaultMetadataState State, int? CurrentVersion, DateTimeOffset? CurrentVersionCreatedAt)
{
    public static readonly VaultMetadata Absent = new(VaultMetadataState.Absent, null, null);
    public static readonly VaultMetadata Deleted = new(VaultMetadataState.Deleted, null, null);
    public static readonly VaultMetadata Unavailable = new(VaultMetadataState.Unavailable, null, null);
}

public enum VaultWriteOutcome
{
    Written,
    NotConfigured,

    /// <summary>ログインできない・不達・5xx。</summary>
    Unavailable,

    /// <summary>Vault が書き込みを拒んだ（403 等。policy と allowlist の食い違い）。</summary>
    Rejected,

    /// <summary>
    /// KV の現在版が削除・破棄されていて、画面からは書けない（IADR-0454 決定 1）。
    /// 🔴 BFF の権限（`create` / `patch`）では削除済みの版の上へ書く手段が無く、権限は広げない。
    /// 運用者がコンソールで版を復元してから書き直す。
    /// </summary>
    CurrentVersionDeleted,
}

public sealed record VaultWriteResult(VaultWriteOutcome Outcome, int Version = 0, DateTimeOffset UpdatedAt = default);

public interface IVaultKvClient
{
    bool IsConfigured { get; }

    /// <summary>ログインできるか（一覧を 503 にするかの判定）。</summary>
    Task<bool> CanAuthenticateAsync(CancellationToken ct);

    Task<VaultMetadata> ReadMetadataAsync(string mount, string path, CancellationToken ct);

    /// <summary>1 プロパティだけを部分更新する（KV が無ければ `cas=0` で作る）。</summary>
    Task<VaultWriteResult> WritePropertyAsync(string mount, string path, string property, string value, CancellationToken ct);
}

public sealed class VaultKvClient(
    IHttpClientFactory httpClientFactory,
    IOptions<VaultOptions> options,
    IServiceAccountTokenReader tokenReader,
    TimeProvider time,
    ILogger<VaultKvClient> logger) : IVaultKvClient
{
    public const string ClientName = "Vault";

    /// <summary>KV v2 の部分更新が要求する Content-Type（`application/json` だと 415 になる）。</summary>
    public const string MergePatchContentType = "application/merge-patch+json";

    // リース満了のこれだけ手前で取り直す（満了ちょうどで 403 を踏まないため）。
    private static readonly TimeSpan RenewMargin = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<bool> CanAuthenticateAsync(CancellationToken ct) =>
        IsConfigured && await GetTokenAsync(forceRefresh: false, ct) is not null;

    public async Task<VaultMetadata> ReadMetadataAsync(string mount, string path, CancellationToken ct)
    {
        if (!IsConfigured) return VaultMetadata.Unavailable;
        try
        {
            using var response = await SendWithTokenAsync(
                () => new HttpRequestMessage(HttpMethod.Get, KvUri(mount, "metadata", path)), ct);
            if (response is null) return VaultMetadata.Unavailable;
            if (response.StatusCode == HttpStatusCode.NotFound) return VaultMetadata.Absent;
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Vault metadata の取得に失敗: {Path} {Status}", path, (int)response.StatusCode);
                return VaultMetadata.Unavailable;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return InterpretMetadata(document.RootElement);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            logger.LogWarning(ex, "Vault metadata を取得できない: {Path}", path);
            return VaultMetadata.Unavailable;
        }
    }

    public async Task<VaultWriteResult> WritePropertyAsync(
        string mount, string path, string property, string value, CancellationToken ct)
    {
        if (!IsConfigured) return new VaultWriteResult(VaultWriteOutcome.NotConfigured);
        try
        {
            var patched = await PatchAsync(mount, path, property, value, ct);
            if (patched is not null)
                return patched;

            // PATCH の 404 は「KV が無い」と「現在版が削除・破棄されている」の 2 通りある。metadata（値を持たない）で見分ける。
            // IADR-0454 決定 1 (#1467): 🔴 **削除・破棄された KV へは POST を送らない。** metadata が在る path への POST を
            // Vault は `update` として権限判定し、BFF の policy に `update` は無い（IADR-0453 決定 10）。
            // 送れば 403 になり、利用者には原因の分からない 502 が返っていた。
            var metadata = await ReadMetadataAsync(mount, path, ct);
            switch (metadata.State)
            {
                case VaultMetadataState.Deleted:
                    return new VaultWriteResult(VaultWriteOutcome.CurrentVersionDeleted);
                case VaultMetadataState.Unavailable:
                    return new VaultWriteResult(VaultWriteOutcome.Unavailable);
                case VaultMetadataState.Present:
                    // PATCH と metadata の間に誰かが作った・復元した。部分更新を 1 度だけやり直す。
                    return await PatchAsync(mount, path, property, value, ct) ?? new VaultWriteResult(VaultWriteOutcome.Rejected);
            }

            // KV がまだ無い（PATCH は既存の KV にしか効かない）。
            // 🔴 **`cas=0` ＝「存在しないときだけ作る」。** 競合して誰かが先に作っていたら 400 が返り、
            // 既存の KV を全置換しない。その場合は部分更新を 1 度だけやり直す。
            // （IADR-0454 帰結: `update` を持たない policy の下では、この競合は 400 ではなく 403 ＝ Rejected として現れる。）
            var created = await CreateIfAbsentAsync(mount, path, property, value, ct);
            if (created is not null)
                return created;
            return await PatchAsync(mount, path, property, value, ct) ?? new VaultWriteResult(VaultWriteOutcome.Rejected);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            // 🔴 例外の中身（要求本文）はログへ出さない。型と経路だけを残す。
            logger.LogWarning("Vault へ書き込めない: {Path} {ExceptionType}", path, ex.GetType().Name);
            return new VaultWriteResult(VaultWriteOutcome.Unavailable);
        }
    }

    // 🔴 部分更新。**KV が無い（404）ときは null** を返し、呼び出し側が作成へ回る。
    private async Task<VaultWriteResult?> PatchAsync(
        string mount, string path, string property, string value, CancellationToken ct)
    {
        using var response = await SendWithTokenAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, KvUri(mount, "data", path))
            {
                Content = JsonBody(new Dictionary<string, object> { ["data"] = new Dictionary<string, string> { [property] = value } }),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(MergePatchContentType);
            return request;
        }, ct);

        if (response is null) return new VaultWriteResult(VaultWriteOutcome.Unavailable);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return await InterpretWriteAsync(response, path, ct);
    }

    private async Task<VaultWriteResult?> CreateIfAbsentAsync(
        string mount, string path, string property, string value, CancellationToken ct)
    {
        using var response = await SendWithTokenAsync(() => new HttpRequestMessage(HttpMethod.Post, KvUri(mount, "data", path))
        {
            Content = JsonBody(new Dictionary<string, object>
            {
                ["options"] = new Dictionary<string, int> { ["cas"] = 0 },
                ["data"] = new Dictionary<string, string> { [property] = value },
            }),
        }, ct);

        if (response is null) return new VaultWriteResult(VaultWriteOutcome.Unavailable);
        // 400 は check-and-set の不一致（先に作られた）。呼び出し側が PATCH をやり直す。
        if (response.StatusCode == HttpStatusCode.BadRequest) return null;
        return await InterpretWriteAsync(response, path, ct);
    }

    private async Task<VaultWriteResult> InterpretWriteAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var data = document.RootElement.GetProperty("data");
            var version = data.GetProperty("version").GetInt32();
            var createdAt = data.TryGetProperty("created_time", out var created)
                            && DateTimeOffset.TryParse(created.GetString(), out var parsed)
                ? parsed
                : time.GetUtcNow();
            return new VaultWriteResult(VaultWriteOutcome.Written, version, createdAt);
        }

        logger.LogWarning("Vault が書き込みを受け付けない: {Path} {Status}", path, (int)response.StatusCode);
        return (int)response.StatusCode >= 500
            ? new VaultWriteResult(VaultWriteOutcome.Unavailable)
            : new VaultWriteResult(VaultWriteOutcome.Rejected);
    }

    // トークン付きで送る。403 のときだけトークンを取り直して 1 度やり直す（リースが先に切れた場合）。
    // ログインできなければ null。
    private async Task<HttpResponseMessage?> SendWithTokenAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetTokenAsync(forceRefresh: attempt > 0, ct);
            if (token is null) return null;

            using var request = build();
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
            try
            {
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
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                logger.LogWarning("Vault へログインできない: {ExceptionType}", ex.GetType().Name);
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static VaultMetadata InterpretMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)) return VaultMetadata.Unavailable;
        var current = data.TryGetProperty("current_version", out var cv) ? cv.GetInt32() : 0;
        if (current <= 0) return VaultMetadata.Absent;
        if (!data.TryGetProperty("versions", out var versions)
            || !versions.TryGetProperty(current.ToString(System.Globalization.CultureInfo.InvariantCulture), out var version))
        {
            return VaultMetadata.Unavailable;
        }

        var deleted = version.TryGetProperty("deletion_time", out var deletion)
                      && !string.IsNullOrEmpty(deletion.GetString());
        var destroyed = version.TryGetProperty("destroyed", out var d) && d.ValueKind == JsonValueKind.True;
        if (deleted || destroyed) return VaultMetadata.Deleted;

        DateTimeOffset? createdAt = version.TryGetProperty("created_time", out var created)
                                    && DateTimeOffset.TryParse(created.GetString(), out var parsed)
            ? parsed
            : null;
        return new VaultMetadata(VaultMetadataState.Present, current, createdAt);
    }

    private static ByteArrayContent JsonBody(object body)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static string KvUri(string mount, string kind, string path) =>
        $"v1/{EscapePath(mount)}/{kind}/{EscapePath(path)}";

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);
}
