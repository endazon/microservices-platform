using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.Documents;

// FR-19, ADR-0054 決定 1〜4, ADR-0046 D-01, [[IADR-0270]] 決定 2:
// doc_scope（文書スコープ）の値域検証と、一般経路での個人資料作成の拒否。
//
// 🔴 判定の向き（集合帰属）の検証には陽性対照が要る —— doc_scope を持たない既存文書の挙動が
// 変わらないこと（欠落を拒否しない・個人資料扱いしない）は、否定（!= organization）で書いた
// 実装では破れる。実データ 0 件の現在、この対照だけが向きを見分ける。
public class DocScopeValidationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static object DocBody(Dictionary<string, string> attributes) => new
    {
        title = $"検証 {Guid.NewGuid():N}",
        attributes,
        tags = new List<string>(),
    };

    // ADR-0054 決定 2: 値域は private-note / organization の 2 値。未知値は 400。
    [Fact]
    public async Task 未知のdoc_scope値は400で拒否される()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync("/documents",
            DocBody(new() { ["confidentiality"] = "internal", ["doc_scope"] = "personal" }), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // [[IADR-0270]] 決定 2: 一般経路（POST /documents）での個人資料の作成は拒否する
    // （台帳の無い個人資料は容量算入から漏れる）。
    [Fact]
    public async Task 一般経路では個人資料を作成できない()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync("/documents",
            DocBody(new()
            {
                ["confidentiality"] = "restricted",
                ["doc_scope"] = "private-note",
                ["owner"] = "someone",
            }), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 陽性対照: organization は一般経路で作成できる。
    [Fact]
    public async Task organizationの文書は一般経路で作成できる()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync("/documents",
            DocBody(new() { ["confidentiality"] = "internal", ["doc_scope"] = "organization" }), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // 陽性対照（集合帰属の向き）: doc_scope を持たない文書は従来どおり作成・更新できる
    // （欠落は拒否しない —— 既存 2,368 件へ遡及付与しない方針）。
    [Fact]
    public async Task doc_scopeを持たない文書は従来どおり作成更新できる()
    {
        var client = factory.CreateClient();
        var create = await client.PostAsJsonAsync("/documents",
            DocBody(new() { ["confidentiality"] = "internal" }), TestContext.Current.CancellationToken);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);

        var update = await client.PutAsJsonAsync($"/documents/{doc!.Id}", new
        {
            title = "更新後",
            attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ADR-0054: 更新・メタデータ更新でも未知値は 400。
    [Fact]
    public async Task 更新経路でも未知のdoc_scope値は400になる()
    {
        var client = factory.CreateClient();
        var create = await client.PostAsJsonAsync("/documents",
            DocBody(new() { ["confidentiality"] = "internal" }), TestContext.Current.CancellationToken);
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);

        var badPut = await client.PutAsJsonAsync($"/documents/{doc!.Id}", new
        {
            title = "更新",
            attributes = new Dictionary<string, string>
            {
                ["confidentiality"] = "internal",
                ["doc_scope"] = "team",
            },
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);
        badPut.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var badPatch = await client.PatchAsJsonAsync($"/documents/{doc.Id}/metadata", new
        {
            attributes = new Dictionary<string, string>
            {
                ["confidentiality"] = "internal",
                ["doc_scope"] = "PRIVATE",
            },
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);
        badPatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ADR-0036 D-04, ADR-0046 D-01: 判定は集合帰属である（ドメイン関数の直接検証）。
    [Fact]
    public void IsPrivateNoteは集合帰属で判定し欠落を個人資料扱いしない()
    {
        DocumentAttributes.IsPrivateNote(new Dictionary<string, string>
        {
            ["doc_scope"] = "private-note",
        }).Should().BeTrue();

        // 陽性対照: organization・欠落・null はいずれも個人資料ではない
        DocumentAttributes.IsPrivateNote(new Dictionary<string, string>
        {
            ["doc_scope"] = "organization",
        }).Should().BeFalse();
        DocumentAttributes.IsPrivateNote(new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
        }).Should().BeFalse("属性を持たない既存文書を個人資料へ巻き込まない（否定で書くと破れる）");
        DocumentAttributes.IsPrivateNote(null).Should().BeFalse();
    }

    // FR-22, [[IADR-0270]] 決定 6: 通知 kind の綴りは NotificationService 側の値と一致させる
    // （プロジェクト参照を張れないため、綴りの正をリテラルでここに固定する）。
    [Fact]
    public void 通知kindの綴りはNotificationService側の値と一致する()
    {
        DocumentService.Domain.Ports.PrivateNoteNotificationKinds.PrivateNotePurgeWeekly
            .Should().Be("private-note-purge-weekly");
        DocumentService.Domain.Ports.PrivateNoteNotificationKinds.PrivateNotePurgeImminent
            .Should().Be("private-note-purge-imminent");
        DocumentService.Domain.Ports.PrivateNoteNotificationKinds.PrivateNotePurgeDone
            .Should().Be("private-note-purge-done");
        DocumentService.Domain.Ports.PrivateNoteNotificationKinds.StorageQuotaWarning
            .Should().Be("storage-quota-warning");
        DocumentService.Domain.Ports.PrivateNoteNotificationKinds.SyncTokenExpiry
            .Should().Be("sync-token-expiry");
    }
}
