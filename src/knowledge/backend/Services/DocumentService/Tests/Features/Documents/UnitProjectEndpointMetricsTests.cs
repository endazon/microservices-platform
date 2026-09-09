using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Common.Observability;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocumentService.Tests.Features.Documents;

// FR-05, FR-09, FR-16, SC-10, SC-12, ADR-0036, ADR-0062, ADR-0085 決定 4, [[IADR-0420]] (#1233):
// **3 つの保存経路（登録・編集・メタデータ更新）が同じ母集合を数える**ことを輸送越しに固定する。
//
// 🔴 **単体試験だけでは足りない。** 計上の位置（`SaveChangesAsync` の後）と
// 「保存が成立しなかった要求は数えない」は**端点の制御フローの性質**であり、
// `UnitProjectMetrics` を直接叩く試験では 1 行も通らない。
[Trait("TestKind", "Integration")]
public sealed class UnitProjectEndpointMetricsTests(UnitProjectEndpointMetricsTests.MeteredFactory factory)
    : IClassFixture<UnitProjectEndpointMetricsTests.MeteredFactory>
{
    private const string KbWriter = "ai-stock-trading-kb-writer";

    // 無人主体（腕 A: `service-account-<clientId>` の利用者名 ＋ `azp`）で叩く。
    private HttpClient MachineClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, $"service-account-{KbWriter}");
        client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, KbWriter);
        return client;
    }

    // 🔴 陽性対照の主体: 対話ログインの人間（`azp` は持つ）。
    private HttpClient HumanClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "hanako");
        client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, "platform-spa");
        return client;
    }

    private static object CreateBody(string? project) => new
    {
        title = $"ユニット主体の保存 {Guid.NewGuid():N}",
        attributes = Attributes(project),
        tags = new List<string>(),
    };

    private static Dictionary<string, string> Attributes(string? project)
    {
        var attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" };
        if (project is not null) attributes["project"] = project;
        return attributes;
    }

    private static async Task<DocumentDto> CreatedAsync(HttpClient client, string? project)
    {
        var resp = await client.PostAsJsonAsync("/documents", CreateBody(project));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
    }

    // (a) 登録: 無人主体 ＋ `project` 欠落 → 1 件計上する。
    [Fact]
    public async Task 登録で無人主体がprojectなしなら計上する()
    {
        var before = factory.Probe.Total;

        await CreatedAsync(MachineClient(), project: null);

        factory.Probe.Total.Should().Be(before + 1);
        factory.Probe.LastOperation.Should().Be(UnitProjectMetrics.OperationCreate);
        factory.Probe.LastClientId.Should().Be(KbWriter);
    }

    // (b) 陽性対照: 無人主体でも `project` が在れば計上しない。
    [Fact]
    public async Task 登録で無人主体がprojectを付けていれば計上しない()
    {
        var before = factory.Probe.Total;

        await CreatedAsync(MachineClient(), project: "ai-stock-trading");

        factory.Probe.Total.Should().Be(before);
    }

    // (c) 🔴 陽性対照: 対話ログインの人間は `project` が無くても計上しない。
    [Fact]
    public async Task 登録で対話ログインの人間は計上しない()
    {
        var before = factory.Probe.Total;

        await CreatedAsync(HumanClient(), project: null);

        factory.Probe.Total.Should().Be(before);
    }

    // (d-1) 編集でも同じ母集合を数える。**属性は全置換なので `project` を落とせる。**
    [Fact]
    public async Task 編集で無人主体がprojectを落としたら計上する()
    {
        var client = MachineClient();
        // 制限外の値で作る（制限 project は保存で落とせない。IADR-0405 決定 2）。
        var doc = await CreatedAsync(client, project: "some-other-project");
        var before = factory.Probe.Total;

        var resp = await client.PutAsJsonAsync($"/documents/{doc.Id}", new
        {
            title = doc.Title,
            attributes = Attributes(null),
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Probe.Total.Should().Be(before + 1);
        factory.Probe.LastOperation.Should().Be(UnitProjectMetrics.OperationUpdate);
    }

    // (d-2) メタデータ更新でも同様（配線を 1 経路だけ落としても気付ける）。
    [Fact]
    public async Task メタデータ更新で無人主体がprojectを落としたら計上する()
    {
        var client = MachineClient();
        var doc = await CreatedAsync(client, project: "some-other-project");
        var before = factory.Probe.Total;

        var resp = await client.PatchAsJsonAsync($"/documents/{doc.Id}/metadata", new
        {
            attributes = Attributes(null),
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Probe.Total.Should().Be(before + 1);
        factory.Probe.LastOperation.Should().Be(UnitProjectMetrics.OperationUpdateMetadata);
    }

    // (e-1) 🔴 保存が成立しない要求は数えない —— 登録が 400 で落ちた場合。
    // **数えると「保存した文書」ではなく「保存しようとした要求」を測ることになり、
    // 0 が正常という読み方が壊れる**（弾いた要求で警報が鳴る）。
    [Fact]
    public async Task 登録が400で落ちたら計上しない()
    {
        var before = factory.Probe.Total;

        var resp = await MachineClient().PostAsJsonAsync("/documents", new
        {
            title = "辞書に無いタグ",
            attributes = Attributes(null),
            tags = new List<string> { "存在しないタグ" },
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Probe.Total.Should().Be(before);
    }

    // (e-2) 編集が 409（版の衝突）で落ちた場合。
    [Fact]
    public async Task 編集が409で落ちたら計上しない()
    {
        var client = MachineClient();
        var doc = await CreatedAsync(client, project: "some-other-project");
        var before = factory.Probe.Total;

        var resp = await client.PutAsJsonAsync($"/documents/{doc.Id}", new
        {
            title = doc.Title,
            attributes = Attributes(null),
            tags = new List<string>(),
            expectedVersion = doc.Version + 99,
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        factory.Probe.Total.Should().Be(before);
    }

    // (e-3) 編集が 404（不存在）で落ちた場合。
    [Fact]
    public async Task 編集が404で落ちたら計上しない()
    {
        var before = factory.Probe.Total;

        var resp = await MachineClient().PutAsJsonAsync($"/documents/{Guid.NewGuid()}", new
        {
            title = "存在しない文書",
            attributes = Attributes(null),
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Probe.Total.Should().Be(before);
    }

    // `IMeterFactory` をテスト用へ差し替えたホスト。**scope で購読を絞る**ため、
    // 並行する他のテストクラスの測定は 1 件も混ざらない。
    public sealed class MeteredFactory : TestWebApplicationFactory
    {
        private readonly UnitProjectMetricsTests.ScopedMeterFactory _meters = new();

        public UnitProjectMetricsTests.MetricsProbe Probe { get; }

        public MeteredFactory() => Probe = new UnitProjectMetricsTests.MetricsProbe(_meters);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMeterFactory>();
                services.AddSingleton<IMeterFactory>(_meters);
            });
        }
    }
}
