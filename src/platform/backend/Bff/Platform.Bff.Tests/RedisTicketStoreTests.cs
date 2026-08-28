using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Platform.Bff.Foundation.Session;
using System.Security.Claims;

namespace Platform.Bff.Tests;

// NFR, ADR-0032, IADR-0251 決定 4, #439 第 3 段(3b): **「全セッション即時失効」の実体を試験する。**
//
// 🔴 **本ファイルは「装置が実体より甘くならない」ための土台である。**
// 既存の BFF テストは `BffTestFactory` が既定スキームを `Test` へ上書きしており、
// **本物の認証経路を丸ごと迂回する**（実測: 既定スキームを BffSession へ移す変異を入れても
// 既存 271 件は緑のままだった）。**つまり既存テストの緑は、セッション方式が動く証拠にならない。**
//
// ここでは実体（`RedisTicketStore`）をそのまま動かす。Redis の代わりに
// `MemoryDistributedCache` を挿すだけで、**チケットの直列化・索引・失効はすべて本物**である。
public class RedisTicketStoreTests
{
    private static RedisTicketStore NewStore(out IDistributedCache cache)
    {
        cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        return new RedisTicketStore(cache);
    }

    private static AuthenticationTicket TicketFor(string subject, string? name = null)
    {
        var identity = new ClaimsIdentity(
            [new Claim("sub", subject), new Claim(ClaimTypes.Name, name ?? subject)],
            BffSessionExtensions.SessionScheme);
        return new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties(),
            BffSessionExtensions.SessionScheme);
    }

    // ★ 往復できること。ここが壊れていると以下すべてが無意味になる（陽性対照）。
    [Fact]
    public async Task Stored_ticket_can_be_retrieved()
    {
        var store = NewStore(out _);
        var key = await store.StoreAsync(TicketFor("user-1", "Alice"));

        var round = await store.RetrieveAsync(key);

        round.Should().NotBeNull();
        round!.Principal.FindFirst("sub")!.Value.Should().Be("user-1");
        round.Principal.Identity!.Name.Should().Be("Alice");
    }

    // ★ 1 セッションの失効。
    [Fact]
    public async Task Removed_ticket_is_gone_immediately()
    {
        var store = NewStore(out _);
        var key = await store.StoreAsync(TicketFor("user-1"));

        await store.RemoveAsync(key);

        (await store.RetrieveAsync(key)).Should().BeNull();
    }

    // 🔴 ★ **非機能要件の本丸: その利用者の「全」セッションが即時に消える。**
    //
    // 索引が無いと「いま使っている 1 本」しか消せず、**別の端末で開いたままのセッションが生き残る。**
    // 退職・アカウント無効化はまさにその状況で使われる。
    [Fact]
    public async Task All_sessions_of_a_user_are_revoked_at_once()
    {
        var store = NewStore(out _);
        var laptop = await store.StoreAsync(TicketFor("user-1"));
        var phone = await store.StoreAsync(TicketFor("user-1"));
        var tablet = await store.StoreAsync(TicketFor("user-1"));

        var removed = await store.RemoveAllForSubjectAsync("user-1");

        removed.Should().Be(3);
        (await store.RetrieveAsync(laptop)).Should().BeNull();
        (await store.RetrieveAsync(phone)).Should().BeNull();
        (await store.RetrieveAsync(tablet)).Should().BeNull();
    }

    // 🔴 ★ **陰性対照: 他人のセッションを巻き込まない。**
    //
    // 「全部消す」実装（索引を無視して総なめする形）でも上のテストは通ってしまう。
    // **1 人を無効化したら全員がログアウトする**のは、要件を満たしているように見えて重大な欠陥である。
    [Fact]
    public async Task Revoking_one_user_does_not_touch_another()
    {
        var store = NewStore(out _);
        var mine = await store.StoreAsync(TicketFor("user-1"));
        var theirs = await store.StoreAsync(TicketFor("user-2"));

        await store.RemoveAllForSubjectAsync("user-1");

        (await store.RetrieveAsync(mine)).Should().BeNull();
        (await store.RetrieveAsync(theirs)).Should().NotBeNull(
            "1 人の無効化で全員がログアウトしてはならない");
    }

    // ★ 索引が追随すること。1 本ずつ消した後に一括失効させても破綻しない。
    [Fact]
    public async Task Index_follows_individual_removals()
    {
        var store = NewStore(out _);
        var first = await store.StoreAsync(TicketFor("user-1"));
        var second = await store.StoreAsync(TicketFor("user-1"));

        await store.RemoveAsync(first);
        var removed = await store.RemoveAllForSubjectAsync("user-1");

        removed.Should().Be(1, "既に消した 1 本を二重に数えない");
        (await store.RetrieveAsync(second)).Should().BeNull();
    }

    // 🔴 ★ **チケットは Cookie ではなくストアに在る。**
    // 決定 4 の要点は「サーバ側に消す対象が在ること」である。キーだけでは中身を復元できない
    // ——ストアから消えていれば、キーを持っていても何も取り出せない。
    [Fact]
    public async Task Session_key_alone_carries_no_identity_once_the_store_is_cleared()
    {
        var store = NewStore(out var cache);
        var key = await store.StoreAsync(TicketFor("user-1"));

        // ストア側だけを消す（ブラウザは Cookie＝キーを持ったまま）。
        await store.RemoveAllForSubjectAsync("user-1");

        (await store.RetrieveAsync(key)).Should().BeNull();
        (await cache.GetAsync("bff:ticket:" + key, TestContext.Current.CancellationToken)).Should().BeNull();
    }
}
