using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

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
            new FeedbackRequest(answerId, "up"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<FeedbackDto>(TestContext.Current.CancellationToken);
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
            new FeedbackRequest(answerId, "DOWN", Comment: "出典が不足していた", Question: "経費規程は？"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<FeedbackDto>(TestContext.Current.CancellationToken);
        dto!.Rating.Should().Be("down");
        dto.Comment.Should().Be("出典が不足していた");
    }

    // T-03: 同一 (AnswerId, UserId) の再送信は upsert（件数増えず内容更新）。
    [Fact]
    public async Task SameUserSameAnswer_Upserts()
    {
        var client = factory.CreateClient();
        var answerId = Guid.NewGuid();

        var first = await client.PostAsJsonAsync("/feedback", new FeedbackRequest(answerId, "up"), TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(answerId, "down", Comment: "やっぱり不十分"), TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK); // 更新は 200

        var list = await client.GetFromJsonAsync<List<FeedbackDto>>($"/feedback?answerId={answerId}", TestContext.Current.CancellationToken);
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
            new FeedbackRequest(Guid.NewGuid(), "maybe"), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // T-05: 空 AnswerId は 400。
    [Fact]
    public async Task EmptyAnswerId_Returns400()
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(Guid.Empty, "up"), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // T-06: 過大なコメント（2001 文字）は 400。
    [Fact]
    public async Task TooLongComment_Returns400()
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(Guid.NewGuid(), "up", Comment: new string('あ', 2001)), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // T-07: 集計（満足率）。同一回答へ複数利用者の想定として、AnswerId を分けて 👍×2・👎×1 を保存。
    // NOTE: answerId 指定なしの全体集計は、同一テストクラスで共有される InMemory DB 上の
    //       他テストが投入したデータも合算する。件数の絶対値は他テストに依存するため、
    //       本テストが投入した最小件数を下限とする「>=」で緩く検証する（満足率の算出式のみ厳密に確認）。
    [Fact]
    public async Task Stats_ComputesSatisfaction()
    {
        var client = factory.CreateClient();
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var a3 = Guid.NewGuid();

        await client.PostAsJsonAsync("/feedback", new FeedbackRequest(a1, "up"), TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/feedback", new FeedbackRequest(a2, "up"), TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/feedback", new FeedbackRequest(a3, "down"), TestContext.Current.CancellationToken);

        var stats = await client.GetFromJsonAsync<FeedbackStatsDto>("/feedback/stats", TestContext.Current.CancellationToken);
        stats!.Up.Should().BeGreaterThanOrEqualTo(2);
        stats.Down.Should().BeGreaterThanOrEqualTo(1);
        stats.Total.Should().Be(stats.Up + stats.Down);
        stats.SatisfactionRate.Should().Be((double)stats.Up / stats.Total);
    }

    // FR-10 / T-15: 集計は days（期間）指定を受け付け、期間内（当日投入）の件数を含める。
    //   ダッシュボードが利用状況と満足率の期間を揃えるために BFF が渡す days に対応する。
    //   （CreatedAt は投入時刻＝当日。days=1 でも本テスト投入分は範囲内に含まれる）。
    [Fact]
    public async Task Stats_WithDays_CountsInRange()
    {
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/feedback", new FeedbackRequest(Guid.NewGuid(), "up"), TestContext.Current.CancellationToken);

        var stats = await client.GetFromJsonAsync<FeedbackStatsDto>("/feedback/stats?days=1", TestContext.Current.CancellationToken);

        stats!.Total.Should().BeGreaterThanOrEqualTo(1); // 当日投入分は days=1 の範囲内。
        stats.Total.Should().Be(stats.Up + stats.Down);
    }

    // T-08: 一覧を rating で絞り込める。
    [Fact]
    public async Task List_FiltersByRating()
    {
        var client = factory.CreateClient();
        var downId = Guid.NewGuid();
        await client.PostAsJsonAsync("/feedback", new FeedbackRequest(downId, "down"), TestContext.Current.CancellationToken);

        var list = await client.GetFromJsonAsync<List<FeedbackDto>>($"/feedback?rating=down&answerId={downId}", TestContext.Current.CancellationToken);
        list!.Should().OnlyContain(f => f.Rating == "down");
        list.Should().ContainSingle(f => f.AnswerId == downId);
    }

    // T-12: 同一 (AnswerId, UserId) への同時 2 重送信でもサーバエラー(5xx)を返さない。
    // POST の upsert は read-then-write（非アトミック）で、一意制約違反 (DbUpdateException) を
    // 捕捉して既存行の更新へフォールバックする。全リクエストが成功ステータスへ収束することを確認する。
    // NOTE: InMemory プロバイダは一意インデックスを強制しないため DbUpdateException 経路自体は
    //       再現されない（実 Postgres の統合環境で担保）。本テストは非アトミック処理でも
    //       未処理例外→500 を返さないこと（no-crash / 全 2xx）を保証する回帰テスト。
    [Fact]
    public async Task ConcurrentDoubleSubmit_NoServerError()
    {
        var client = factory.CreateClient();
        var answerId = Guid.NewGuid();

        var requests = Enumerable.Range(0, 8)
            .Select(_ => client.PostAsJsonAsync("/feedback", new FeedbackRequest(answerId, "up"), TestContext.Current.CancellationToken));
        var responses = await Task.WhenAll(requests);

        responses.Should().OnlyContain(r => r.IsSuccessStatusCode); // 500 等を出さない（冪等・no-crash）。
    }

    // T-13: 一覧 (GET /feedback) は個人特定情報(Comment/UserId)を含むため AdminOnly。非管理ロールは 403。
    [Fact]
    public async Task List_WithoutAdminRole_Returns403()
    {
        var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/feedback");
        req.Headers.Add(TestAuthHandler.RolesHeader, "viewer"); // 管理者以外のロールを明示。

        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // T-14: 一覧はページング（take）で返却件数を制限できる（全件無制限返却を防ぐ）。
    [Fact]
    public async Task List_RespectsTakeLimit()
    {
        var client = factory.CreateClient();
        // 同一利用者(test-user)でも AnswerId が異なれば別行になる（3 行以上を投入）。
        for (var i = 0; i < 3; i++)
            await client.PostAsJsonAsync("/feedback", new FeedbackRequest(Guid.NewGuid(), "up"), TestContext.Current.CancellationToken);

        // 共有 DB には他テスト分も含め十分な行がある。take=2 で返却が 2 件に制限されることを確認する。
        var list = await client.GetFromJsonAsync<List<FeedbackDto>>("/feedback?take=2", TestContext.Current.CancellationToken);
        list!.Should().HaveCount(2);
    }

    // ── #521: 端点認可（計画 FR-08 確定 2026-08-07・裁定依頼 planning#236 案 2）
    //
    // 計画の受け入れ基準（`02_requirements:209`）:
    //   「フィードバックの投稿端点が無認証で 401 を返す。統計の取得端点は、認証済みでも
    //    運用者・管理者以外には 403 を返す」
    //
    // **後段（本サービス）にも置くのは多層防御である**（[[IADR-0044]]）。BFF だけに付けると、
    // クラスタ内から後段へ直接到達する経路が空いたままになる。

    // T-15（#521）: 投稿は無認証で 401（匿名投稿を許さない）。
    [Fact]
    public async Task PostFeedback_WhenAnonymous_IsUnauthorized()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var resp = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(Guid.NewGuid(), "up"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // T-18（#521）: 投稿にロールは要らない（認証さえあれば一般利用者も送れる）。
    // **狭めすぎていないことの側**——ロールまで要求すると FR-08 が成り立たなくなる。
    [Fact]
    public async Task PostFeedback_AsNonPrivilegedRole_IsAllowed()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var resp = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(Guid.NewGuid(), "up"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // T-17（#521）: 統計は無認証で 401。
    [Fact]
    public async Task Stats_WhenAnonymous_IsUnauthorized()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var resp = await client.GetAsync("/feedback/stats", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // T-16（#521）: 統計は認証済みでも運用者・管理者以外は 403（[[IADR-0039]] 決定 3: 401 と区別する）。
    [Fact]
    public async Task Stats_AsNonPrivilegedRole_IsForbidden()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var resp = await client.GetAsync("/feedback/stats", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // T-19（#521）: 運用者は統計を取得できる（SC-10 の閲覧ロールと揃える。#544 と同じ線）。
    // 403 の側と**対**で固定する——片側だけだと「全部拒否」でも緑になる。
    [Fact]
    public async Task Stats_AsOperator_IsAllowed()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");

        var resp = await client.GetAsync("/feedback/stats", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
