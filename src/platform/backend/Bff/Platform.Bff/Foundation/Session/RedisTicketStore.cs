using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Distributed;
using System.Security.Claims;

namespace Platform.Bff.Foundation.Session;

// NFR, ADR-0032, IADR-0250 決定 4, #439: セッション実体を Redis に置く。
//
// 🔴 **これが「全セッション即時失効」の実現手段そのものである。**
// `CookieAuthenticationOptions.SessionStore` の既定は `null` で、その場合チケットは
// **Cookie 本体に載る**（実測）。すると**サーバ側に消す対象が無く、失効は Cookie の有効期限切れを
// 待つしかない** —— 非機能要件（無効化・退職時の即時失効）が構造的に満たせない。
//
// `ITicketStore` を挟むと Cookie はセッションキーだけを運び、**ストアから消した瞬間に失効する。**
public sealed class RedisTicketStore(IDistributedCache cache) : ITicketStore
{
    private const string TicketPrefix = "bff:ticket:";
    private const string UserIndexPrefix = "bff:user:";

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Guid.NewGuid().ToString("N");
        await RenewAsync(key, ticket);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var options = new DistributedCacheEntryOptions();
        // チケット自身の失効時刻をストアの TTL に写す。持たない場合は呼び出し側（Cookie 設定）に従う。
        if (ticket.Properties.ExpiresUtc is { } expires)
            options.SetAbsoluteExpiration(expires);

        await cache.SetAsync(TicketPrefix + key, TicketSerializer.Default.Serialize(ticket), options);

        // FR-09 / NFR: 利用者単位で失効させるための索引。**これが無いと「その人の全セッション」を
        // 列挙できず、即時失効が「今使っている 1 本だけ」に縮む。**
        var subject = SubjectOf(ticket);
        if (subject is null) return;

        var indexKey = UserIndexPrefix + subject;
        var existing = await cache.GetStringAsync(indexKey);
        var keys = Split(existing);
        if (keys.Add(key))
            await cache.SetStringAsync(indexKey, string.Join(',', keys), options);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var bytes = await cache.GetAsync(TicketPrefix + key);
        return bytes is null ? null : TicketSerializer.Default.Deserialize(bytes);
    }

    public async Task RemoveAsync(string key)
    {
        // 索引から外すために、消す前に持ち主を読む。
        var ticket = await RetrieveAsync(key);
        await cache.RemoveAsync(TicketPrefix + key);

        var subject = ticket is null ? null : SubjectOf(ticket);
        if (subject is null) return;

        var indexKey = UserIndexPrefix + subject;
        var keys = Split(await cache.GetStringAsync(indexKey));
        if (!keys.Remove(key)) return;

        if (keys.Count == 0) await cache.RemoveAsync(indexKey);
        else await cache.SetStringAsync(indexKey, string.Join(',', keys));
    }

    /// <summary>
    /// NFR: **その利用者の全セッションを即時失効させる。**
    /// アカウント無効化・退職・パスワードリセット、および認可サーバからの
    /// バックチャネルログアウトで呼ぶ。
    /// </summary>
    public async Task<int> RemoveAllForSubjectAsync(string subject)
    {
        var indexKey = UserIndexPrefix + subject;
        var keys = Split(await cache.GetStringAsync(indexKey));
        foreach (var key in keys)
            await cache.RemoveAsync(TicketPrefix + key);
        await cache.RemoveAsync(indexKey);
        return keys.Count;
    }

    // Keycloak の subject。`sub` が無い場合は NameIdentifier へ写像されている。
    private static string? SubjectOf(AuthenticationTicket ticket) =>
        ticket.Principal.FindFirst("sub")?.Value
        ?? ticket.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private static HashSet<string> Split(string? csv) =>
        string.IsNullOrEmpty(csv)
            ? []
            : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
