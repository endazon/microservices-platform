using System.Net;
using System.Net.Http.Json;
using AiAnalysisService.Api.Foundation.Services;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Api.Tests;

// FR-04, FR-05, SC-01, SC-08, UC-01, #539: `AskRequest` の対象範囲（属性フィルタ）。
//
// **計画（L198・裁定 Q1）**:「`SearchRequest` は既に `AttributeFilters` を持つのに
// `AnalysisRequest` だけが持たない非対称を解消する。」
//
// **最重要の不変条件は narrowing-only である** —— 範囲指定は ABAC 許可スコープを**決して広げない**。
// クライアントが権限外の値を送っても結果は広がらず、権限の外だけを指す範囲は全体 deny へ倒れる。
public class AskAttributeFilterTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static AccessScopeResponse Abac(bool granted, params AttributeFilter[] filters)
        => new("u1", filters.ToList(), granted);

    // **記録はこのクラスの factory が持つ singleton から読む。**
    // `static` にすると、`/analysis/ask/stream` を叩く別のクラスと**並列に**走ったときに
    // 記録が上書きされる（CI で実際に落ちた）。`IClassFixture` はクラスごとに factory を作るので、
    // ここから解決した実体は本クラス専用である。
    private StubRagOrchestrator Stub =>
        (StubRagOrchestrator)factory.Services.GetRequiredService<IRagOrchestrator>();

    // ── 交差の規則（純粋ロジック）────────────────────────────────────────
    //
    // **`ask` と `analyze` は同じ規則を共有する**（[[IADR-0153]] とは別の話。計画 L342 裁定 Q3
    // 「同じ『範囲を絞る』操作が画面ごとに違う挙動になると、利用者は操作を覚え直すことになる」）。

    // ★ 受け入れ基準 2: 範囲は ABAC と交差する（権限を広げない）。
    [Fact]
    public void Resolve_NarrowsWithinAbac_DoesNotWiden()
    {
        var abac = Abac(true, new AttributeFilter("department", ["sales", "hr"]));

        var scope = DataRangeScopeResolver.Resolve(abac,
            new Dictionary<string, List<string>> { ["department"] = ["sales"] });

        scope.GrantsAccess.Should().BeTrue();
        scope.Filters.Should().ContainSingle()
            .Which.AllowedValues.Should().Equal(["sales"], "許可の内側へ狭まる");
    }

    // ★ 受け入れ基準 3: 権限の外だけを指す範囲は**全体 deny** へ倒れる（漏えい防止の安全側）。
    [Fact]
    public void Resolve_RangeOutsideAbac_DeniesEverything()
    {
        var abac = Abac(true, new AttributeFilter("department", ["sales"]));

        var scope = DataRangeScopeResolver.Resolve(abac,
            new Dictionary<string, List<string>> { ["department"] = ["finance"] });

        scope.GrantsAccess.Should().BeFalse();
    }

    // ABAC が granted でなければ、いかなる範囲でも何も開かない（deny-by-default）。
    [Fact]
    public void Resolve_NotGranted_StaysDenied()
    {
        var scope = DataRangeScopeResolver.Resolve(Abac(false),
            new Dictionary<string, List<string>> { ["department"] = ["sales"] });

        scope.GrantsAccess.Should().BeFalse();
    }

    // ABAC が当該キーを無制約に許可しているなら、範囲で絞るのは安全な narrowing である。
    [Fact]
    public void Resolve_KeyUnconstrainedByAbac_AddsRangeFilter()
    {
        var abac = Abac(true, new AttributeFilter("department", ["sales"]));

        var scope = DataRangeScopeResolver.Resolve(abac,
            new Dictionary<string, List<string>> { [AttributeValueKeys.Tags] = ["経理"] });

        scope.GrantsAccess.Should().BeTrue();
        scope.Filters.Should().Contain(f => f.Key == AttributeValueKeys.Tags);
    }

    // **2 つの多重定義は同じ結果を出す。** 規則を 2 本持たないことが目的なので、
    // ここが割れたら「検索では効くが分析では効かない」型の食い違いが戻ってくる。
    [Fact]
    public void Resolve_BothOverloadsAgree()
    {
        var abac = Abac(true, new AttributeFilter("department", ["sales", "hr"]));
        var filters = new Dictionary<string, List<string>> { ["department"] = ["sales"] };

        var viaDictionary = DataRangeScopeResolver.Resolve(abac, filters);
        var viaRange = DataRangeScopeResolver.Resolve(abac, new AnalysisDataRange(AttributeFilters: filters));

        viaDictionary.GrantsAccess.Should().Be(viaRange.GrantsAccess);
        viaDictionary.Filters.Select(f => (f.Key, string.Join(",", f.AllowedValues)))
            .Should().BeEquivalentTo(
                viaRange.Filters.Select(f => (f.Key, string.Join(",", f.AllowedValues))));
    }

    // 範囲を指定しないときは ABAC をそのまま保つ（受け入れ基準 6 の土台）。
    [Fact]
    public void Resolve_NullFilters_PreservesAbac()
    {
        var abac = Abac(true, new AttributeFilter("department", ["sales", "hr"]));

        var scope = DataRangeScopeResolver.Resolve(abac, (IReadOnlyDictionary<string, List<string>>?)null);

        scope.GrantsAccess.Should().BeTrue();
        scope.Filters.Should().ContainSingle().Which.AllowedValues.Should().Equal(["sales", "hr"]);
    }

    // ── 端点（契約が実際に後段へ届くか）────────────────────────────────
    //
    // **回答本文は範囲に依存しない**ので、端点の外からは「渡ったか」を観測できない。
    // スタブが受け取った値を記録して確かめる。

    // ★ 受け入れ基準 1: `/analysis/ask` が対象範囲を受け取り、後段へ渡す。
    [Fact]
    public async Task Ask_PassesAttributeFiltersDownstream()
    {
        Stub.ResetRecording();

        var resp = await factory.CreateClient().PostAsJsonAsync("/analysis/ask", new
        {
            question = "経理の規程は？",
            attributeFilters = new Dictionary<string, string[]>
            {
                ["tags"] = ["経理", "規程"],
                ["department"] = ["finance"],
            },
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        Stub.LastAskFilters.Should().NotBeNull();
        Stub.LastAskFilters!["tags"].Should().Equal(["経理", "規程"]);
        Stub.LastAskFilters["department"].Should().Equal(["finance"]);
    }

    // ★ 受け入れ基準 1: `/analysis/ask/stream` も同じ形で受け取る（SC-01 の本文は SSE 経路である）。
    [Fact]
    public async Task AskStream_PassesAttributeFiltersDownstream()
    {
        Stub.ResetRecording();

        var resp = await factory.CreateClient().PostAsJsonAsync("/analysis/ask/stream", new
        {
            question = "経理の規程は？",
            attributeFilters = new Dictionary<string, string[]> { ["tags"] = ["経理"] },
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Stub.LastStreamFilters.Should().NotBeNull();
        Stub.LastStreamFilters!["tags"].Should().Equal(["経理"]);
    }

    // ★ 受け入れ基準 6: 範囲を指定しないときの挙動は従来どおり（既定値つき追加は非破壊）。
    // **旧クライアントは `attributeFilters` を送らない。** 送らなくても 200 で回答が返ること。
    [Fact]
    public async Task Ask_WithoutFilters_IsUnchanged()
    {
        Stub.ResetRecording();

        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/analysis/ask", new { question = "経理の規程は？" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<AiAnswerDto>(TestContext.Current.CancellationToken))!.Answer.Should().NotBeNullOrEmpty();
        Stub.LastAskFilters.Should().BeNull("送らなければ後段にも渡らない");
    }
}
