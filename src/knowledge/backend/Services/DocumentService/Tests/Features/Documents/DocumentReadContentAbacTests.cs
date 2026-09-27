using System.Security.Claims;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents;
using Platform.Shared.Contracts.Dtos;

namespace DocumentService.Tests.Features.Documents;

// FR-05, FR-19, NFR-09, 計画 ADR-0121 決定 2・4・5, ADR-0119 決定 3, ADR-0036 D-01・D-08, ADR-0034 決定 9 (#1615):
// **読み取りの判定点（`DocumentReadAccess`）が、内容の ABAC の門で 2 つの判定を切り替える**ことを固定する。
//
// - 門が閉じている間は、#1614 の判定を 1 ビットも変えない（組織文書は全主体に返り、個人資料は所有者・利用者共有を認可サービスへ問わずに返す）。
// - 門が開いたら、判定は認可サービスの分岐だけで行う（所有者・利用者共有・グループ共有・機密・部門のすべて）。
//
// 🔴 陰性は必ず陽性対照と対にする（「常に偽」の実装でも陰性だけは緑になる）。
// 分岐は、可能な限り dev seed を入れた認可サービスの応答の期待値（`OwnerReadSeedScopes`）を入力にする。
[Trait("TestKind", "Unit")]
public class DocumentReadContentAbacTests
{
    private const string KbWriter = "service-account-ai-stock-trading-kb-writer";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Document Doc(Dictionary<string, string> attributes)
        => Document.Create($"doc-{Guid.NewGuid():N}", null, null, attributes);

    private static Document Org(string confidentiality, string? owner = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = confidentiality,
            ["doc_scope"] = "organization",
        };
        if (owner is not null) attributes["owner"] = owner;
        return Doc(attributes);
    }

    private static Document PrivateNote(string owner) => Doc(new Dictionary<string, string>
    {
        ["confidentiality"] = "restricted",
        ["doc_scope"] = "private-note",
        ["owner"] = owner,
    });

    private static ClaimsPrincipal Authenticated(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    private static (DocumentReadAccess Access, StubDocumentReadScopeSource Scopes, StubContentAbacGate Gate) Open()
    {
        var scopes = new StubDocumentReadScopeSource();
        var gate = new StubContentAbacGate(open: true);
        return (new DocumentReadAccess(scopes, gate), scopes, gate);
    }

    private static (DocumentReadAccess Access, StubDocumentReadScopeSource Scopes, StubContentAbacGate Gate) Closed()
    {
        var scopes = new StubDocumentReadScopeSource();
        var gate = new StubContentAbacGate();
        return (new DocumentReadAccess(scopes, gate), scopes, gate);
    }

    // ── AC-1: 門が閉じている間は判定を変えない ──────────────────────────────────

    // T-54: 機密の組織文書は、機械・属性の無い利用者・名前の無い主体にも返る（内容の ABAC は効かない）。認可サービスを問わない。
    [Fact]
    public async Task 門が閉じている間は機密の組織文書が誰にでも返り認可サービスを問わない()
    {
        var (access, scopes, _) = Closed();
        var restricted = Org("restricted", owner: "someone-else");

        (await access.CanReadAsync(DocumentReadPrincipal.RelayedUser(KbWriter), restricted, null, Ct)).Should().BeTrue();
        (await access.CanReadAsync(DocumentReadPrincipal.RelayedUser("alice"), restricted, null, Ct)).Should().BeTrue();
        (await access.CanReadAsync(DocumentReadPrincipal.FromUser(Authenticated()), restricted, null, Ct)).Should().BeTrue();

        access.ContentAbacEnabled.Should().BeFalse();
        scopes.CallsFor(KbWriter).Should().Be(0);
        scopes.CallsFor("alice").Should().Be(0);
    }

    // T-54: 個人資料は、所有者・利用者の共有先に認可サービスを問わずに返り、他人には返らない（#1614 のまま）。
    [Fact]
    public async Task 門が閉じている間は個人資料の所有者と利用者共有をコードで判定する()
    {
        var (access, scopes, _) = Closed();
        var note = PrivateNote("alice");

        (await access.CanReadAsync(DocumentReadPrincipal.RelayedUser("alice"), note, null, Ct)).Should().BeTrue();
        (await access.CanReadAsync(DocumentReadPrincipal.RelayedUser("bob"), note, ["bob"], Ct)).Should().BeTrue();
        (await access.CanReadAsync(DocumentReadPrincipal.RelayedUser("mallory"), note, null, Ct)).Should().BeFalse();

        scopes.CallsFor("alice").Should().Be(0);
        scopes.CallsFor("bob").Should().Be(0, "利用者の共有先は台帳だけで決まる（門が閉じている間）");
        // ［2026-09-28 / #1676］他人の・共有なしの個人資料は、認可サービスへ問うまでもなく読めない（共有が 0 件なら所有者しか読まない）。
        // 問い合わせの有無は結果に出ない（スタブの既定は「許可なし」）ので、回数で固定する。
        scopes.CallsFor("mallory").Should().Be(0, "共有なしの他人の個人資料では、閉じた枝は認可サービスを問わない");
    }

    // ── AC-2: 門が開くと、属性の合わない主体には機密・制限の組織文書が返らない ─────────

    // T-55: seed の bob（clearance=internal）は internal を読めるが restricted は読めない。自分の restricted は所有者の分岐で読める。
    [Fact]
    public async Task 門が開くと属性の合わない利用者には機密の組織文書が返らない()
    {
        var (access, scopes, _) = Open();
        scopes.Grant("bob", OwnerReadSeedScopes.BranchesOf("bob"));
        var bob = DocumentReadPrincipal.RelayedUser("bob");

        (await access.CanReadAsync(bob, Org("internal"), null, Ct)).Should().BeTrue("陽性対照: 階段の分岐");
        (await access.CanReadAsync(bob, Org("restricted", owner: "carol"), null, Ct)).Should().BeFalse();
        (await access.CanReadAsync(bob, Org("restricted", owner: "bob"), null, Ct)).Should().BeTrue("所有者の分岐");
        (await access.CanReadAsync(bob, Org("public", owner: "system"), null, Ct)).Should().BeTrue("陽性対照: public は階段で読める");

        scopes.CallsFor("bob").Should().Be(1, "主体ごとに要求の中で高々 1 回");
    }

    // T-55: 属性の無い利用者（seed の alice）は、他人の組織文書を区分に依らず読めない。
    [Fact]
    public async Task 門が開くと属性の無い利用者は他人の組織文書を読めない()
    {
        var (access, scopes, _) = Open();
        scopes.Grant("alice", OwnerReadSeedScopes.BranchesOf("alice"));
        var alice = DocumentReadPrincipal.RelayedUser("alice");

        (await access.CanReadAsync(alice, Org("public", owner: "carol"), null, Ct)).Should().BeFalse();
        (await access.CanReadAsync(alice, Org("public", owner: "alice"), null, Ct)).Should().BeTrue("陽性対照: 自分の文書");
    }

    // ── AC-3: 所有者の判定も認可サービスが答える（コード判定を残さない） ───────────────

    // T-56: 所有者の read の分岐が無ければ、所有者でも自分の個人資料・組織文書を読めない（ADR-0121 §結果 のトレードオフ）。
    [Fact]
    public async Task 門が開くと所有者の分岐が無い所有者は自分の文書を読めない()
    {
        var (access, scopes, _) = Open();
        // 所有者の read ポリシーが消えた構成（共有先の分岐だけ）。
        scopes.Grant("alice", [[new AttributeFilter("shared_with", ["alice"])]]);
        var alice = DocumentReadPrincipal.RelayedUser("alice");

        (await access.CanReadAsync(alice, PrivateNote("alice"), null, Ct)).Should().BeFalse();
        (await access.CanReadAsync(alice, Org("public", owner: "alice"), null, Ct)).Should().BeFalse();
        scopes.CallsFor("alice").Should().Be(1, "所有者でも認可サービスへ問う（ADR-0121 決定 5）");
    }

    // T-56: 陽性対照 —— seed の所有者の分岐があれば、所有者は自分の個人資料を読める（認可サービスの答えで）。
    [Fact]
    public async Task 門が開くと所有者の分岐で自分の個人資料を読める()
    {
        var (access, scopes, _) = Open();
        scopes.Grant("alice", OwnerReadSeedScopes.BranchesOf("alice"));

        (await access.CanReadAsync(DocumentReadPrincipal.RelayedUser("alice"), PrivateNote("alice"), null, Ct))
            .Should().BeTrue();
        (await access.CanReadAsync(DocumentReadPrincipal.RelayedUser("alice"), PrivateNote("bob"), null, Ct))
            .Should().BeFalse("他人の個人資料");
        scopes.CallsFor("alice").Should().Be(1);
    }

    // ── AC-4: 利用者共有・グループ共有も分岐で答える。静的属性の分岐は他人の個人資料を開かない ─────

    // T-57
    [Fact]
    public async Task 門が開くと共有先は認可サービスの共有先の分岐で判定する()
    {
        var (access, scopes, _) = Open();
        var group = Guid.NewGuid().ToString("D");
        scopes.Grant("bob", OwnerReadSeedScopes.BranchesOf("bob"));
        scopes.GrantSharedWith("member", "member", group);
        var note = PrivateNote("carol");

        (await access.CanReadAsync(DocumentReadPrincipal.RelayedUser("bob"), note, ["bob"], Ct))
            .Should().BeTrue("利用者の共有先（${current_user} の束縛）");
        (await access.CanReadAsync(DocumentReadPrincipal.RelayedUser("member"), note, [group], Ct))
            .Should().BeTrue("グループの共有先（${current_groups} の束縛）");
        (await access.CanReadAsync(DocumentReadPrincipal.RelayedUser("bob"), note, [group], Ct))
            .Should().BeFalse("共有されていない");
    }

    // T-57: D-08 —— 静的属性だけの分岐（restricted を読める管理者）は、他人の個人資料を開かない。組織文書は開く（陽性対照）。
    [Fact]
    public async Task 門が開いても静的属性の分岐は他人の個人資料を開かない()
    {
        var (access, scopes, _) = Open();
        scopes.Grant("admin", [[new AttributeFilter("confidentiality", ["restricted"])]]);
        var admin = DocumentReadPrincipal.RelayedUser("admin");

        (await access.CanReadAsync(admin, PrivateNote("alice"), null, Ct)).Should().BeFalse();
        (await access.CanReadAsync(admin, Org("restricted"), null, Ct)).Should().BeTrue("陽性対照");
    }

    // ── AC-5: 機械の主体は個人資料を読まない ────────────────────────────────────────

    // T-58: 分岐が一致しても（所有者の分岐・静的属性の分岐）、機械の主体には個人資料を返さない。組織文書は返る（陽性対照）。
    [Fact]
    public async Task 門が開いても機械の主体は個人資料を読まない()
    {
        var (access, scopes, _) = Open();
        scopes.Grant(KbWriter, [
            [new AttributeFilter("owner", [KbWriter])],
            [new AttributeFilter("confidentiality", ["restricted"])],
        ]);
        var machine = DocumentReadPrincipal.RelayedUser(KbWriter);

        (await access.CanReadAsync(machine, PrivateNote(KbWriter), null, Ct)).Should().BeFalse();
        (await access.CanReadAsync(machine, Org("restricted", owner: KbWriter), null, Ct)).Should().BeTrue("陽性対照");
    }

    // T-69（#1676）: **利用者文脈の無い gRPC 呼び出し**（主体は呼び出し元サービス自身。ここでは `service-account-bff`）は、
    // 門が開いた枝で、その名前を所有者・共有先に持つ個人資料を読めない。共有先の subjectId には所有者が任意の値を入れられるので、
    // サービスアカウント名を共有先に入れた個人資料が機械へ漏れる経路になり得る。組織文書は分岐のとおり読める（陽性対照）。
    // 分岐は seed の所有者・共有先のポリシーを、この主体の名前で束縛した形（認可サービスの答えの形）で与える。
    [Fact]
    public async Task 門が開いても利用者文脈の無いgRPC呼び出しは自分の名前を所有者と共有先に持つ個人資料を読めない()
    {
        const string Bff = "service-account-bff";
        var (access, scopes, _) = Open();
        scopes.Grant(Bff, [
            [new AttributeFilter("shared_with", [Bff])],
            [new AttributeFilter("owner", [Bff])],
        ]);
        var caller = Authenticated(new Claim(ClaimTypes.Name, Bff), new Claim("azp", "bff"));
        var principal = DocumentReadGrpcService.PrincipalOf(null, caller, new DocumentReadRelayOptions());

        principal.IsMachine.Should().BeTrue();
        principal.AbacSubject.Should().Be(Bff);
        (await access.CanReadAsync(principal, PrivateNote(Bff), null, Ct)).Should().BeFalse("所有者に自分の名前");
        (await access.CanReadAsync(principal, PrivateNote("carol"), [Bff], Ct)).Should().BeFalse("共有先に自分の名前");
        (await access.CanReadAsync(principal, Org("internal", owner: Bff), null, Ct)).Should().BeTrue("陽性対照: 所有する組織文書");
        scopes.CallsFor(Bff).Should().Be(1, "分岐は実際に引かれている");
    }

    // ── AC-6: 認可サービスが答えない・主体が決まらないときは何も読めない ─────────────────

    // T-59: 分岐が引けない（null ＝ 許可なし・不達・時間切れ・未構成）なら、自分の個人資料も public の組織文書も読めない。
    [Fact]
    public async Task 門が開くと認可サービスが答えないとき何も読めない()
    {
        var (access, scopes, _) = Open(); // alice には何も与えない ＝ null
        var alice = DocumentReadPrincipal.RelayedUser("alice");

        (await access.CanReadAsync(alice, PrivateNote("alice"), null, Ct)).Should().BeFalse();
        (await access.CanReadAsync(alice, Org("public", owner: "alice"), null, Ct)).Should().BeFalse();
        scopes.CallsFor("alice").Should().Be(1, "問い合わせた上で読めない（段を飛ばしたのではない）");
    }

    // T-59: 名前の分からない主体（人の名前も、機械のクライアント識別も無い）は、認可サービスを問わずに何も読めない。
    [Fact]
    public async Task 門が開くと名前の分からない主体は何も読めない()
    {
        var (access, scopes, _) = Open();
        scopes.Grant("", [[new AttributeFilter("confidentiality", ["public"])]]);

        (await access.CanReadAsync(DocumentReadPrincipal.FromUser(Authenticated()), Org("public"), null, Ct))
            .Should().BeFalse();
        scopes.CallsFor("").Should().Be(0);
    }

    // ── AC-9: 機械の主体の名前は、作成時に owner へ入れる名前と同じ ──────────────────

    // T-60: AST の KB の書き手（腕 A: `service-account-…` の利用者名 ／ 腕 B: クライアント識別だけ）は、seed の分岐の下で
    // 自分が owner の写しを読める。他人・`system`・`owner` の欠落の組織文書は読めない。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 門が開いてもASTのKBの書き手は自分が所有する写しを読める(bool withUsername)
    {
        var (access, scopes, _) = Open();
        scopes.Grant(KbWriter, OwnerReadSeedScopes.BranchesOf(KbWriter));
        var claims = withUsername
            ? new[] { new Claim(ClaimTypes.Name, KbWriter), new Claim("azp", "ai-stock-trading-kb-writer") }
            : [new Claim("azp", "ai-stock-trading-kb-writer")];
        var principal = DocumentReadPrincipal.FromUser(Authenticated(claims));

        principal.IsMachine.Should().BeTrue();
        principal.AbacSubject.Should().Be(KbWriter);
        (await access.CanReadAsync(principal, Org("internal", owner: KbWriter), null, Ct)).Should().BeTrue();
        (await access.CanReadAsync(principal, Org("public", owner: "system"), null, Ct)).Should().BeFalse();
        (await access.CanReadAsync(principal, Org("public"), null, Ct)).Should().BeFalse("owner の欠落");
        (await access.CanReadAsync(principal, Org("public", owner: "alice"), null, Ct)).Should().BeFalse();
    }

    // ── AC-11: 門は要求の中で最初に読んだ値に固定する ──────────────────────────────

    // T-61: 1 要求の途中で門が開いても、その要求の判定は閉じたまま。門は 1 度しか読まない。次の要求（新しいインスタンス）から開く。
    [Fact]
    public async Task 門は要求の中で最初に読んだ状態に固定する()
    {
        var scopes = new StubDocumentReadScopeSource();
        var gate = new StubContentAbacGate();
        var access = new DocumentReadAccess(scopes, gate);
        var restricted = Org("restricted");
        var machine = DocumentReadPrincipal.RelayedUser(KbWriter);

        (await access.CanReadAsync(machine, restricted, null, Ct)).Should().BeTrue();
        gate.Open();
        (await access.CanReadAsync(machine, restricted, null, Ct)).Should().BeTrue("同じ要求の中では閉じた判定のまま");
        gate.Reads.Should().Be(1);

        var next = new DocumentReadAccess(scopes, gate);
        (await next.CanReadAsync(machine, restricted, null, Ct)).Should().BeFalse("次の要求から内容の ABAC が効く");
    }
}
