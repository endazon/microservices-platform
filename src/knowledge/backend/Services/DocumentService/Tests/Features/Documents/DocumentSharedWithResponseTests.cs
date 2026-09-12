using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents;

// FR-19, FR-20, UC-11, ADR-0036 D-06, ADR-0098 決定 1, [[IADR-0447]] 決定 4 (#1447):
// **応答（`DocumentDto.SharedWith`）が共有台帳を運ぶ。**
//
// 運ばないと、BFF の単体判定（`GET /bff/documents/{id}`）とグラフの複製が共有先へ到達できず、
// **共有は台帳に入るが誰にも何も許可しない**（#1447 が名指した穴）。到達させる手段として
// 「消費側が台帳を引く」（DB per Service の越境）ではなく「所有者が写しを運ぶ」を採っている。
//
// 🔴 **解決点は `DocumentEndpoints.ResolveSharedWithAsync` ただ 1 つである。** その事実は
// 「応答とイベントが同じ値・同じ順である」ことでしか外から確かめられない ——
// 経路ごとに解決すると、**どちらが正しいかを誰も言えなくなる**（識別子 → 表示名の変換点を
// 1 つに保っているのと同じ理由）。下の陽性対照がその同値を固定する。
[Trait("TestKind", "Integration")]
public class DocumentSharedWithResponseTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private RecordingMessageBus Bus => factory.Services.GetRequiredService<RecordingMessageBus>();

    private List<DocumentUpdated> UpdatesFor(Guid documentId) =>
        [.. Bus.PublishedOf<DocumentUpdated>().Where(e => e.DocumentId == documentId)];

    private HttpClient ClientAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    // 🔴 **その利用者として作成する**（ADR-0060 決定 3 / #1057）—— 所有者は「誰が作ったか」で決まり、
    // 共有を付与できるのは所有者だけである。
    private async Task<Guid> CreateOwnedAsync(HttpClient owner, string title)
    {
        var resp = await owner.PostAsJsonAsync("/documents", new
        {
            title,
            attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>(
            TestContext.Current.CancellationToken))!.Id;
    }

    private static async Task GrantAsync(HttpClient owner, Guid id, string subjectType, string subjectId)
    {
        var resp = await owner.PostAsJsonAsync($"/documents/{id}/shares",
            new { subjectType, subjectId }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task<DocumentDto> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<DocumentDto>(
            $"/documents/{id}", TestContext.Current.CancellationToken))!;

    // 受け入れ基準 5（陰性）: 共有が 1 件も無ければ `SharedWith` は **null** である。
    // 🔴 **空リストにしない** —— 契約の既定が null であり、空集合を載せると
    // `shared_with` 属性が「持つが空」として合成され得る（`WithSharedWith` の禁則）。
    [Fact]
    public async Task 共有が無ければ応答の共有先はnullである()
    {
        var owner = ClientAs("sw-none");
        var id = await CreateOwnedAsync(owner, $"共有なし {Guid.NewGuid():N}");

        (await GetAsync(owner, id)).SharedWith.Should().BeNull();
    }

    // 🔴 陽性対照: 同じ口で、共有を付与した文書には載る。
    // これが無いと「常に null を返す」実装でも上の陰性が緑になる。
    [Fact]
    public async Task 共有を付与すると応答の共有先に載る()
    {
        var owner = ClientAs("sw-one");
        var id = await CreateOwnedAsync(owner, $"共有あり {Guid.NewGuid():N}");

        await GrantAsync(owner, id, "user", "bob");

        (await GetAsync(owner, id)).SharedWith.Should().Equal("bob");
    }

    // 受け入れ基準 5（陽性対照・**同じ解決点であることの固定**）:
    // 応答の `SharedWith` は `DocumentUpdated.SharedWith` と**同じ値・同じ順**である。
    //
    // 🔴 **種別（`SubjectType`）は落ちる。** 判定規則
    // `doc.shared_with ∩ ({${current_user}} ∪ ${current_groups}) ≠ ∅` は 1 つの集合として
    // 突き合わせるためであり（利用者名とグループ ID は名前空間が交わらない）、
    // **個人とグループが同じ列に並ぶ**ことをここで固定する。
    [Fact]
    public async Task 応答の共有先はイベントの共有先と同じ値で同じ順である()
    {
        var owner = ClientAs("sw-same");
        var id = await CreateOwnedAsync(owner, $"応答とイベント {Guid.NewGuid():N}");

        await GrantAsync(owner, id, "user", "bob");
        await GrantAsync(owner, id, "group", "11111111-1111-1111-1111-111111111111");

        var published = UpdatesFor(id);
        published.Should().NotBeEmpty("共有の付与は再発行の契機である（IADR-0396 決定 3）");

        var dto = await GetAsync(owner, id);
        dto.SharedWith.Should().Equal(published[^1].SharedWith!,
            "解決点が 2 つあると、応答とイベントで別の答えが出る");
        dto.SharedWith.Should().Equal("bob", "11111111-1111-1111-1111-111111111111");
    }

    // 受け入れ基準 5: **一覧でも文書ごとに正しく分配される**（1 クエリで引く実装の正しさ）。
    // 🔴 **「全件に同じ共有先が載る」「先頭だけ載る」が典型の壊れ方**なので、
    // 共有あり 2 件（別の相手）と共有なし 1 件を同時に見る。
    [Fact]
    public async Task 一覧は文書ごとに共有先を分配する()
    {
        var owner = ClientAs("sw-list");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var withBob = await CreateOwnedAsync(owner, $"一覧A {tag}");
        var withCarol = await CreateOwnedAsync(owner, $"一覧B {tag}");
        var withNone = await CreateOwnedAsync(owner, $"一覧C {tag}");

        await GrantAsync(owner, withBob, "user", "bob");
        await GrantAsync(owner, withCarol, "user", "carol");

        var list = (await owner.GetFromJsonAsync<List<DocumentDto>>(
            "/documents", TestContext.Current.CancellationToken))!;

        list.Single(d => d.Id == withBob).SharedWith.Should().Equal("bob");
        list.Single(d => d.Id == withCarol).SharedWith.Should().Equal("carol");
        list.Single(d => d.Id == withNone).SharedWith.Should().BeNull();
    }

    // 受け入れ基準 5（陰性）: 取り消しは応答からも消える。
    // **取り消しの未反映は漏れる向きの乖離である**（取り消した相手に見え続ける）。
    [Fact]
    public async Task 共有の取り消しは応答からも消える()
    {
        var owner = ClientAs("sw-revoke");
        var id = await CreateOwnedAsync(owner, $"取り消し {Guid.NewGuid():N}");
        await GrantAsync(owner, id, "user", "dave");

        (await GetAsync(owner, id)).SharedWith.Should()
            .Equal(new[] { "dave" }, "陽性対照: 付与は載っている");

        var revoked = await owner.DeleteAsync($"/documents/{id}/shares/user/dave",
            TestContext.Current.CancellationToken);
        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await GetAsync(owner, id)).SharedWith.Should().BeNull();
    }
}
