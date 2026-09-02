using System.Net;
using System.Net.Http.Json;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Tags;

// FR-06, FR-09, SC-05, SC-09, UC-03, UC-05, #634: タグ辞書（IADR-0152）。
//
// SC-09 が確定した規則（参照が 1 件でもあるタグは削除拒否・削除前に使用件数を示す）は
// **使用件数の数え方が正しいことに全面的に依存している**。数え方を間違えると、
// 使われているタグを削除できてしまうか、逆に**一度でも使われたタグを永久に削除できなくなる**。
public class TagDictionaryTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient ClientAs(params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    // 各テストが自分のタグ名を使う（TestWebApplicationFactory は共有され、DB も共有されるため）。
    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<TagDto> AddTagAsync(string name)
    {
        var resp = await ClientAs("platform-admin").PostAsJsonAsync("/tags", new CreateTagRequest(name));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<TagDto>())!;
    }

    private async Task<TagDto?> FindTagAsync(string name)
    {
        var resp = await ClientAs("platform-admin").GetAsync("/tags");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<TagDictionaryResponse>();
        return body!.Tags.SingleOrDefault(t => t.Name == name);
    }

    // 版履歴・アーカイブの扱いを検証するため、DB を直接組み立てる。
    // エンドポイント経由では「版履歴だけが参照する状態」を作れない（現行版にも必ず残るため）。
    private async Task SeedAsync(Func<DocumentDbContext, Task> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    // 受け入れ基準 1: 辞書の値集合を管理者・運用者が引ける。
    [Theory]
    [InlineData("platform-admin")]
    [InlineData("platform-operator")]
    public async Task List_AdminOrOperator_IsAllowed(string role)
    {
        (await ClientAs(role).GetAsync("/tags", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 受け入れ基準 1（裏）: 一般利用者は辞書を引けない。
    // **辞書を丸ごと返すと権限外の文書に固有の値からその存在が推測できる**（ADR-0043 決定 1）。
    // 一般利用者の候補は Qdrant の facet 経由である（#540 / IADR-0152 決定 4）。
    [Fact]
    public async Task List_GeneralUser_IsForbidden()
    {
        (await ClientAs("viewer").GetAsync("/tags", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 受け入れ基準 3: 追加はシステム管理者のみ（SC-09 のアクセス制御）。
    // **運用者は読めるが書けない** —— 読み書きで境界が違うことを固定する。
    [Theory]
    [InlineData("platform-operator")]
    [InlineData("viewer")]
    public async Task Create_NonAdmin_IsForbidden(string role)
    {
        var resp = await ClientAs(role).PostAsJsonAsync("/tags", new CreateTagRequest(UniqueName("x")), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_Admin_AddsTagWithZeroUsage()
    {
        var name = UniqueName("新規");
        var created = await AddTagAsync(name);

        created.Name.Should().Be(name);
        created.UsageCount.Should().Be(0, "追加直後はまだどの文書にも付いていない");
        (await FindTagAsync(name)).Should().NotBeNull();
    }

    // SC-09: 「新しい名前は既存値と重複しない」。
    // **正規化後の名前で見る** —— 前後の空白だけが違う 2 つを別物として登録できると、
    // 辞書が実質的に重複を許すことになる。
    [Fact]
    public async Task Create_DuplicateName_Returns409_IgnoringSurroundingWhitespace()
    {
        var name = UniqueName("重複");
        await AddTagAsync(name);

        var resp = await ClientAs("platform-admin")
            .PostAsJsonAsync("/tags", new CreateTagRequest($"  {name}  "), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_BlankName_IsRejected(string name)
    {
        var resp = await ClientAs("platform-admin").PostAsJsonAsync("/tags", new CreateTagRequest(name), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 受け入れ基準 4: 使用件数は**現行版の文書の件数**である（IADR-0152 決定 2）。
    [Fact]
    public async Task UsageCount_CountsDocumentsOnCurrentVersion()
    {
        var name = UniqueName("計上");
        var tag = await AddTagAsync(name);
        var other = await AddTagAsync(UniqueName("他"));
        await SeedAsync(db =>
        {
            db.Documents.Add(Document.Create("文書1", null, null, null, [tag.Id]));
            db.Documents.Add(Document.Create("文書2", null, null, null, [tag.Id, other.Id]));
            db.Documents.Add(Document.Create("文書3", null, null, null, [other.Id]));
            return Task.CompletedTask;
        });

        (await FindTagAsync(name))!.UsageCount.Should().Be(2);
    }

    // 受け入れ基準 5: **版履歴だけが参照するタグは 0 件である。**
    //
    // 版履歴は append-only で付け替えられない（`Document.Snapshot` が `_versions.Add` するだけである）。
    // **数えると、一度でも使われたタグを永久に削除できなくなり**、
    // SC-09 の「使用件数 0 件のときに限り削除できる」が空文になる（IADR-0152 決定 2）。
    [Fact]
    public async Task UsageCount_DoesNotCountVersionHistory()
    {
        var name = UniqueName("旧タグ");
        var tag = await AddTagAsync(name);
        await SeedAsync(db =>
        {
            // 版 1 でタグを付け、版 2 で外す。版履歴には版 1 のタグが残る。
            var doc = Document.Create("付け替え", null, null, null, [tag.Id]);
            doc.UpdateMetadata([], [], "タグを外した");
            db.Documents.Add(doc);

            doc.Versions.Should().HaveCount(2);
            doc.Versions[0].Tags.Should().Contain(tag.Id, "版履歴には付いていた事実が残る");
            doc.Tags.Should().BeEmpty("現行版からは外れている");
            return Task.CompletedTask;
        });

        (await FindTagAsync(name))!.UsageCount
            .Should().Be(0, "版履歴を数えると付け替えても削除できなくなる");
    }

    // 受け入れ基準 6: **アーカイブ済みの文書は数える。**
    //
    // `Document.UpdateMetadata` にアーカイブ済みの禁止が無い（guard が掛かっているのは `Publish` だけ）ため、
    // **アーカイブ済みでもタグは付け替えられる**。数えても管理者は行動できる。
    // 除くと、削除した直後にアーカイブ済み文書が消えたタグを指したままになる（IADR-0152 決定 2）。
    [Fact]
    public async Task UsageCount_CountsArchivedDocuments()
    {
        var name = UniqueName("保管");
        var tag = await AddTagAsync(name);
        await SeedAsync(db =>
        {
            var doc = Document.Create("アーカイブ済み", null, null, null, [tag.Id]);
            doc.Archive();
            db.Documents.Add(doc);
            return Task.CompletedTask;
        });

        (await FindTagAsync(name))!.UsageCount.Should().Be(1);
    }

    // 同じ文書に同じタグが 2 度入っていても 1 件と数える（数えるのは「使っている文書の数」である）。
    [Fact]
    public async Task UsageCount_CountsEachDocumentOnce()
    {
        var name = UniqueName("重出");
        var tag = await AddTagAsync(name);
        await SeedAsync(db =>
        {
            db.Documents.Add(Document.Create("重複タグ", null, null, null, [tag.Id, tag.Id]));
            return Task.CompletedTask;
        });

        (await FindTagAsync(name))!.UsageCount.Should().Be(1);
    }

    // 辞書に在るが誰も使っていないタグは 0 件で**辞書には出る**。
    // 管理面（辞書）と利用者面（facet）で集合が一致しないのは**意図した差**である（IADR-0152 決定 4）。
    [Fact]
    public async Task List_IncludesUnusedTags()
    {
        var name = UniqueName("未使用");
        await AddTagAsync(name);

        (await FindTagAsync(name))!.UsageCount.Should().Be(0);
    }
}
