using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents;
using Platform.Shared.Contracts.Dtos;

namespace DocumentService.Tests.Features.Documents;

// FR-19, NFR-09, 計画 ADR-0121 決定 1・5, ADR-0119 決定 3, ADR-0036 D-05・D-08, [[IADR-0476]] (#1664):
// **dev seed の所有者の分岐が、文書台帳の読み取りの判定（`DocumentReadAccess`）を広げない**ことを固定する。
//
// `DocumentReadAccess` は所有者・利用者の共有先をコードで判定し、認可サービスの分岐を見るのは
// グループ共有の段だけである（`IADR-0476`。判定を認可サービスへ寄せるのは ADR-0121 決定 5 の後段）。
// その段へ seed の分岐（所有者の分岐を含む）が届いたとき、**他人の個人資料を許さない**ことを見る。
// 入力は seed を入れた認可サービスの応答の期待値
// （`AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json`。認可サービスの試験が実物と突き合わせる）。
[Trait("TestKind", "Unit")]
public class DocumentReadOwnerSeedTests
{
    private static Document PrivateNoteOwnedBy(string owner) => Document.Create(
        "個人メモ", null, null, new Dictionary<string, string>
        {
            ["doc_scope"] = "private-note",
            ["owner"] = owner,
            ["confidentiality"] = "restricted",
        });

    // T-38: bob はグループ共有の段まで進むが、seed の分岐（所有者 = bob・共有先 = bob）は alice の資料に一致しない。
    // 観測: 分岐を実際に引いた（段を飛ばして偽になったのではない）。
    [Fact]
    public async Task Seedの分岐は他人のグループ共有の個人資料を許さない()
    {
        var scopes = new StubDocumentReadScopeSource();
        scopes.Grant("bob", OwnerReadSeedScopes.BranchesOf("bob"));
        var access = new DocumentReadAccess(scopes, new StubContentAbacGate());

        var readable = await access.CanReadAsync(DocumentReadPrincipal.RelayedUser("bob"),
            PrivateNoteOwnedBy("alice"), ["g-not-a-member"], TestContext.Current.CancellationToken);

        readable.Should().BeFalse("所有者の分岐は本人の名前にしか一致しない");
        scopes.CallsFor("bob").Should().Be(1, "グループ共有の段で認可サービスの分岐を引いた");
    }

    // 陽性対照: 所有者本人は（分岐を引くまでもなく）読める。seed の有無で変わらない（IADR-0476 のコード判定）。
    [Fact]
    public async Task 所有者本人は読める()
    {
        var scopes = new StubDocumentReadScopeSource();
        var access = new DocumentReadAccess(scopes, new StubContentAbacGate());

        (await access.CanReadAsync(DocumentReadPrincipal.RelayedUser("alice"),
            PrivateNoteOwnedBy("alice"), null, TestContext.Current.CancellationToken)).Should().BeTrue();
        scopes.CallsFor("alice").Should().Be(0);
    }
}
