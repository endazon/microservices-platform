using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using DocumentService.Api.Foundation.Domain;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Api.Tests;

// FR-21, UC-03: 文書本文の直接受け入れ経路。計画 `02_requirements` の FR-21 受け入れ基準
// ①〜⑧ をテストへ写像する（⑨⑩ は FR-19 の 3 トグルが実装されるまで陽性検証できないため対象外。
// 分離構造の ⑨ は Knowledge.Contracts.Tests 側で固定する）。
//
// **本クラスは専用のファクトリを持つ**（`IClassFixture`）—— 格納内容と格納回数を数えるため、
// 他クラスの書き込みが混ざらない状態で測る必要がある。
public class DocumentBodyIntakeTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string Confidentiality = "internal";

    private HttpClient ClientAs(string? user = null, string? roles = null)
    {
        var client = factory.CreateClient();
        if (user is not null) client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        if (roles is not null) client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static object CreatePayload(string title, string? body = null,
        string? originalUri = null, string? owner = null)
    {
        var attributes = new Dictionary<string, string> { ["confidentiality"] = Confidentiality };
        if (owner is not null) attributes["owner"] = owner;
        return new { title, body, originalUri, attributes, tags = new List<string>() };
    }

    // 所有者つきの文書を作る（本文は付けない）。本文投入（PUT）の対象として使う。
    private async Task<DocumentDto> CreateOwnedAsync(string title, string owner)
    {
        var resp = await ClientAs().PostAsJsonAsync("/documents",
            CreatePayload(title, owner: owner));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
    }

    // FR-21 ①: 本文を伴う文書を登録でき、取り込み・分割・埋め込みが起動する
    //           （起動条件は `DocumentUpdated.MarkdownUri` が非 null であること。IngestionService の
    //            `DocumentUpdatedConsumer` は MarkdownUri が null なら取り込みをスキップする）。
    [Fact]
    public async Task 本文つきで登録すると本文の参照が付き取り込みを起動するイベントが出る()
    {
        var resp = await ClientAs().PostAsJsonAsync("/documents",
            CreatePayload("本文つき登録", body: "# 見出し\n\n本文である。"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
        doc.MarkdownUri.Should().NotBeNull();
        doc.Status.Should().Be("normalized");

        var harness = factory.Services.GetRequiredService<ITestHarness>();
        (await harness.Published.Any<DocumentUpdated>(m =>
            m.Context.Message.DocumentId == doc.Id
            && m.Context.Message.MarkdownUri != null)).Should().BeTrue();
    }

    // FR-21 ②: 登録した本文が RAG 検索の結果として返る。
    //           取り込みが読む本文が**登録した本文そのもの**であることをここで固定する
    //           （索引そのものは Qdrant を要するため結合テストの担当）。
    [Fact]
    public async Task 登録した本文は取り込みが読む参照先からそのまま取得できる()
    {
        const string body = "# RAG 検索の対象\n\n検索で当たるべき本文である。";
        var resp = await ClientAs().PostAsJsonAsync("/documents",
            CreatePayload("検索対象", body: body));
        var doc = (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;

        factory.Storage.CanResolve(doc.MarkdownUri).Should().BeTrue();
        (await factory.Storage.GetTextAsync(doc.MarkdownUri!)).Should().Be(body);
    }

    // FR-21 ③: 本文と OriginalUri は排他ではなく**併存**できる。
    [Fact]
    public async Task 本文と元文書の所在は併存できる()
    {
        var resp = await ClientAs().PostAsJsonAsync("/documents",
            CreatePayload("併存", body: "本文", originalUri: "https://example.invalid/original.docx"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
        doc.MarkdownUri.Should().NotBeNull();

        // `DocumentDto` は `OriginalUri` を外へ出さないため、**併存**は永続化された実体で見る。
        // 「本文を入れたら所在が消える／所在があるから本文を拒否する」のどちらでもないことを固定する。
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<DocumentService.Api.Foundation.Persistence.DocumentDbContext>();
        var stored = await db.Documents.FindAsync(doc.Id);
        stored!.MarkdownUri.Should().Be(doc.MarkdownUri);
        stored.OriginalUri.Should().Be("https://example.invalid/original.docx");
    }

    // FR-21 ④: 本文はオブジェクトストレージへ格納され、DB は参照のみ持つ。
    [Fact]
    public async Task 本文はオブジェクトストレージへ格納されDBは参照のみ持つ()
    {
        const string body = "本文の実体はストレージ側にある。";
        var resp = await ClientAs().PostAsJsonAsync("/documents",
            CreatePayload("参照のみ", body: body));
        var doc = (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;

        // DB（＝API が返す文書）が持つのは storage:// の参照だけで、本文そのものではない。
        doc.MarkdownUri.Should().StartWith("storage://");
        doc.MarkdownUri.Should().NotContain(body);
        factory.Storage.Texts[doc.MarkdownUri!].Should().Be(body);
    }

    // FR-21 ⑤: 一般利用者が自分の文書の本文を投入できる（ABAC の動的束縛による。ロールを問わない）。
    [Fact]
    public async Task 一般利用者はロールが無くても自分の文書の本文を投入できる()
    {
        var doc = await CreateOwnedAsync("自分の資料", owner: "alice");

        var resp = await ClientAs(user: "alice", roles: "viewer")
            .PutAsJsonAsync($"/documents/{doc.Id}/body", new { body = "自分で書いた本文である。" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
        updated.MarkdownUri.Should().NotBeNull();
        (await factory.Storage.GetTextAsync(updated.MarkdownUri!))
            .Should().Be("自分で書いた本文である。");
    }

    // FR-21 ⑥: 本文が 1 MB を超える登録要求は 413 で拒否される（**切り詰めて成功を返さない**）。
    [Fact]
    public async Task 上限を超える本文の登録は413で拒否され格納もされない()
    {
        var before = factory.Storage.PutTextCallCount;
        var tooLarge = new string('あ', DocumentBodyIntake.MaxBytes); // UTF-8 で 3 MB

        var resp = await ClientAs().PostAsJsonAsync("/documents",
            CreatePayload("上限超過", body: tooLarge));

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        // 切り詰めて成功を返していないこと＝**格納を 1 度も呼んでいないこと**で見る。
        factory.Storage.PutTextCallCount.Should().Be(before);
    }

    // FR-21 ⑥: 既存文書への本文投入も同じ上限で拒否される（口が 2 つあるので両方を測る）。
    [Fact]
    public async Task 上限を超える本文の投入も413で拒否される()
    {
        var doc = await CreateOwnedAsync("上限超過の投入", owner: "alice");
        var tooLarge = new string('x', DocumentBodyIntake.MaxBytes + 1); // ASCII で 1 MB + 1 バイト

        var resp = await ClientAs(user: "alice")
            .PutAsJsonAsync($"/documents/{doc.Id}/body", new { body = tooLarge });

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    // FR-21 ⑦: 1 MB 以下の本文は切り詰められることなく全文が索引される（⑥ の陽性対照）。
    [Fact]
    public async Task 上限ちょうどの本文は切り詰められず全文が格納される()
    {
        var atLimit = new string('x', DocumentBodyIntake.MaxBytes); // ASCII で ちょうど 1 MB

        var resp = await ClientAs().PostAsJsonAsync("/documents",
            CreatePayload("上限ちょうど", body: atLimit));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
        var stored = await factory.Storage.GetTextAsync(doc.MarkdownUri!);
        stored.Length.Should().Be(atLimit.Length);
        Encoding.UTF8.GetByteCount(stored).Should().Be(DocumentBodyIntake.MaxBytes);
        stored.Should().Be(atLimit);
    }

    // FR-21 ⑧: 別の利用者として同じ文書 ID へ書き込みを試みると拒否される
    //           （認可判定のキャッシュキーに主体が含まれることの検証。ADR-0036 D-14）。
    [Fact]
    public async Task 同じ文書IDへ別の利用者が書き込むと拒否される()
    {
        var doc = await CreateOwnedAsync("alice の資料", owner: "alice");

        // 先に所有者が書く（判定がキャッシュされるならこの時点で載る）。
        var owned = await ClientAs(user: "alice")
            .PutAsJsonAsync($"/documents/{doc.Id}/body", new { body = "alice の本文" });
        owned.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyUri = (await owned.Content.ReadFromJsonAsync<DocumentDto>())!.MarkdownUri!;

        // **同じ文書 ID へ別の主体で書く。** 主体がキャッシュキーに入っていなければここが通ってしまう。
        var denied = await ClientAs(user: "bob")
            .PutAsJsonAsync($"/documents/{doc.Id}/body", new { body = "bob が上書きした本文" });

        // ADR-0056 決定 1・[[IADR-0277]]: 拒否は 404（存在秘匿）。403 にしない。
        denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // 拒否が「書き込みの後で拒否を返した」ではないこと ——本文は alice のままである。
        (await factory.Storage.GetTextAsync(bodyUri)).Should().Be("alice の本文");
    }

    // FR-21 ⑧（陽性対照）: 拒否が「誰も書けない」ではないこと —— bob は自分の文書には書ける。
    [Fact]
    public async Task 別の利用者でも自分の文書には書ける()
    {
        var doc = await CreateOwnedAsync("bob の資料", owner: "bob");

        var resp = await ClientAs(user: "bob")
            .PutAsJsonAsync($"/documents/{doc.Id}/body", new { body = "bob の本文" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-21 ⑤, ADR-0036 §未決 6: `owner` を持たない文書（取り込み経路の既定は `system`）は
    // 所有者ベースでは書き込めない。編集は SC-05 の管理者経路が担う。
    [Fact]
    public async Task 所有者属性を持たない文書には本文を投入できない()
    {
        var resp0 = await ClientAs().PostAsJsonAsync("/documents", CreatePayload("所有者なし"));
        var doc = (await resp0.Content.ReadFromJsonAsync<DocumentDto>())!;

        var resp = await ClientAs(user: "alice")
            .PutAsJsonAsync($"/documents/{doc.Id}/body", new { body = "誰かの本文" });

        // ADR-0056 決定 1: 拒否は 404。
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // FR-21: 不在の文書への投入は 404。
    // 🔴 **ADR-0056 決定 1 の適用後、不在と拒否は同じ 404 であり本テストは両者を区別しない。**
    // それが存在秘匿の狙いである。検出力は陽性対照（所有者は 200）が担う。
    [Fact]
    public async Task 不在の文書への本文投入は404()
    {
        var resp = await ClientAs(user: "alice")
            .PutAsJsonAsync($"/documents/{Guid.NewGuid()}/body", new { body = "本文" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // FR-21: 本文欄は**任意**であり、既存の登録経路を壊さない（要求文「既存の登録経路を壊さない」）。
    [Fact]
    public async Task 本文を伴わない登録は従来どおり本文の参照を持たない()
    {
        var resp = await ClientAs().PostAsJsonAsync("/documents", CreatePayload("本文なし"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
        doc.MarkdownUri.Should().BeNull();
        doc.Status.Should().Be("draft");
    }
}

// FR-21 ⑤⑧, ADR-0036 D-02/D-07/D-14: 書き込み認可の**判定そのもの**を単体で固定する。
// 端点越しの ⑧ と対で置く —— 端点だけだと「たまたま 403 になっている」経路の変化を検出できない。
public class DocumentBodyIntakeAuthorizationTests
{
    private static Dictionary<string, string> Owned(string owner) => new() { ["owner"] = owner };

    // FR-21 ⑤: 所有者本人は書ける（動的束縛 `doc.owner ∈ { ${current_user} }`）。
    [Fact]
    public void 所有者本人は書ける()
        => DocumentBodyIntake.CanWrite(Owned("alice"), "alice").Should().BeTrue();

    // FR-21 ⑧: **主体は判定の入力である。** 同じ文書属性でも主体が変われば結果が変わる。
    // 主体が判定に効いていなければ（＝キャッシュキーから主体が抜けていれば）この対が壊れる。
    [Fact]
    public void 同じ文書でも主体が変われば結果が変わる()
    {
        var attributes = Owned("alice");
        DocumentBodyIntake.CanWrite(attributes, "alice").Should().BeTrue();
        DocumentBodyIntake.CanWrite(attributes, "bob").Should().BeFalse();
    }

    // FR-21, ADR-0036 D-04: deny-by-default。所有者が決まらない文書は誰も書けない。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 所有者が空の文書は誰も書けない(string? owner)
    {
        var attributes = owner is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["owner"] = owner };
        DocumentBodyIntake.CanWrite(attributes, "alice").Should().BeFalse();
    }

    // FR-21, ADR-0036 D-04: 主体が特定できない呼び出しは拒否する。
    [Fact]
    public void 主体が空なら書けない()
        => DocumentBodyIntake.CanWrite(Owned("alice"), null).Should().BeFalse();

    // FR-21 ⑧: 所有者の比較は**大文字小文字を区別する**。利用者識別子は Keycloak の
    // `preferred_username` であり、揺らぎを吸収すると別人の識別子と衝突し得る。
    [Fact]
    public void 所有者の比較は大文字小文字を区別する()
        => DocumentBodyIntake.CanWrite(Owned("alice"), "Alice").Should().BeFalse();

    // FR-21 ⑥: 上限は UTF-8 の**バイト数**で測る（文字数で測ると日本語が 3 倍通る）。
    [Fact]
    public void 上限はUTF8のバイト数で測る()
    {
        var justUnder = new string('x', DocumentBodyIntake.MaxBytes);
        DocumentBodyIntake.ExceedsLimit(justUnder).Should().BeFalse();
        DocumentBodyIntake.ExceedsLimit(justUnder + "x").Should().BeTrue();
        // 多バイト文字は文字数が上限の 3 分の 1 でも超過する。
        DocumentBodyIntake.ExceedsLimit(new string('あ', DocumentBodyIntake.MaxBytes / 3 + 1))
            .Should().BeTrue();
    }

    // FR-21 ④: オブジェクトキーは文書 ID から決まる（再投入は同じキーを上書きする）。
    [Fact]
    public void 格納キーは文書IDから決まる()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        DocumentBodyIntake.StorageKey(id)
            .Should().Be("documents/11111111-2222-3333-4444-555555555555/body.md");
    }
}
