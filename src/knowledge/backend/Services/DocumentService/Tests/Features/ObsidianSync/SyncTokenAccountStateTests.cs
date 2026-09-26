using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.ExternalServices;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using DocumentService.Features.ObsidianSync.Push;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.ObsidianSync;

// FR-20, SC-17, UC-11, NFR-14, 計画 ADR-0114 決定 1・2・3, ADR-0037 フォローアップ 3,
// ADR-0096 フォローアップ 2, [[IADR-0270]] 決定 3, [[IADR-0474]] (#1532):
// **アカウントを無効化したら、その利用者の同期トークンは次の同期要求から 401 になる。**
//
// 🔴 陰性（401）は必ず陽性対照（同じトークン・同じ要求で 200）と対にする ——
// 「常に 401 を返す実装」でも陰性だけは緑になる。
// 🔴 アカウント状態は `StubOwnerAccountDirectory` で宣言する（既定は Enabled）。
// 本番の縮退（未構成なら通さない）は下の別クラスが、スタブへ差し替えずに測る。
[Trait("TestKind", "Integration")]
public class SyncTokenAccountStateTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient SessionAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    private HttpClient PluginWith(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string NewUser(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<string> IssueTokenAsync(string user)
    {
        var resp = await SessionAs(user).PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "laptop" }, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(Ct))!.Token;
    }

    private static object PushBody(string path, string content, Guid? noteId = null, int? baseVersion = null)
        => new
        {
            noteId,
            baseVersion,
            vaultPath = path,
            title = "メモ",
            edits = new[] { new { content } },
        };

    private static Task<HttpResponseMessage> ManifestAsync(HttpClient plugin)
        => plugin.GetAsync("/private-notes/sync/manifest", Ct);

    // ── 決定 1: 無効化の後の最初の要求から 401 ───────────────────────────────

    // T-AS-01（ADR-0114 決定 1 の受け入れ基準）: 発行 → 無効化 → 同じトークンで同期 → 401。
    // 無効化の前の同じ要求は 200（陽性対照）。
    [Fact]
    public async Task 無効化した利用者の同期トークンは次の同期要求から401になる()
    {
        var user = NewUser("dis");
        var plugin = PluginWith(await IssueTokenAsync(user));

        (await ManifestAsync(plugin)).StatusCode.Should().Be(HttpStatusCode.OK, "無効化の前は通る（陽性対照）");

        factory.OwnerAccounts.Declare(user, OwnerAccountState.Disabled);

        (await ManifestAsync(plugin)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "無効化の後の最初の要求から拒否する（期限切れを待たない）");
    }

    // T-AS-02: 5 端点（manifest / push / pull / delete / move）のどれも無効化の後は 401。
    // 🔴 端点ごとに門を足すと 1 つ書き忘れる —— 門は `ResolveDeviceAsync` の 1 か所にあることを、
    // 5 端点を並べて固定する。資料は無効化の前に作る（有効な間の push は 201 ＝陽性対照）。
    [Fact]
    public async Task 無効化した利用者の同期トークンは5端点すべてで401になる()
    {
        var user = NewUser("dis5");
        var plugin = PluginWith(await IssueTokenAsync(user));
        var created = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("notes/a.md", "本文"), Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created, "有効な間の push は通る（陽性対照）");
        var note = (await created.Content.ReadFromJsonAsync<PushNoteResponse>(Ct))!;

        factory.OwnerAccounts.Declare(user, OwnerAccountState.Disabled);

        (await ManifestAsync(plugin)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "manifest");
        (await plugin.PostAsJsonAsync("/private-notes/sync/notes",
                PushBody("notes/a.md", "更新", note.NoteId, note.Version), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "push（更新）");
        (await plugin.PostAsJsonAsync("/private-notes/sync/notes", PushBody("notes/b.md", "新規"), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "push（新規）");
        (await plugin.GetAsync($"/private-notes/sync/notes/{note.NoteId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "pull");
        (await plugin.PostAsync($"/private-notes/sync/notes/{note.NoteId}/delete", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "delete");
        (await plugin.PostAsJsonAsync($"/private-notes/sync/notes/{note.NoteId}/move",
                new { vaultPath = "notes/c.md", version = note.Version }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "move");

        // 拒否された要求は何も書いていない（資料は無効化の前のまま残る）。
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var stored = await db.PrivateNotes.FindAsync([note.NoteId], Ct);
        stored!.VaultPath.Should().Be("notes/a.md");
        stored.IsDeleted.Should().BeFalse();
    }

    // ── 決定 3: 再有効化したとき端末が使えるようになるか ──────────────────────────

    // T-AS-03（[[IADR-0474]] 決定 3）: **案 A の帰結として、再有効化すると未失効・期限内のトークンは再び通る。**
    // 無効化は端末を失効させていない（`RevokedAt` は立たない）ことも併せて固定する ——
    // これが崩れる（案 B へ寄る）なら IADR と画面仕様書の記述を改めなければならない。
    [Fact]
    public async Task 再有効化すると未失効で期限内の同期トークンは再び通る()
    {
        var user = NewUser("reen");
        var plugin = PluginWith(await IssueTokenAsync(user));

        factory.OwnerAccounts.Declare(user, OwnerAccountState.Disabled);
        (await ManifestAsync(plugin)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        factory.OwnerAccounts.Declare(user, OwnerAccountState.Enabled);
        (await ManifestAsync(plugin)).StatusCode.Should().Be(HttpStatusCode.OK,
            "端末は失効していないので、再有効化で同じトークンが再び通る");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        db.SyncDevices.Where(d => d.OwnerId == user).Should().OnlyContain(d => d.RevokedAt == null,
            "無効化は端末の記録（失効日時）を書き換えない");
    }

    // ── 決定 2: 判定できないときは通さない ─────────────────────────────────

    // T-AS-04（ADR-0114 決定 2）: 名簿を引けない・時間切れ（Unknown）なら 401。
    [Fact]
    public async Task 有効か判定できない利用者の同期トークンは401になる()
    {
        var user = NewUser("unk");
        var plugin = PluginWith(await IssueTokenAsync(user));
        (await ManifestAsync(plugin)).StatusCode.Should().Be(HttpStatusCode.OK, "陽性対照");

        factory.OwnerAccounts.Declare(user, OwnerAccountState.Unknown);

        (await ManifestAsync(plugin)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "判定できないときに通すと、名簿の障害中に無効化した利用者の同期が通る");
    }

    // T-AS-05: 名簿に居ない（削除された等）なら 401 —— 有効だと確かめられない。
    [Fact]
    public async Task 名簿に居ない利用者の同期トークンは401になる()
    {
        var user = NewUser("nf");
        var plugin = PluginWith(await IssueTokenAsync(user));
        (await ManifestAsync(plugin)).StatusCode.Should().Be(HttpStatusCode.OK, "陽性対照");

        factory.OwnerAccounts.Declare(user, OwnerAccountState.NotFound);

        (await ManifestAsync(plugin)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── 有効な利用者への影響が無いこと ───────────────────────────────────

    // T-AS-06: 有効な利用者は通り、**名簿はトークンの所有者の ID で引かれる**。
    // 別の利用者が無効化されていても巻き込まれない。
    [Fact]
    public async Task 有効な利用者は他者の無効化に巻き込まれず名簿は所有者のIDで引かれる()
    {
        var alice = NewUser("en-a");
        var bob = NewUser("en-b");
        var alicePlugin = PluginWith(await IssueTokenAsync(alice));
        await IssueTokenAsync(bob);
        factory.OwnerAccounts.Declare(bob, OwnerAccountState.Disabled);
        factory.OwnerAccounts.Declare(alice, OwnerAccountState.Enabled);

        var before = factory.OwnerAccounts.Queried.Count;
        (await ManifestAsync(alicePlugin)).StatusCode.Should().Be(HttpStatusCode.OK);
        var push = await alicePlugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("notes/ok.md", "本文"), Ct);
        push.StatusCode.Should().Be(HttpStatusCode.Created);

        factory.OwnerAccounts.Queried.Skip(before).Should().Equal([alice, alice],
            "1 要求につき 1 回、トークンの所有者で引く（キャッシュしない・要求の中身で宛先を変えない）");
    }

    // T-AS-07: トークンが無い・不正・期限切れ・失効なら**名簿を引かない**（同じ 401 のまま）。
    // 🔴 無資格の要求が認可サービスへの往復を生めると、名簿の負荷を外から操れる。
    [Fact]
    public async Task 端末が有効と確定しない要求では名簿を引かない()
    {
        var expiredOwner = NewUser("exp");
        var revokedOwner = NewUser("rev");
        var (expiredToken, expiredHash) = SyncTokens.Generate();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            db.SyncDevices.Add(SyncDevice.Create(expiredOwner, "old-pc", expiredHash,
                DateTimeOffset.UtcNow.AddDays(-31)));
            await db.SaveChangesAsync(Ct);
        }
        var session = SessionAs(revokedOwner);
        var issued = await (await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "phone" }, Ct)).Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(Ct);
        (await session.DeleteAsync($"/private-notes/devices/{issued!.DeviceId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var before = factory.OwnerAccounts.Queried.Count;
        (await ManifestAsync(factory.CreateClient())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ManifestAsync(PluginWith("deadbeef"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ManifestAsync(PluginWith(expiredToken))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ManifestAsync(PluginWith(issued.Token))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        factory.OwnerAccounts.Queried.Count.Should().Be(before, "端末の検証に落ちた要求は名簿へ届かない");
    }
}

// FR-20, ADR-0114 決定 2, [[IADR-0474]] (#1532):
// **本番の縮退の向き**を、スタブへ差し替えずに測る。
// 試験のスタブは既定 Enabled（本番と逆）なので、ここが無いと「未構成なら通す」へ倒す変異が緑のまま通る。
public sealed class UnconfiguredAccountDirectoryFactory : TestWebApplicationFactory
{
    protected override bool ReplaceOwnerAccountDirectory => false;
}

[Trait("TestKind", "Integration")]
public class SyncTokenAccountDirectoryUnconfiguredTests(UnconfiguredAccountDirectoryFactory factory)
    : IClassFixture<UnconfiguredAccountDirectoryFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-AS-08: `Services:AuthorizationServiceGrpc` が無い配備では縮退が選ばれ、同期トークンは通らない。
    // 陽性対照: 同じホストでブラウザ経路（発行）は通る —— サービスが壊れているのではなく、門が閉じている。
    [Fact]
    public async Task 口が構成されていない配備では同期トークンが通らない()
    {
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IOwnerAccountDirectory>()
                .Should().BeOfType<UnavailableOwnerAccountDirectory>();
        }

        var session = factory.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, $"unc-{Guid.NewGuid():N}"[..20]);
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "laptop" }, Ct);
        issued.StatusCode.Should().Be(HttpStatusCode.Created, "発行（ブラウザ経路）は通る（陽性対照）");
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(Ct))!.Token;

        var plugin = factory.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await plugin.GetAsync("/private-notes/sync/manifest", Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "名簿を引けない配備では通さない（fail-closed）");
    }
}
