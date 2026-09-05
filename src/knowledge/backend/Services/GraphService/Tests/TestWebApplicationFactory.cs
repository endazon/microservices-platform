using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using GraphService.Domain.Ports;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Contracts.Dtos;
using Wolverine;

namespace GraphService.Tests;

// FR-17: GraphService の統合テスト用ホスト。
//
// ABAC スコープは差し替え可能にする（既定は「条件無しで全許可」）。認可サービスへの実通信は
// 行わない —— GraphAccessResolver 自体の deny-closed 縮退は GraphAccessResolverTests が
// HttpMessageHandler 層で直接試験する。
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    // 各テストクラスで DB を分離するための一意名（InMemory）。
    // 固定名にすると xUnit のクラス並列実行で書き込みが混ざる。
    private readonly string _dbName = $"GraphTest_{Guid.NewGuid()}";

    // 既定は「マッチするポリシーがあり、条件は無し」＝全許可。**read のスコープ。**
    public Func<HttpContext, AccessScopeResponse> ScopeProvider { get; set; } =
        _ => new AccessScopeResponse("test-user", [], true);

    // #993, IADR-0272 決定 2: **write のスコープ**（書き込み経路が解決するもの）。
    // null なら ScopeProvider へ委譲する —— 既存テストは read だけを差し替えて書けたままになる。
    // **書き込みの認可を測るテストは、必ずここを明示的に置くこと。**
    public Func<HttpContext, AccessScopeResponse>? WriteScopeProvider { get; set; }

    // FR-18, ADR-0063 決定 1〜3, IADR-0364 (#1187 / #1014): DocumentService との 2 本の経路を差し替える。
    //
    // **反映先（`IDocumentTagWriter`）は記録するスタブ**である —— 呼ばれた文書 ID・タグ値を残し、
    // 応答は `TagWriter.Outcome` で差し替える（辞書外・後段の拒否・不達を再現する）。
    // 実 HTTP アダプタ（`HttpDocumentTagWriter`）の写像は `HttpDocumentTagWriterTests` が
    // `HttpMessageHandler` 層で直接試験する。
    public RecordingTagWriter TagWriter { get; } = new();

    // **辞書（`ITagDictionaryReader`）**。既定は「引けた・空」ではなく **null（引けなかった）**にする ——
    // fail-closed の既定を試験の既定にしておけば、辞書を置き忘れたテストはタグ提案を 1 件も作れず、
    // 「辞書を突き合わせていない実装」で偶然通ることが無い。
    public Func<IReadOnlySet<string>?> TagDictionary { get; set; } = () => null;

    // FR-18, IADR-0380 (#1244): **LLM 境界の差し替え。** 封（SuggestionPrompt）を受け取り、提案の候補を返す。
    // 既定は「封に入っている候補すべてへ、辞書の先頭の型でリンクを提案する」—— 生成経路の結合テスト
    // （SimilaritySourceWiringTests）が「実文書 2 件から pending の提案が 1 件以上生まれる」を測るための最小の
    // 応答である。**封しか受け取れない**ので、スコープ外の文書がここへ届く経路は型として無い（IADR-0266 決定 1）。
    // 実 HTTP アダプタ（LlmGatewaySuggestionClient）の写像は AiSuggestionGenerationTests が直接試験する。
    public Func<SuggestionPrompt, IReadOnlyList<LlmSuggestionProposal>> SuggestionLlm { get; set; } =
        prompt => prompt.Candidates
            .Select(c => new LlmSuggestionProposal(
                SuggestionKind.Link, c.DocumentId, prompt.EdgeTypeNames.FirstOrDefault(), null, "test"))
            .ToList();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Services:AuthorizationService"] = "http://localhost/authz",
            }));
        builder.ConfigureServices(services =>
        {
            ReplaceDbContext<GraphDbContext>(services, _dbName);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // ABAC スコープを差し替える。
            services.RemoveAll<IGraphAccessResolver>();
            services.AddScoped<IGraphAccessResolver>(_ => new StubAccessResolver(this));

            // DocumentService への 2 経路（#1187 / #1014）。実通信は行わない。
            services.RemoveAll<IDocumentTagWriter>();
            services.AddSingleton<IDocumentTagWriter>(TagWriter);
            services.RemoveAll<ITagDictionaryReader>();
            services.AddScoped<ITagDictionaryReader>(_ => new StubTagDictionaryReader(this));

            // LLM 境界（#1244）。実通信は行わない。
            // 🔴 **ISimilarityCandidateSource は差し替えない** —— 本番 DI が何を解決するかを測るのが
            // SimilaritySourceWiringTests の目的であり、ここで差し替えると回帰対照が空振りする。
            services.RemoveAll<ISuggestionLlmClient>();
            services.AddScoped<ISuggestionLlmClient>(_ => new StubSuggestionLlm(this));

            // ADR-0027 / #1016: graph-delete 段の購読は Wolverine。
            // 🔴 **これが無いとテストが約 135 秒ハングする** —— Program.cs が UseWolverine +
            // UseRabbitMq を呼ぶため、テストホストの起動が実ブローカへの接続を試みる
            // （E1 の DataSourceService.Tests と同じ作法）。
            services.DisableAllExternalWolverineTransports();
        });
    }

    // テストデータの投入。DbContext を直接触る（#908 はイベント購読を持たないため）。
    public async Task SeedAsync(Func<GraphDbContext, Task> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    private sealed class StubAccessResolver(TestWebApplicationFactory owner) : IGraphAccessResolver
    {
        public Task<AccessScopeResponse> ResolveAsync(
            HttpContext ctx, string action, CancellationToken ct = default)
            => Task.FromResult(action == GraphAccessAction.Write
                ? (owner.WriteScopeProvider ?? owner.ScopeProvider)(ctx)
                : owner.ScopeProvider(ctx));
    }

    private sealed class StubTagDictionaryReader(TestWebApplicationFactory owner) : ITagDictionaryReader
    {
        public Task<IReadOnlySet<string>?> ReadNamesAsync(CancellationToken ct = default)
            => Task.FromResult(owner.TagDictionary());
    }

    private sealed class StubSuggestionLlm(TestWebApplicationFactory owner) : ISuggestionLlmClient
    {
        public Task<IReadOnlyList<LlmSuggestionProposal>> ProposeAsync(
            SuggestionPrompt prompt, CancellationToken ct = default)
            => Task.FromResult(owner.SuggestionLlm(prompt));
    }

    // FR-18, ADR-0063 決定 1 (#1187): 反映先の記録スタブ。**テスト間で共有される**（IClassFixture）ため、
    // 観測する側が呼ぶ前に `Reset()` すること。
    public sealed class RecordingTagWriter : IDocumentTagWriter
    {
        private readonly List<(Guid DocumentId, string TagName)> _calls = [];

        public TagWriteOutcome Outcome { get; set; } = TagWriteOutcome.Applied;

        public IReadOnlyList<(Guid DocumentId, string TagName)> Calls
        {
            get { lock (_calls) return [.. _calls]; }
        }

        public void Reset(TagWriteOutcome outcome = TagWriteOutcome.Applied)
        {
            lock (_calls) _calls.Clear();
            Outcome = outcome;
        }

        public Task<TagWriteOutcome> AddTagAsync(Guid documentId, string tagName, CancellationToken ct = default)
        {
            lock (_calls) _calls.Add((documentId, tagName));
            return Task.FromResult(Outcome);
        }
    }

    private static void ReplaceDbContext<TContext>(IServiceCollection services, string dbName)
        where TContext : DbContext
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<TContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?.Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(TContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<TContext>(opt => opt.UseInMemoryDatabase(dbName));
    }
}
