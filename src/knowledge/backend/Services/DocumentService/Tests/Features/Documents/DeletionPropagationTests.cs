using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents;
using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using DocumentService.Features.PrivateNotes.Maintenance;

namespace DocumentService.Tests.Features.Documents;

// FR-06, FR-19, UC-03, UC-11, SC-19, ADR-0057 決定 1, [[IADR-0296]]:
// **削除は本文の実体と資産まで及ぶ。**
//
// 🔴 **「削除 API が 204 を返した」は検出力の証拠にならない。** 従前も 204 は返っており、
// 実体だけが残っていた。ここで測るのは **台帳から逆引きした URI が過不足なく消されたか**である。
// 器は `RecordingObjectStorageClient`（消された URI を記録する）—— Docker 非依存で、
// MinIO を立てずに「何が消えたか」を直接見られる唯一の観測点である。
[Trait("TestKind", "Integration")]
public class DeletionPropagationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client() => factory.CreateClient();

    private IReadOnlyList<string> Deleted()
    {
        lock (factory.Storage.Deleted) return [.. factory.Storage.Deleted];
    }

    private async Task<DocumentDto> CreateWithBodyAsync(string title, string body)
    {
        var resp = await Client().PostAsJsonAsync("/documents", new
        {
            title,
            body,
            attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            tags = new List<string>(),
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
    }

    // FR-06 / ADR-0057 決定 1: 文書削除は本文の実体を消す。**変異 1（削除呼び出しを消す）を落とす。**
    [Fact]
    public async Task 文書削除は本文の実体を消す()
    {
        factory.Storage.ResetDeletions();
        var doc = await CreateWithBodyAsync("実体削除の対象", "# 本文\n消える");
        var bodyUri = doc.MarkdownUri!;
        factory.Storage.Texts.Should().ContainKey(bodyUri, "前提: 本文が格納されている");

        var resp = await Client().DeleteAsync($"/documents/{doc.Id}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Deleted().Should().Contain(bodyUri,
            "ADR-0057 受け入れ基準①: オブジェクトストレージに本文が残っていないこと");
        factory.Storage.Texts.Should().NotContainKey(bodyUri);
    }

    // 🔴 **変異 4（版スナップショットの URI を集めない）を落とす。**
    // 本文のキーは経路の切り替え（取り込み → 本文直接受け入れ）で変わり得るため、
    // **現行行だけを見ると過去に指していた本文を取りこぼす。**
    [Fact]
    public async Task 文書削除は過去版が指していた本文も消す()
    {
        factory.Storage.ResetDeletions();
        var id = Guid.NewGuid();
        var oldUri = $"storage://knowledge-normalized/{id:N}/document.md";   // 取り込み経路のキー
        var newUri = $"storage://knowledge-normalized/documents/{id:D}/body.md"; // 本文直接受け入れのキー

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var doc = Document.CreateNormalized(id, "経路が切り替わった文書", oldUri,
                new Dictionary<string, string> { ["confidentiality"] = "internal" });
            doc.SetMarkdownUri(newUri);   // 版 2 が新しいキーを指し、版 1 は古いキーのまま残る
            db.Documents.Add(doc);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var resp = await Client().DeleteAsync($"/documents/{id}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Deleted().Should().Contain(newUri, "現行の本文");
        Deleted().Should().Contain(oldUri,
            "版スナップショットを集めないと、過去に指していた本文が実体として残り続ける");
    }

    // FR-12 / ADR-0057 決定 1: 図表資産も消す（台帳に `AssetUris` を持たせた目的そのもの）。
    [Fact]
    public async Task 文書削除は図表資産の実体も消す()
    {
        factory.Storage.ResetDeletions();
        var id = Guid.NewGuid();
        var bodyUri = $"storage://knowledge-normalized/{id:N}/document.md";
        var figure1 = $"storage://knowledge-normalized/{id:N}/assets/fig-1.png";
        var figure2 = $"storage://knowledge-normalized/{id:N}/assets/fig-2.png";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            db.Documents.Add(Document.CreateNormalized(id, "図のある文書", bodyUri,
                new Dictionary<string, string> { ["confidentiality"] = "internal" },
                assetUris: [figure1, figure2]));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await Client().DeleteAsync($"/documents/{id}", TestContext.Current.CancellationToken);

        Deleted().Should().Contain([figure1, figure2],
            "資産を台帳へ持たせないと、図表は辿れず削除が届かない");
    }

    // 台帳の値が storage:// でなければ（外部 URL 等）触らない —— 本サービスの持ち物ではない。
    [Fact]
    public async Task storage以外の参照は削除対象にしない()
    {
        factory.Storage.ResetDeletions();
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var doc = Document.CreateWithBody(id, "外部原本つき",
                $"storage://knowledge-normalized/documents/{id:D}/body.md",
                originalUri: "https://example.com/original.docx", contentType: "text/markdown",
                attributes: new Dictionary<string, string> { ["confidentiality"] = "internal" });
            db.Documents.Add(doc);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await Client().DeleteAsync($"/documents/{id}", TestContext.Current.CancellationToken);

        Deleted().Should().NotContain("https://example.com/original.docx");
    }

    // FR-19 / SC-19: 個人資料の完全削除は本文の実体を消す（SC-19 の「復元できません」の裏付け）。
    [Fact]
    public async Task 個人資料の完全削除は本文の実体を消す()
    {
        factory.Storage.ResetDeletions();
        var owner = $"del-{Guid.NewGuid():N}"[..20];
        var session = factory.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, owner);

        var created = await session.PostAsJsonAsync("/private-notes/",
            new { title = "消える資料" }, TestContext.Current.CancellationToken);
        var note = (await created.Content.ReadFromJsonAsync<PrivateNoteDto>(TestContext.Current.CancellationToken))!;

        // 本文を持たせる（作成経路は本文を書かないため、台帳へ直接置く）。
        var bodyUri = StorageUri.Build(RecordingObjectStorageClient.Bucket,
            DocumentBodyIntake.StorageKey(note.Id));
        await factory.Storage.PutTextAsync(DocumentBodyIntake.StorageKey(note.Id), "個人の本文",
            DocumentBodyIntake.ContentType, TestContext.Current.CancellationToken);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var doc = await db.Documents.FindAsync([note.Id], TestContext.Current.CancellationToken);
            doc!.SetMarkdownUri(bodyUri);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await session.DeleteAsync($"/private-notes/{note.Id}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var purge = await session.PostAsJsonAsync("/private-notes/purge",
            new { ids = new[] { note.Id } }, TestContext.Current.CancellationToken);

        purge.StatusCode.Should().Be(HttpStatusCode.OK);
        Deleted().Should().Contain(bodyUri,
            "SC-19 は「いかなる方法でも復元できません」と言い切る画面である");
    }

    // FR-19 / ADR-0037 決定 5: 90 日経過の自動物理削除も本文の実体を消す。
    [Fact]
    public async Task 自動物理削除は本文の実体を消す()
    {
        factory.Storage.ResetDeletions();
        var owner = $"auto-{Guid.NewGuid():N}"[..20];
        var now = DateTimeOffset.UtcNow;
        var (id, bodyUri) = await SeedDeletedNoteAsync(owner, "自動削除の対象", now.AddDays(-91));

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PrivateNoteMaintenanceService>()
                .RunAsync(now, TestContext.Current.CancellationToken);
        }

        Deleted().Should().Contain(bodyUri);
        await AssertDocumentGoneAsync(id);
    }

    // 🔴 **fail-closed**: オブジェクトを消せなければ DB 行を消さない（[[IADR-0296]] 決定 3）。
    // 「消したのに実体が残り、参照も失われた」を作らないことが要点である。
    [Fact]
    public async Task オブジェクトを消せなければ文書行は残る()
    {
        factory.Storage.ResetDeletions();
        var doc = await CreateWithBodyAsync("削除に失敗する文書", "残るべき本文");
        factory.Storage.FailDeleteWhen = _ => true;
        try
        {
            var act = async () => await Client().DeleteAsync($"/documents/{doc.Id}",
                TestContext.Current.CancellationToken);
            // ホストは例外を伝播させる（テストサーバは既定で再スロー）。
            await act.Should().ThrowAsync<Exception>();
        }
        finally
        {
            factory.Storage.FailDeleteWhen = null;
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        (await db.Documents.AnyAsync(d => d.Id == doc.Id, TestContext.Current.CancellationToken))
            .Should().BeTrue("実体を消せていないのに台帳を消すと、残留を誰も観測できなくなる");
    }

    // 定期処理は**文書ごとに隔離**する。1 件の失敗で周期全体を止めない（[[IADR-0296]] 決定 3）。
    [Fact]
    public async Task 定期処理は1件の失敗で他の資料の削除を止めない()
    {
        factory.Storage.ResetDeletions();
        var owner = $"iso-{Guid.NewGuid():N}"[..20];
        var now = DateTimeOffset.UtcNow;
        var (badId, badUri) = await SeedDeletedNoteAsync(owner, "消せない資料", now.AddDays(-91));
        var (goodId, goodUri) = await SeedDeletedNoteAsync(owner, "消せる資料", now.AddDays(-91));

        factory.Storage.FailDeleteWhen = uri => uri == badUri;
        try
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PrivateNoteMaintenanceService>()
                .RunAsync(now, TestContext.Current.CancellationToken);
        }
        finally
        {
            factory.Storage.FailDeleteWhen = null;
        }

        Deleted().Should().Contain(goodUri, "1 件の失敗で周期全体を止めてはならない");
        await AssertDocumentGoneAsync(goodId);

        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<DocumentDbContext>();
        (await db.Documents.AnyAsync(d => d.Id == badId, TestContext.Current.CancellationToken))
            .Should().BeTrue("消せなかった資料は行を残し、次周期で再試行する");
    }

    // 論理削除（90 日の猶予つき）では実体を消さない —— 復元できる状態を壊さない。
    [Fact]
    public async Task 論理削除では実体を消さない()
    {
        factory.Storage.ResetDeletions();
        var owner = $"soft-{Guid.NewGuid():N}"[..20];
        var session = factory.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, owner);
        var created = await session.PostAsJsonAsync("/private-notes/",
            new { title = "論理削除だけの資料" }, TestContext.Current.CancellationToken);
        var note = (await created.Content.ReadFromJsonAsync<PrivateNoteDto>(TestContext.Current.CancellationToken))!;

        (await session.DeleteAsync($"/private-notes/{note.Id}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Deleted().Should().BeEmpty("論理削除は 90 日間復元できる。実体を消すと復元が嘘になる");
    }

    // 削除済み・本文つきの個人資料を 1 件仕込む（`deletedAt` を遡らせて purge 期限を超えさせる）。
    private async Task<(Guid Id, string BodyUri)> SeedDeletedNoteAsync(
        string owner, string title, DateTimeOffset deletedAt)
    {
        var session = factory.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, owner);
        var created = await session.PostAsJsonAsync("/private-notes/", new { title });
        var note = (await created.Content.ReadFromJsonAsync<PrivateNoteDto>())!;

        var key = DocumentBodyIntake.StorageKey(note.Id);
        var bodyUri = await factory.Storage.PutTextAsync(key, $"{title} の本文",
            DocumentBodyIntake.ContentType);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = await db.Documents.FindAsync([note.Id]);
        doc!.SetMarkdownUri(bodyUri);
        var ledger = await db.PrivateNotes.FindAsync([note.Id]);
        ledger!.SoftDelete(deletedAt);
        await db.SaveChangesAsync();
        return (note.Id, bodyUri);
    }

    private async Task AssertDocumentGoneAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        (await db.Documents.AnyAsync(d => d.Id == id, TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }
}
