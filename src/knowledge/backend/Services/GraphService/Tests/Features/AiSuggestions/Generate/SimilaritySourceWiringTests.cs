using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.ExternalServices;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GraphService.Tests.Features.AiSuggestions.Generate;

// FR-18, ADR-0051, IADR-0380 (#1244): 🔴 **回帰対照 —— 「提案が構造的に 0 件」を赤にする。**
//
// #1244 の欠陥は「テストも CI も緑のまま壊れている」型だった。ISimilarityCandidateSource の本番実装が
// 「常に空を返す」1 つだけで、それが Program.cs の DI に刺さっていたが、生成経路のテストはすべて stub を
// 注入していたため、**本番 DI が何を解決するかを見るテストが 1 本も無かった。**
//
// 本クラスは stub を注入しない。**Program が既定構成で組む DI**（TestWebApplicationFactory は
// ISimilarityCandidateSource を差し替えない）に対して次を固定する。
//
//   T-48 解決される型が UnconfiguredSimilarityCandidateSource ではない
//   T-49 実文書 2 件から `POST /graph/suggestions/generate/{id}` で pending の提案が 1 件以上生まれ、一覧に出る
//   T-50 `Source=none` で Unconfigured が解決される（切り替えの陽性対照）／未知の値は起動が落ちる
//
// **Program.cs の登録を Unconfigured へ戻すと T-48・T-49 が落ちる**（変異試験で実走して確認。作業仕様書 §T-53）。
[Trait("TestKind", "Integration")]
public class SimilaritySourceWiringTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SimilaritySourceWiringTests(TestWebApplicationFactory factory) => _factory = factory;

    private static GraphDocument Doc(Guid id, string title)
        => GraphDocument.Create(id, title,
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, "fp", DateTimeOffset.UtcNow);

    private static GraphDocumentTermProfile Profile(Guid id, string title, string body)
        => GraphDocumentTermProfile.Create(id, TermProfile.Extract(title, body), "fp", DateTimeOffset.UtcNow);

    // T-48 🔴 既定構成の DI が「常に空」を解決しない。
    [Fact]
    public void Default_configuration_resolves_a_real_similarity_source()
    {
        using var scope = _factory.Services.CreateScope();

        var source = scope.ServiceProvider.GetRequiredService<ISimilarityCandidateSource>();

        source.Should().NotBeOfType<UnconfiguredSimilarityCandidateSource>(
            "#1244: 既定が「常に空」だと FR-18 の提案は構造的に 0 件になる");
        source.Should().BeOfType<TermOverlapSimilarityCandidateSource>();
    }

    // T-49 🔴 結合: 起点 → 類似度 → 列挙 → 封 → LLM → pending。**「0 件でも緑」にならない。**
    [Fact]
    public async Task Generating_from_two_real_documents_yields_at_least_one_pending_suggestion()
    {
        var ct = TestContext.Current.CancellationToken;
        var origin = Guid.NewGuid();
        var similar = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        await _factory.SeedAsync(db =>
        {
            db.Documents.AddRange(
                Doc(origin, "知識グラフの ABAC 判定設計"),
                Doc(similar, "グラフ探索の認可レビュー"),
                Doc(unrelated, "Quarterly budget planning"));
            db.TermProfiles.AddRange(
                Profile(origin, "知識グラフの ABAC 判定設計",
                    "ホップごとに認可述語を評価し、不許可ノードでは探索を打ち切る。属性の複製はイベントで追随する。"),
                Profile(similar, "グラフ探索の認可レビュー",
                    "探索はホップごとに認可述語を評価する。不許可ノードは打ち切り、属性の複製で判定する。"),
                Profile(unrelated, "Quarterly budget planning",
                    "Revenue forecast and headcount plan for the fiscal year. Travel expenses are capped."));
            return Task.CompletedTask;
        });
        var client = _factory.CreateClient();

        var res = await client.PostAsync($"/graph/suggestions/generate/{origin}", null, ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await res.Content.ReadFromJsonAsync<List<AiSuggestionDto>>(ct);
        created.Should().NotBeNullOrEmpty("#1244: 実文書 2 件から提案が 1 件も生まれなければ供給元が死んでいる");
        created!.Should().OnlyContain(s => s.State == SuggestionState.Pending);
        created.Select(s => s.TargetDocumentId).Should().Contain(similar);
        created.Select(s => s.TargetDocumentId).Should().NotContain(unrelated, "無関係な文書は提案しない");

        // 表示まで: 一覧（SC-03 の承認欄が読む口）に同じ提案が出る。
        var list = await client.GetFromJsonAsync<List<AiSuggestionDto>>(
            $"/graph/suggestions/?state=pending&documentId={origin}", ct);
        list.Should().NotBeNull();
        list!.Select(s => s.Id).Should().Contain(created.Select(s => s.Id));
    }

    // T-50 陽性対照: `Source=none` で旧既定（Unconfigured）が解決される —— 切り替えが実際に効いている。
    [Fact]
    public void Source_none_resolves_the_unconfigured_source()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AiSuggestionSimilarityOptions.SectionName}:Source"] = AiSuggestionSimilarityOptions.None,
            })));
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ISimilarityCandidateSource>()
            .Should().BeOfType<UnconfiguredSimilarityCandidateSource>();
    }

    // T-50 未知の値は起動が落ちる（黙って空へ倒さない）。
    [Fact]
    public void Unknown_source_fails_at_startup()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AiSuggestionSimilarityOptions.SectionName}:Source"] = "qdrant",
            })));

        var act = () => factory.Services;

        act.Should().Throw<Exception>()
            .Which.ToString().Should().Contain("AiSuggestions:Similarity:Source");
    }
}
