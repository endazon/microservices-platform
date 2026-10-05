using System.Net.Http.Json;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Ports.Secrets;

namespace Knowledge.Bff.Endpoints.Secrets;

// SC-22, SC-06, FR-01, NFR-18, 計画 ADR-0126 決定 1・2・4, [[IADR-0501]] 決定 1・3 (#458 段 S2):
// SC-22 の「データソースの資格情報」の群（`datasource-credentials`）の成員を DataSourceService から供給する。
//
// - 成員 = `GET /datasources/credentials`（有効で、コネクタが資格情報のキーを宣言するデータソース）。
//   🔴 **登録済みの ID の検査は、BFF がこの一覧で行う**（決定 2「登録済みの ID は BFF のコードで限る」）。
// - 参照の配置 = `PUT /datasources/{id}/credentials/{key}/reference`（本文なし。値はこの経路を通らない）。
// - 利用者の資格情報（Authorization）を後段へ伝播する（`DataSourceBffEndpoints` と同じ。後段にも同じ認可がある）。
// - 🔴 **失敗は null で返す**（例外を外へ出さない）。ログは状態コードと例外の型名だけ。
public sealed class DataSourceCredentialGroupSource(
    IHttpClientFactory httpFactory,
    ILogger<DataSourceCredentialGroupSource> logger) : ISecretItemGroupSource
{
    /// <summary>`deploy/bootstrap/sc22-secret-items.json` の `groups[].group` と一致させる。</summary>
    public const string GroupName = "datasource-credentials";

    public string Group => GroupName;

    public async Task<IReadOnlyList<SecretItemGroupMember>?> ListMembersAsync(HttpContext http, CancellationToken ct)
    {
        try
        {
            using var client = CreateForwardingClient(http);
            using var response = await client.GetAsync("/datasources/credentials", ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("データソースの資格情報の一覧を取得できない: {Status}", (int)response.StatusCode);
                return null;
            }

            var items = await response.Content.ReadFromJsonAsync<List<DataSourceCredentialItemDto>>(ct);
            if (items is null)
                return null;

            return [.. items.Select(item => new SecretItemGroupMember(
                // 🔴 Vault のパスと参照（`vault:datasource/<ID>#…`）の両方で `D` 形式（小文字）に揃える。
                item.Id.ToString("D"),
                item.Name,
                item.SourceType,
                [.. (item.Properties ?? []).Select(p => new SecretItemGroupProperty(p.Name, SupplyOf(p.Supply)))]))];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                       && !ct.IsCancellationRequested)
        {
            logger.LogWarning("データソースの資格情報の一覧を取得できない: {ExceptionType}", ex.GetType().Name);
            return null;
        }
    }

    public async Task<SecretItemGroupSupply?> EnsureReferenceAsync(
        HttpContext http, string memberId, string property, CancellationToken ct)
    {
        if (!Guid.TryParseExact(memberId, "D", out var id))
            return null;
        try
        {
            using var client = CreateForwardingClient(http);
            using var response = await client.PutAsync(
                $"/datasources/{id:D}/credentials/{Uri.EscapeDataString(property)}/reference", content: null, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("データソースの資格情報の参照を置けない: {Status}", (int)response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<DataSourceCredentialReferenceResultDto>(ct);
            return result is null ? null : SupplyOf(result.Supply);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                       && !ct.IsCancellationRequested)
        {
            logger.LogWarning("データソースの資格情報の参照を置けない: {ExceptionType}", ex.GetType().Name);
            return null;
        }
    }

    // 計画 ADR-0126 決定 4: 🔴 **未知の符号は「画面以外」へ倒す**（「画面」と出して、書いた値が使われると誤認させない）。
    internal static SecretItemGroupSupply SupplyOf(string? supply) => supply switch
    {
        DataSourceCredentialSupplies.Reference => SecretItemGroupSupply.Referenced,
        DataSourceCredentialSupplies.Absent => SecretItemGroupSupply.Unset,
        _ => SecretItemGroupSupply.OtherSource,
    };

    private HttpClient CreateForwardingClient(HttpContext http)
    {
        var client = httpFactory.CreateClient("DataSourceService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
        return client;
    }
}

// SC-22, [[IADR-0501]] 決定 1: BFF ホスト（Platform.Bff の Program.cs）から 1 行で呼ぶ。
// 名前付きクライアント `DataSourceService` はホスト側に既に在る（`/bff/datasources` が使うもの）ので、ここでは作らない。
public static class DataSourceCredentialGroupExtensions
{
    public static IServiceCollection AddDataSourceCredentialGroup(this IServiceCollection services)
    {
        services.AddSingleton<ISecretItemGroupSource, DataSourceCredentialGroupSource>();
        return services;
    }
}
