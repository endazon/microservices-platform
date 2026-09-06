using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.Documents;

// FR-06, FR-16, UC-03, SC-05, AST/ADR-0032 決定 2, [[IADR-0373]] 決定 1・3, [[IADR-0405]] 決定 2 (#1233):
// **文書が現に持つ制限 project の値は、保存で外せない。**
//
// 🔴 **何を守っているか。** MCP の外部エージェント経路からの除外は
// `ServiceAccountDocumentFilter` が `attributes["project"]` の集合帰属で行う。属性は**全置換**
// なので、SC-05 の保存で `project` を落とすだけで除外は 1 件も効かなくなる ——
// 「割当禁止＋後段除外」で守ったはずの統制が、**文書側の 1 回の保存で消える**。
//
// 🔴 **陽性対照が要る理由。** ここを「`project` を必須にする」「`project` を不変にする」と
// 実装しても陰性はすべて緑になるが、**制限の射程外の文書の挙動が変わる** ——
// 計画は `project` を**任意**と定めており（07_abac-attribute-model §文書の基本属性）、
// 必須化の射程を AST ユニットの文書に限っているのは `AST/ADR-0032` 決定 2 (1) 自身である。
// **陽性対照はその 2 つの実装を落とすために置く。**
[Trait("TestKind", "Integration")]
public class RestrictedProjectRetentionTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // AST/ADR-0032 決定 2 (1) が定める制限プロジェクトのコード。
    private const string Restricted = "ai-stock-trading";

    private HttpClient Client() => factory.CreateClient();

    // 応答本文の `errors` を**列挙順のまま** `"<鍵>=<メッセージ列を | で連結>"` へ写す。
    // `ValidationProblemContractTests` と同じ形にしてある（鍵の列・順序を 1 つの比較で見る）。
    private static async Task<List<string>> ErrorsOf(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("errors").EnumerateObject()
            .Select(p => $"{p.Name}={string.Join(" | ", p.Value.EnumerateArray().Select(v => v.GetString()))}")];
    }

    private async Task<DocumentDto> CreateAsync(string? project)
    {
        var attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" };
        if (project is not null) attributes["project"] = project;

        var resp = await Client().PostAsJsonAsync("/documents", new
        {
            title = $"制限プロジェクト検証 {Guid.NewGuid():N}",
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

    // 陰性 1: 制限 project を落とす更新は拒否される（issue #1233 受け入れ基準 1）。
    [Fact]
    public async Task 制限プロジェクトの値を落とす更新は拒否される()
    {
        var doc = await CreateAsync(Restricted);

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "属性は全置換なので、キーを落とすと後段の除外が効かなくなる");
    }

    // 陰性 2: 別の値へ差し替える更新も拒否される（落とすのと結果が同じである）。
    [Fact]
    public async Task 制限プロジェクトを別の値へ差し替える更新は拒否される()
    {
        var doc = await CreateAsync(Restricted);

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["project"] = "knowledge-base",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 陰性 3: メタデータ経路でも同じである（経路を 1 つ塞いでも隣が開いていたのが元の欠陥）。
    [Fact]
    public async Task メタデータ経路でも制限プロジェクトの値は落とせない()
    {
        var doc = await CreateAsync(Restricted);

        var resp = await PatchAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 🔴 応答の形を固定する（status / body / 鍵）。**鍵は `project` である。**
    // sink を増やしていないこと・鍵が推論名（`Attributes`）へ落ちていないことを見る。
    [Fact]
    public async Task 拒否は400のRFC7807でありprojectの鍵で返る()
    {
        var doc = await CreateAsync(Restricted);

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = await ErrorsOf(resp);
        errors.Should().ContainSingle("形 α（先頭 1 件・鍵つき）である");
        errors[0].Should().StartWith("project=")
            .And.Contain(Restricted, "どの値が外れたかを丸めない");
    }

    // 🔴 陽性対照 1（本テストの要点）: **同じ値の同送は通る。**
    // SC-05 の属性編集フォームは既存属性をスプレッドして送る。「存在で弾く」実装はここで落ちる。
    [Fact]
    public async Task 同じ制限プロジェクトを同送する機密区分の変更は通る()
    {
        var doc = await CreateAsync(Restricted);

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "confidential",
            ["project"] = Restricted,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await resp.Content.ReadFromJsonAsync<DocumentDto>(
            TestContext.Current.CancellationToken))!;
        updated.Attributes["confidentiality"].Should().Be("confidential",
            "拒否が「何も更新できない」に化けていないこと");
        updated.Attributes["project"].Should().Be(Restricted);
    }

    // 🔴 陽性対照 2（issue #1233 受け入れ基準 3・陰性対照）: **制限外の project は従来どおり外せる。**
    // 判定を「`project` を不変にする」と書いていたらここが落ちる。
    [Fact]
    public async Task 制限外のプロジェクトの値は従来どおり落とせる()
    {
        var doc = await CreateAsync("knowledge-base");

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "計画は project を任意と定めており、制限の射程外の文書の挙動は変えない");
    }

    // 🔴 陽性対照 3（同上）: **`project` を持たない文書の更新は従来どおり通る。**
    // 判定を「`project` を必須にする」と書いていたらここが落ちる。実データ（2,368 件）は
    // `confidentiality` 以外の属性を持たない。
    [Fact]
    public async Task projectを持たない文書の更新は従来どおり通る()
    {
        var doc = await CreateAsync(project: null);

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "restricted",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 陽性対照 4: **後からの制限 project の付与は通る**（統制を強める向きであり、
    // `doc_scope` の「作成時に確定」（ADR-0058）に当たる決定は `project` に無い）。
    [Fact]
    public async Task 制限プロジェクトの後からの付与は通る()
    {
        var doc = await CreateAsync(project: null);

        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["project"] = Restricted,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 位置の固定: `doc_scope` の不変性違反と同時に起こしたとき、**従来どおり `doc_scope` が出る**
    // （宣言順が応答の契約である。[[IADR-0398]] 決定 1 / [[IADR-0371]] 決定 2）。
    [Fact]
    public async Task 文書スコープ違反と同時なら従来どおり文書スコープが返る()
    {
        var resp0 = await Client().PostAsJsonAsync("/documents", new
        {
            title = $"位置の固定 {Guid.NewGuid():N}",
            attributes = new Dictionary<string, string>
            {
                ["confidentiality"] = "internal",
                ["doc_scope"] = "organization",
                ["project"] = Restricted,
            },
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);
        resp0.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = (await resp0.Content.ReadFromJsonAsync<DocumentDto>(
            TestContext.Current.CancellationToken))!;

        // doc_scope も project も同時に落とす。
        var resp = await UpdateAsync(doc, new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = await ErrorsOf(resp);
        errors.Should().ContainSingle();
        errors[0].Should().StartWith("doc_scope=",
            "宣言順が応答の契約であり、先に宣言された doc_scope が 1 件だけ出る");
    }
}
