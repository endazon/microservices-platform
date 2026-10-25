using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using global::DocumentService.Domain;
using global::DocumentService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.DocumentService;

// 🔴 FR-02, FR-09, IADR-0637 決定 6 (#2150 段 1b): **多数の文書を 1 回で確定する口（タグの改名・再発行）は、発行の連番（並行トークン）に
// 負けたら全体を読み直してやり直し、上限を超えたら何も確定せずに 409 を返す**ことを実 PostgreSQL で測る。
//
// InMemory はトランザクションを持たないので、「何も確定しない」（改名した辞書の名前が巻き戻る）はここでしか確かめられない。
// 競合の差し込み: 文書行を書く保存の直前に、別の接続で同じ文書の連番を 1 進めて確定させる（`SET "EventSequence" = "EventSequence" + 1`）。
// 差し込みは `ExecuteSql` なので保存の割り込みを再び呼ばない。回数を指定して、上限まで負け続ける場合を作る。
[Trait("Category", "Integration")]
public sealed class DocumentBatchCommitRetryTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // `DocumentVersionConflict.MaxBatchCommitAttempts`（internal）と同じ値。変えたら両方を直す。
    private const int MaxAttempts = 3;

    private readonly BumpSequenceBeforeSave _interleave = new();
    private DocumentServiceFactory _root = null!;
    private WebApplicationFactory<global::DocumentService.DocumentServiceTestMarker> _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return;
        _root = new DocumentServiceFactory(postgres, rabbit);
        _factory = _root.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.ConfigureDbContext<DocumentDbContext>(o => o.AddInterceptors(_interleave))));
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_root is not null) await _root.DisposeAsync();
    }

    [Fact]
    public async Task 改名は1回負けても読み直して確定する()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var (tagId, docId) = await TaggedDocumentAsync();
        var before = await SequenceAsync(docId);
        var renamed = $"seq-renamed-{Guid.NewGuid():N}"[..28];

        _interleave.Arm(docId, times: 1);
        using var response = await _client.PutAsJsonAsync($"/tags/{tagId}", new { name = renamed }, Ct);

        _interleave.Fired.Should().Be(1, "前提: 1 回目の確定の直前に連番を進めた");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "1 回負けても上限の内なので読み直して確定する");
        (await TagNameAsync(tagId)).Should().Be(renamed);
        (await SequenceAsync(docId)).Should().Be(before + 2, "差し込んだ 1 と、やり直して確定した改名の再発行の 1");
    }

    [Fact]
    public async Task 改名は上限まで負けたら何も確定せず409を返し_同じ要求の再送で再開できる()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var (tagId, docId) = await TaggedDocumentAsync();
        var original = await TagNameAsync(tagId);
        var before = await SequenceAsync(docId);
        var renamed = $"seq-renamed-{Guid.NewGuid():N}"[..28];

        _interleave.Arm(docId, times: MaxAttempts);
        using (var lost = await _client.PutAsJsonAsync($"/tags/{tagId}", new { name = renamed }, Ct))
        {
            _interleave.Fired.Should().Be(MaxAttempts, "前提: 毎回の確定の直前に連番を進めた");
            lost.StatusCode.Should().Be(HttpStatusCode.Conflict, "上限を超えた負けは 500 にしない");
            var body = await lost.Content.ReadFromJsonAsync<ConflictBody>(Ct);
            body!.Error.Should().Be("version_conflict");
            body.Message.Should().NotBeNullOrWhiteSpace("SC-09 は 409 の本文の message をそのまま出す");
        }
        (await TagNameAsync(tagId)).Should().Be(original, "改名と再発行は 1 トランザクションで巻き戻る（辞書だけが改名された中間の状態を残さない）");
        (await SequenceAsync(docId)).Should().Be(before + MaxAttempts, "進んだのは差し込んだ側だけ");

        using var resent = await _client.PutAsJsonAsync($"/tags/{tagId}", new { name = renamed }, Ct);
        resent.StatusCode.Should().Be(HttpStatusCode.OK, "未処理の範囲は要求の全体なので、同じ要求の再送で再開できる");
        (await TagNameAsync(tagId)).Should().Be(renamed);
    }

    [Fact]
    public async Task 再発行は上限まで負けたら負けたページの先頭のカーソルを返し_そこから再開できる()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var (_, docId) = await TaggedDocumentAsync();
        var before = await SequenceAsync(docId);
        var request = new { dryRun = false, ids = new[] { docId }, limit = 10, requestedBy = "integration", reason = "IADR-0637 段 1b" };

        _interleave.Arm(docId, times: MaxAttempts);
        string? resumeCursor;
        using (var lost = await _client.PostAsJsonAsync("/documents/republish-updated", request, Ct))
        {
            _interleave.Fired.Should().Be(MaxAttempts);
            lost.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await lost.Content.ReadFromJsonAsync<RepublishConflictBody>(Ct);
            body!.Error.Should().Be("version_conflict");
            resumeCursor = body.ResumeCursor;
            resumeCursor.Should().BeNull("負けたのは先頭のページなので、再開のカーソルは要求と同じく無い");
        }
        (await SequenceAsync(docId)).Should().Be(before + MaxAttempts, "負けたページの発行は確定していない");

        using var resumed = await _client.PostAsJsonAsync("/documents/republish-updated",
            new { request.dryRun, request.ids, request.limit, request.requestedBy, request.reason, cursor = resumeCursor }, Ct);
        resumed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resumed.Content.ReadFromJsonAsync<RepublishBody>(Ct))!.Published.Should().Be(1);
        (await SequenceAsync(docId)).Should().Be(before + MaxAttempts + 1);
    }

    private async Task<(Guid TagId, Guid DocId)> TaggedDocumentAsync()
    {
        var tagName = $"seq-{Guid.NewGuid():N}"[..20];
        using var tag = await _client.PostAsJsonAsync("/tags", new { name = tagName }, Ct);
        tag.StatusCode.Should().Be(HttpStatusCode.Created);
        var tagId = (await tag.Content.ReadFromJsonAsync<TagBody>(Ct))!.Id;

        using var create = await _client.PostAsJsonAsync("/documents",
            new { title = $"連番-{Guid.NewGuid():N}", attributes = new { confidentiality = "internal", doc_scope = "organization" }, tags = new[] { tagName } }, Ct);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var docId = (await create.Content.ReadFromJsonAsync<DocBody>(Ct))!.Id;
        return (tagId, docId);
    }

    private async Task<long> SequenceAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => d.EventSequence).SingleAsync(Ct);
    }

    private async Task<string> TagNameAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.Tags.AsNoTracking().Where(t => t.Id == id).Select(t => t.Name).SingleAsync(Ct);
    }

    // 文書行を書く保存の直前に、別の接続で対象の文書の連番を進めて確定させる（`times` 回まで）。
    private sealed class BumpSequenceBeforeSave : SaveChangesInterceptor
    {
        private Guid? _target;
        private int _remaining;
        public int Fired { get; private set; }

        public void Arm(Guid target, int times)
        {
            _target = target;
            _remaining = times;
            Fired = 0;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_target is { } id && _remaining > 0 && eventData.Context is DocumentDbContext db
                && db.ChangeTracker.Entries<Document>().Any(e => e.Entity.Id == id && e.State == EntityState.Modified))
            {
                _remaining--;
                // 別の接続で確定させる（保存側のトランザクションの外）。同じ接続で流すと同じトランザクションに載ってしまう。
                await using var other = new DocumentDbContext(new DbContextOptionsBuilder<DocumentDbContext>()
                    .UseNpgsql(db.Database.GetConnectionString()).Options);
                await other.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"Documents\" SET \"EventSequence\" = \"EventSequence\" + 1 WHERE \"Id\" = {id}", cancellationToken);
                Fired++;
            }
            return result;
        }
    }

    private sealed record TagBody(Guid Id, string Name);

    private sealed record DocBody(Guid Id);

    private sealed record ConflictBody(string Error, string? Message);

    private sealed record RepublishConflictBody(string Error, string? ResumeCursor);

    private sealed record RepublishBody(int Published, string? NextCursor);
}
