using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Authz;

// FR-05, FR-06, FR-19, ADR-0036, IADR-0009, IADR-0253, IADR-0272 (#901):
// BffScopeResolver.ResolveAsync（認可スコープ解決の **HTTP 経路**）を共有ライブラリ側で固定する。
//
// 🔴 **なぜここに要るのか。** Platform.Bff.Tests/BffScopeResolverTests.cs は純ロジックの Matches と
// ExtractUserAttributes を直接検証しているが、**ResolveAsync については引数の既定値を見る
// reflection 試験 1 件しか持たない**（同ファイル冒頭が「ResolveAsync の HTTP 経路は
// Document/Search の BFF エンドポイントテストが回帰保証する」と明記している）。
// #901 が確定した知見のとおり **リフレクションのみの試験は行被覆に 1 行も寄与しない** ——
// deny-by-default へ縮退する 4 経路（非 2xx・Granted=false・空本文・通信例外）は、
// 共有ライブラリ側では 1 本も実行されていなかった。
//
// ここが緩むと **全 BFF 経路の認可が同時に緩む**（本ライブラリは全サービスが依存する）。
// 実 HttpClient・実 JSON で観測し、ダブルは「認可サービスの応答」だけに留める。
public class BffScopeResolveTests
{
    private const string ScopePath = "/authz/scope";

    // 認可サービスの応答を差し替えるスタブ。要求本文を捕捉し「何を送ったか」も検査できる。
    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }
        public Uri? CapturedUri { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            CapturedUri = request.RequestUri;
            if (request.Content is not null)
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, cancellationToken);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://authorization-service") };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpContext Ctx(string? name = "alice", params (string Key, string Value)[] claims)
    {
        var list = claims.Select(c => new Claim(c.Key, c.Value)).ToList();
        if (name is not null) list.Add(new Claim(ClaimTypes.Name, name));
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(list, authenticationType: "test")),
        };
    }

    // ── 許可される場合 ────────────────────────────────────────────────────────

    // FR-05: Granted=true の応答は BffAccessScope として返る（フィルタを保持する）。
    [Fact]
    public async Task 許可応答はフィルタを保持したスコープとして返る()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """
            {"userId":"alice","allowedFilters":[{"key":"department","allowedValues":["sales"]}],"granted":true}
            """));

        var scope = await BffScopeResolver.ResolveAsync(
            new SingleClientFactory(handler), Ctx(), BffScopeAction.Read,
            TestContext.Current.CancellationToken);

        scope.Should().NotBeNull();
        scope!.GrantsAccess.Should().BeTrue();
        scope.Filters.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new AttributeFilter("department", ["sales"]));
    }

    // FR-19, IADR-0253 決定 1（段 3 / #989）: **Branches を運ぶ**。
    // 落とすと「BFF は分岐で判定するが後段は従来評価」の食い違いが残り、検索経路だけが
    // どのポリシー単独も許可しない混成を通す（IADR-0253 決定 2 の反例）。
    [Fact]
    public async Task 許可応答の名前つき分岐は後段の契約型まで運ばれる()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """
            {"userId":"alice","allowedFilters":[],"granted":true,
             "branches":[{"name":"owner","filters":[{"key":"owner","allowedValues":["alice"]}]},
                         {"name":"attribute","filters":[{"key":"department","allowedValues":["sales"]}]}]}
            """));

        var scope = await BffScopeResolver.ResolveAsync(
            new SingleClientFactory(handler), Ctx(), BffScopeAction.Read,
            TestContext.Current.CancellationToken);

        scope.Should().NotBeNull();
        scope!.Branches.Should().HaveCount(2);
        scope.Branches!.Select(b => b.Name).Should().BeEquivalentTo(["owner", "attribute"]);

        // 段 3 完了後は契約型へも運ぶ（ここが null へ戻ると後段が従来評価へ落ちる）。
        scope.ToContractScope().Branches.Should().HaveCount(2);
    }

    // ── deny-by-default へ縮退する 4 経路 ──────────────────────────────────────
    //
    // 🔴 4 つとも「null を返す」ため、1 つだけ書いても「常に null を返す」壊れた実装が通る。
    // 上の許可系 2 件が対照条件である（許可を許可として返せることを先に固定してある）。

    // FR-05: 許可ポリシーが無い（Granted=false）＝閲覧可能なし。フィルタが空でも全件開放ではない。
    [Fact]
    public async Task 未許可応答はフィルタが空でもnullへ縮退する()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """
            {"userId":"alice","allowedFilters":[],"granted":false}
            """));

        var scope = await BffScopeResolver.ResolveAsync(
            new SingleClientFactory(handler), Ctx(), BffScopeAction.Read,
            TestContext.Current.CancellationToken);

        scope.Should().BeNull("granted=false でフィルタ空を『条件なし全件許可』と読むと全開放になる");
    }

    // FR-05: 非 2xx は本文を読まずに deny-by-default。
    // 403（認可サービスが拒否）と 500（認可サービスの障害）の双方で同じに縮退する。
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task 非2xx応答は本文に関わらずnullへ縮退する(HttpStatusCode status)
    {
        // 本文には「許可」を書いておく。読んでしまう実装なら通ってしまう仕掛け。
        var handler = new StubHandler((_, _) => Json(status, """
            {"userId":"alice","allowedFilters":[],"granted":true}
            """));

        var scope = await BffScopeResolver.ResolveAsync(
            new SingleClientFactory(handler), Ctx(), BffScopeAction.Read,
            TestContext.Current.CancellationToken);

        scope.Should().BeNull("非 2xx の本文を信用すると、認可サービスのエラー応答で認可が緩む");
    }

    // FR-05: 2xx だが本文が JSON の null（デシリアライズ結果が null）＝解決不能。
    [Fact]
    public async Task 本文が空の応答はnullへ縮退する()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, "null"));

        var scope = await BffScopeResolver.ResolveAsync(
            new SingleClientFactory(handler), Ctx(), BffScopeAction.Read,
            TestContext.Current.CancellationToken);

        scope.Should().BeNull();
    }

    // FR-05: 認可サービス不調（接続不可・タイムアウト）は **例外を投げず** deny-by-default。
    // 🔴 投げると BFF が 500 を返し、可用性障害が「認可の緩み」ではなく「全断」に化ける。
    // 逆に握って許可へ倒すと障害時に全開放になる。null（＝閲覧可能なし）が正しい縮退先である。
    public static TheoryData<Exception> TransportFailures() =>
    [
        new HttpRequestException("connection refused"),
        new TaskCanceledException("timeout"),
    ];

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task 認可サービス不調は例外を投げずnullへ縮退する(Exception failure)
    {
        var handler = new StubHandler((_, _) => throw failure);

        var scope = await BffScopeResolver.ResolveAsync(
            new SingleClientFactory(handler), Ctx(), BffScopeAction.Read,
            TestContext.Current.CancellationToken);

        scope.Should().BeNull();
    }

    // 🔴 FR-05: **呼び出し元のキャンセルは deny へ化けさせない。**
    // catch の when 条件 `!ct.IsCancellationRequested` が守っているのはここである。
    // ct がキャンセル済みなら例外を伝播させる —— null を返すと、呼び出し元は「キャンセルされた」
    // ではなく「閲覧可能な文書が無い」と読み、打ち切られた要求が空応答として成功してしまう。
    [Fact]
    public async Task 呼び出し元がキャンセル済みなら例外を伝播しdenyへ化けさせない()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new StubHandler((_, ct) => throw new TaskCanceledException("cancelled", null, ct));

        var act = async () => await BffScopeResolver.ResolveAsync(
            new SingleClientFactory(handler), Ctx(), BffScopeAction.Read, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "キャンセルを null（＝閲覧可能なし）へ縮退すると、打ち切りが空応答の成功に化ける");
    }

    // ── 要求本文（権限昇格の防止） ────────────────────────────────────────────

    // 🔴 FR-05: **利用者はサーバ側の HttpContext.User から決める。** クライアントが指定した
    // 値は一切使わない（IADR-0009 の「クライアント指定 Scope を信頼しない」の実装点）。
    // action は引数どおり運ぶ（IADR-0272 決定 4 / #1010: 既定値が無く、呼び出し側が明示する）。
    [Fact]
    public async Task 要求本文の利用者はサーバ側のIDで属性とアクションを伴って送られる()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """
            {"userId":"alice","allowedFilters":[],"granted":true}
            """));

        await BffScopeResolver.ResolveAsync(
            new SingleClientFactory(handler),
            Ctx("alice", ("clearance", "secret"), ("department", "sales")),
            BffScopeAction.Write,
            TestContext.Current.CancellationToken);

        handler.CapturedUri!.AbsolutePath.Should().Be(ScopePath);

        var sent = JsonDocument.Parse(handler.CapturedBody!).RootElement;
        sent.GetProperty("userId").GetString().Should().Be("alice");
        sent.GetProperty("action").GetString().Should().Be("write",
            "書き込み経路が read のスコープで判定されると #1010 の欠陥が再発する");
        var attrs = sent.GetProperty("userAttributes");
        attrs.GetProperty("clearance").GetString().Should().Be("secret");
        attrs.GetProperty("department").GetString().Should().Be("sales");
    }

    // FR-05: 未認証（Identity.Name が無い）は "anonymous" として解決へ送る。
    // 認可サービス側で deny されることが期待だが、**呼び出し側で例外にならない**ことを固定する。
    [Fact]
    public async Task 未認証の利用者はanonymousとして送られる()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """
            {"userId":"anonymous","allowedFilters":[],"granted":false}
            """));

        var scope = await BffScopeResolver.ResolveAsync(
            new SingleClientFactory(handler), Ctx(name: null), BffScopeAction.Read,
            TestContext.Current.CancellationToken);

        JsonDocument.Parse(handler.CapturedBody!).RootElement
            .GetProperty("userId").GetString().Should().Be("anonymous");
        scope.Should().BeNull();
    }
}
