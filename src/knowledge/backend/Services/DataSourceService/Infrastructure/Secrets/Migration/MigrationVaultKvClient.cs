using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataSourceService.Infrastructure.Secrets.Migration;

// NFR-18, 計画 ADR-0126 フォローアップ 2, [[IADR-0663]] 決定 3 (#458 段 S4): 移送が使う **KV v2 の読み書き**（運用者が渡した短命のトークンで名乗る）。
//
// - 名乗りは datasource-service の Pod の役（`datasource-connector-reader`。read だけ）ではない。運用者が
//   `datasource-credential-migrate` の policy（専用接頭辞の 1 セグメントに create・update・read）で発行したトークンを、
//   移送のコマンドが標準入力から受け取る（引数・環境変数に置かない —— プロセスの一覧と Pod の定義に残さない）。
// - 書き込みは check-and-set（`options.cas`）。読んだ版のまま書く（無ければ 0 ＝作成だけ）。同じパスの別のキーを消さない（読んだ値に足して書く）。
// - 🔴 値・トークン・パスをログ・例外の文言に出さない。返すのは状態コードだけ。
public sealed class MigrationVaultKvClient(HttpClient http, string token, string kvMount)
{
    public sealed record KvSecret(int Version, IReadOnlyDictionary<string, string> Data);

    // 現在版を読む。無い（404・現在版の削除・破棄）なら版 0 の空。失敗は例外（状態コードだけ）。
    public async Task<KvSecret> ReadAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/{kvMount}/data/{path}");
        request.Headers.Add("X-Vault-Token", token);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return await ReadMetadataVersionAsync(path, ct);
        if (!response.IsSuccessStatusCode)
            throw new VaultMigrationException((int)response.StatusCode);

        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        var envelope = root?["data"] as JsonObject;
        var version = envelope?["metadata"]?["version"]?.GetValue<int>() ?? 0;
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (envelope?["data"] is JsonObject values)
        {
            foreach (var (k, v) in values)
            {
                if (v is JsonValue jv && jv.TryGetValue<string>(out var text))
                    data[k] = text;
            }
        }

        return new KvSecret(version, data);
    }

    // 現在版が削除・破棄されていると data の GET は 404 になるが、版は残る。check-and-set に要る版を metadata から読む。
    private async Task<KvSecret> ReadMetadataVersionAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/{kvMount}/metadata/{path}");
        request.Headers.Add("X-Vault-Token", token);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new KvSecret(0, new Dictionary<string, string>());
        if (!response.IsSuccessStatusCode)
            throw new VaultMigrationException((int)response.StatusCode);

        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        var version = root?["data"]?["current_version"]?.GetValue<int>() ?? 0;
        return new KvSecret(version, new Dictionary<string, string>());
    }

    // check-and-set で書く（`cas` は読んだ版）。版が進んでいれば 400 で拒まれる（例外）。
    public async Task WriteAsync(string path, IReadOnlyDictionary<string, string> data, int cas, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/{kvMount}/data/{path}")
        {
            Content = JsonContent.Create(new { data, options = new { cas } }),
        };
        request.Headers.Add("X-Vault-Token", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new VaultMigrationException((int)response.StatusCode);
    }
}

// 移送の Vault の呼び出しの失敗。🔴 **状態コードだけ**を運ぶ（応答の本文・パス・値を運ばない）。
public sealed class VaultMigrationException(int status) : Exception($"Vault が移送の要求を受け付けない: {status}")
{
    public int Status { get; } = status;
}
