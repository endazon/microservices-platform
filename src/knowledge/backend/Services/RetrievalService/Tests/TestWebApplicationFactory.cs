using RetrievalService.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using RetrievalService.Domain.Ports;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Wolverine;
using Platform.Shared.Contracts.Dtos;
using Microsoft.AspNetCore.Http;

namespace RetrievalService.Tests;

// FR-03: 既定は InMemory ストア ＋ ゼロベクトルのスタブ。
// 🔴 **xUnit の class fixture は「公開コンストラクタ 1 本・引数なし」しか構築できない**ため、
// 差し替えが要るテストは**本クラスを継承して `ConfigureWebHost` を重ねる**（#995 で実測）。
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Qdrant:Host"] = "localhost",
                ["Qdrant:Port"] = "6334",
                ["Services:LlmGateway"] = "http://localhost:5007"
            }));
        builder.ConfigureServices(services =>
        {
            // Qdrant クライアントとベクトルストアをインメモリ実装へ差し替え
            services.RemoveAll<QdrantClient>();
            services.RemoveAll<IVectorStore>();
            services.AddSingleton<IVectorStore, InMemoryVectorStore>();

            // 埋め込みサービスをスタブへ差し替え
            services.RemoveAll<IEmbeddingService>();
            services.AddSingleton<IEmbeddingService, StubEmbeddingService>();

            // ADR-0027 / #1016: retrieval-delete 段の購読は Wolverine。
            // 🔴 **これが無いとテストが約 135 秒ハングする** —— Program.cs が UseWolverine +
            // UseRabbitMq を呼ぶため、テストホストの起動が実ブローカへの接続を試みる
            // （E1 の DataSourceService.Tests と同じ作法）。
            services.DisableAllExternalWolverineTransports();

            // FR-05, NFR-09, [[IADR-0416]] (#1339): 🔴 **権限の根拠は受け口が自分で引く。**
            // 実 IdP も認可サービスも持たないので、**引く先だけ**を器から差し替える。
            //
            // 🔴 **既定は「全許可（フィルタ無し）」である。** そうすると
            // `ScopeNarrowing.Apply(全許可, 本文の Scope)` は**本文の Scope そのもの**になり、
            // 「与えたスコープで絞られること」を測る既存試験の意味が 1 つも変わらない。
            // **権威が効いていることを測る試験は `Authoritative` を狭めて書く。**
            services.RemoveAll<ISearchAccessResolver>();
            services.AddSingleton<ISearchAccessResolver>(new StubSearchAccessResolver(this));

            // FR-05, NFR-09, ADR-0004, ADR-0084, [[IADR-0418]] (#1318 欠陥 B):
            // 🔴 **`/search` 群は認証を要するようになった。** 実 IdP を持たないので、
            // ヘッダ `X-Test-User` の有無で「未認証／認証済み」を切り替える器を置く
            // （`AiAnalysisService.Tests` の `AnalysisTestUserAuthHandler` と同型）。
            //
            // 🔴 **「常に認証済み」にはしない。** そうすると未認証の契約（401）が測れず、
            // 門を外す変異が生き残る —— 本器を作った理由そのものが失われる。
            services.AddAuthentication(RetrievalTestUserAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, RetrievalTestUserAuthHandler>(
                    RetrievalTestUserAuthHandler.SchemeName, _ => { });
        });
    }

    /// <summary>既定のクライアントが名乗る利用者。</summary>
    public const string DefaultUser = "test-user";

    // 🔴 **既定のクライアントは認証済みである。** 既存の端点試験はいずれも
    // 「認証済みの利用者が検索する」ことを書いており、認証の有無は主題ではない ——
    // ここで載せることで**既存 25 箇所を 1 行も書き換えずに済む**（#1318）。
    // 未認証を測る試験は下の <see cref="CreateAnonymousClient"/> を使う。
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            RetrievalTestUserAuthHandler.UserHeader, DefaultUser);
    }

    /// <summary>
    /// FR-05, NFR-09, [[IADR-0418]] (#1318): **未認証**のクライアント。
    /// 認証ヘッダを載せないので `/search` 群は 401 になる。
    /// </summary>
    public HttpClient CreateAnonymousClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Remove(RetrievalTestUserAuthHandler.UserHeader);
        return client;
    }

    /// <summary>
    /// 受け口が**自分で引く**許可スコープ（#1339）。既定は全許可。
    /// 狭めると「呼び出し元が何を主張しても、これを超えられない」ことを測れる。
    /// </summary>
    public AccessScopeResponse Authoritative { get; set; } =
        new("test-user", [], Granted: true);

    private sealed class StubSearchAccessResolver(TestWebApplicationFactory owner) : ISearchAccessResolver
    {
        public Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, CancellationToken ct = default)
            => Task.FromResult(owner.Authoritative);

        // #1255: gRPC 面は本文の利用者文脈から入る。**器の答えは入口によらず同じ**である
        // —— 入口で答えが変わる器を作ると、輸送を替えたときの同値試験が意味を失う。
        public Task<AccessScopeResponse> ResolveForUserAsync(
            string userId, IReadOnlyDictionary<string, string> attributes, CancellationToken ct = default)
            => Task.FromResult(owner.Authoritative);
    }
}

// FR-05, NFR-09, ADR-0004, ADR-0084, [[IADR-0418]] (#1318): ヘッダ `X-Test-User` が在るときだけ
// 認証済みにする。無いときは `NoResult()`（＝未認証 → `RequireAuthorization()` が 401 を返す）。
public sealed class RetrievalTestUserAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestUser";
    public const string UserHeader = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrWhiteSpace(user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

// テスト用スタブ — 常にゼロベクトルを返す
public class StubEmbeddingService : IEmbeddingService
{
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new float[1536]);
}
