using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Api.Tests;

// FR-06, FR-19, UC-03, SC-05, ADR-0054, ADR-0058 決定 1・2, [[IADR-0278]]:
// **`doc_scope` は作成時に確定し、以後変更できない。**
//
// 🔴 **何を守っているか。** 実装は作成経路（`POST /documents`）を「台帳を持たない個人資料が
// できると容量算入（FR-19）から漏れる」という理由で塞いでいたが、**更新経路は値域しか
// 見ておらず、同じ結果を作れた**。組織文書を `private-note` に変えると、Wiki.js から消え・
// 健全性の母数から外れ・MCP から見えなくなる一方で、**台帳が無いため SC-19 の一覧に現れず、
// 誰の容量にも算入されず、90 日ライフサイクルにも乗らない** —— FR-19 が前提としていない
// 「所有者の無い個人資料」ができる。
//
// 🔴 **陽性対照が要る理由（これが本テストの要点である）。**
// 「`doc_scope` を含む更新を拒否」と実装すると陰性はすべて緑になるが、**SC-05 の通常の保存が
// 全部壊れる** —— 属性編集フォームは既存属性をスプレッドして送るため、機密区分だけを変える
// 保存でも `doc_scope` が同送されるからである。**陽性対照はその実装を落とすために置く。**
public class DocScopeImmutabilityTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client() => factory.CreateClient();

    private async Task<DocumentDto> CreateAsync(string? docScope)
    {
        var attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" };
        if (docScope is not null) attributes["doc_scope"] = docScope;

        var resp = await Client().PostAsJsonAsync("/documents", new
        {
            title = $"文書スコープ検証 {Guid.NewGuid():N}",
            attributes,
            tags = new List<string>(),
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
    }

    private Task<HttpResponseMessage> UpdateAsync(
        DocumentDto doc, Dictionary<string, string> attributes) =>
        Client().PutAsJsonAsync($"/documents/{doc.Id}", new
        {
            title = doc.Title,
            attributes,
            tags = new List<string>(),
        });

    private Task<HttpResponseMessage> PatchAsync(
        DocumentDto doc, Dictionary<string, string> attributes) =>
        Client().PatchAsJsonAsync($"/documents/{doc.Id}/metadata", new
        {
            attributes,
            tags = new List<string>(),
        });

    // 陰性 1: 組織文書を個人資料へ変えられない（環流 planning#472 が実測した抜け道）。
    [Fact]
    public async Task 組織文書を個人資料へ変えられない()
    {
        var doc = await CreateAsync("organization");

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["doc_scope"] = "private-note",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "台帳・容量算入・90 日ライフサイクルを持たない個人資料ができる");
    }

    // 陰性 2: メタデータ経路でも同じである（経路を 1 つ塞いでも隣が開いていたのが元の欠陥）。
    [Fact]
    public async Task メタデータ経路でも個人資料へ変えられない()
    {
        var doc = await CreateAsync("organization");

        var resp = await PatchAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["doc_scope"] = "private-note",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 陰性 3: **属性は全置換であるため、既存値の削除も変更である。**
    // `private-note` を落とすと、集合帰属の判定（== private-note）で組織文書へ化ける。
    [Fact]
    public async Task 既存の文書スコープを落とす更新も拒否される()
    {
        var doc = await CreateAsync("organization");

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "全置換なので、キーを落とすことは値を変えることと同じ結果になる");
    }

    // 陰性 4: **後からの新規付与も拒否する**（作成時に確定するという決定 1 に反する）。
    [Fact]
    public async Task 文書スコープを持たない文書へ後から付与できない()
    {
        var doc = await CreateAsync(docScope: null);

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["doc_scope"] = "organization",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 🔴 陽性対照 1（本テストの要点）: **同じ値の同送は通る。**
    // SC-05 の属性編集フォームは既存属性をスプレッドして送るため、機密区分だけを変える
    // 通常の保存でも `doc_scope` が同送される。「存在で弾く」実装はここで落ちる。
    [Fact]
    public async Task 同じ文書スコープを同送する機密区分の変更は通る()
    {
        var doc = await CreateAsync("organization");

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "confidential",
            ["doc_scope"] = "organization",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
        updated.Attributes["confidentiality"].Should().Be("confidential",
            "拒否が「何も更新できない」に化けていないこと");
        updated.Attributes["doc_scope"].Should().Be("organization");
    }

    // 🔴 陽性対照 2: **文書スコープを持たない既存文書の更新は通る。**
    // 既存文書へ遡及付与しない方針（ADR-0054 決定 5）と衝突させない。
    [Fact]
    public async Task 文書スコープを持たない文書の更新は通る()
    {
        var doc = await CreateAsync(docScope: null);

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "restricted",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 陽性対照 3: メタデータ経路でも同値の同送は通る。
    [Fact]
    public async Task メタデータ経路でも同じ文書スコープの同送は通る()
    {
        var doc = await CreateAsync("organization");

        var resp = await PatchAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "confidential",
            ["doc_scope"] = "organization",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 値域検証は残っている（不変性検証がそれを置き換えたのではない）。
    [Fact]
    public async Task 未知の文書スコープは従来どおり値域で弾かれる()
    {
        var doc = await CreateAsync("organization");

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["doc_scope"] = "team-shared",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
