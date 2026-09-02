using System.Net;
using System.Reflection;
using AwesomeAssertions;
using GraphService.Domain.Ports;
using GraphService.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Features.AiSuggestions.Generate;

// FR-18, ADR-0034 決定 5, ADR-0051 決定 3, IADR-0266 決定 1 (#915):
// **LLM への送信物の型ゲートが回避不能であることを固定する。**
//
// ADR-0051 決定 3 は「絞りを LLM 呼び出しより後ろに置いてはならない」と定めた。
// 🔴 **本テストが守っているのは「ゲートが後から緩められないこと」である。**
// コンストラクタを public に上げる・スコープを取らないファクトリを足す・LLM ポートの引数を
// 生の文書へ緩める、といった変更はいずれもコンパイルが通ってしまうため、機械で見張る必要がある。
//
// GraphTypeGateArchitectureTests（探索側の 2 段ゲート）と同じ作法である。
public class SuggestionPromptGateTests
{
    // ADR-0034 決定 5: 封は述語を通さずに作れない。
    [Fact]
    public void SuggestionPrompt_has_no_accessible_constructor()
    {
        var ctors = typeof(SuggestionPrompt)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        ctors.Should().OnlyContain(c => c.IsPrivate,
            "封を許可判定なしに構築できると、スコープ外の文書を LLM へ渡すコードが書けてしまう");
    }

    // ADR-0051 決定 3: 封を返す経路は必ずスコープを要求する。
    [Fact]
    public void Every_factory_returning_SuggestionPrompt_requires_a_scope()
    {
        var factories = typeof(SuggestionPrompt)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(SuggestionPrompt))
            .ToList();

        factories.Should().NotBeEmpty("構築経路が 1 つも無いと本テストが空振りする");
        factories.Should().OnlyContain(
            m => m.GetParameters().Any(p => p.ParameterType == typeof(AccessScopeResponse)),
            "スコープを取らない構築経路があると、濾していない候補を封に入れられる");
    }

    // 🔴 ADR-0034 決定 5: LLM ポートは封しか受け取らない。
    // 引数を GraphDocument や string へ緩めると、封を迂回して送信できるようになる。
    [Fact]
    public void Llm_port_accepts_only_the_sealed_prompt()
    {
        var method = typeof(ISuggestionLlmClient).GetMethod(nameof(ISuggestionLlmClient.ProposeAsync));

        method.Should().NotBeNull();
        method!.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .Should().OnlyContain(p => p.ParameterType == typeof(SuggestionPrompt),
                "封以外を受け取れると、許可判定を経ていない内容を LLM へ送れてしまう");
    }

    // 🔴 ADR-0051 決定 3: 候補列挙の口はスコープを要求し、許可済みノードしか返さない。
    // 戻り値を GraphDocument へ緩めると「LLM へ渡してから捨てる」形が書けるようになる。
    [Fact]
    public void Candidate_enumeration_requires_a_scope_and_returns_only_authorized_nodes()
    {
        var method = typeof(IGraphStore)
            .GetMethod(nameof(IGraphStore.EnumerateAuthorizedCandidatesAsync));

        method.Should().NotBeNull();
        method!.GetParameters().Should().Contain(p => p.ParameterType == typeof(AccessScopeResponse),
            "スコープを取らない候補列挙は、絞りを後段へ押し出す");
        method.ReturnType.GetGenericArguments().Should()
            .ContainSingle(t => t == typeof(IReadOnlyList<AuthorizedNode>),
                "候補列挙が生の文書を返すと、非許可ノードが LLM 呼び出しの引数になり得る");
    }
}

// FR-18 (#915): 生成の口が配線されていることと、応答が件数を持たないことを HTTP 面で固定する。
public class AiSuggestionGenerationEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // ADR-0034 決定 2: 起点が存在しない（または見えない）なら 404。403 は存在を漏らす。
    [Fact]
    public async Task Generating_for_an_unknown_document_returns_404()
    {
        factory.ScopeProvider = _ => new AccessScopeResponse("test-user", [], true);

        var res = await factory.CreateClient().PostAsync(
            $"/graph/suggestions/generate/{Guid.NewGuid()}", null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 🔴 ADR-0051 決定 2: 応答は**生成できた提案の配列のみ**であり、
    // 「候補が N 件あった」「N 件落とした」を持たない（件数欄が構造として存在しない）。
    [Fact]
    public async Task Response_is_a_bare_array_with_no_count_fields()
    {
        var documentId = Guid.NewGuid();
        await factory.SeedAsync(db =>
        {
            db.Documents.Add(GraphService.Domain.GraphDocument.Create(
                documentId, "起点", new Dictionary<string, string> { ["confidentiality"] = "internal" },
                null, DateTimeOffset.UnixEpoch));
            return Task.CompletedTask;
        });
        factory.ScopeProvider = _ => new AccessScopeResponse("test-user", [], true);

        var res = await factory.CreateClient().PostAsync(
            $"/graph/suggestions/generate/{documentId}", null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // 既定の類似度アダプタは候補を返さない（供給元が未配線）。**それでも件数欄は生えない。**
        body.Trim().Should().Be("[]");
    }
}
