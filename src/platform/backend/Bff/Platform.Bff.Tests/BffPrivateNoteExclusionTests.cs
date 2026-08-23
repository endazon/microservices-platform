using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-19, ADR-0036 D-08 (#1009): SC-05 文書管理の経路に個人資料を出さない。
//
// 🔴 **ABAC のスコープ判定だけでは足りない。** `BffScopeResolver.Matches` は `scope.Filters` に
// 現れたキーだけを見るため、`doc_scope` を条件に持たないポリシー（＝現行の全ポリシー）では
// この属性は判定に一切効かない。個人資料は `confidentiality=restricted` で作られるので、
// 「restricted 取扱者は全区分を読める」型のポリシーに**そのまま合致する**。
//
// ADR-0036 D-08 は「管理者・運用者は平時、非公開の個人資料を**一切閲覧できない**」と定め、
// 第三者の発動経路を管理者を含めて設けないとしている。
//
// **陽性対照を必ず対で置く** —— 「常に 404 を返す」「一覧を常に空にする」実装でも
// 陰性だけなら緑になる。
public class BffPrivateNoteExclusionTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffPrivateNoteExclusionTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        // 「restricted 取扱者は全区分を読める」型の実ポリシーを再現する。
        // 🔴 doc_scope を条件に持たない —— これが投入済みポリシーの実際の形である。
        _factory.ScopeFilters =
            [new AttributeFilter("confidentiality", ["public", "internal", "confidential", "restricted"])];
        _factory.DocumentStatusCode = HttpStatusCode.OK;
    }

    private static readonly Guid PrivateNoteId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    // 個人資料の実物と同じ属性で作る（PrivateNoteDefaults と同じ 3 つ）。
    private static DocumentDto PrivateNote() => new()
    {
        Id = PrivateNoteId,
        Title = "他人の個人メモ",
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
    };

    private static DocumentDto OrganizationDocument() => new()
    {
        Id = BffTestFactory.StubDocumentId,
        Title = "経費規程 2025",
        Status = "published",
        MarkdownUri = "storage://bucket/expense.md",
        Version = 3,
        // 🔴 個人資料と**同じ機密区分**にする。区分では区別できないことを固定するため。
        Attributes = new Dictionary<string, string> { ["confidentiality"] = "restricted" },
        Tags = ["hr"],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // 陰性: 一覧に他人の個人資料が現れない。
    [Fact]
    public async Task List_does_not_expose_a_private_note()
    {
        _factory.StubDocumentList = [PrivateNote(), OrganizationDocument()];

        var body = await _factory.CreateClient()
            .GetFromJsonAsync<List<DocumentDto>>("/bff/documents");

        body!.Select(d => d.Id).Should().NotContain(PrivateNoteId);
    }

    // 🔴 陽性対照: 同じ機密区分の組織文書は**現れる**。
    // これが無いと「一覧を常に空にする」実装でも上の陰性が緑になる。
    [Fact]
    public async Task List_still_exposes_an_organization_document_of_the_same_confidentiality()
    {
        _factory.StubDocumentList = [PrivateNote(), OrganizationDocument()];

        var body = await _factory.CreateClient()
            .GetFromJsonAsync<List<DocumentDto>>("/bff/documents");

        body!.Select(d => d.Id).Should().Contain(BffTestFactory.StubDocumentId);
    }

    // 陰性: 詳細・版履歴・本文のいずれでも 404（存在秘匿。403 にはしない）。
    [Theory]
    [InlineData("")]
    [InlineData("/versions")]
    [InlineData("/content")]
    public async Task Reading_a_private_note_is_indistinguishable_from_absence(string suffix)
    {
        _factory.StubDocument = PrivateNote();

        var resp = await _factory.CreateClient()
            .GetAsync($"/bff/documents/{PrivateNoteId}{suffix}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 🔴 陽性対照: 同じ経路で組織文書は読める。
    // これが無いと「常に 404」の実装でも上の 3 件が緑になる。
    [Theory]
    [InlineData("")]
    [InlineData("/versions")]
    [InlineData("/content")]
    public async Task Reading_an_organization_document_still_succeeds(string suffix)
    {
        _factory.StubDocument = OrganizationDocument();

        var resp = await _factory.CreateClient()
            .GetAsync($"/bff/documents/{BffTestFactory.StubDocumentId}{suffix}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 陽性対照: 判定は**集合帰属**である。`doc_scope` を持たない既存文書は組織文書として扱う
    // （否定形で書くと、遡及付与しない既存文書が全部 個人資料 に化けて一覧が空になる。
    // ADR-0054 決定 5）。
    [Fact]
    public async Task A_document_without_doc_scope_is_treated_as_an_organization_document()
    {
        var legacy = OrganizationDocument();
        legacy.Attributes.Should().NotContainKey("doc_scope");
        _factory.StubDocument = legacy;

        var resp = await _factory.CreateClient()
            .GetAsync($"/bff/documents/{BffTestFactory.StubDocumentId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
