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

// FR-06, UC-03, SC-05, IADR-0597 (#2103): 同じ版を前提にした 2 つの更新の一方だけが通り、他方は 409 `version_conflict` になる（実 PostgreSQL）。
//
// **単体テスト（EF InMemory）では踏めない経路である。** 窓は「期待版の比較（読んだ時点）と保存の間」にあり、
// 塞ぐのは PostgreSQL の行の UPDATE（`WHERE "Version" = @読んだ版`）と版テーブルの一意索引である。
// InMemory は一意索引を強制せず（`TagDictionaryUniquenessTests` の実測）、並行するトランザクションも持たない。
//
// **窓を決定的に開く。** 2 つの要求を `Task.WhenAll` で同時に送るだけでは、片方が読む前に他方が保存を終えることが多く、
// その場合は期待版の比較（修正前から在る）が 409 を返して**修正を外しても緑**になる。そこで、先の要求が
// 保存する直前（`SavingChangesAsync`）に、同じ版を前提にしたもう 1 つの要求を走らせて確定させる ——
// 先の要求は比較を通った後で版を追い越される。修正前はここで版テーブルの一意索引違反の 500 になる（変異で確かめた）。
// #2112 / [[IADR-0598]]: 作成口が `doc_scope=organization` を補うため、更新でも同値を同送する（SC-05 の形。属性は全置換）。
[Trait("Category", "Integration")]
public sealed class DocumentConcurrentUpdateTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InterleaveBeforeSave _interleave = new();
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

    // 受け入れ基準 1（Update）: 同じ版 1 を前提にした 2 つの PUT。追い越した側が 200、追い越された側が 409。
    // 追い越す側は 2 回続けて書き（版 1 → 2 → 3）、409 の `currentVersion` が**読み直した版**（3）であって、
    // 負けた側が手元で進めた版（2）ではないことも見る。
    [Fact]
    public async Task Update_OvertakenAfterVersionCheck_LoserGets409_AndWinnerIsKept()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var id = await CreateAsync("並行更新");

        HttpResponseMessage? winner = null;
        _interleave.Arm(async () =>
        {
            using (var first = await _client.PutAsJsonAsync($"/documents/{id}",
                       new { title = "勝った側（途中）", attributes = new { confidentiality = "internal", doc_scope = "organization" }, tags = Array.Empty<string>(), expectedVersion = 1 }))
                first.StatusCode.Should().Be(HttpStatusCode.OK);
            winner = await _client.PutAsJsonAsync($"/documents/{id}",
                new { title = "勝った側", attributes = new { confidentiality = "internal", doc_scope = "organization" }, tags = Array.Empty<string>(), expectedVersion = 2 });
        });

        using var loser = await _client.PutAsJsonAsync($"/documents/{id}",
            new { title = "負けた側", attributes = new { confidentiality = "internal", doc_scope = "organization" }, tags = Array.Empty<string>(), expectedVersion = 1 }, Ct);

        _interleave.Fired.Should().BeTrue("先の要求の保存の直前に、もう 1 つの要求を確定させた");
        using (winner)
        {
            winner!.StatusCode.Should().Be(HttpStatusCode.OK);
            await AssertConflictAsync(loser, expectedVersion: 1, currentVersion: 3);
        }
        await AssertKeptAsync(id, "勝った側", version: 3);
    }

    // 受け入れ基準 1（UpdateMetadata）: 同じ版 1 を前提にした 2 つの PATCH。
    [Fact]
    public async Task UpdateMetadata_OvertakenAfterVersionCheck_LoserGets409_AndWinnerIsKept()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var id = await CreateAsync("並行メタデータ更新");

        HttpResponseMessage? winner = null;
        _interleave.Arm(async () => winner = await _client.PatchAsJsonAsync($"/documents/{id}/metadata",
            new { attributes = new { confidentiality = "internal", doc_scope = "organization", dept = "winner" }, tags = Array.Empty<string>(), expectedVersion = 1 }));

        using var loser = await _client.PatchAsJsonAsync($"/documents/{id}/metadata",
            new { attributes = new { confidentiality = "internal", doc_scope = "organization", dept = "loser" }, tags = Array.Empty<string>(), expectedVersion = 1 }, Ct);

        _interleave.Fired.Should().BeTrue();
        using (winner)
        {
            winner!.StatusCode.Should().Be(HttpStatusCode.OK);
            await AssertConflictAsync(loser, expectedVersion: 1, currentVersion: 2);
        }
        var stored = await AssertKeptAsync(id, "並行メタデータ更新", version: 2);
        stored.Attributes["dept"].Should().Be("winner", "負けた側の属性で黙って上書きしない");
    }

    // 期待版を送らない更新も、読んだ後に版を追い越されたら黙って上書きしない（409。`expectedVersion` は null）。
    [Fact]
    public async Task Update_WithoutExpectedVersion_Overtaken_Gets409_NotSilentOverwrite()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var id = await CreateAsync("期待版なし");

        HttpResponseMessage? winner = null;
        _interleave.Arm(async () => winner = await _client.PutAsJsonAsync($"/documents/{id}",
            new { title = "勝った側", attributes = new { confidentiality = "internal", doc_scope = "organization" }, tags = Array.Empty<string>() }));

        using var loser = await _client.PutAsJsonAsync($"/documents/{id}",
            new { title = "負けた側", attributes = new { confidentiality = "internal", doc_scope = "organization" }, tags = Array.Empty<string>() }, Ct);

        _interleave.Fired.Should().BeTrue();
        using (winner)
        {
            winner!.StatusCode.Should().Be(HttpStatusCode.OK);
            await AssertConflictAsync(loser, expectedVersion: null, currentVersion: 2);
        }
        await AssertKeptAsync(id, "勝った側", version: 2);
    }

    // 受け入れ基準 2（補助・非決定的）: 同じ版を前提にした更新を本当に同時に送っても、500 は 1 件も出ず、
    // 通った件数だけ版が進む（通らなかった要求の版は残らない）。窓に入るかどうかは実行ごとに変わるので、
    // 修正を外したときに必ず赤になるのは上の 3 件である。
    [Fact]
    public async Task Update_TrulyConcurrent_OnlyOkOrConflict_AndVersionsMatchSuccesses()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var id = await CreateAsync("同時送信");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            _client.PutAsJsonAsync($"/documents/{id}",
                new { title = $"同時-{i}", attributes = new { confidentiality = "internal", doc_scope = "organization" }, tags = Array.Empty<string>(), expectedVersion = 1 })));
        try
        {
            responses.Should().OnlyContain(
                r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict,
                "版の競合は 409 が契約であり、500 にしてはならない");
            responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, "版 1 を前提にした更新は 1 件だけが通る");

            var versions = await _client.GetFromJsonAsync<List<VersionResponse>>($"/documents/{id}/versions", Ct);
            versions!.Count.Should().Be(2, "作成の版と、通った 1 件の版だけが残る");
        }
        finally
        {
            foreach (var r in responses) r.Dispose();
        }
    }

    // 並行トークンそのもの: **版を進めない保存**（露出トグル `SetExposureAttributes`）も、読んだ後に別の保存が版を進めていれば負ける。
    // この形は版の行を足さないので版テーブルの一意索引では捕まらず、トークンが無いと勝った側の属性を黙って上書きする。
    [Fact]
    public async Task SaveWithoutVersionBump_AfterVersionAdvanced_LosesWithConcurrencyException()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var id = await CreateAsync("版を進めない保存");

        await using var staleScope = _factory.Services.CreateAsyncScope();
        var stale = staleScope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var staleDoc = await stale.Documents.SingleAsync(d => d.Id == id, Ct); // 版 1 を読む

        using (var advanced = await _client.PutAsJsonAsync($"/documents/{id}",
                   new { title = "後から確定した編集", attributes = new { confidentiality = "internal", doc_scope = "organization", dept = "kept" }, tags = Array.Empty<string>(), expectedVersion = 1 }, Ct))
            advanced.StatusCode.Should().Be(HttpStatusCode.OK);

        staleDoc.SetExposureAttributes(includeInSearch: true, includeInGraph: true, includeInAi: true);
        var save = () => stale.SaveChangesAsync(Ct);
        await save.Should().ThrowAsync<DbUpdateConcurrencyException>("読んだ版 1 は既に 2 へ進んでいる");

        var stored = await _client.GetFromJsonAsync<DocResponse>($"/documents/{id}", Ct);
        stored!.Version.Should().Be(2);
        stored.Attributes["dept"].Should().Be("kept", "古い版から作った属性で黙って上書きしない");
    }

    // IADR-0597 決定 4（#2103 のレビュー対応）: 削除が読んだ後に版を追い越されたら、黙って追い越した更新ごと消さず 409。
    // 文書は勝った側の内容で残り、削除をやり直せば完了する（本文の実体は先に消す順のまま）。
    [Fact]
    public async Task Delete_OvertakenByUpdate_Gets409_AndDocumentIsKept_ThenRetrySucceeds()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var id = await CreateAsync("並行削除");

        HttpResponseMessage? winner = null;
        _interleave.Arm(async () => winner = await _client.PutAsJsonAsync($"/documents/{id}",
            new { title = "勝った側", attributes = new { confidentiality = "internal", doc_scope = "organization" }, tags = Array.Empty<string>(), expectedVersion = 1 }));

        using var loser = await _client.DeleteAsync($"/documents/{id}", Ct);

        _interleave.Fired.Should().BeTrue();
        using (winner)
        {
            winner!.StatusCode.Should().Be(HttpStatusCode.OK);
            await AssertConflictAsync(loser, expectedVersion: null, currentVersion: 2);
        }
        await AssertKeptAsync(id, "勝った側", version: 2);

        using var retry = await _client.DeleteAsync($"/documents/{id}", Ct);
        retry.StatusCode.Should().Be(HttpStatusCode.NoContent, "やり直した削除は追い越されていないので通る");
    }

    // IADR-0597 決定 4（#2103 のレビュー対応）: 露出トグル（版を進めない）が、読んだ後に別の保存に版を進められたら 409。
    // 古い属性で黙って上書きせず、台帳の露出も巻き戻る。
    [Fact]
    public async Task SetExposure_OvertakenByVersionBump_Gets409_AndWinnerAttributesKept()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        using var create = await _client.PostAsJsonAsync("/private-notes", new { title = $"露出-{Guid.NewGuid():N}" }, Ct);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await create.Content.ReadFromJsonAsync<NoteResponse>(Ct))!.Id;

        // 勝つ側: 版を進める別の保存（同期の push と同じ `Document.Update`）。属性に印を足して、上書きされないことを見る。
        _interleave.Arm(async () =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var doc = await db.Documents.SingleAsync(d => d.Id == id);
            doc.Update(doc.Title, new Dictionary<string, string>(doc.Attributes) { ["marker"] = "kept" }, doc.Tags.ToList(), "sync-edit");
            await db.SaveChangesAsync();
        });

        using var loser = await _client.PutAsJsonAsync($"/private-notes/{id}/exposure",
            new { includeInSearch = true, includeInGraph = true, includeInAi = true }, Ct);

        _interleave.Fired.Should().BeTrue();
        await AssertConflictAsync(loser, expectedVersion: null, currentVersion: 2);

        await using var verify = _factory.Services.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var stored = await vdb.Documents.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        stored.Version.Should().Be(2);
        stored.Attributes["marker"].Should().Be("kept", "古い属性から作った露出の属性で黙って上書きしない");
        var note = await vdb.PrivateNotes.AsNoTracking().SingleAsync(n => n.DocumentId == id, Ct);
        note.IncludeInSearch.Should().BeFalse("負けた保存は台帳の露出も巻き戻す");
    }

    private async Task<Guid> CreateAsync(string title)
    {
        using var create = await _client.PostAsJsonAsync("/documents",
            new { title, attributes = new { confidentiality = "internal", doc_scope = "organization" }, tags = Array.Empty<string>() }, Ct);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = await create.Content.ReadFromJsonAsync<DocResponse>(Ct);
        doc!.Version.Should().Be(1);
        return doc.Id;
    }

    private static async Task AssertConflictAsync(HttpResponseMessage loser, int? expectedVersion, int currentVersion)
    {
        loser.StatusCode.Should().Be(HttpStatusCode.Conflict, "追い越された保存は 500 ではなく 409");
        var body = await loser.Content.ReadFromJsonAsync<ConflictResponse>(Ct);
        body!.Error.Should().Be("version_conflict");
        body.ExpectedVersion.Should().Be(expectedVersion);
        body.CurrentVersion.Should().Be(currentVersion, "現在版は勝った側の版を読み直して返す");
    }

    // 黙った上書きが無い: 文書は勝った側の内容と版を持ち、版の履歴に負けた側の行が無い。
    private async Task<DocResponse> AssertKeptAsync(Guid id, string title, int version)
    {
        var stored = await _client.GetFromJsonAsync<DocResponse>($"/documents/{id}", Ct);
        stored!.Title.Should().Be(title, "負けた側の内容で黙って上書きしない");
        stored.Version.Should().Be(version);

        var versions = await _client.GetFromJsonAsync<List<VersionResponse>>($"/documents/{id}/versions", Ct);
        versions!.Select(v => v.Version).Should().BeEquivalentTo(Enumerable.Range(1, version),
            "負けた側の版の行は巻き戻っている（勝った側の版だけが連番で残る）");
        return stored;
    }

    // 文書を書き換える最初の保存の直前に 1 度だけ、別の要求を走らせて確定させる（その要求自身の保存では発火しない）。
    private sealed class InterleaveBeforeSave : SaveChangesInterceptor
    {
        private Func<Task>? _competitor;
        public bool Fired { get; private set; }

        public void Arm(Func<Task> competitor)
        {
            Fired = false;
            _competitor = competitor;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var modifiesDocument = eventData.Context?.ChangeTracker.Entries<Document>()
                .Any(e => e.State is EntityState.Modified or EntityState.Deleted) == true;
            if (modifiesDocument && Interlocked.Exchange(ref _competitor, null) is { } competitor)
            {
                await competitor();
                Fired = true;
            }
            return result;
        }
    }

    private sealed record DocResponse(Guid Id, string Title, string Status, int Version,
        Dictionary<string, string> Attributes);

    private sealed record VersionResponse(Guid DocumentId, int Version, string Title);

    private sealed record NoteResponse(Guid Id);

    private sealed record ConflictResponse(string Error, int? ExpectedVersion, int CurrentVersion);
}
