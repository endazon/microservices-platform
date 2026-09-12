using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-19, FR-20, UC-11, ADR-0036 D-06・D-08, ADR-0098 決定 1, ADR-0080 決定 2,
// [[IADR-0447]] 決定 4, [[IADR-0448]] (#1447 / #1448):
// **BFF の単体判定が共有先へ到達する。**
//
// #1447 が実測した穴: 共有台帳は貯蔵と管理 API までで、**共有先ベースの分岐（計画 read 規則の
// 選言の第 3 節）が認可スコープへ載っていなかった** —— グループ共有は台帳に入るが誰にも何も
// 許可しない。到達の手段は「所有者（DocumentService）が写しを運ぶ」であり、BFF は
// `DocumentDto.SharedWith` を `DocumentAttributeEncoding.WithSharedWith` で
// `shared_with` の**集合値属性**として読んでから突き合わせる。
//
// 🔴 **陰性と陽性を対で置く。** 「共有された資料が読める」だけを見ると、`IsPrivateNote` の
// 除外を丸ごと外した実装でも緑になる —— **SC-05 の一覧に現れないこと**（管理面は従前どおり
// 一律除外）と、**静的属性の分岐では読めないこと**（D-08）が残り半分の証拠である。
public class BffSharedDocumentReadTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffSharedDocumentReadTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        _factory.ScopeBranches = null;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
    }

    private static readonly Guid NoteId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    // 個人資料の実物と同じ属性（`PrivateNoteDefaults` と同じ 3 つ）＋ 共有先。
    private static DocumentDto SharedNote(List<string>? sharedWith) => new()
    {
        Id = NoteId,
        Title = "共有された個人メモ",
        Status = "published",
        MarkdownUri = "storage://bucket/note.md",
        Version = 1,
        Attributes = new Dictionary<string, string>
        {
            ["doc_scope"] = "private-note",
            ["owner"] = "someone-else",
            ["confidentiality"] = "restricted",
        },
        Tags = [],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        SharedWith = sharedWith,
    };

    private static DocumentDto OrganizationDocument() => new()
    {
        Id = BffTestFactory.StubDocumentId,
        Title = "経費規程 2025",
        Status = "published",
        MarkdownUri = "storage://bucket/expense.md",
        Version = 3,
        Attributes = new Dictionary<string, string> { ["confidentiality"] = "restricted" },
        Tags = ["hr"],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // 共有先ベースの分岐（`${current_user}` / `${current_groups}` は認可サービスが束縛済み）。
    private static AccessScopeBranch SharedWithBranch(params string[] subjects)
        => new("共有先ベース",
            [new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, [.. subjects])]);

    private static AccessScopeBranch StaticBranch()
        => new("静的属性ベース",
            [new AttributeFilter("confidentiality", ["public", "internal", "confidential", "restricted"])]);

    // 受け入れ基準 4（陽性）: 共有先に自分が含まれる個人資料は、`shared_with` 分岐を持つ
    // スコープで**詳細・本文・版のいずれからも読める**。
    [Theory]
    [InlineData("")]
    [InlineData("/versions")]
    [InlineData("/content")]
    public async Task 共有された個人資料は共有先ベースの分岐で読める(string suffix)
    {
        _factory.ScopeBranches = [SharedWithBranch("alice")];
        _factory.StubDocument = SharedNote(["alice"]);

        var resp = await _factory.CreateClient()
            .GetAsync($"/bff/documents/{NoteId}{suffix}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 受け入れ基準 4（陰性）: 共有先が自分と交わらなければ 404（存在秘匿。403 にしない）。
    [Fact]
    public async Task 共有先に含まれない相手には404である()
    {
        _factory.ScopeBranches = [SharedWithBranch("alice")];
        _factory.StubDocument = SharedNote(["bob"]);

        var resp = await _factory.CreateClient()
            .GetAsync($"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 受け入れ基準 4（陰性）: 共有が 1 件も無い個人資料は 404。
    // 🔴 **`SharedWith = null` で `shared_with` 属性が載らない**（空集合は載せない）ため、
    // 「属性キーの欠落は不一致」（欠落は安全側）で落ちる。
    [Fact]
    public async Task 共有が無い個人資料は404である()
    {
        _factory.ScopeBranches = [SharedWithBranch("alice")];
        _factory.StubDocument = SharedNote(null);

        var resp = await _factory.CreateClient()
            .GetAsync($"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ADR-0080 決定 2 / #1448: **集合値は交差で判定する。** 共有先が複数（利用者 ＋ グループ）の
    // とき、**どれか 1 つが交わればよい** —— 従前の単一文字列比較では `"alice,g-1"` が
    // `{g-1}` に 1 件も一致しなかった。
    [Theory]
    [InlineData("alice", true)]
    [InlineData("g-1", true)]
    [InlineData("g-2", false)]
    public async Task 共有先が複数のときは交差で判定する(string subject, bool readable)
    {
        _factory.ScopeBranches = [SharedWithBranch(subject)];
        _factory.StubDocument = SharedNote(["alice", "g-1"]);

        var resp = await _factory.CreateClient()
            .GetAsync($"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(readable ? HttpStatusCode.OK : HttpStatusCode.NotFound);
    }

    // 🔴 陰性対照（ADR-0036 D-08 / ADR-0061 決定 6）: **静的属性の分岐では読めない。**
    // 「restricted 取扱者は全区分を読める」型のポリシーは個人資料の属性にそのまま合致するが、
    // 裁量の分岐（`owner` / `shared_with`）でなければ個人資料を許可してはならない。
    // これが無いと「個人資料の除外を丸ごと外した」実装でも上の陽性が緑になる。
    [Fact]
    public async Task 静的属性の分岐では共有された個人資料も読めない()
    {
        _factory.ScopeBranches = [StaticBranch()];
        _factory.StubDocument = SharedNote(["alice"]);

        var resp = await _factory.CreateClient()
            .GetAsync($"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // [[IADR-0447]] 決定 4 の副作用（監査 🟡 の写像）: **`owner` 分岐が一致する所有者自身の個人資料も
    // 同じ経路で読める**（従前は `IsManageable` の一律除外で 404 だった。ADR-0036 D-05 の範囲内）。
    // `${current_user}` は評価器が束縛済みで、BFF へは値（利用者名）として届く。
    // 陰性: `owner` が別人なら 404 のまま（`owner` 分岐は「所有者にだけ」効く）。
    [Theory]
    [InlineData("someone-else", HttpStatusCode.OK)]
    [InlineData("another-user", HttpStatusCode.NotFound)]
    public async Task 所有者分岐が一致する自分の個人資料は読める(string boundOwner, HttpStatusCode expected)
    {
        _factory.ScopeBranches =
            [new AccessScopeBranch("owner", [new AttributeFilter("owner", [boundOwner])])];
        _factory.StubDocument = SharedNote(null);

        var resp = await _factory.CreateClient()
            .GetAsync($"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(expected);
    }

    // 🔴 陰性対照（従前どおり）: **SC-05 の一覧には共有された個人資料も現れない。**
    // 一覧は組織文書の管理面（`IsManageable` の一律除外）であり、個人資料は SC-19 が持つ。
    [Fact]
    public async Task SC05の一覧に共有された個人資料は現れない()
    {
        _factory.ScopeBranches = [SharedWithBranch("alice"), StaticBranch()];
        _factory.StubDocumentList = [SharedNote(["alice"]), OrganizationDocument()];

        var body = (await _factory.CreateClient().GetFromJsonAsync<List<DocumentDto>>(
            "/bff/documents", TestContext.Current.CancellationToken))!;

        body.Select(d => d.Id).Should().NotContain(NoteId);
        // 陽性対照: 同じ呼び出しで組織文書は現れる（「一覧を常に空にする」実装を落とす）。
        body.Select(d => d.Id).Should().Contain(BffTestFactory.StubDocumentId);
    }

    // 🔴 陽性対照: 組織文書の判定は 1 ビットも変わらない（`shared_with` を持たない文書が
    // 静的分岐で読める）。像の合成（`WithSharedWith`）が既存の属性を壊していないことの固定。
    [Fact]
    public async Task 組織文書は従前どおり静的属性の分岐で読める()
    {
        _factory.ScopeBranches = [StaticBranch()];
        _factory.StubDocument = OrganizationDocument();

        var resp = await _factory.CreateClient().GetAsync(
            $"/bff/documents/{BffTestFactory.StubDocumentId}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // #1448 の述語そのもの（契約側）。**BFF の HTTP 経路と同じ答えを出すことの単体の固定点**で、
    // `BffScopeResolver.MatchesAll` の集合値対応（platform 側）とは独立に緑である。
    [Theory]
    [InlineData("alice,g-1", new[] { "g-1" }, true)]
    [InlineData("alice,g-1", new[] { "alice" }, true)]
    [InlineData("alice,g-1", new[] { "g-2" }, false)]
    [InlineData("", new[] { "alice" }, false)]
    public void 契約側の述語は集合値を交差で突き合わせる(string joined, string[] allowed, bool expected)
    {
        var attributes = new Dictionary<string, string>
        {
            [DocumentAttributeEncoding.SharedWithKey] = joined,
        };

        AttributeFilterMatch.MatchesAll(attributes,
            [new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, [.. allowed])])
            .Should().Be(expected);
    }

    // [[IADR-0447]] 決定 4: **像の合成は元の属性辞書を変えない。**
    // `DocumentDto.Attributes` は書き戻しの入力にもなるため、共有先を属性へ混ぜて保存させない。
    [Fact]
    public void 像の合成は元の属性辞書を変えない()
    {
        var doc = SharedNote(["alice"]);

        var view = DocumentAttributeEncoding.WithSharedWith(doc.Attributes, doc.SharedWith);

        view.Should().ContainKey(DocumentAttributeEncoding.SharedWithKey);
        doc.Attributes.Should().NotContainKey(DocumentAttributeEncoding.SharedWithKey);
    }

    // FR-19, ADR-0098 フォローアップ 5（利用者裁定 planning#626）, [[IADR-0450]] (#1451):
    // **共有先の写しは所有者にだけ返す。** 陽性: `owner` 分岐で読んだ所有者には `sharedWith` がそのまま返る。
    [Fact]
    public async Task 所有者には共有先の写しが返る()
    {
        _factory.ScopeBranches =
            [new AccessScopeBranch("owner", [new AttributeFilter("owner", ["someone-else"])])];
        _factory.StubDocument = SharedNote(["alice", "11111111-1111-1111-1111-111111111111"]);

        var body = await _factory.CreateClient().GetFromJsonAsync<DocumentDto>(
            $"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);

        body!.SharedWith.Should().Equal("alice", "11111111-1111-1111-1111-111111111111");
    }

    // 陰性: 共有先ベースの分岐だけで読めた相手（所有者ではない）には、読めることは変わらないが
    // `sharedWith` は項目ごと落ちる（他の共有先の識別子を見せない）。他の項目は不変（陽性対照）。
    [Fact]
    public async Task 共有された相手には共有先の写しを返さない()
    {
        _factory.ScopeBranches = [SharedWithBranch("alice")];
        _factory.StubDocument = SharedNote(["alice", "bob"]);

        var body = await _factory.CreateClient().GetFromJsonAsync<DocumentDto>(
            $"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);

        body!.SharedWith.Should().BeNull();
        body.Id.Should().Be(NoteId);
        body.Title.Should().Be("共有された個人メモ");
    }

    // 所有者分岐と共有先分岐の両方を持つスコープ（実運用の形）: 所有者分岐が一致した閲覧者にだけ返る。
    // 陰性側は `owner` 分岐が別人に束縛されており、共有先分岐だけで読めている。
    [Theory]
    [InlineData("someone-else", true)]
    [InlineData("another-user", false)]
    public async Task 両分岐を持つスコープでは所有者分岐の一致で写しの有無が決まる(string boundOwner, bool visible)
    {
        _factory.ScopeBranches =
            [SharedWithBranch("alice"), new AccessScopeBranch("owner", [new AttributeFilter("owner", [boundOwner])])];
        _factory.StubDocument = SharedNote(["alice"]);

        var body = await _factory.CreateClient().GetFromJsonAsync<DocumentDto>(
            $"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);

        (body!.SharedWith is not null).Should().Be(visible);
    }

    // 🔴 「共有の有無」の 1 ビットも漏らさない: 空集合を運ぶ応答でも、所有者以外には `null` に畳む。
    // 組織文書（静的分岐で読める）を使い、読めること自体は変わらないことも固定する。
    [Fact]
    public async Task 所有者以外には空集合も項目ごと落とす()
    {
        _factory.ScopeBranches = [StaticBranch()];
        _factory.StubDocument = OrganizationDocument() with { SharedWith = [] };

        var body = await _factory.CreateClient().GetFromJsonAsync<DocumentDto>(
            $"/bff/documents/{BffTestFactory.StubDocumentId}", TestContext.Current.CancellationToken);

        body!.SharedWith.Should().BeNull();
    }

    // SC-05 の一覧（管理面）も同じ 1 点を通る: 管理者・運用者は所有者ではないので写しは返らない。
    [Fact]
    public async Task SC05の一覧でも所有者以外には写しを返さない()
    {
        _factory.ScopeBranches = [StaticBranch()];
        _factory.StubDocumentList = [OrganizationDocument() with { SharedWith = ["alice"] }];

        var body = (await _factory.CreateClient().GetFromJsonAsync<List<DocumentDto>>(
            "/bff/documents", TestContext.Current.CancellationToken))!;

        body.Should().ContainSingle().Which.SharedWith.Should().BeNull();
    }
}
