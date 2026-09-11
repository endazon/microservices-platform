using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Domain.Clustering;
using Knowledge.Contracts.Dtos;

namespace GraphService.Tests.Domain.Clustering;

// FR-17, FR-18, ADR-0035 決定 3・6・8, ADR-0051 決定 2, [[IADR-0430]] 決定 1・2 (#1395):
// **クラスタ要約の封（型ゲート）。** ここが破れると、要約の LLM 呼び出しへ
// 個人資料や区分を超える文書が載る —— **送信そのものが違反であり、後段では償えない**
// （ADR-0034 決定 5）。
[Trait("TestKind", "Unit")]
public class ClusterSummaryPromptTests
{
    private static ClusterMemberDocument Doc(string title, string? confidentiality = null, bool privateNote = false)
    {
        var attributes = new Dictionary<string, string>();
        if (confidentiality is not null)
            attributes[ConfidentialityLevels.AttributeKey] = confidentiality;
        if (privateNote)
            attributes[GraphDocumentScope.Key] = GraphDocumentScope.PrivateNote;
        return new ClusterMemberDocument(Guid.NewGuid(), title, attributes);
    }

    // 🔴 FR-17, ADR-0035 決定 8 (T-3): **個人資料は封に入らない。**
    // 検出の入力から既に除いてあるため、これは**多層防御**である（迂回経路が生まれても出口で濾す）。
    // **陽性対照つき** —— 組織文書はちゃんと入る（判定を否定形で書くと全部落ちる）。
    [Fact]
    public void 個人資料は封に入らない()
    {
        var note = Doc("個人メモ", ConfidentialityLevels.Internal, privateNote: true);
        var org = Doc("組織文書", ConfidentialityLevels.Internal);

        var prompt = ClusterSummaryPrompt.Seal(
            Guid.NewGuid(), ConfidentialityLevels.Internal, [note, org]);

        prompt.Should().NotBeNull();
        prompt!.Members.Select(m => m.DocumentId).Should().NotContain(note.DocumentId,
            "ADR-0035 決定 8 —— 共有グラフは個人資料を含まない");
        prompt.Members.Select(m => m.DocumentId).Should().Contain(org.DocumentId,
            "陽性対照 —— 組織文書は入る");
        prompt.Render().Should().NotContain("個人メモ", "表題も送信本文へ現れない");
        prompt.Render().Should().Contain("組織文書", "陽性対照 —— 組織文書の表題は載る");
    }

    // 🔴 FR-11, ADR-0035 決定 6 (T-4): **区分を超える文書は封に入らない**（1 生成 = 1 機密区分）。
    // 「機密区分ごとに入力文書集合が異なる」——`public` の要約に `confidential` の文書を混ぜない。
    [Fact]
    public void 区分を超える文書は封に入らない()
    {
        var open = Doc("公開資料", ConfidentialityLevels.Public);
        var secret = Doc("秘密資料", ConfidentialityLevels.Confidential);

        var prompt = ClusterSummaryPrompt.Seal(
            Guid.NewGuid(), ConfidentialityLevels.Public, [open, secret]);

        prompt!.Members.Select(m => m.Title).Should().Equal("公開資料");
        prompt.Render().Should().NotContain("秘密資料");
    }

    // FR-11 (T-4 陽性対照): 上位区分の封には下位区分の文書が入る（積み上がる）。
    [Fact]
    public void 上位区分の封には下位区分の文書も入る()
    {
        var open = Doc("公開資料", ConfidentialityLevels.Public);
        var secret = Doc("秘密資料", ConfidentialityLevels.Confidential);

        var prompt = ClusterSummaryPrompt.Seal(
            Guid.NewGuid(), ConfidentialityLevels.Restricted, [open, secret]);

        prompt!.Members.Should().HaveCount(2);
    }

    // 🔴 FR-05, ADR-0035 決定 6: **属性を持たない文書は安全側（restricted）へ倒れる。**
    // ここが `public` へ倒れると、区分の分からない文書が公開区分の要約へ紛れ込む。
    [Fact]
    public void 機密区分の属性が無い文書は安全側へ倒れる()
    {
        var unknown = Doc("区分不明", confidentiality: null);

        ClusterSummaryPrompt.Seal(Guid.NewGuid(), ConfidentialityLevels.Public, [unknown])
            .Should().BeNull("属性の欠落は最も強い区分へ倒す（ConfidentialityLevels.SafeDefault）");
        ClusterSummaryPrompt.Seal(Guid.NewGuid(), ConfidentialityLevels.Restricted, [unknown])
            .Should().NotBeNull("陽性対照 —— 最上位区分の封には入る");
    }

    // (T-5): 見えるものが 1 件も無ければ封は作れない（送るものが無い）。
    [Fact]
    public void 見える文書が無ければ封は作れない()
        => ClusterSummaryPrompt.Seal(
                Guid.NewGuid(), ConfidentialityLevels.Public,
                [Doc("秘密資料", ConfidentialityLevels.Restricted)])
            .Should().BeNull();

    // [[IADR-0425]] 決定 2 と同じ向き: 入力の並び順が変わっても本文は変わらない
    // （変わると、内容が同じでも「作り直した」ことになり無駄な呼び出しが増える）。
    [Fact]
    public void 入力の並び順を変えても送信本文は変わらない()
    {
        var docs = new[]
        {
            Doc("あ", ConfidentialityLevels.Internal),
            Doc("い", ConfidentialityLevels.Internal),
            Doc("う", ConfidentialityLevels.Internal),
        };
        var id = Guid.NewGuid();

        var forward = ClusterSummaryPrompt.Seal(id, ConfidentialityLevels.Internal, docs)!.Render();
        var reversed = ClusterSummaryPrompt.Seal(
            id, ConfidentialityLevels.Internal, [.. docs.Reverse()])!.Render();

        reversed.Should().Be(forward);
    }

    // 🔴 [[IADR-0266]] 決定 1 と同じ作法: **構築経路は Seal ただ 1 つである。**
    // public なコンストラクタが生えると型ゲートが無効になる。
    [Fact]
    public void 封の構築経路はSealだけである()
        => typeof(ClusterSummaryPrompt)
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Should().BeEmpty("公開コンストラクタがあると封を通らない値を作れる");
}
