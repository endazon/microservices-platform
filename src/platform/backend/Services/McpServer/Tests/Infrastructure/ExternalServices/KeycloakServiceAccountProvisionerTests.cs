using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, SC-12, 計画 ADR-0123 決定 1・2・フォローアップ 1・3, [[IADR-0516]] 決定 1・3・4・6 (#1786):
// SC-12 の IdP への書き込み口（Keycloak Admin REST）。
//
// 🔴 **Keycloak は状態を持つ偽物（`FakeKeycloak`）で置き換える。** 稼働の Keycloak での疎通は未検証であり（IADR-0516 §残余）、
// ここで固定するのは「どの順で何を送り、失敗したら何を戻すか」である。**緑は「実 IdP へ反映できる」を意味しない。**
[Trait("TestKind", "Unit")]
public class KeycloakServiceAccountProvisionerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceAccountProvisioningOptions Options => new()
    {
        BaseUrl = "https://auth.example.test",
        Realm = "platform",
        ClientId = "mcp-client-admin",
        ClientSecret = "injected-at-deploy-time",
    };

    private static KeycloakServiceAccountProvisioner Provisioner(FakeKeycloak keycloak)
        => new(new StubFactory(keycloak), Options, TimeProvider.System,
            NullLogger<KeycloakServiceAccountProvisioner>.Instance);

    private static Dictionary<string, string> Attrs(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    // T-1786-01: 登録で機密クライアント（サービスアカウントつき）を作り、属性をその利用者属性として書く。
    [Fact]
    public async Task 登録で機密クライアントを作り属性をサービスアカウントの利用者属性として書く()
    {
        var keycloak = new FakeKeycloak();

        var written = await Provisioner(keycloak).CreateAsync(
            "Agent-X", "エージェント X", Attrs(("clearance", "internal"), ("tags", "sales,hr")), Ct);

        written.Kind.Should().Be(IdpWriteKind.Created);
        var client = keycloak.Clients.Should().ContainSingle().Subject;
        client.ClientId.Should().Be("Agent-X");
        client.Representation["publicClient"]!.GetValue<bool>().Should().BeFalse("機密クライアントである");
        client.Representation["serviceAccountsEnabled"]!.GetValue<bool>().Should().BeTrue();
        client.Representation["standardFlowEnabled"]!.GetValue<bool>().Should().BeFalse("人の流れは閉じる");
        client.Representation["directAccessGrantsEnabled"]!.GetValue<bool>().Should().BeFalse("人の流れは閉じる");
        client.Representation["implicitFlowEnabled"]!.GetValue<bool>().Should().BeFalse("人の流れは閉じる");

        var user = keycloak.Users[client.ServiceAccountUserId];
        user.Username.Should().Be("service-account-agent-x", "Keycloak はサービスアカウントの利用者名を小文字で持つ");
        user.Attributes["clearance"].Should().Equal("internal");
        user.Attributes["tags"].Should().Equal("sales", "hr");

        // `PUT /users/{id}` は部分更新ではない（IADR-0329 の実測）。現在の表現を送り返し、サーバ計算の値は送らない。
        var put = keycloak.Requests.Single(r => r.Method == "PUT").Body!;
        put.Should().Contain("keep-me", "read-modify-write でないと送らなかった項目が消える");
        put.Should().NotContain("\"access\"", "サーバ計算の値は送り返さない");
    }

    // T-1786-02（ADR-0123 フォローアップ 3）: 書く前に、認可サービスと同じ照会（`username=service-account-<client>&exact=true`）で
    // サービスアカウントの利用者が引けることを確かめる。
    [Fact]
    public async Task 書く前に認可サービスと同じ照会でサービスアカウントの利用者を引き直す()
    {
        var keycloak = new FakeKeycloak();

        await Provisioner(keycloak).CreateAsync("agent-y", "Y", Attrs(("clearance", "public")), Ct);

        var lookup = keycloak.Requests.FindIndex(r =>
            r.Method == "GET" && r.Path == "admin/realms/platform/users?username=service-account-agent-y&exact=true&briefRepresentation=false&max=2");
        var put = keycloak.Requests.FindIndex(r => r.Method == "PUT");
        lookup.Should().BeGreaterThanOrEqualTo(0, "認可サービスの照会と同じ形で引く");
        put.Should().BeGreaterThan(lookup, "照会が通ってから書く");
        keycloak.Requests.Where(r => r.Path.StartsWith("admin/")).Should()
            .OnlyContain(r => r.Authorization == "Bearer admin-token");
    }

    // T-1786-03（否定形）: 照会がサービスアカウントの利用者を返さなければ、**属性を書かずに**作ったクライアントを消して失敗する。
    [Fact]
    public async Task 照会でサービスアカウントが引けなければ属性を書かずにクライアントを消す()
    {
        var keycloak = new FakeKeycloak { LookupHidesServiceAccounts = true };

        var act = () => Provisioner(keycloak).CreateAsync("agent-z", "Z", Attrs(("clearance", "public")), Ct);

        (await act.Should().ThrowAsync<IdpProvisioningException>())
            .Which.Failure.Should().Be(IdpProvisioningFailure.Failed);
        keycloak.Requests.Should().NotContain(r => r.Method == "PUT", "判定に使われない属性は書かない");
        keycloak.Clients.Should().BeEmpty("作りかけのクライアントは消す（補償）");
    }

    // T-1786-04（否定形）: 同じ clientId のクライアントが IdP に既に在れば、**何も書かない**（入口を通らない主体へ属性を書かない）。
    [Fact]
    public async Task 既にあるクライアントへは何も書かない()
    {
        var keycloak = new FakeKeycloak();
        keycloak.SeedClient("identity-admin", new Dictionary<string, string[]>());

        var written = await Provisioner(keycloak).CreateAsync("identity-admin", "既存", Attrs(("clearance", "internal")), Ct);

        written.Kind.Should().Be(IdpWriteKind.AlreadyExists);
        keycloak.Requests.Should().NotContain(r => r.Method == "PUT");
        keycloak.Requests.Should().NotContain(r => r.Method == "DELETE", "入口が作っていないクライアントを消さない");
        keycloak.Users.Values.Single().Attributes.Should().BeEmpty();
    }

    // T-1786-05: realm が unmanaged 属性を黙って捨てたら（204 のまま読み戻せない）失敗にし、クライアントを消す。
    [Fact]
    public async Task 書いた属性が読み戻せなければ失敗にしてクライアントを消す()
    {
        var keycloak = new FakeKeycloak { DropAttributesOnPut = true };

        var act = () => Provisioner(keycloak).CreateAsync("agent-d", "D", Attrs(("clearance", "internal")), Ct);

        await act.Should().ThrowAsync<IdpProvisioningException>();
        keycloak.Clients.Should().BeEmpty();
    }

    // T-1786-06: 差し替えは書く前の属性を返し、取り消しでそれを書き戻す（補償）。
    [Fact]
    public async Task 差し替えは元の属性を返し取り消しで書き戻す()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        await provisioner.CreateAsync("agent-r", "R", Attrs(("clearance", "public")), Ct);

        var written = await provisioner.ReplaceAttributesAsync("agent-r", "R", Attrs(("clearance", "internal")), true, Ct);

        written.Kind.Should().Be(IdpWriteKind.Updated);
        written.PreviousAttributes.Should().BeEquivalentTo(Attrs(("clearance", "public")));
        keycloak.ServiceAccountOf("agent-r").Attributes["clearance"].Should().Equal("internal");

        await provisioner.UndoAsync(written, Ct);

        keycloak.ServiceAccountOf("agent-r").Attributes["clearance"].Should().Equal("public");
    }

    // T-1786-07: 差し替えで IdP にクライアントが無ければ（本入口ができる前の登録簿の行）作ってから書く。取り消しは削除。
    [Fact]
    public async Task 差し替えでIdPに無ければ作り取り消しで消す()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);

        var written = await provisioner.ReplaceAttributesAsync("legacy", "旧", Attrs(("clearance", "public")), true, Ct);

        written.Kind.Should().Be(IdpWriteKind.Created);
        keycloak.ServiceAccountOf("legacy").Attributes["clearance"].Should().Equal("public");

        await provisioner.UndoAsync(written, Ct);

        keycloak.Clients.Should().BeEmpty();
    }

    // T-1786-08: 属性は丸ごと置き換える（この入口だけが書く）。差し替えで外したキーは IdP からも消える。
    [Fact]
    public async Task 差し替えは属性を丸ごと置き換える()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        await provisioner.CreateAsync("agent-w", "W", Attrs(("clearance", "public"), ("tags", "sales")), Ct);

        await provisioner.ReplaceAttributesAsync("agent-w", "W", Attrs(("clearance", "internal")), true, Ct);

        keycloak.ServiceAccountOf("agent-w").Attributes.Keys.Should().Equal("clearance");
    }

    // T-1786-09: IdP へ到達できなければ Failed（呼び出し元は 502 を返し、登録簿へ書かない）。
    [Fact]
    public async Task 到達できなければFailedを投げる()
    {
        var keycloak = new FakeKeycloak { Unreachable = true };

        var act = () => Provisioner(keycloak).CreateAsync("agent-u", "U", Attrs(("clearance", "public")), Ct);

        (await act.Should().ThrowAsync<IdpProvisioningException>())
            .Which.Failure.Should().Be(IdpProvisioningFailure.Failed);
    }

    // T-1786-10: 集合値キーの比較は順序を問わない（読み戻しの突合で偽の失敗を出さない）。
    [Fact]
    public void 集合値キーは順序を問わずに突き合わせる()
    {
        KeycloakServiceAccountProvisioner.SameAttributes(
                Attrs(("tags", "sales,hr"), ("clearance", "internal")),
                Attrs(("tags", "hr,sales"), ("clearance", "internal")))
            .Should().BeTrue();
        KeycloakServiceAccountProvisioner.SameAttributes(
                Attrs(("clearance", "internal")), Attrs(("clearance", "confidential")))
            .Should().BeFalse();
    }

    // T-1786-12（否定形・監査 🔴-1）: 差し替えでも、入口の印が無いクライアント（`abac-seeder` 等）へは何も書かない。
    [Fact]
    public async Task 差し替えでも入口の印が無いクライアントへは書かない()
    {
        var keycloak = new FakeKeycloak();
        keycloak.SeedClient("abac-seeder", new Dictionary<string, string[]> { ["clearance"] = ["restricted"] });

        var written = await Provisioner(keycloak).ReplaceAttributesAsync(
            "abac-seeder", "旧", Attrs(("clearance", "public")), true, Ct);

        written.Kind.Should().Be(IdpWriteKind.AlreadyExists);
        keycloak.Requests.Should().NotContain(r => r.Method == "PUT", "入口が作っていない主体の属性を上書きしない");
        keycloak.ServiceAccountOf("abac-seeder").Attributes["clearance"].Should().Equal("restricted");
        keycloak.Requests.Should().Contain(r => r.Method == "GET" && r.Path.EndsWith("/clients/" + keycloak.Clients[0].Id),
            "印はクライアントの表現から読む");
    }

    // T-1786-13: テンプレートは realm の全ロールをトークンへ載せない。
    [Fact]
    public void テンプレートはfullScopeAllowedを閉じる()
        => KeycloakServiceAccountProvisioner.ServiceAccountClientTemplate("a", "A")["fullScopeAllowed"].Should().Be(false);

    // T-1786-14（監査 🟡-1）: POST /clients の後の時間切れでも、作ったクライアントを消して Failed（502）にする。
    [Fact]
    public async Task 作成の後の時間切れでもクライアントを消してFailedにする()
    {
        var keycloak = new FakeKeycloak { TimeoutAfterCreate = true };

        var act = () => Provisioner(keycloak).CreateAsync("agent-t", "T", Attrs(("clearance", "public")), Ct);

        (await act.Should().ThrowAsync<IdpProvisioningException>())
            .Which.Failure.Should().Be(IdpProvisioningFailure.Failed, "時間切れは 500 ではなく 502 へ写す");
        keycloak.Clients.Should().BeEmpty("時間切れでも補償する");
    }

    // T-1786-15（監査 🟢）: Location が無く引き直しも失敗したら、補償で引き直して消す（try の外へ投げない）。
    [Fact]
    public async Task Locationが無く引き直しに失敗しても補償で消す()
    {
        var keycloak = new FakeKeycloak { OmitLocation = true, ClientLookupFailures = 1 };
        var provisioner = Provisioner(keycloak);

        var act = () => provisioner.CreateAsync("agent-l", "L", Attrs(("clearance", "public")), Ct);

        await act.Should().ThrowAsync<IdpProvisioningException>();
        // 1 回目の引き直しは失敗した。補償は引き直してから消す —— 照会が直っていれば消せる。
        keycloak.Clients.Should().BeEmpty();
    }

    // T-1786-16（監査 🟢）: 管理用トークンが 401 で拒まれたら、1 度だけ取り直して送り直す。
    [Fact]
    public async Task 管理要求が401なら1度だけトークンを取り直す()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        await provisioner.CreateAsync("agent-k", "K", Attrs(("clearance", "public")), Ct);
        var before = keycloak.TokenRequests;

        keycloak.RejectNextAdminCallOnce = true;
        var written = await provisioner.ReplaceAttributesAsync("agent-k", "K", Attrs(("clearance", "internal")), true, Ct);

        written.Kind.Should().Be(IdpWriteKind.Updated);
        keycloak.TokenRequests.Should().Be(before + 1);
    }

    // T-1786-17（監査 🟡-2）: 取り消しは、現在値がこの要求の書いた値のままのときだけ戻す（後の差し替えを潰さない）。
    [Fact]
    public async Task 取り消しは後から書かれた値を潰さない()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        await provisioner.CreateAsync("agent-c", "C", Attrs(("clearance", "public")), Ct);
        var first = await provisioner.ReplaceAttributesAsync("agent-c", "C", Attrs(("clearance", "internal")), true, Ct);
        await provisioner.ReplaceAttributesAsync("agent-c", "C", Attrs(("clearance", "confidential")), true, Ct);

        await provisioner.UndoAsync(first, Ct);

        keycloak.ServiceAccountOf("agent-c").Attributes["clearance"].Should().Equal("confidential");
    }

    // T-1786-18（Q3 の決定）: 登録簿で無効化された行の差し替えで IdP に作るときは、無効のまま作る。
    [Fact]
    public async Task 無効な行の差し替えでは無効のまま作る()
    {
        var keycloak = new FakeKeycloak();

        await Provisioner(keycloak).ReplaceAttributesAsync("legacy-off", "旧", Attrs(("clearance", "public")), false, Ct);

        keycloak.Clients.Single().Representation["enabled"]!.GetValue<bool>().Should().BeFalse();
    }

    // T-1786-19: 偽の Keycloak も `clientId=` を完全一致で扱う（前方一致の別クライアントを拾わない）。
    [Fact]
    public async Task クライアントの照会は完全一致である()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        await provisioner.CreateAsync("agent-long", "L", Attrs(("clearance", "public")), Ct);

        var written = await provisioner.ReplaceAttributesAsync("agent", "A", Attrs(("clearance", "public")), true, Ct);

        written.Kind.Should().Be(IdpWriteKind.Created, "`agent` は `agent-long` ではない");
        keycloak.ServiceAccountOf("agent-long").Attributes["clearance"].Should().Equal("public");
    }

    // T-1786-20（再監査 🟡-A）: `POST /clients` が Keycloak 側で作り終えてから時間切れになっても、印つきの孤児を残さず Failed。
    [Fact]
    public async Task 作成の要求そのものが時間切れでも作られたクライアントを消す()
    {
        var keycloak = new FakeKeycloak { TimeoutOnCreateAfterCommit = true };

        var act = () => Provisioner(keycloak).CreateAsync("agent-p3", "P3", Attrs(("clearance", "public")), Ct);

        (await act.Should().ThrowAsync<IdpProvisioningException>())
            .Which.Failure.Should().Be(IdpProvisioningFailure.Failed);
        keycloak.Clients.Should().BeEmpty("作成の成否が分からない失敗でも、印つきのものは消す");
        keycloak.Requests.Should().Contain(r => r.Method == "DELETE");
    }

    // T-1786-28（再監査 🟡-B / b3）: 取り消しは渡された ct が取り消し済みでも最後まで走る。
    [Fact]
    public async Task 取り消しは取り消し済みのトークンでも最後まで走る()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        var written = await provisioner.CreateAsync("agent-u2", "U", Attrs(("clearance", "public")), Ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await provisioner.UndoAsync(written, cancelled.Token);

        keycloak.Clients.Should().BeEmpty();
    }

    // T-1786-29（再監査 🟡-B）: 書き始めた後に要求が取り消されても、IdP への書き込みと補償は要求の取り消しで止まらない。
    // 書き込みの途中の失敗（読み戻せない）では、作ったクライアントを消す。
    [Fact]
    public async Task 書き始めた後の要求の取り消しは補償を止めない()
    {
        using var request = new CancellationTokenSource();
        var keycloak = new FakeKeycloak { DropAttributesOnPut = true, OnCreate = request.Cancel };

        var act = () => Provisioner(keycloak).CreateAsync("agent-x2", "X", Attrs(("clearance", "public")), request.Token);

        (await act.Should().ThrowAsync<IdpProvisioningException>()).Which.Failure.Should().Be(IdpProvisioningFailure.Failed);
        keycloak.Clients.Should().BeEmpty("要求の取り消しを補償へ伝えると孤児が残る");
        keycloak.Requests.Should().Contain(r => r.Method == "PUT", "要求の取り消しを書き込みへ伝えると途中で止まる");
    }

    // T-1786-30（再監査 🟢）: 401 の取り直しでは、古い Bearer をトークンの取得口へ運ばない。
    [Fact]
    public async Task 取り直しのトークン要求に古いBearerを載せない()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        await provisioner.CreateAsync("agent-b", "B", Attrs(("clearance", "public")), Ct);

        keycloak.RejectNextAdminCallOnce = true;
        await provisioner.ReplaceAttributesAsync("agent-b", "B", Attrs(("clearance", "internal")), true, Ct);

        keycloak.Requests.Where(r => r.Path.EndsWith("openid-connect/token", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Authorization == null);
    }

    // T-1786-31b（再監査 🟢）: 差し替えの try の外で応答が読めなくても 500 ではなく Failed（502）。
    [Fact]
    public async Task 差し替えで応答が読めなければFailed()
    {
        var keycloak = new FakeKeycloak { MalformedClientLookup = true };

        var act = () => Provisioner(keycloak).ReplaceAttributesAsync("agent-j", "J", Attrs(("clearance", "public")), true, Ct);

        (await act.Should().ThrowAsync<IdpProvisioningException>()).Which.Failure.Should().Be(IdpProvisioningFailure.Failed);
    }

    // ── ［2026-10-09 / #1829］無効化・再有効化の写し（IADR-0516 決定 4a）──────────────────────────

    private static bool EnabledOf(FakeKeycloak keycloak, string clientId)
        => keycloak.Clients.Single(c => c.ClientId == clientId).Representation["enabled"]!.GetValue<bool>();

    // C-51（#1829 受け入れ基準 1）: 入口の印つきのクライアントの `enabled` を書き、読み戻す。送るのは `enabled` と
    // SA・authorization の現在値だけ（表現を丸ごと送り返さない＝secret を古い値へ戻さない）。テンプレートの他の項目は変わらない。
    // 🔴 PR #1832 監査 🔴1: 無効化・再有効化の後もサービスアカウントの利用者とその属性が残る。一覧も有効・無効を返す。
    [Fact]
    public async Task 無効化と再有効化はenabledを書いてサービスアカウントを壊さず読み戻す()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        await provisioner.CreateAsync("agent-t", "T", Attrs(("clearance", "public"), ("tags", "sales,hr")), Ct);
        var saUserId = keycloak.Clients.Single().ServiceAccountUserId;
        keycloak.Requests.Clear();

        var disabled = await provisioner.SetEnabledAsync("agent-t", false, Ct);

        // 🔴 PR #1832 監査 🔴1: 無効化で SA の利用者が属性ごと消えない（Keycloak は serviceAccountsEnabled が TRUE でない PUT で SA を消す）。
        keycloak.Users.Should().ContainKey(saUserId, "無効化でサービスアカウントの利用者を消さない");
        keycloak.Users[saUserId].Attributes["tags"].Should().Equal("sales", "hr");

        disabled.Should().BeEquivalentTo(new IdpWrite(IdpWriteKind.EnabledChanged, "agent-t", "c1",
            PreviousEnabled: true, WrittenEnabled: false));
        EnabledOf(keycloak, "agent-t").Should().BeFalse();
        var put = keycloak.Requests.Should().ContainSingle(r => r.Method == "PUT").Subject;
        put.Path.Should().Be("admin/realms/platform/clients/c1");
        var sentBody = JsonNode.Parse(put.Body!)!.AsObject();
        sentBody.Select(kv => kv.Key).Should().BeEquivalentTo(
            ["enabled", "serviceAccountsEnabled", "authorizationServicesEnabled"], "表現を丸ごと送らない（secret を載せない）");
        sentBody["serviceAccountsEnabled"]!.GetValue<bool>().Should().BeTrue("現在値（SA あり）で送る");
        keycloak.Requests.Last().Method.Should().Be("GET", "書いた後に読み戻す");
        var rep = keycloak.Clients.Single().Representation;
        rep["serviceAccountsEnabled"]!.GetValue<bool>().Should().BeTrue("他の項目は変えない");
        rep["attributes"]![KeycloakServiceAccountProvisioner.ManagedByAttribute]!.GetValue<string>()
            .Should().Be(KeycloakServiceAccountProvisioner.ManagedByValue);
        (await provisioner.ListClientsAsync(Ct)).Single().Enabled.Should().BeFalse("照合は一覧の有効・無効を比べる");

        var enabled = await provisioner.SetEnabledAsync("agent-t", true, Ct);

        enabled.PreviousEnabled.Should().BeFalse();
        EnabledOf(keycloak, "agent-t").Should().BeTrue();
        keycloak.Users.Should().ContainKey(saUserId, "再有効化の後もサービスアカウントの利用者が同じ ID で残る");
        keycloak.Users[saUserId].Attributes["clearance"].Should().Equal("public");
        keycloak.Users[saUserId].Attributes["tags"].Should().Equal("sales", "hr");
        (await provisioner.ReadServiceAccountAttributesAsync("agent-t", Ct)).Should().BeEquivalentTo(
            new Dictionary<string, string> { ["clearance"] = "public", ["tags"] = "sales,hr" }, "認可サービスと同じ照会で属性が読める");
        (await provisioner.ListClientsAsync(Ct)).Single().Enabled.Should().BeTrue();

        keycloak.Requests.Clear();
        (await provisioner.SetEnabledAsync("agent-t", true, Ct)).Kind.Should().Be(IdpWriteKind.EnabledChanged);
        keycloak.Requests.Should().NotContain(r => r.Method == "PUT", "同じ値なら書かない");
    }

    // C-52（#1829 受け入れ基準 2・否定形）: 入口の印が無いクライアント（`abac-seeder` 等）と、IdP に無いクライアントには何も書かない。
    [Fact]
    public async Task 入口の印が無いクライアントとIdPに無いクライアントのenabledは書かない()
    {
        var keycloak = new FakeKeycloak();
        keycloak.SeedClient("abac-seeder", new Dictionary<string, string[]> { ["clearance"] = ["restricted"] });
        keycloak.Clients.Single().Representation["enabled"] = true;
        var provisioner = Provisioner(keycloak);

        var seeder = await provisioner.SetEnabledAsync("abac-seeder", false, Ct);
        var absent = await provisioner.SetEnabledAsync("abac", false, Ct);

        seeder.Kind.Should().Be(IdpWriteKind.AlreadyExists);
        absent.Kind.Should().Be(IdpWriteKind.Absent, "`abac` は `abac-seeder` ではない（完全一致）");
        EnabledOf(keycloak, "abac-seeder").Should().BeTrue("プラットフォームのクライアントを無効化の経路から止めない");
        keycloak.Requests.Should().NotContain(r => r.Method == "PUT" || r.Method == "DELETE");
    }

    // C-53（#1829）: 書いた後に読み戻せなければ Failed。開く書き込み（再有効化）の失敗は無効へ戻す。閉じる書き込み（無効化）の失敗は
    // 戻さない（通っていたかもしれない無効化を取り消して開かない）。書き込みそのものの失敗（500）も Failed。
    [Fact]
    public async Task 読み戻せなければFailedで開く側の失敗だけを閉じる側へ戻す()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        await provisioner.CreateAsync("agent-r", "R", Attrs(("clearance", "public")), Ct);
        await provisioner.SetEnabledAsync("agent-r", false, Ct);

        keycloak.IgnoreEnabledOnPut = 1;
        var enable = () => provisioner.SetEnabledAsync("agent-r", true, Ct);
        (await enable.Should().ThrowAsync<IdpProvisioningException>()).Which.Failure.Should().Be(IdpProvisioningFailure.Failed);
        EnabledOf(keycloak, "agent-r").Should().BeFalse();
        keycloak.Requests.Count(r => r.Method == "PUT" && r.Path.Contains("/clients/")).Should().Be(3, "無効化 1 ＋ 再有効化 1 ＋ 無効へ戻す 1");

        await provisioner.SetEnabledAsync("agent-r", true, Ct);
        keycloak.Requests.Clear();
        keycloak.IgnoreEnabledOnPut = 1;
        var disable = () => provisioner.SetEnabledAsync("agent-r", false, Ct);
        (await disable.Should().ThrowAsync<IdpProvisioningException>()).Which.Failure.Should().Be(IdpProvisioningFailure.Failed);
        keycloak.Requests.Count(r => r.Method == "PUT" && r.Path.Contains("/clients/")).Should().Be(1, "閉じる側の失敗では開く側へ戻さない");

        var failing = new FakeKeycloak { FailClientPut = true };
        var p2 = Provisioner(failing);
        await p2.CreateAsync("agent-f", "F", Attrs(("clearance", "public")), Ct);
        var act = () => p2.SetEnabledAsync("agent-f", false, Ct);
        (await act.Should().ThrowAsync<IdpProvisioningException>()).Which.Failure.Should().Be(IdpProvisioningFailure.Failed);
    }

    // C-54（#1829）: 再有効化の取り消し（登録簿への書き込みが失敗したときの補償）は前の値（無効）へ戻す。
    // 現在値がこの要求の書いた値でなければ（後から無効化された）書かない。値が無い取り消し（同じ値で書かなかった）も書かない。
    [Fact]
    public async Task enabledの取り消しは書いた値のままのときだけ前の値へ戻す()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        await provisioner.CreateAsync("agent-u", "U", Attrs(("clearance", "public")), Ct);
        await provisioner.SetEnabledAsync("agent-u", false, Ct);
        var reenabled = await provisioner.SetEnabledAsync("agent-u", true, Ct);

        await provisioner.UndoAsync(reenabled, Ct);
        EnabledOf(keycloak, "agent-u").Should().BeFalse("書いた値のままなら前の値へ戻す");

        var again = await provisioner.SetEnabledAsync("agent-u", true, Ct);
        await provisioner.SetEnabledAsync("agent-u", false, Ct);
        var unchanged = await provisioner.SetEnabledAsync("agent-u", false, Ct);
        keycloak.Requests.Clear();
        await provisioner.UndoAsync(again, Ct);
        await provisioner.UndoAsync(unchanged, Ct);
        keycloak.Requests.Should().NotContain(r => r.Method == "PUT", "後から書かれた値・書かなかった値は戻さない");
        EnabledOf(keycloak, "agent-u").Should().BeFalse();
    }

    // ── 状態を持つ偽の Keycloak ──────────────────────────────────────────────

    internal sealed record Recorded(string Method, string Path, string? Body, string? Authorization);

    internal sealed class FakeClient(string id, string clientId, string serviceAccountUserId, JsonObject representation)
    {
        public string Id { get; } = id;
        public string ClientId { get; } = clientId;
        public string ServiceAccountUserId { get; } = serviceAccountUserId;
        public JsonObject Representation { get; } = representation;
    }

    internal sealed class FakeUser(string id, string username)
    {
        public string Id { get; } = id;
        public string Username { get; } = username;
        public Dictionary<string, string[]> Attributes { get; set; } = [];
    }

    internal sealed class FakeKeycloak : HttpMessageHandler
    {
        private const string Admin = "admin/realms/platform/";
        private int _sequence;

        public List<Recorded> Requests { get; } = [];
        public List<FakeClient> Clients { get; } = [];
        public Dictionary<string, FakeUser> Users { get; } = new(StringComparer.Ordinal);
        public bool LookupHidesServiceAccounts { get; init; }
        public bool DropAttributesOnPut { get; init; }
        public bool Unreachable { get; init; }
        /// <summary>`POST /clients` の後の最初の要求で時間切れ（TaskCanceledException）を投げる。</summary>
        public bool TimeoutAfterCreate { get; init; }
        /// <summary>`POST /clients` でクライアントを作り終えてから時間切れ（TaskCanceledException）を投げる。</summary>
        public bool TimeoutOnCreateAfterCommit { get; init; }
        /// <summary>`POST /clients` でクライアントを作った直後に呼ぶ（要求の取り消しを差し込む）。</summary>
        public Action? OnCreate { get; init; }
        /// <summary>`POST /clients` の応答に Location を付けない。</summary>
        public bool OmitLocation { get; init; }
        /// <summary>`clients?clientId=` の照会を、この回数だけ 500 にする。</summary>
        public int ClientLookupFailures { get; set; }
        /// <summary>`clients?clientId=` の応答を JSON として読めない本文にする。</summary>
        public bool MalformedClientLookup { get; init; }
        /// <summary>次の管理要求を 1 度だけ 401 にする（トークンの取り直しを見る）。</summary>
        public bool RejectNextAdminCallOnce { get; set; }
        public int TokenRequests { get; private set; }
        /// <summary>［#1818］`clients?first=&max=` の列挙が、頁の大きさちょうどの合成のクライアントを返し続ける（上限の試験）。</summary>
        public bool EndlessClientList { get; init; }
        /// <summary>［#1818］`clients?first=&max=` の列挙が、呼び出し元の取り消しまで返らない。</summary>
        public bool HangOnClientList { get; init; }
        /// <summary>［#1818］`clients?first=&max=` の列挙で時間切れ（TaskCanceledException。呼び出し元は取り消していない）を投げる。</summary>
        public bool TimeoutOnClientList { get; init; }
        /// <summary>［#1829］`PUT /clients/{id}` を 500 にする。</summary>
        public bool FailClientPut { get; init; }
        /// <summary>［#1829］`PUT /clients/{id}` を 204 で受けるが `enabled` を変えない（読み戻しの不一致）。この回数だけ。</summary>
        public int IgnoreEnabledOnPut { get; set; }
        private bool _timedOut;

        public void SeedClient(string clientId, Dictionary<string, string[]> attributes)
        {
            // 入口を通らずに作られたクライアント: **入口の印（managed-by）を持たない**。
            var client = NewClient(clientId, new JsonObject
            {
                ["clientId"] = clientId,
                ["attributes"] = new JsonObject { ["client.secret.creation.time"] = "0" },
            });
            Users[client.ServiceAccountUserId].Attributes = attributes;
        }

        public FakeUser ServiceAccountOf(string clientId)
            => Users[Clients.Single(c => c.ClientId == clientId).ServiceAccountUserId];

        private FakeClient NewClient(string clientId, JsonObject representation)
        {
            var n = ++_sequence;
            var client = new FakeClient($"c{n}", clientId, $"u{n}", representation);
            Clients.Add(client);
            Users[client.ServiceAccountUserId] = new FakeUser(client.ServiceAccountUserId,
                "service-account-" + clientId.ToLowerInvariant());
            return client;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Recorded(request.Method.Method, path, body, request.Headers.Authorization?.ToString()));
            if (Unreachable) throw new HttpRequestException("connection refused");

            var method = request.Method.Method;
            if (method == "POST" && path == "realms/platform/protocol/openid-connect/token")
            {
                TokenRequests++;
                return Ok("""{"access_token":"admin-token","expires_in":300}""");
            }

            if (RejectNextAdminCallOnce)
            {
                RejectNextAdminCallOnce = false;
                return Status(HttpStatusCode.Unauthorized);
            }

            if (TimeoutAfterCreate && Clients.Count > 0 && !_timedOut && method != "DELETE")
            {
                _timedOut = true;
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
            }

            if (method == "POST" && path == Admin + "clients")
            {
                var rep = JsonNode.Parse(body!)!.AsObject();
                var clientId = rep["clientId"]!.GetValue<string>();
                if (Clients.Any(c => c.ClientId == clientId)) return Status(HttpStatusCode.Conflict);
                var created = NewClient(clientId, rep);
                OnCreate?.Invoke();
                if (TimeoutOnCreateAfterCommit)
                    throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
                var response = Status(HttpStatusCode.Created);
                if (!OmitLocation)
                    response.Headers.Location = new Uri($"https://auth.example.test/{Admin}clients/{created.Id}");
                return response;
            }

            // ［#1818］照合の列挙（`clients?first=&max=`）。表現はクライアント属性（入口の印）を含む。
            if (method == "GET" && path.StartsWith(Admin + "clients?first=", StringComparison.Ordinal))
            {
                if (HangOnClientList) await Task.Delay(Timeout.Infinite, ct);
                if (TimeoutOnClientList)
                    throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
                var query = path[(Admin + "clients?").Length..].Split('&')
                    .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => int.Parse(p[1]));
                if (EndlessClientList)
                    return Ok(JsonSerializer.Serialize(Enumerable.Range(query["first"], query["max"])
                        .Select(i => new { id = $"synthetic-{i}", clientId = $"synthetic-{i}" })));
                return Ok(new JsonArray([.. Clients.Skip(query["first"]).Take(query["max"]).Select(c =>
                {
                    var rep = c.Representation.DeepClone().AsObject();
                    rep["id"] = c.Id;
                    return (JsonNode)rep;
                })]).ToJsonString());
            }

            if (method == "GET" && path.StartsWith(Admin + "clients?clientId=", StringComparison.Ordinal))
            {
                if (MalformedClientLookup) return Ok("{not json");
                if (ClientLookupFailures > 0)
                {
                    ClientLookupFailures--;
                    return Status(HttpStatusCode.InternalServerError);
                }
                // Keycloak と同じく、`search=true` が無ければ完全一致（あれば部分一致）。
                var query = path[(Admin + "clients?").Length..].Split('&')
                    .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                var clientId = query["clientId"];
                var search = query.TryGetValue("search", out var v) && v == "true";
                return Ok(JsonSerializer.Serialize(Clients
                    .Where(c => search ? c.ClientId.Contains(clientId, StringComparison.OrdinalIgnoreCase) : c.ClientId == clientId)
                    .Select(c => new { id = c.Id, clientId = c.ClientId })));
            }

            if (method == "GET" && path.StartsWith(Admin + "clients/", StringComparison.Ordinal)
                && !path[(Admin + "clients/").Length..].Contains('/'))
            {
                var client = Clients.SingleOrDefault(c => c.Id == path[(Admin + "clients/").Length..]);
                if (client is null) return Status(HttpStatusCode.NotFound);
                var rep = client.Representation.DeepClone().AsObject();
                rep["id"] = client.Id;
                rep["attributes"] ??= new JsonObject();
                return Ok(rep.ToJsonString());
            }

            // ［#1829 / PR #1832 監査 🔴1］`PUT /clients/{id}`: Keycloak 24 の `ClientResource.updateClientFromRep` と同じ分岐を持つ。
            // `serviceAccountsEnabled` が TRUE でなければ（null・欠落を含む）SA の利用者を属性ごと消し、`authorizationServicesEnabled` が
            // TRUE でなければ authorization を無効にする。そのあと（`RepresentationToModel.updateClient`）送られた非 null の項目だけを変える。
            if (method == "PUT" && path.StartsWith(Admin + "clients/", StringComparison.Ordinal)
                && !path[(Admin + "clients/").Length..].Contains('/'))
            {
                var client = Clients.SingleOrDefault(c => c.Id == path[(Admin + "clients/").Length..]);
                if (client is null) return Status(HttpStatusCode.NotFound);
                if (FailClientPut) return Status(HttpStatusCode.InternalServerError);
                if (IgnoreEnabledOnPut > 0)
                {
                    IgnoreEnabledOnPut--;
                    return Status(HttpStatusCode.NoContent);
                }
                var sent = JsonNode.Parse(body!)!.AsObject();
                if (sent["serviceAccountsEnabled"]?.GetValue<bool>() != true)
                {
                    Users.Remove(client.ServiceAccountUserId);
                    client.Representation["serviceAccountsEnabled"] = false;
                }
                if (sent["authorizationServicesEnabled"]?.GetValue<bool>() != true)
                    client.Representation["authorizationServicesEnabled"] = false;
                foreach (var (key, value) in sent)
                    if (value is not null) client.Representation[key] = value.DeepClone();
                return Status(HttpStatusCode.NoContent);
            }

            if (method == "GET" && path.StartsWith(Admin + "clients/", StringComparison.Ordinal)
                && path.EndsWith("/service-account-user", StringComparison.Ordinal))
            {
                var id = path[(Admin + "clients/").Length..^"/service-account-user".Length];
                var client = Clients.SingleOrDefault(c => c.Id == id);
                return client is null || !Users.TryGetValue(client.ServiceAccountUserId, out var saUser)
                    ? Status(HttpStatusCode.NotFound) : Ok(UserJson(saUser));
            }

            if (method == "DELETE" && path.StartsWith(Admin + "clients/", StringComparison.Ordinal))
            {
                var id = path[(Admin + "clients/").Length..];
                var client = Clients.SingleOrDefault(c => c.Id == id);
                if (client is null) return Status(HttpStatusCode.NotFound);
                Clients.Remove(client);
                Users.Remove(client.ServiceAccountUserId);
                return Status(HttpStatusCode.NoContent);
            }

            if (method == "GET" && path.StartsWith(Admin + "users?username=", StringComparison.Ordinal))
            {
                var query = path[(Admin + "users?username=").Length..];
                var username = Uri.UnescapeDataString(query[..query.IndexOf('&')]);
                var matched = LookupHidesServiceAccounts
                    ? []
                    : Users.Values.Where(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)).ToList();
                return Ok("[" + string.Join(",", matched.Select(UserJson)) + "]");
            }

            if (path.StartsWith(Admin + "users/", StringComparison.Ordinal))
            {
                var id = path[(Admin + "users/").Length..];
                if (!Users.TryGetValue(id, out var user)) return Status(HttpStatusCode.NotFound);
                if (method == "GET") return Ok(UserJson(user));
                if (method == "PUT")
                {
                    if (!DropAttributesOnPut)
                        user.Attributes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                            JsonNode.Parse(body!)!["attributes"]!.ToJsonString()) ?? [];
                    return Status(HttpStatusCode.NoContent);
                }
            }

            return Status(HttpStatusCode.NotFound);
        }

        private static string UserJson(FakeUser user) => JsonSerializer.Serialize(new
        {
            id = user.Id,
            username = user.Username,
            enabled = true,
            firstName = "keep-me",
            access = new { manage = true },
            attributes = user.Attributes,
        });

        private static HttpResponseMessage Ok(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Status(HttpStatusCode status)
            => new(status) { Content = new StringContent("") };
    }

    internal sealed class StubFactory(FakeKeycloak handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(Options.BaseUrl.TrimEnd('/') + "/"),
        };
    }
}
