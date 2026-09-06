using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.AddTag;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents.AddTag;

// FR-05, FR-18, NFR-09, NFR-16, SC-03, SC-05, SC-09, ADR-0004, ADR-0029, ADR-0036 D-07,
// ADR-0063 決定 1〜3, ADR-0075, 計画 ADR-0086 決定 1・3, [[IADR-0044]], [[IADR-0364]],
// [[IADR-0379]], [[IADR-0410]] (#1255): タグ反映の gRPC 面（`knowledge.document.v1.DocumentTagWrite`）を
// **実 Kestrel の h2c ポート**で往復し、s2s トークンの検証・**判定の位置**・404 の一本道を固定する。
//
// 陽性対照と陰性対照を同じ器で対にする —— 「拒否された」だけでは器が壊れているのか
// 認可が効いているのか区別できない。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcDocumentTagWriteTests
{
    private const string ServiceSubject = "service-account-graph-service";
    private readonly GrpcKestrelFactory _factory;

    public GrpcDocumentTagWriteTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    private DocumentTagWrite.DocumentTagWriteClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private async Task<T> SeedAsync<T>(Func<DocumentDbContext, T> seed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var result = seed(db);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return result;
    }

    private Task<string> RegisterTagAsync()
    {
        var name = $"tag-{Guid.NewGuid():N}";
        return SeedAsync(db =>
        {
            db.Tags.Add(Tag.Create(name));
            return name;
        });
    }

    private Task<Document> SeedDocumentAsync(string? owner)
    {
        var attrs = new Dictionary<string, string> { ["confidentiality"] = "internal" };
        if (owner is not null) attrs["owner"] = owner;
        return SeedAsync(db =>
        {
            var doc = Document.CreateNormalized(
                Guid.NewGuid(), $"反映先 {Guid.NewGuid():N}", "storage://b/x", attrs, hasBody: false);
            db.Documents.Add(doc);
            return doc;
        });
    }

    private Task<AddTagResponse> AddAsync(
        Guid documentId, string tagName, string userId, params string[] roles)
        => PlainClient().AddTagAsync(
            new AddTagRequest
            {
                DocumentId = documentId.ToString(),
                TagName = tagName,
                UserId = userId,
                Action = "write",
                UserRoles = { roles },
            },
            Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;

    // 🔴 T-01: 陽性対照。**本文で運ばれた `user_id` が所有者と一致すれば書ける。**
    // 呼び出し先が本文の身元で自分の判定を行っていることの、いちばん直接の観測である。
    [Fact]
    public async Task 所有者として運ばれた身元なら反映される()
    {
        var tag = await RegisterTagAsync();
        var doc = await SeedDocumentAsync(owner: "alice");

        var resp = await AddAsync(doc.Id, tag, "alice");

        resp.Result.Should().Be(TagWriteResult.Applied);
    }

    // 🔴 T-02: 陰性対照（T-01 と対）。**別人の身元では書けない。**
    // 呼び出し元の判定結果を信じているなら、ここも `APPLIED` になってしまう。
    [Fact]
    public async Task 所有者でない身元では書けない()
    {
        var tag = await RegisterTagAsync();
        var doc = await SeedDocumentAsync(owner: "alice");

        var resp = await AddAsync(doc.Id, tag, "mallory");

        resp.Result.Should().Be(TagWriteResult.NotWritable);
    }

    // 🔴 T-03: **管理者ロールは `user_roles` で運ぶ。** 取り込み文書（所有者無し）は
    // ①では誰も書けないので、②が無いと誰も承認できない（`ADR-0063` 決定 3）。
    [Fact]
    public async Task 管理者ロールを運べば所有者のいない文書へも反映される()
    {
        var tag = await RegisterTagAsync();
        var doc = await SeedDocumentAsync(owner: null);

        var asAdmin = await AddAsync(doc.Id, tag, "operator", PlatformAuthPolicies.AdminRole);
        var asUser = await AddAsync(doc.Id, tag, "operator");

        asAdmin.Result.Should().Be(TagWriteResult.Applied, "★ 陽性対照");
        asUser.Result.Should().Be(TagWriteResult.NotWritable);
    }

    // 🔴 T-04: **辞書に無い名前は却下**（`ADR-0063` 決定 2）。
    // 🔴 **辞書照合は認可の後ろである** —— 書けない主体には「辞書に無い」を返さない。
    [Fact]
    public async Task 辞書に無い名前は却下され書けない主体には辞書の情報を返さない()
    {
        var doc = await SeedDocumentAsync(owner: "alice");

        var owner = await AddAsync(doc.Id, $"未登録-{Guid.NewGuid():N}", "alice");
        var stranger = await AddAsync(doc.Id, $"未登録-{Guid.NewGuid():N}", "mallory");

        owner.Result.Should().Be(TagWriteResult.UnknownTag);
        stranger.Result.Should().Be(TagWriteResult.NotWritable, "辞書の情報を漏らさない");
    }

    // 🔴 T-05: **「書けない」と「文書が無い」は同じ応答である**（404 の一本道）。
    // status で割ると、割り方そのものが文書の実在を漏らす。
    [Fact]
    public async Task 不存在と書けないは同じ応答である()
    {
        var tag = await RegisterTagAsync();
        var doc = await SeedDocumentAsync(owner: "alice");

        var missing = await AddAsync(Guid.NewGuid(), tag, "alice");
        var forbidden = await AddAsync(doc.Id, tag, "mallory");

        missing.Result.Should().Be(forbidden.Result).And.Be(TagWriteResult.NotWritable);
    }

    // 🔴 T-06: **冪等**。2 回目も `APPLIED` である（版は進めない）。
    [Fact]
    public async Task 既に付いていても成功を返す()
    {
        var tag = await RegisterTagAsync();
        var doc = await SeedDocumentAsync(owner: "alice");

        var first = await AddAsync(doc.Id, tag, "alice");
        var second = await AddAsync(doc.Id, tag, "alice");

        first.Result.Should().Be(TagWriteResult.Applied);
        second.Result.Should().Be(TagWriteResult.Applied);
    }

    // 🔴 T-07: **利用者文脈の欠落は要求の誤りであって「書けない」ではない。**
    // deny へ畳むと、呼び出し元の配線誤りが「承認できない文書だった」に化ける。
    [Fact]
    public async Task 身元の欠落は不正な引数である()
    {
        var tag = await RegisterTagAsync();
        var doc = await SeedDocumentAsync(owner: "alice");

        var act = async () => await AddAsync(doc.Id, tag, string.Empty);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // 🔴 T-08: 空のタグ名は**取得・認可より前**に弾く（REST の 400 と同じ位置）。
    // 後ろへ動かすと `NOT_WRITABLE` に化ける。
    [Fact]
    public async Task 空のタグ名は認可より前に弾かれる()
    {
        var act = async () => await AddAsync(Guid.NewGuid(), "   ", "誰でもよい");

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument,
                "存在しない文書でも 400 である（順序が動いていたら NOT_WRITABLE になる）");
    }

    // 🔴 T-09: 資格情報が無ければ `UNAUTHENTICATED`。
    [Fact]
    public async Task 資格情報が無ければ認証されない()
    {
        var act = async () => await PlainClient().AddTagAsync(
            new AddTagRequest { DocumentId = Guid.NewGuid().ToString(), TagName = "t", UserId = "alice" },
            cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    // 🔴 T-10: **承認者本人のトークンでは通らない**（管理者であっても）。
    // 利用者文脈は本文で運び、面には s2s だけを通す（confused deputy の防止）。
    [Fact]
    public async Task 管理者の利用者トークンでも権限が拒否される()
    {
        var adminToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);

        var act = async () => await PlainClient().AddTagAsync(
            new AddTagRequest { DocumentId = Guid.NewGuid().ToString(), TagName = "t", UserId = "alice" },
            Bearer(adminToken), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // 🔴 T-11: **`ServiceCaller` の宣言をリフレクションで固定する。**
    [Fact]
    public void Grpc面はServiceCallerポリシーを宣言する()
    {
        var attribute = typeof(DocumentTagWriteGrpcService)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attribute.Should().NotBeNull();
        attribute!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }
}
