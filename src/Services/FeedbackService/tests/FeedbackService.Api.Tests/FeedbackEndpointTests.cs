using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace FeedbackService.Api.Tests;

// FR-08, UC-01: 回答へのフィードバック（👍/👎・コメント）収集のエンドポイントテスト。
// 各テストは固有の AnswerId を用いて共有 InMemory DB 上で独立させる。
public class FeedbackEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // T-01: 👍 送信で 201・保持される。
    [Fact]
    public async Task PostUpFeedback_Creates()
    {
        var client = factory.CreateClient();
        var answerId = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(answerId, "up"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<FeedbackDto>();
        dto!.Rating.Should().Be("up");
        dto.AnswerId.Should().Be(answerId);
    }

    // T-02: 👎＋コメントで 201・コメント保持。大文字 "DOWN" も正規化される。
    [Fact]
    public async Task PostDownWithComment_Persists()
    {
        var client = factory.CreateClient();
        var answerId = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(answerId, "DOWN", Comment: "出典が不足していた", Question: "経費規程は？"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<FeedbackDto>();
        dto!.Rating.Should().Be("down");
        dto.Comment.Should().Be("出典が不足していた");
    }

    // T-03: 同一 (AnswerId, UserId) の再送信は upsert（件数増えず内容更新）。
    [Fact]
    public async Task SameUserSameAnswer_Upserts()
    {
        var client = factory.CreateClient();
        var answerId = Guid.NewGuid();

        var first = await client.PostAsJsonAsync("/feedback", new FeedbackRequest(answerId, "up"));
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(answerId, "down", Comment: "やっぱり不十分"));
        second.StatusCode.Should().Be(HttpStatusCode.OK); // 更新は 200

        var list = await client.GetFromJsonAsync<List<FeedbackDto>>($"/feedback?answerId={answerId}");
        list!.Should().HaveCount(1);
        list[0].Rating.Should().Be("down");
        list[0].Comment.Should().Be("やっぱり不十分");
    }

    // T-04: 不正な rating は 400。
    [Fact]
    public async Task InvalidRating_Returns400()
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(Guid.NewGuid(), "maybe"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // T-05: 空 AnswerId は 400。
    [Fact]
    public async Task EmptyAnswerId_Returns400()
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(Guid.Empty, "up"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // T-06: 過大なコメント（2001 文字）は 400。
    [Fact]
    public async Task TooLongComment_Returns400()
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(Guid.NewGuid(), "up", Comment: new string('あ', 2001)));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // T-07: 集計（満足率）。同一回答へ複数利用者の想定として、AnswerId を分けて 👍×2・👎×1 を保存。
    [Fact]
    public async Task Stats_ComputesSatisfaction()
    {
        var client = factory.CreateClient();
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var a3 = Guid.NewGuid();

        await client.PostAsJsonAsync("/feedback", new FeedbackRequest(a1, "up"));
        await client.PostAsJsonAsync("/feedback", new FeedbackRequest(a2, "up"));
        await client.PostAsJsonAsync("/feedback", new FeedbackRequest(a3, "down"));

        var stats = await client.GetFromJsonAsync<FeedbackStatsDto>("/feedback/stats");
        stats!.Up.Should().BeGreaterThanOrEqualTo(2);
        stats.Down.Should().BeGreaterThanOrEqualTo(1);
        stats.Total.Should().Be(stats.Up + stats.Down);
        stats.SatisfactionRate.Should().Be((double)stats.Up / stats.Total);
    }

    // T-08: 一覧を rating で絞り込める。
    [Fact]
    public async Task List_FiltersByRating()
    {
        var client = factory.CreateClient();
        var downId = Guid.NewGuid();
        await client.PostAsJsonAsync("/feedback", new FeedbackRequest(downId, "down"));

        var list = await client.GetFromJsonAsync<List<FeedbackDto>>($"/feedback?rating=down&answerId={downId}");
        list!.Should().OnlyContain(f => f.Rating == "down");
        list.Should().ContainSingle(f => f.AnswerId == downId);
    }
}
