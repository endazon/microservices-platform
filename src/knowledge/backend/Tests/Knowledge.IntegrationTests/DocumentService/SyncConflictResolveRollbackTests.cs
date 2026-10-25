using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Wolverine;
using global::DocumentService.Domain;
using global::DocumentService.Features.PrivateNotes;
using global::DocumentService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.DocumentService;

// 🔴 FR-20, FR-02, IADR-0637 決定 7 (#2150 段 1c): **同期の衝突の解決（`local`）が発行の連番に負けたら、衝突の記録まで巻き戻る**ことを
// 実 PostgreSQL で測る（InMemory はトランザクションを持たないので、単体の試験では測れない）。
//
// 解決は 1 つのトランザクション（`IDocumentUpdatedPublisher.CommitAsync`）の中で 2 回保存する。
//   ① 資料の更新・台帳・衝突の記録の解決（`SaveChangesAsync`）
//   ② 唯一の発行点が発行の連番を進める保存
// 窓が開くのは ① の手前だけである。① が文書行を書いた時点で、その行のロックはトランザクションの終わりまで持たれる。
// 並行する書き手は終わりまで待たされ、その後に自分が照合で負ける（② の手前で窓を開けようとすると、ここでは待ち合いになる。実測）。
// ① で負ければ ① の保存そのものが失敗し、衝突の記録の解決も同じ保存に載っているので一緒に消える。
// 窓: 文書行を書く保存の直前に、別の接続で同じ文書の連番を 1 進めて確定させる（`ExecuteSql` なので割り込みを再び呼ばない）。
//
// 外部のトランスポートは落とし、MassTransit の Bus も起こさない（ブローカに依らない。Outbox の表は PostgreSQL にある）。本文は記憶域の偽物に置く。
[Trait("Category", "Integration")]
public sealed class SyncConflictResolveRollbackTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // `IntegrationTestAuthHandler` の主体名。個人資料の所有者として使う。
    private const string Owner = "integration-admin";

    private readonly BumpSequenceAtSave _interleave = new();
    private readonly InMemoryStorage _storage = new();
    private DocumentServiceFactory _root = null!;
    private WebApplicationFactory<global::DocumentService.DocumentServiceTestMarker> _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable) return;
        _root = new DocumentServiceFactory(postgres, rabbit);
        _factory = _root.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.ConfigureDbContext<DocumentDbContext>(o => o.AddInterceptors(_interleave));
            services.RemoveAll<IObjectStorageClient>();
            services.AddSingleton<IObjectStorageClient>(_storage);
            services.DisableAllExternalWolverineTransports();
            // 取り込みの受け口（MassTransit）の Bus を起こさない（解決は Bus を使わない。起こすとブローカへの接続を待つ）。
            foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)
                         && (d.ImplementationType?.Namespace?.StartsWith("MassTransit", StringComparison.Ordinal) ?? false)).ToList())
                services.Remove(hosted);
        }));
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DocumentDbContext>().Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_root is not null) await _root.DisposeAsync();
    }

    [Fact]
    public async Task localの解決が並行する確定に負けたら409になり_衝突の記録まで巻き戻り_選び直すとローカル本文が書かれる()
    {
        const string where = "資料の更新と衝突の解決を書く保存";
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var (documentId, conflictId) = await SeedConflictAsync();

        _interleave.Arm(documentId);
        using var response = await _client.PostAsJsonAsync($"/private-notes/conflicts/{conflictId}/resolve",
            new { resolution = "local" }, Ct);

        _interleave.Fired.Should().BeTrue($"前提: {where}の直前に、同じ文書の連番を進めた");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, $"{where}で負けても 500 にしない");
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("version_conflict");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        (await db.SyncConflicts.AsNoTracking().SingleAsync(c => c.Id == conflictId, Ct)).IsResolved
            .Should().BeFalse($"{where}で負けたら、衝突の解決も巻き戻る（選び直せる）");
        (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId, Ct)).Version
            .Should().Be(1, "負けた解決の版は残らない");

        // 選び直せる: 競合の窓の無い再送は通り、退避したローカル本文が資料へ書かれる（負けた解決が退避を消していない）。
        using var retry = await _client.PostAsJsonAsync($"/private-notes/conflicts/{conflictId}/resolve",
            new { resolution = "local" }, Ct);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var after = _factory.Services.CreateAsyncScope();
        var stored = await after.ServiceProvider.GetRequiredService<DocumentDbContext>().Documents.AsNoTracking()
            .SingleAsync(d => d.Id == documentId, Ct);
        _storage.Texts[stored.MarkdownUri!].Should().Be("ローカル版", "負けた 1 回目が退避したローカル本文を消していれば、ここは空になる");
    }

    // 露出 ON の個人資料（解決の発行が唯一の発行点を通り、② の保存が起こる）と、その未解決の衝突を台帳へ置く。
    private async Task<(Guid DocumentId, Guid ConflictId)> SeedConflictAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var bodyUri = $"mem://{DocumentBodyIntake.StorageKey(id)}";
        _storage.Texts[bodyUri] = "サーバ版";
        var doc = Document.CreateWithBody(id, $"衝突-{id:N}", bodyUri, null, "text/markdown",
            PrivateNoteEndpoints.PrivateNoteDefaults(Owner));
        doc.SetExposureAttributes(includeInSearch: true, includeInGraph: false, includeInAi: false);
        var note = PrivateNote.Create(id, Owner, $"conflict/{id:N}.md", 12, "hash", now);
        note.SetExposure(true, false, false, now);
        var conflict = SyncConflict.Detect(id, Owner, Guid.NewGuid(), localBaseVersion: 1, serverVersion: 1, now);
        var localUri = $"mem://conflicts/{conflict.Id:N}.md";
        _storage.Texts[localUri] = "ローカル版";
        conflict.RecordLocalContent(localUri);
        db.Documents.Add(doc);
        db.PrivateNotes.Add(note);
        db.SyncConflicts.Add(conflict);
        await db.SaveChangesAsync(Ct);
        return (id, conflict.Id);
    }

    // 文書行を書く保存の直前に、別の接続で対象の文書の連番を進めて確定させる（1 回だけ）。
    private sealed class BumpSequenceAtSave : SaveChangesInterceptor
    {
        private Guid? _target;
        public bool Fired { get; private set; }

        public void Arm(Guid target)
        {
            _target = target;
            Fired = false;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_target is { } id && eventData.Context is DocumentDbContext db
                && db.ChangeTracker.Entries<Document>().Any(e => e.Entity.Id == id && e.State == EntityState.Modified))
            {
                _target = null;
                await using var other = new DocumentDbContext(new DbContextOptionsBuilder<DocumentDbContext>()
                    .UseNpgsql(db.Database.GetConnectionString()).Options);
                await other.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"Documents\" SET \"EventSequence\" = \"EventSequence\" + 1 WHERE \"Id\" = {id}", cancellationToken);
                Fired = true;
            }
            return result;
        }
    }

    // 本文の記憶域の偽物（`mem://` を鍵とする）。解決が読む・書く・消す分だけを持つ。
    private sealed class InMemoryStorage : IObjectStorageClient
    {
        public ConcurrentDictionary<string, string> Texts { get; } = new();

        public Task<string> PutTextAsync(string key, string text, string contentType, CancellationToken ct = default)
        {
            var uri = $"mem://{key}";
            Texts[uri] = text;
            return Task.FromResult(uri);
        }

        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType, CancellationToken ct = default)
            => PutTextAsync(key, Convert.ToBase64String(bytes), contentType, ct);

        public Task<string> GetTextAsync(string uri, CancellationToken ct = default)
            => Texts.TryGetValue(uri, out var text) ? Task.FromResult(text) : throw new FileNotFoundException(uri);

        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default)
            => Task.FromResult(Convert.FromBase64String(Texts[uri]));

        public bool CanResolve(string? uri) => uri is not null && uri.StartsWith("mem://", StringComparison.Ordinal);

        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => throw new NotSupportedException();

        public Task DeleteAsync(string uri, CancellationToken ct = default)
        {
            Texts.TryRemove(uri, out _);
            return Task.CompletedTask;
        }
    }
}
