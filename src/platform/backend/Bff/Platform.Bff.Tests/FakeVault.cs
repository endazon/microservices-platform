using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Bff.Foundation.Secrets;

namespace Platform.Bff.Tests;

// SC-22, IADR-0433 決定 1・2, IADR-0453 決定 4・5・7 (#1411): Vault（KV v2 ＋ k8s auth）の偽物。
//
// 🔴 **実 Vault の権限の形を写す**: data の GET（値の読み出し）は 403 を返す —— BFF が値を読みに行く実装へ
// 変わったら、ここで落ちる。PATCH は `application/merge-patch+json` 以外を 415 で拒み、KV が無いか
// **現在版が削除・破棄されていれば 404**（KV v2 の patch は削除済みの版に効かない）。
// IADR-0454 決定 1 (#1467): 🔴 **POST は metadata が在る KV に対して `cas` に関係なく 403**。実 Vault は既存 path への
// POST を `update` として権限判定し、BFF の policy に `update` は無い（IADR-0453 決定 10）。
public sealed class FakeVault
{
    public const string Address = "http://vault.test:8200";
    public const string ServiceAccountJwt = "fake-service-account-jwt";
    public const string ExpectedRole = "bff-secret-writer";

    public sealed record RecordedRequest(string Method, string Path, string? ContentType, string? Body, string? Token);

    public sealed class Kv
    {
        public int Version { get; set; }
        public Dictionary<string, string> Data { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset CreatedAt { get; set; }
        public bool Deleted { get; set; }
        public bool Destroyed { get; set; }
    }

    private int _loginCount;

    // IADR-0454 決定 3 (#1467): 作成時刻は**書き込みごとに進める**。版から決めると、metadata を消して作り直した版 1 が
    // 元の版 1 と同じ時刻を持ち、最終更新者の時刻の突き合わせを試験できない。
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 14, 1, 0, 0, TimeSpan.Zero);
    private int _writeSequence;

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
    public ConcurrentDictionary<string, Kv> Store { get; } = new(StringComparer.Ordinal);

    /// <summary>ログインで払い出すトークン。差し替えると、BFF が保持中のトークンは 403 になる。</summary>
    public string CurrentToken { get; set; } = "fake-vault-token-1";

    public HttpStatusCode? LoginStatus { get; set; }
    public ConcurrentDictionary<string, HttpStatusCode> MetadataStatus { get; } = new(StringComparer.Ordinal);
    public HttpStatusCode? WriteStatus { get; set; }
    public bool Throws { get; set; }

    public int LoginCount => _loginCount;

    public void Reset()
    {
        Requests.Clear();
        Store.Clear();
        MetadataStatus.Clear();
        // 🔴 `CurrentToken` は戻さない（#1502 で実測した順序依存）。BFF の Vault クライアントはトークンを singleton で保持し、
        // 試験をまたいで残る。トークンを回した試験（`Vault_token_is_reused_and_renewed_once_on_403`）の直後にここで既定値へ戻すと、
        // 次の試験の最初の書き込みが 403 → 再ログイン → 再送になり、data への要求が 2 本になる（1 本を数える試験が落ちる）。
        LoginStatus = null;
        WriteStatus = null;
        Throws = false;
        Interlocked.Exchange(ref _loginCount, 0);
        Interlocked.Exchange(ref _writeSequence, 0);
    }

    /// <summary>KV を置く（版は既存 ＋1）。コンソール・bootstrap からの書き込みの再現にも使う。</summary>
    public Kv Put(string path, params (string Key, string Value)[] data)
    {
        var kv = Store.GetOrAdd(path, _ => new Kv());
        foreach (var (key, value) in data) kv.Data[key] = value;
        kv.Version++;
        kv.CreatedAt = BaseTime.AddSeconds(Interlocked.Increment(ref _writeSequence));
        kv.Deleted = false;
        kv.Destroyed = false;
        return kv;
    }

    public HttpMessageHandler CreateHandler() => new Handler(this);

    public sealed class TokenReader : IServiceAccountTokenReader
    {
        public Task<string> ReadAsync(CancellationToken ct) => Task.FromResult(ServiceAccountJwt);
    }

    private sealed class Handler(FakeVault vault) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var token = request.Headers.TryGetValues("X-Vault-Token", out var values) ? values.First() : null;
            vault.Requests.Enqueue(new RecordedRequest(
                request.Method.Method, path, request.Content?.Headers.ContentType?.MediaType, body, token));

            if (vault.Throws)
                throw new HttpRequestException("vault unreachable (fake)");

            if (path == "/v1/auth/kubernetes/login")
            {
                Interlocked.Increment(ref vault._loginCount);
                if (vault.LoginStatus is { } loginStatus) return Json(loginStatus, """{"errors":["denied"]}""");
                var login = JsonNode.Parse(body ?? "{}");
                if ((string?)login?["role"] != ExpectedRole || (string?)login?["jwt"] != ServiceAccountJwt)
                    return Json(HttpStatusCode.BadRequest, """{"errors":["invalid role or jwt"]}""");
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["auth"] = new JsonObject { ["client_token"] = vault.CurrentToken, ["lease_duration"] = 3600 },
                }.ToJsonString());
            }

            if (token != vault.CurrentToken)
                return Json(HttpStatusCode.Forbidden, """{"errors":["permission denied"]}""");

            const string metadataPrefix = "/v1/secret/metadata/";
            const string dataPrefix = "/v1/secret/data/";
            if (path.StartsWith(metadataPrefix, StringComparison.Ordinal))
            {
                var kvPath = Uri.UnescapeDataString(path[metadataPrefix.Length..]);
                if (request.Method != HttpMethod.Get) return Json(HttpStatusCode.Forbidden, """{"errors":["permission denied"]}""");
                if (vault.MetadataStatus.TryGetValue(kvPath, out var status)) return Json(status, """{"errors":["forced"]}""");
                if (!vault.Store.TryGetValue(kvPath, out var kv)) return Json(HttpStatusCode.NotFound, """{"errors":[]}""");
                var deletion = kv.Deleted ? "2026-09-14T02:00:00Z" : "";
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["data"] = new JsonObject
                    {
                        ["current_version"] = kv.Version,
                        ["updated_time"] = kv.CreatedAt.ToString("O"),
                        ["versions"] = new JsonObject
                        {
                            [kv.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                            {
                                ["created_time"] = kv.CreatedAt.ToString("O"),
                                ["deletion_time"] = deletion,
                                ["destroyed"] = kv.Destroyed,
                            },
                        },
                    },
                }.ToJsonString());
            }

            if (path.StartsWith(dataPrefix, StringComparison.Ordinal))
            {
                var kvPath = Uri.UnescapeDataString(path[dataPrefix.Length..]);
                if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Put)
                    // 🔴 data の read は与えていない。PUT（全置換）は BFF が使ってはならない方式である。
                    return Json(HttpStatusCode.Forbidden, """{"errors":["permission denied"]}""");
                if (vault.WriteStatus is { } writeStatus) return Json(writeStatus, """{"errors":["forced"]}""");

                var node = JsonNode.Parse(body ?? "{}")!;
                var data = node["data"]?.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!) ?? [];

                if (request.Method == HttpMethod.Patch)
                {
                    if (request.Content?.Headers.ContentType?.MediaType != VaultKvClient.MergePatchContentType)
                        return Json(HttpStatusCode.UnsupportedMediaType, """{"errors":["unsupported media type"]}""");
                    if (!vault.Store.TryGetValue(kvPath, out var existing) || existing.Deleted || existing.Destroyed)
                        return Json(HttpStatusCode.NotFound, """{"errors":[]}""");
                    var kv = vault.Put(kvPath, [.. data.Select(p => (p.Key, p.Value))]);
                    return Written(kv);
                }

                if (request.Method == HttpMethod.Post)
                {
                    // 🔴 metadata が在る path への POST は `update`（policy に無い）。cas の検査より前に権限で拒まれる。
                    if (vault.Store.ContainsKey(kvPath))
                        return Json(HttpStatusCode.Forbidden, """{"errors":["1 error occurred:\n\t* permission denied\n\n"]}""");
                    var kv = vault.Put(kvPath, [.. data.Select(p => (p.Key, p.Value))]);
                    return Written(kv);
                }
            }

            return Json(HttpStatusCode.NotFound, """{"errors":[]}""");
        }

        private static HttpResponseMessage Written(Kv kv) => Json(HttpStatusCode.OK, new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["version"] = kv.Version,
                ["created_time"] = kv.CreatedAt.ToString("O"),
                ["deletion_time"] = "",
                ["destroyed"] = false,
            },
        }.ToJsonString());

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

// SC-22, IADR-0453 決定 3 (#1411): 最終更新者の書き込み記録の置き場（Redis の代わり）。
public sealed class InMemorySecretWriteRecordStore : ISecretWriteRecordStore
{
    public ConcurrentDictionary<string, SecretWriteRecord> Records { get; } = new(StringComparer.Ordinal);

    public Task<SecretWriteRecord?> GetAsync(string item, CancellationToken ct) =>
        Task.FromResult(Records.TryGetValue(item, out var record) ? record : null);

    public Task SaveAsync(string item, SecretWriteRecord record, CancellationToken ct)
    {
        Records[item] = record;
        return Task.CompletedTask;
    }
}
