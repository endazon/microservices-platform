using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Tags.Names;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace DocumentService.Tests.Features.Tags.Names;

// FR-18, NFR-09, NFR-16, SC-09, ADR-0029, ADR-0043, ADR-0063 決定 2, ADR-0075,
// [[IADR-0299]], [[IADR-0364]] 決定 2, [[IADR-0379]] 決定 4, [[IADR-0401]] 決定 2,
// [[IADR-0402]], [[IADR-0412]] (#1255): タグ辞書読み取りの gRPC 面
// （`knowledge.document.v1.TagDictionary`）を**実 Kestrel の h2c ポート**で往復し、
// REST `GET /internal/tags/names` との同値・s2s の要求・面に出さないものを固定する。
//
// 陽性対照（T-01 / T-04）と陰性対照（T-02 / T-03）を同じ器で対にする ——
// 「拒否された」だけでは器が壊れているのか認可が効いているのか区別できない。
//
// 🔴 **器の DB はコレクションの寿命で共有される。** 他の試験が入れたタグも `ListNames` に載るので、
// 各試験は**自分の接頭辞を持つ名前だけ**を取り出して主張する（全件に対する主張は順序だけ）。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcTagDictionaryTests
{
    private const string ServiceSubject = "service-account-graph";
    private readonly GrpcKestrelFactory _factory;

    public GrpcTagDictionaryTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    private TagDictionary.TagDictionaryClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private async Task SeedAsync(params string[] names)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        foreach (var name in names) db.Tags.Add(Tag.Create(name));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<string>> ListNamesAsync() =>
        [.. (await PlainClient().ListNamesAsync(
            new ListNamesRequest(), headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken)).Names];

    // T-01: 陽性対照。s2s トークンを CallCredentials で付けた h2c チャネルで往復し、辞書の名前が返る。
    [Fact]
    public async Task ListNames_over_h2c_with_service_token_returns_the_dictionary()
    {
        var prefix = $"h2c-{Guid.NewGuid():N}";
        await SeedAsync($"{prefix}-a");

        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress, new FixedTokenProvider(ServiceToken()));
        var client = new TagDictionary.TagDictionaryClient(channel);

        var resp = await client.ListNamesAsync(
            new ListNamesRequest(), cancellationToken: TestContext.Current.CancellationToken);

        resp.Names.Should().Contain($"{prefix}-a");
    }

    // 🔴 T-11: **名前順は契約である**（`TagNamesEndpoint.ReadNamesAsync` の `OrderBy`）。
    // 投入順を昇順の逆にして、**並べ替えが実際に起きている**ことを観測する ——
    // 投入順のまま返す実装だと降順になる。
    [Fact]
    public async Task ListNames_is_ordered_by_name()
    {
        var prefix = $"ord-{Guid.NewGuid():N}";
        await SeedAsync($"{prefix}-c", $"{prefix}-b", $"{prefix}-a");

        var names = await ListNamesAsync();

        names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Should().ContainInOrder($"{prefix}-a", $"{prefix}-b", $"{prefix}-c")
            .And.HaveCount(3);
        names.Should().BeInAscendingOrder("全体としても名前順である");
    }

    // 🔴 T-12: **前後の空白は辞書側で正規化される**（`Tag.Normalize`）。面が生の値を返すと
    // 呼び出し元（Ordinal で集合化する）の照合が静かに外れる。
    [Fact]
    public async Task ListNames_returns_normalized_names()
    {
        var prefix = $"trim-{Guid.NewGuid():N}";
        await SeedAsync($"  {prefix}-x  ");

        (await ListNamesAsync()).Should().Contain($"{prefix}-x");
    }

    // 🔴 T-13: **使用件数を面へ出さない**（`ADR-0043` 決定 2 / [[IADR-0364]] 決定 2。
    // REST 側が「本文に `usageCount` が現れない」ことを固定しているのと同じ主張を、
    // gRPC では**契約そのもの**に対して置く —— 後から欄が足されたらここで落ちる）。
    // 🔴 併せて **`user_id` / `user_attributes` を要求へ置かない**ことも固定する
    // （呼び出し元が要らないものを面へ出さない。[[IADR-0401]] 決定 2）。
    [Fact]
    public void The_face_carries_only_names()
    {
        ListNamesResponse.Descriptor.Fields.InDeclarationOrder()
            .Select(f => f.Name).Should().Equal(["names"]);
        ListNamesRequest.Descriptor.Fields.InDeclarationOrder()
            .Should().BeEmpty("読む主体はサービス自身であり、利用者文脈は要らない");
    }

    // T-02: 陰性対照。資格情報が無ければ UNAUTHENTICATED。
    [Fact]
    public async Task ListNames_without_credentials_is_unauthenticated()
    {
        var act = async () => await PlainClient().ListNamesAsync(
            new ListNamesRequest(), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unauthenticated);
    }

    // 🔴 T-03: **利用者トークンの転送を機械で止める点。**
    //
    // REST の受け口は**認証を持たない**（[[IADR-0364]] 決定 2 のメッシュ内部 API）ので、
    // 「認証さえあれば通る」形にすると s2s の面が利用者トークンでも開く。開くと呼び出し先は
    // 「利用者が直接呼んだ」と区別できず confused deputy が成立する（[[IADR-0379]] 決定 4）。
    // **管理者の利用者トークンでも PERMISSION_DENIED** であることを固定する。
    [Fact]
    public async Task ListNames_with_forwarded_admin_user_token_is_permission_denied()
    {
        var adminToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);

        var act = async () => await PlainClient().ListNamesAsync(
            new ListNamesRequest(), headers: Bearer(adminToken),
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.PermissionDenied);
    }

    // T-04 ＋ T-09: REST と gRPC が**同じ問い合わせ**（`TagNamesEndpoint.ReadNamesAsync`）を
    // 通ることの観測。順序を含めて同値である。
    //
    // 🔴 REST が同じプロセスの HTTP/1.1 ポートで応えること自体が、**h2c を有効にしても
    // 8080 側が消えていない**ことの証明でもある（`AddPlatformGrpcListener` の 🔴）。
    //
    // 🔴 REST は**無認可**で通り、gRPC は s2s トークンを要る —— 面ごとに通る資格情報が違うことが、
    // そのまま「利用者トークンを転送していない」ことの現れである。
    [Fact]
    public async Task Rest_and_grpc_report_the_same_names()
    {
        var prefix = $"same-{Guid.NewGuid():N}";
        await SeedAsync($"{prefix}-b", $"{prefix}-a");

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        var rest = (await http.GetFromJsonAsync<TagNamesResponse>(
            TagNamesEndpoint.NamesPath, TestContext.Current.CancellationToken))!;

        var grpc = await ListNamesAsync();

        grpc.Should().Equal(rest.Names, "輸送を替えても応答の意味も順序も変わらない");
        grpc.Should().Contain([$"{prefix}-a", $"{prefix}-b"], "★ 陽性対照 —— 両方が空で一致したのではない");
    }

    // T-08: 構造の門。gRPC サービス型が ServiceCaller ポリシーを宣言していること
    // （属性が外れると T-02 / T-03 が落ちるが、どの層で外れたかを名指しするためにここでも固定する）。
    [Fact]
    public void Grpc_service_declares_service_caller_policy()
    {
        var attr = typeof(TagDictionaryGrpcService)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }

    // s2s トークンの発行側を固定値へ差し替える（IdP を持たないため）。
    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct = default) => new(token);
    }
}
