using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace LlmGateway.Tests.Features.Authorization;

// NFR-09, FR-02, FR-04, FR-11, ADR-0004, ADR-0084 決定 1, [[IADR-0379]] 決定 4, [[IADR-0424]] (#1364):
// REST 3 口（/complete・/complete/stream・/embed）の**門**を固定する。
//
// 🔴 **陰性対照が主題である。** 陽性対照（有資格で 200）だけの試験は、門を外しても緑のままであり
// **何も守らない** —— この 3 口は #1364 の時点で実際に「陽性しか無い」状態だった
// （既存の端点試験 30 件超はすべて資格情報なしで 200 を得ていた）。
//
// 🔴 **陰性は 2 種類ある。**
//   ① 資格情報なし → **401**（認証されていない）
//   ② 利用者のトークン（**管理者であっても**）→ **403**（認証はされたが `platform-service` ではない）
// ② が無いと「認証さえ通れば誰でも叩ける」形（confused deputy）へ緩めたときに気付けない。
// gRPC 面が `GrpcEmbedTests` T-S-02 / T-S-03 で同じ 2 つを固定しており、**面をまたいで対にしてある**。
// 🔴 [[IADR-0110]] / [[IADR-0394]]: **共有 Meter へ発行するので `SharedMeterCollection` へ加入する。**
// 陽性対照（T-A-01 / T-A-04）は補完を実際に成立させるため、`LlmCompletionMetrics` へ測定を発行する ——
// 加入しないと `CompletionMetricsTests` の probe が本クラスの発行を拾い、**別のクラスが落ちる**
// （加入せずに走らせて実測した。落ちる試験が実行のたびに変わる形で現れる）。
[Collection(SharedMeterCollection.Name)]
public class RestServiceCallerGateTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static CompletionApiRequest Completion() =>
        new("質問", MaxTokens: 64, Model: null, Confidentiality: "public", Purpose: "rag-answer");

    private static EmbedApiRequest Embed() =>
        new("本文", "public", EmbedPurpose.Index);

    // ---- /complete ----------------------------------------------------------------

    // T-A-01: 陽性対照。s2s トークン（platform-service）なら通る。
    [Fact]
    public async Task Complete_with_service_caller_token_succeeds()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/complete", Completion(), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // T-A-02: 🔴 陰性対照。資格情報が無ければ 401。
    [Fact]
    public async Task Complete_without_credentials_is_unauthorized()
    {
        var resp = await factory.CreateAnonymousClient().PostAsJsonAsync(
            "/complete", Completion(), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // T-A-03: 🔴 陰性対照。**利用者のトークンは管理者でも通らない**（ServiceCaller は別軸である）。
    [Fact]
    public async Task Complete_with_admin_user_token_is_forbidden()
    {
        var resp = await factory.CreateUserClient().PostAsJsonAsync(
            "/complete", Completion(), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- /complete/stream ---------------------------------------------------------

    // T-A-04: 陽性対照。SSE の枠は 200 ＋ text/event-stream で始まる。
    [Fact]
    public async Task CompleteStream_with_service_caller_token_succeeds()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/complete/stream", Completion(), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
    }

    // T-A-05: 🔴 陰性対照。資格情報が無ければ 401。
    //
    // 🔴 **SSE は「200 で開いてから中身で断る」形にしない。** 開いてしまうと呼び出し側は
    // 縮退イベントと拒否の区別が付かず、また上流プロバイダを呼ぶ前に止める保証も消える。
    [Fact]
    public async Task CompleteStream_without_credentials_is_unauthorized()
    {
        var resp = await factory.CreateAnonymousClient().PostAsJsonAsync(
            "/complete/stream", Completion(), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream");
    }

    // T-A-06: 🔴 陰性対照。利用者のトークン（管理者）は 403。
    [Fact]
    public async Task CompleteStream_with_admin_user_token_is_forbidden()
    {
        var resp = await factory.CreateUserClient().PostAsJsonAsync(
            "/complete/stream", Completion(), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- /embed -------------------------------------------------------------------

    // T-A-07: 陽性対照。s2s トークンなら通る。
    [Fact]
    public async Task Embed_with_service_caller_token_succeeds()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/embed", Embed(), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // T-A-08: 🔴 陰性対照。資格情報が無ければ 401。
    [Fact]
    public async Task Embed_without_credentials_is_unauthorized()
    {
        var resp = await factory.CreateAnonymousClient().PostAsJsonAsync(
            "/embed", Embed(), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // T-A-09: 🔴 陰性対照。利用者のトークン（管理者）は 403。
    [Fact]
    public async Task Embed_with_admin_user_token_is_forbidden()
    {
        var resp = await factory.CreateUserClient().PostAsJsonAsync(
            "/embed", Embed(), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- 群の外は触っていないこと ---------------------------------------------------

    // T-A-10: 🔴 陰性対照（門の**射程**）。`/health/*` と introspection は**無認可のままである**。
    // 門を「サービス全体の既定」（FallbackPolicy）で掛けると readiness まで 401 になり、
    // Pod が起動しない。`ADR-0084` 決定 1 が「端点または端点群に付くものだけを門とする」と
    // 定めているのはこの区別のためである。
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/internal/introspection")]
    public async Task Operational_endpoints_stay_open(string path)
    {
        var resp = await factory.CreateAnonymousClient().GetAsync(path, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    // T-A-11: ポリシー名は**共有の定数**であり、gRPC 面と同じ 1 つである（新しい軸を作っていない）。
    [Fact]
    public void Rest_and_grpc_faces_share_one_policy_name()
    {
        PlatformAuthPolicies.ServiceCaller.Should().Be("ServiceCaller");
        PlatformAuthPolicies.ServiceRole.Should().Be("platform-service");
    }
}
