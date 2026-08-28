using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-19, FR-20, UC-11, SC-19, SC-20, ADR-0036, ADR-0037, #451: `/bff/private-notes*` の認可を固定する。
//
// **個人資料は本人のみ**である。BFF がその本人性をどう担保しているかを層ごとに測る。
//   1. 無認証は 401（`/bff/*` の不変条件。NFR-09 の暫定運用）
//   2. 🔴 **本人絞りの実体は「利用者の資格情報を後段へ渡すこと」**である —— 後段は主体を
//      トークンからしか採らず、台帳の所有者で絞る。転送を落とすと後段は主体を決められない。
//      スタブがそれを再現するので、転送を外すとこのクラスは赤くなる（変異試験 1）。
//   3. 他人の資料・端末は **404**（403 ではない。403 は他人の ID の実在を漏らす）
//   4. 書き込みは ABAC の **write** スコープで前段を絞る（#1010 / IADR-0272）。
//      read しか持たない主体は 403 になり、**読み取りは通る**（陽性対照）
//
// 🔴 否定形（拒否・不可視）は必ず陽性対照（許可・可視）と対で置く ——
// 「常に 404」「常に空」の実装でも陰性だけなら緑になる。
public class BffPrivateNoteEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffPrivateNoteEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        _factory.WriteScopeGranted = true;
        _factory.WriteScopeFilters = [];
        _factory.PrivateNoteQuotaExceeded = false;
        _factory.LastPrivateNoteForwardedAuthorization = null;
        _factory.ScopeActionsRequested.Clear();
    }

    // 本人（alice）として呼ぶ。**資格情報は明示的に載せる** —— BFF はこれを後段へ転送し、
    // 後段はこの主体で台帳を絞る（本番では BFF セッションのアクセストークンが同じ位置に入る）。
    private HttpClient As(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", subject);
        return client;
    }

    private static string NotePath(Guid id) => $"/bff/private-notes/{id}";

    // ── 1. 無認証は全端点で 401 ────────────────────────────────────
    //
    // **群に `RequireAuthorization()` が在ることの検査である。** 個人資料は本人のものしか
    // 返さないため「漏れないから無認証でもよい」と考えがちだが、それは防御が 1 枚になる
    // （後段が主体を決められないことだけに支えられた安全。IADR-0044）。
    [Theory]
    [InlineData("GET", "/bff/private-notes")]
    [InlineData("POST", "/bff/private-notes")]
    [InlineData("DELETE", "/bff/private-notes/19191919-1919-1919-1919-191919191919")]
    [InlineData("POST", "/bff/private-notes/19191919-1919-1919-1919-191919191919/restore")]
    [InlineData("PUT", "/bff/private-notes/19191919-1919-1919-1919-191919191919/exposure")]
    [InlineData("POST", "/bff/private-notes/purge")]
    [InlineData("GET", "/bff/private-notes/devices")]
    [InlineData("POST", "/bff/private-notes/devices")]
    [InlineData("POST", "/bff/private-notes/devices/d0d0d0d0-d0d0-d0d0-d0d0-d0d0d0d0d0d0/reissue")]
    [InlineData("DELETE", "/bff/private-notes/devices/d0d0d0d0-d0d0-d0d0-d0d0-d0d0d0d0d0d0")]
    [InlineData("POST", "/bff/private-notes/devices/revoke-all")]
    public async Task Anonymous_requests_are_rejected_with_401(string method, string path)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        using var req = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { title = "x", deviceName = "x", ids = Array.Empty<Guid>() }),
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── 2. 資格情報の転送（本人絞りの実体）────────────────────────────
    [Fact]
    public async Task The_callers_credentials_reach_the_downstream_service()
    {
        _factory.LastPrivateNoteForwardedAuthorization = null;

        var resp = await As(BffTestFactory.NoteOwner).GetAsync("/bff/private-notes", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastPrivateNoteForwardedAuthorization.Should()
            .Be($"Bearer {BffTestFactory.NoteOwner}",
                "後段は主体をトークンからしか採らない。転送しなければ誰の資料かを決められない");
    }

    // ── 3. 本人は自分の資料へ到達できる（陽性対照）──────────────────────
    [Fact]
    public async Task The_owner_sees_their_own_note_and_quota()
    {
        var body = await As(BffTestFactory.NoteOwner)
            .GetFromJsonAsync<PrivateNoteListResponse>("/bff/private-notes", TestContext.Current.CancellationToken);

        body!.Notes.Select(n => n.Id).Should().Contain(BffTestFactory.StubPrivateNoteId);
        body.Usage.LimitBytes.Should().BeGreaterThan(0, "SC-19 は使用量と上限の両方を示す");
    }

    // 陰性: 他人の資料は一覧に 1 件も現れない。
    [Fact]
    public async Task The_list_never_contains_another_users_note()
    {
        var body = await As(BffTestFactory.NoteOwner)
            .GetFromJsonAsync<PrivateNoteListResponse>("/bff/private-notes", TestContext.Current.CancellationToken);

        body!.Notes.Select(n => n.Id).Should().NotContain(BffTestFactory.OtherOwnerNoteId);
    }

    // 陰性: 他人の資料への操作は**不在と同じ 404**（403 にすると ID の実在が漏れる）。
    [Theory]
    [InlineData("DELETE", "")]
    [InlineData("POST", "/restore")]
    [InlineData("PUT", "/exposure")]
    public async Task Operating_on_another_users_note_is_indistinguishable_from_absence(
        string method, string suffix)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method),
            NotePath(BffTestFactory.OtherOwnerNoteId) + suffix)
        {
            Content = JsonContent.Create(new UpdateExposureRequest(true, true, true)),
        };
        var resp = await As(BffTestFactory.NoteOwner).SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 🔴 陽性対照: **同じ 3 経路が自分の資料では通る。**
    // これが無いと「常に 404」の実装でも上の 3 件が緑になる。
    [Theory]
    [InlineData("DELETE", "")]
    [InlineData("POST", "/restore")]
    [InlineData("PUT", "/exposure")]
    public async Task Operating_on_the_owners_own_note_succeeds(string method, string suffix)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method),
            NotePath(BffTestFactory.StubPrivateNoteId) + suffix)
        {
            Content = JsonContent.Create(new UpdateExposureRequest(true, true, true)),
        };
        var resp = await As(BffTestFactory.NoteOwner).SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ADR-0037 決定 20: 完全削除は単票も一括も同じ口。**他人の ID を混ぜた一括は通らない。**
    [Fact]
    public async Task Purging_a_batch_that_contains_another_users_note_is_rejected()
    {
        var resp = await As(BffTestFactory.NoteOwner).PostAsJsonAsync("/bff/private-notes/purge",
            new PurgePrivateNotesRequest(
                [BffTestFactory.StubPrivateNoteId, BffTestFactory.OtherOwnerNoteId]), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 陽性対照: 自分の資料だけの一括は通り、解放される容量が返る（SC-19 の確認ダイアログの根拠）。
    [Fact]
    public async Task Purging_the_owners_own_notes_returns_the_freed_capacity()
    {
        var resp = await As(BffTestFactory.NoteOwner).PostAsJsonAsync("/bff/private-notes/purge",
            new PurgePrivateNotesRequest([BffTestFactory.StubPrivateNoteId]), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PurgePrivateNotesResponse>(TestContext.Current.CancellationToken);
        body!.PurgedCount.Should().Be(1);
        body.FreedBytes.Should().BeGreaterThan(0);
    }

    // ADR-0037 決定 19・20: 論理削除では容量は空かない。**機械可読な形で画面へ届くこと**を固定する
    // （SC-19 の確認ダイアログの固定文言は、この事実を根拠にしている）。
    [Fact]
    public async Task Soft_delete_tells_the_screen_that_capacity_is_not_freed()
    {
        var resp = await As(BffTestFactory.NoteOwner)
            .DeleteAsync(NotePath(BffTestFactory.StubPrivateNoteId), TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<PrivateNoteDeletedResponse>(TestContext.Current.CancellationToken);
        body!.CapacityFreed.Should().BeFalse();
        body.PurgeAt.Should().NotBeNull("SC-19 は完全削除までの残り日数を表示する");
    }

    // ── 4. 書き込みは write スコープで絞る（#1010 / IADR-0272）─────────────
    //
    // **read を「許可」に立てたまま write だけ落とす**のが要点である ——
    // read の可否で 403 になっているなら、このテストは action の書き分けを検出できない。
    [Theory]
    [InlineData("POST", "/bff/private-notes")]
    [InlineData("DELETE", "/bff/private-notes/19191919-1919-1919-1919-191919191919")]
    [InlineData("POST", "/bff/private-notes/19191919-1919-1919-1919-191919191919/restore")]
    [InlineData("PUT", "/bff/private-notes/19191919-1919-1919-1919-191919191919/exposure")]
    [InlineData("POST", "/bff/private-notes/purge")]
    public async Task Writes_are_forbidden_for_a_subject_without_a_write_policy(
        string method, string path)
    {
        _factory.SearchScopeGranted = true;    // read は許可のまま
        _factory.WriteScopeGranted = false;    // write ポリシーは 1 件も無い
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = JsonContent.Create(new { title = "x", ids = new[] { BffTestFactory.StubPrivateNoteId } }),
            };
            var resp = await As(BffTestFactory.NoteOwner).SendAsync(req, TestContext.Current.CancellationToken);

            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            _factory.WriteScopeGranted = true;
        }
    }

    // 🔴 陽性対照 1: **同じ主体の読み取りは通る。** 読み取りに ABAC の前段を置いていないこと
    // （作業仕様書 §認可の設計 4）を固定する —— 置くと、文書属性を持たない台帳の投影が
    // 全件不一致になり、利用者は自分の資料を 1 件も見られなくなる。
    [Fact]
    public async Task Reading_still_works_for_a_subject_without_any_document_policy()
    {
        _factory.SearchScopeGranted = false;
        _factory.WriteScopeGranted = false;
        try
        {
            var resp = await As(BffTestFactory.NoteOwner).GetAsync("/bff/private-notes", TestContext.Current.CancellationToken);

            resp.StatusCode.Should().Be(HttpStatusCode.OK,
                "返すのは呼び出し者自身の資料だけであり、秘匿する相手が居ない");
        }
        finally
        {
            _factory.SearchScopeGranted = true;
            _factory.WriteScopeGranted = true;
        }
    }

    // 🔴 陽性対照 2（SC-20）: **端末の失効は write ポリシーが無くても通る。**
    // 計画は個別失効を「端末紛失時の唯一の防御線」と定めている ——
    // 文書 ABAC の整備状況に依存させると、ポリシー未整備の環境で紛失端末を失効できない。
    [Fact]
    public async Task Revoking_a_lost_device_works_without_a_write_policy()
    {
        _factory.WriteScopeGranted = false;
        try
        {
            var resp = await As(BffTestFactory.NoteOwner)
                .DeleteAsync($"/bff/private-notes/devices/{BffTestFactory.StubSyncDeviceId}", TestContext.Current.CancellationToken);

            resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        finally
        {
            _factory.WriteScopeGranted = true;
        }
    }

    // 書き込みが `/authz/scope` へ送る action が **write** であること（観測点で固定）。
    // 「劣化させて read を送る」変異はここと上の 403 の両方で赤くなる。
    [Fact]
    public async Task Write_paths_resolve_the_write_action()
    {
        _factory.ScopeActionsRequested.Clear();

        var resp = await As(BffTestFactory.NoteOwner).PostAsJsonAsync("/bff/private-notes",
            new CreatePrivateNoteRequest("新しい資料"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.ScopeActionsRequested.Should().Equal("write");
    }

    // ── 5. SC-20: 端末とトークン ─────────────────────────────────
    //
    // **平文トークンは発行の応答にだけ現れ、一覧には載らない**（発行直後に一度だけ表示・再表示不可）。
    [Fact]
    public async Task The_plaintext_token_appears_only_in_the_issue_response()
    {
        var issued = await (await As(BffTestFactory.NoteOwner).PostAsJsonAsync(
            "/bff/private-notes/devices", new CreateSyncDeviceRequest("Obsidian（自宅 PC）"), TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(TestContext.Current.CancellationToken);

        issued!.Token.Should().Be(BffTestFactory.StubSyncTokenPlaintext);

        var listBody = await As(BffTestFactory.NoteOwner)
            .GetStringAsync("/bff/private-notes/devices", TestContext.Current.CancellationToken);

        listBody.Should().NotContain(BffTestFactory.StubSyncTokenPlaintext,
            "一覧にはトークンの平文もハッシュも載らない（SC-20）");
    }

    // 陰性: 他人の端末は 404（一覧にも現れない・失効も再発行もできない）。
    [Fact]
    public async Task Another_users_device_is_not_reachable()
    {
        var devices = await As(BffTestFactory.NoteOwner)
            .GetFromJsonAsync<List<SyncDeviceDto>>("/bff/private-notes/devices", TestContext.Current.CancellationToken);
        devices!.Select(d => d.Id).Should().NotContain(BffTestFactory.OtherOwnerDeviceId);

        var revoke = await As(BffTestFactory.NoteOwner)
            .DeleteAsync($"/bff/private-notes/devices/{BffTestFactory.OtherOwnerDeviceId}", TestContext.Current.CancellationToken);
        revoke.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var reissue = await As(BffTestFactory.NoteOwner).PostAsync(
            $"/bff/private-notes/devices/{BffTestFactory.OtherOwnerDeviceId}/reissue", null, TestContext.Current.CancellationToken);
        reissue.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 陽性対照: 自分の端末は一覧に現れ、再発行も一括失効も通る。
    [Fact]
    public async Task The_owners_own_device_is_listed_and_can_be_reissued()
    {
        var devices = await As(BffTestFactory.NoteOwner)
            .GetFromJsonAsync<List<SyncDeviceDto>>("/bff/private-notes/devices", TestContext.Current.CancellationToken);
        devices!.Select(d => d.Id).Should().Contain(BffTestFactory.StubSyncDeviceId);

        var reissue = await As(BffTestFactory.NoteOwner).PostAsync(
            $"/bff/private-notes/devices/{BffTestFactory.StubSyncDeviceId}/reissue", null, TestContext.Current.CancellationToken);
        reissue.StatusCode.Should().Be(HttpStatusCode.OK);

        var revokeAll = await As(BffTestFactory.NoteOwner)
            .PostAsync("/bff/private-notes/devices/revoke-all", null, TestContext.Current.CancellationToken);
        revokeAll.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── 6. 後段の本文を詰め替えない（SC-19 の固定文言の根拠が画面へ届く）─────────
    //
    // ADR-0037 決定 17: 容量 100% では**新規作成だけ**が 507 で拒まれる。
    // 本文には「容量を空ける手段」の案内が入っており、詰め替えると画面が理由を出せない。
    [Fact]
    public async Task The_quota_problem_body_reaches_the_screen_untouched()
    {
        _factory.PrivateNoteQuotaExceeded = true;
        try
        {
            var resp = await As(BffTestFactory.NoteOwner).PostAsJsonAsync("/bff/private-notes",
                new CreatePrivateNoteRequest("上限到達後の新規作成"), TestContext.Current.CancellationToken);

            resp.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage);
            (await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .Should().Contain(BffTestFactory.QuotaProblemMarker);
        }
        finally
        {
            _factory.PrivateNoteQuotaExceeded = false;
        }
    }
}
