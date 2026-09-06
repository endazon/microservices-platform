using System.Net.Http.Json;
using DocumentService.Domain;
using DocumentService.Features.Documents;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Grpc;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace DocumentService.Tests.Features.Documents;

// FR-05, FR-06, UC-03, SC-03, SC-05, NFR-09, NFR-16, ADR-0004, ADR-0029, ADR-0056, ADR-0070,
// ADR-0075, [[IADR-0041]], [[IADR-0290]], [[IADR-0379]], [[IADR-0388]], [[IADR-0402]] (#1255):
// 文書読み取りの gRPC 面（`knowledge.document.v1.DocumentRead`）を**実 Kestrel の h2c ポート**で
// 往復し、s2s トークンの検証・REST との同値・proto3 の既定値の再適用を固定する。
//
// 陽性対照（T-01 / T-04）と陰性対照（T-02 / T-03）を同じ器で対にする ——
// 「拒否された」だけでは器が壊れているのか認可が効いているのか区別できない。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcDocumentReadTests
{
    private const string ServiceSubject = "service-account-bff";
    private readonly GrpcKestrelFactory _factory;

    public GrpcDocumentReadTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    private DocumentRead.DocumentReadClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    // 器の DB は器の寿命で共有される（コレクション固定）。各試験は自分が入れた文書だけを見る。
    private async Task<Document> SeedAsync(Document doc)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        db.Documents.Add(doc);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return doc;
    }

    // T-01: 陽性対照。s2s トークン（platform-service）を CallCredentials で付けた h2c チャネルで
    // 往復し、台帳の文書が返る。
    [Fact]
    public async Task GetDocument_over_h2c_with_service_token_returns_the_document()
    {
        var doc = await SeedAsync(Document.Create("h2c 陽性対照", null, null,
            new Dictionary<string, string> { ["department"] = "sales" }));

        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress, new FixedTokenProvider(ServiceToken()));
        var client = new DocumentRead.DocumentReadClient(channel);

        var resp = await client.GetDocumentAsync(
            new GetDocumentRequest { Id = doc.Id.ToString("D") },
            cancellationToken: TestContext.Current.CancellationToken);

        resp.Found.Should().BeTrue();
        resp.Document.Title.Should().Be("h2c 陽性対照");
        resp.Document.Attributes["department"].Should().Be("sales");
    }

    // T-07: 「無い」は**応答**である（`NOT_FOUND` を投げない）。
    // 呼び出し元がこれを 404 秘匿へ倒すのは呼び出し元の判断であり、輸送の側では決めない。
    [Fact]
    public async Task GetDocument_for_unknown_id_is_a_not_found_response()
    {
        var resp = await PlainClient().GetDocumentAsync(
            new GetDocumentRequest { Id = Guid.NewGuid().ToString("D") },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Found.Should().BeFalse();
    }

    // 🔴 綴り誤りは「無い」ではなく**要求の誤り**である。`found=false` に畳むと、
    // 呼び出し元の綴り誤りが「その文書は存在しない」に化けて気付けなくなる。
    [Fact]
    public async Task GetDocument_with_malformed_id_is_invalid_argument()
    {
        var act = async () => await PlainClient().GetDocumentAsync(
            new GetDocumentRequest { Id = "not-a-guid" },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.InvalidArgument);
    }

    // 🔴 T-05: **proto3 の既定と DTO の既定が逆向きの真偽値**（`ADR-0070` 決定 3 / [[IADR-0388]] 決定 2）。
    // `HasBody` は DTO の既定が true・proto3 の既定が false である。サーバが明示代入を落とすと
    // **全文書が「本文なし」に見え**、SC-03 が本文の位置へ「本文なし（原本を参照）」を出す。
    // 陽性（true が true のまま）と陰性（false が false のまま）を対で固定する。
    [Fact]
    public async Task HasBody_survives_the_wire_in_both_directions()
    {
        var withBody = await SeedAsync(Document.CreateNormalized(
            Guid.NewGuid(), "本文あり", "storage://b/with", hasBody: true));
        var withoutBody = await SeedAsync(Document.CreateNormalized(
            Guid.NewGuid(), "本文なし", "storage://b/without", hasBody: false));

        var yes = await PlainClient().GetDocumentAsync(
            new GetDocumentRequest { Id = withBody.Id.ToString("D") },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
        var no = await PlainClient().GetDocumentAsync(
            new GetDocumentRequest { Id = withoutBody.Id.ToString("D") },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        yes.Document.HasBody.Should().BeTrue("陽性対照: proto3 の既定へ落ちていない");
        no.Document.HasBody.Should().BeFalse("本文なしの文書が「本文あり」に化けない");
    }

    // 🔴 T-06: `MarkdownUri` の **null と空文字は別物**である（前者は本文プレースホルダの「(未設定)」）。
    // proto3 には null が無いので `optional`（field presence）で運ぶ。
    [Fact]
    public async Task MarkdownUri_null_is_distinguishable_from_empty()
    {
        var noUri = await SeedAsync(Document.Create("URI 無し", null, null));

        var resp = await PlainClient().GetDocumentAsync(
            new GetDocumentRequest { Id = noUri.Id.ToString("D") },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Document.HasMarkdownUri.Should().BeFalse("null は presence を立てない");
    }

    // T-01（版側の陽性対照）＋「文書が無い」と「版が無い」の分離。
    [Fact]
    public async Task ListVersions_separates_missing_document_from_missing_versions()
    {
        var doc = await SeedAsync(Document.Create("版あり", null, null));

        var present = await PlainClient().ListVersionsAsync(
            new ListVersionsRequest { DocumentId = doc.Id.ToString("D") },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
        present.Found.Should().BeTrue();
        present.Versions.Should().NotBeEmpty("陽性対照: 作成時点の版 1 が在る");

        var absent = await PlainClient().ListVersionsAsync(
            new ListVersionsRequest { DocumentId = Guid.NewGuid().ToString("D") },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
        absent.Found.Should().BeFalse("文書そのものが無いのは版が 0 件なのとは別の事実である");
    }

    // T-01（特定版の陽性対照）と不在。
    [Fact]
    public async Task GetVersion_returns_the_snapshot_or_a_not_found_response()
    {
        var doc = await SeedAsync(Document.Create("特定版", null, null));

        var found = await PlainClient().GetVersionAsync(
            new GetVersionRequest { DocumentId = doc.Id.ToString("D"), Version = 1 },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
        found.Found.Should().BeTrue();
        found.Snapshot.Version.Should().Be(1);
        found.Snapshot.Title.Should().Be("特定版");

        var missing = await PlainClient().GetVersionAsync(
            new GetVersionRequest { DocumentId = doc.Id.ToString("D"), Version = 99 },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
        missing.Found.Should().BeFalse();
    }

    // T-02: 陰性対照。資格情報が無ければ UNAUTHENTICATED（4 rpc とも）。
    [Fact]
    public async Task DocumentRead_without_credentials_is_unauthenticated()
    {
        var id = Guid.NewGuid().ToString("D");
        var client = PlainClient();
        var ct = TestContext.Current.CancellationToken;

        var calls = new Func<Task>[]
        {
            async () => await client.ListDocumentsAsync(new ListDocumentsRequest(), cancellationToken: ct),
            async () => await client.GetDocumentAsync(new GetDocumentRequest { Id = id }, cancellationToken: ct),
            async () => await client.ListVersionsAsync(new ListVersionsRequest { DocumentId = id }, cancellationToken: ct),
            async () => await client.GetVersionAsync(new GetVersionRequest { DocumentId = id, Version = 1 }, cancellationToken: ct),
        };

        foreach (var call in calls)
            (await call.Should().ThrowAsync<RpcException>()).Which.StatusCode
                .Should().Be(StatusCode.Unauthenticated);
    }

    // 🔴 T-03: **利用者トークンの転送を機械で止める唯一の点。**
    //
    // REST の読み取り 4 口は**ロールで塞いでいない**（SC-03 の一般利用者の閲覧）ので、
    // 「管理者なら通る」形にすると s2s の面が利用者トークンでも開く。開くと呼び出し先は
    // 「利用者が直接呼んだ」と区別できず confused deputy が成立する（[[IADR-0379]] 決定 4）。
    // **管理者の利用者トークンでも PERMISSION_DENIED** であることを固定する。
    [Fact]
    public async Task DocumentRead_with_forwarded_admin_user_token_is_permission_denied()
    {
        var adminToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);
        var id = Guid.NewGuid().ToString("D");
        var client = PlainClient();
        var ct = TestContext.Current.CancellationToken;

        var calls = new Func<Task>[]
        {
            async () => await client.ListDocumentsAsync(new ListDocumentsRequest(), headers: Bearer(adminToken), cancellationToken: ct),
            async () => await client.GetDocumentAsync(new GetDocumentRequest { Id = id }, headers: Bearer(adminToken), cancellationToken: ct),
            async () => await client.ListVersionsAsync(new ListVersionsRequest { DocumentId = id }, headers: Bearer(adminToken), cancellationToken: ct),
            async () => await client.GetVersionAsync(new GetVersionRequest { DocumentId = id, Version = 1 }, headers: Bearer(adminToken), cancellationToken: ct),
        };

        foreach (var call in calls)
            (await call.Should().ThrowAsync<RpcException>()).Which.StatusCode
                .Should().Be(StatusCode.PermissionDenied);
    }

    // T-04 ＋ T-09: REST と gRPC が**同じ本体**（`DocumentReadUseCase`）を通ることの観測。
    //
    // 🔴 REST が同じプロセスの HTTP/1.1 ポートで応えること自体が、**h2c を有効にしても
    // 8080 側が消えていない**ことの証明でもある（`AddPlatformGrpcListener` の 🔴）。
    //
    // 🔴 REST の読み取りは**無認可**で通り、gRPC は s2s トークンを要る ——
    // 面ごとに通る資格情報が違うことが、そのまま「利用者トークンを転送していない」ことの現れである。
    [Fact]
    public async Task Rest_and_grpc_report_the_same_documents()
    {
        var doc = await SeedAsync(Document.CreateNormalized(
            Guid.NewGuid(), "同値の対象", "storage://b/same",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, hasBody: false));

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        var restDoc = (await http.GetFromJsonAsync<DocumentDto>(
            $"/documents/{doc.Id}", TestContext.Current.CancellationToken))!;

        var grpcResp = await PlainClient().GetDocumentAsync(
            new GetDocumentRequest { Id = doc.Id.ToString("D") },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        var viaGrpc = Knowledge.Contracts.Grpc.DocumentReadGrpcMapping.ToDto(grpcResp.Document);
        viaGrpc.Should().BeEquivalentTo(restDoc, "輸送を替えても応答の意味は変わらない");

        // 一覧も同値（順序を含む）。
        var restList = (await http.GetFromJsonAsync<List<DocumentDto>>(
            "/documents", TestContext.Current.CancellationToken))!;
        var grpcList = await PlainClient().ListDocumentsAsync(
            new ListDocumentsRequest(), headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        grpcList.Documents.Select(Knowledge.Contracts.Grpc.DocumentReadGrpcMapping.ToDto)
            .Should().BeEquivalentTo(restList, o => o.WithStrictOrdering());

        // 版の一覧も同値。
        var restVersions = (await http.GetFromJsonAsync<List<DocumentVersionDto>>(
            $"/documents/{doc.Id}/versions", TestContext.Current.CancellationToken))!;
        var grpcVersions = await PlainClient().ListVersionsAsync(
            new ListVersionsRequest { DocumentId = doc.Id.ToString("D") },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        grpcVersions.Versions.Select(Knowledge.Contracts.Grpc.DocumentReadGrpcMapping.ToDto)
            .Should().BeEquivalentTo(restVersions, o => o.WithStrictOrdering());
    }

    // T-08: 構造の門。gRPC サービス型が ServiceCaller ポリシーを宣言していること
    // （属性が外れると T-02 / T-03 が落ちるが、どの層で外れたかを名指しするためにここでも固定する）。
    [Fact]
    public void Grpc_service_declares_service_caller_policy()
    {
        var attr = typeof(DocumentReadGrpcService)
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
