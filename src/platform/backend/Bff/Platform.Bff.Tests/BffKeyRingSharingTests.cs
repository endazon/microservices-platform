using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Platform.Bff.Foundation.Endpoints;
using Platform.Bff.Foundation.Session;
using StackExchange.Redis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;

namespace Platform.Bff.Tests;

// NFR-07, ADR-0032, IADR-0251 決定 5, [[IADR-0510]] (#1780 / #1534 受け入れ基準 1):
// **2 つの BFF レプリカが同じ DataProtection 鍵リングを引き、互いのセッション Cookie を復号できること**を、
// 本番の配線（Program → AddBffSession）のまま 2 つの WebApplicationFactory で測る。
//
// 🔴 **単一ホストのテストでは鍵リングの共有は絶対に捕まらない** —— 鍵リングが 1 つしか無いので、
// 共有し忘れていても緑になる（BffSessionExtensions のコメント）。だからホストを 2 つ立てる。
//
// 差し替えるのは I/O の器だけである:
//   - Redis への接続（IConnectionMultiplexer）→ プロセス内のリスト。**鍵の保存先（RedisXmlRepository・
//     キー名・アプリケーション名）は本番の配線がそのまま置く。** テストは保存先を設定し直さない。
//   - セッション本体（IDistributedCache）→ メモリ。陽性・陰性とも 2 ホストで**同じ**実体を共有する
//     （変える変数を鍵リングだけにするため）。
//
// 変異の結果（#1780 作業仕様書 §結果）: キー名をレプリカごとに変える／保存先の設定を外す／
// SetApplicationName を外す、のいずれでも本クラスが赤になる。
public class BffKeyRingSharingTests
{
    // 🔴 **定数（BffSessionExtensions.DataProtectionKeysRedisKey）を引かずにリテラルで固定する。**
    // 定数を引くと「定数ごと綴りを変える」変更が素通りする。綴りを変えると、ローリング更新中の
    // 旧版と新版が別の鍵リングを引く（＝その間ログアウトが起きる）。Runbook の `LLEN` もこの綴りを見る。
    private const string KeyRingRedisKey = "bff:dataprotection-keys";

    // T-27: 鍵リングを共有した 2 ホストは、互いの Cookie を復号できる。
    [Fact]
    public async Task Replicas_sharing_the_key_ring_decrypt_each_others_session_cookie()
    {
        var keyRing = new InMemoryRedisLists();
        var sessions = NewSessionCache();
        await using var a = new KeyRingReplicaFactory(keyRing, sessions);
        await using var b = new KeyRingReplicaFactory(keyRing, sessions);
        // 順に起動する（A の起動時に作られた鍵を B が読む。並行起動の競合は本テストの論点ではない）。
        var clientA = a.CreateReplicaClient();
        var clientB = b.CreateReplicaClient();

        var cookieFromA = await SignInAsync(clientA, "alice");
        var cookieFromB = await SignInAsync(clientB, "bob");

        (await MeAsync(clientB, cookieFromA)).Should().Be((HttpStatusCode.OK, "alice"),
            "A が発行した Cookie を B が復号できなければ、B へ振られた要求は無作為にログアウトになる");
        (await MeAsync(clientA, cookieFromB)).Should().Be((HttpStatusCode.OK, "bob"),
            "逆向きも同じ（どちらのレプリカでログインしても、もう一方が受け付ける）");

        // 鍵は共有ストアの所定のキーにちょうど 1 件。B は A の鍵を使い、自前の鍵を作っていない。
        keyRing.Count(KeyRingRedisKey).Should().Be(1);
        keyRing.Keys.Should().BeEquivalentTo([KeyRingRedisKey]);

        // 🔴 アプリケーション識別子を直接読む。2 ホストは同じプロセス・同じ content root なので、
        // SetApplicationName を外しても既定の識別子（content root）が一致し、上の復号は通ってしまう。
        // 配置パスの違う版が混ざるローリング更新では一致しないため、固定値であることをここで見る。
        Discriminator(a).Should().Be("microservices-platform-bff");
        Discriminator(b).Should().Be("microservices-platform-bff");
    }

    // T-28: 陰性対照。鍵リングを共有しない 2 ホストでは、B は A の Cookie を復号できない。
    // **これが無いと、陽性は「共有していなくても通る器」でも緑になる**（例: 保存先の設定が外れて
    // 既定のファイルシステムへ落ちると、同じマシンの 2 ホストはそこを共有してしまう）。
    [Fact]
    public async Task Replicas_with_separate_key_rings_cannot_decrypt_each_others_session_cookie()
    {
        var keyRingA = new InMemoryRedisLists();
        var keyRingB = new InMemoryRedisLists();
        var sessions = NewSessionCache();
        await using var a = new KeyRingReplicaFactory(keyRingA, sessions);
        await using var b = new KeyRingReplicaFactory(keyRingB, sessions);
        var clientA = a.CreateReplicaClient();
        var clientB = b.CreateReplicaClient();

        var cookieFromA = await SignInAsync(clientA, "alice");

        (await MeAsync(clientA, cookieFromA)).Should().Be((HttpStatusCode.OK, "alice"),
            "Cookie そのものは有効である（B の 401 が Cookie の破損によるものでないことの対照）");
        (await MeAsync(clientB, cookieFromA)).Status.Should().Be(HttpStatusCode.Unauthorized,
            "鍵リングが別なら、セッション本体を共有していても Cookie を復号できない");

        keyRingA.Count(KeyRingRedisKey).Should().Be(1);
        keyRingB.Count(KeyRingRedisKey).Should().Be(1, "B は A の鍵を見られず、自前の鍵を作る");
    }

    private static IDistributedCache NewSessionCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static string Discriminator(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator!;

    /// <summary>本物の Cookie ハンドラでサインインさせ、Cookie ヘッダ用の `名前=値` を返す。</summary>
    private static async Task<string> SignInAsync(HttpClient client, string sub)
    {
        var resp = await client.GetAsync($"{SignInStartupFilter.Path}?sub={Uri.EscapeDataString(sub)}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var pairs = resp.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';', 2)[0])
                .Where(p => p.StartsWith("__Host-", StringComparison.Ordinal))
                .ToList()
            : [];
        pairs.Should().NotBeEmpty("サインインでセッション Cookie が発行されていない");
        return string.Join("; ", pairs);
    }

    private static async Task<(HttpStatusCode Status, string? Sub)> MeAsync(HttpClient client, string cookie)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/bff/auth/me");
        req.Headers.Add("Cookie", cookie);
        using var resp = await client.SendAsync(req);
        if (resp.StatusCode != HttpStatusCode.OK) return (resp.StatusCode, null);
        var me = await resp.Content.ReadFromJsonAsync<BffIdentityDto>();
        return (resp.StatusCode, me?.Subject);
    }

    /// <summary>
    /// BFF のレプリカ 1 つ。`BffTestFactory`（下流のスタブ・構成）を継ぎ、Redis 接続とセッション本体の
    /// 器だけを差し替える。鍵リングの配線（AddBffSession）には触れない。
    /// </summary>
    private sealed class KeyRingReplicaFactory(InMemoryRedisLists keyRing, IDistributedCache sessions)
        : BffTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                // NFR-07, IADR-0510 (#1780 監査 🟡): 鍵リングは本番の DI 登録の IConnectionMultiplexer を引く唯一の利用者である。
                // 差し替える前に本番の登録が在ることを確かめる（消すと本番では初回のログインで GetRequiredService が落ちるのに、
                // 差し替えるテストは緑のまま残る）。
                services.Should().Contain(
                    d => d.ServiceType == typeof(IConnectionMultiplexer),
                    "鍵リングの保存先は本番の DI 登録の IConnectionMultiplexer を引く（AddBffSession が登録する）");
                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton(keyRing.Multiplexer);
                services.RemoveAll<IDistributedCache>();
                services.AddSingleton(sessions);
                services.AddSingleton<IStartupFilter, SignInStartupFilter>();
            });
        }

        public HttpClient CreateReplicaClient() =>
            CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                // Cookie はテストが手で運ぶ（A で受けた Cookie を B へ送るため）。
                HandleCookies = false,
            });
    }

    /// <summary>
    /// テスト専用のサインイン口を本番のパイプラインの前に置く。`SignInAsync` は本物の Cookie ハンドラ
    /// （＝本物の DataProtection と RedisTicketStore）が処理する。
    /// </summary>
    private sealed class SignInStartupFilter : IStartupFilter
    {
        public const string Path = "/test/keyring-signin";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMiddleware) =>
            {
                if (!ctx.Request.Path.Equals(Path, StringComparison.Ordinal))
                {
                    await nextMiddleware(ctx);
                    return;
                }

                var sub = ctx.Request.Query["sub"].ToString();
                var props = new AuthenticationProperties();
                props.StoreTokens(
                [
                    new AuthenticationToken { Name = "access_token", Value = "AT-keyring" },
                    new AuthenticationToken { Name = "refresh_token", Value = "RT-keyring" },
                    // refresh の閾値（期限の 60 秒前）から十分遠ざける（refresh は本テストの論点ではない）。
                    new AuthenticationToken
                    {
                        Name = "expires_at",
                        Value = DateTimeOffset.UtcNow.AddHours(1).ToString("o", CultureInfo.InvariantCulture),
                    },
                ]);
                await ctx.SignInAsync(
                    BffSessionExtensions.SessionScheme,
                    new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("sub", sub), new Claim(ClaimTypes.Name, sub), new Claim("sid", "sid-" + sub)],
                        BffSessionExtensions.SessionScheme)),
                    props);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
            });
            next(app);
        };
    }
}

/// <summary>
/// Redis の偽物。鍵リングの保存先（`RedisXmlRepository`）が使う**リスト操作だけ**を実装する。
/// それ以外の呼び出しは NotSupportedException で落とす（黙って既定値を返すと、想定外の経路が緑を通る）。
/// 同じインスタンスを 2 ホストへ渡す＝同じ Redis を共有する、を表す。
/// </summary>
public sealed class InMemoryRedisLists
{
    private readonly Dictionary<string, List<RedisValue>> _lists = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public InMemoryRedisLists()
    {
        var database = DispatchProxy.Create<IDatabase, RedisProxy>();
        ((RedisProxy)(object)database).Handler = InvokeDatabase;
        var multiplexer = DispatchProxy.Create<IConnectionMultiplexer, RedisProxy>();
        ((RedisProxy)(object)multiplexer).Handler = (method, _) => method.Name == nameof(IConnectionMultiplexer.GetDatabase)
            ? database
            : throw new NotSupportedException($"偽 Redis（接続）は {method.Name} を実装していない");
        Multiplexer = multiplexer;
    }

    public IConnectionMultiplexer Multiplexer { get; }

    public IReadOnlyCollection<string> Keys
    {
        get { lock (_gate) return [.. _lists.Keys]; }
    }

    public int Count(string key)
    {
        lock (_gate) return _lists.TryGetValue(key, out var list) ? list.Count : 0;
    }

    private object? InvokeDatabase(MethodInfo method, object?[] args)
    {
        lock (_gate)
        {
            switch (method.Name)
            {
                case nameof(IDatabase.ListRange):
                    return _lists.TryGetValue(Key(args[0]), out var list) ? list.ToArray() : Array.Empty<RedisValue>();
                case nameof(IDatabase.ListRightPush) when args[1] is RedisValue value:
                    var target = GetOrAddList(Key(args[0]));
                    target.Add(value);
                    return (long)target.Count;
                case nameof(IDatabase.ListRangeAsync):
                    return Task.FromResult(_lists.TryGetValue(Key(args[0]), out var l) ? l.ToArray() : Array.Empty<RedisValue>());
                case nameof(IDatabase.ListRightPushAsync) when args[1] is RedisValue value:
                    var t = GetOrAddList(Key(args[0]));
                    t.Add(value);
                    return Task.FromResult((long)t.Count);
                default:
                    throw new NotSupportedException($"偽 Redis（データベース）は {method.Name} を実装していない");
            }
        }
    }

    private static string Key(object? arg) => ((RedisKey)arg!).ToString();

    private List<RedisValue> GetOrAddList(string key)
    {
        if (!_lists.TryGetValue(key, out var list)) _lists[key] = list = [];
        return list;
    }

    public class RedisProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Handler { get; set; } =
            (m, _) => throw new InvalidOperationException(m.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args ?? []);
    }
}
