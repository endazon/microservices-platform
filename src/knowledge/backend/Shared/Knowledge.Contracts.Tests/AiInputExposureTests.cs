using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Xunit;

namespace Knowledge.Contracts.Tests;

// FR-21 受け入れ基準 ⑨, FR-19, [[IADR-0283]] 決定 2:
// **「AI の入力に含める」トグルを写した属性 `ai_input` の判定**を固定する。
//
// `RagContextPolicyTests` が固定したのは「2 つの集合が別物である」構造であり、
// **本テストが固定するのはその述語の中身**である。両者は対であり、片方だけでは ⑨ を満たさない。
//
// **否定形と陽性対照を対で置く**（`個人資料で欠落 → 拒否` と `組織文書で欠落 → 許可`）。
// 片方だけだと「全部拒否」実装も「全部許可」実装も通り抜ける。
public class AiInputExposureTests
{
    private static Dictionary<string, string> PrivateNote(params (string Key, string Value)[] extra)
    {
        var a = new Dictionary<string, string>
        {
            [DocumentScopes.Key] = DocumentScopes.PrivateNote,
            ["owner"] = "alice",
            [ConfidentialityLevels.AttributeKey] = ConfidentialityLevels.Restricted,
        };
        foreach (var (k, v) in extra) a[k] = v;
        return a;
    }

    private static Dictionary<string, string> Organization(params (string Key, string Value)[] extra)
    {
        var a = new Dictionary<string, string>
        {
            [ConfidentialityLevels.AttributeKey] = ConfidentialityLevels.Internal,
        };
        foreach (var (k, v) in extra) a[k] = v;
        return a;
    }

    // FR-21 ⑨（否定形の核）: **「AI の入力に含める」が OFF の個人資料は AI の入力に含めない。**
    [Fact]
    public void AI入力OFFの個人資料は許可されない()
    {
        AiInputExposure.IsAllowed(
            PrivateNote((AiInputExposure.AttributeKey, AiInputExposure.Excluded)))
            .Should().BeFalse();
    }

    // FR-21 ⑨（陽性対照）: ON なら含める。**これが無いと「全部落とす」実装が通る。**
    [Fact]
    public void AI入力ONの個人資料は許可される()
    {
        AiInputExposure.IsAllowed(
            PrivateNote((AiInputExposure.AttributeKey, AiInputExposure.Included)))
            .Should().BeTrue();
    }

    // FR-19, [[IADR-0283]] 決定 2: 🔴 **個人資料で属性が欠落していたら OFF 扱い（fail-closed）。**
    // 供給側が既定を書き忘れても、見える側へ倒れない。
    [Fact]
    public void 個人資料でAI入力属性が欠落していたら許可されない()
    {
        AiInputExposure.IsAllowed(PrivateNote()).Should().BeFalse();
    }

    // 空文字・未知値も個人資料では欠落と同じ扱い（安全側）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("on")]
    [InlineData("true")]
    [InlineData("include")]
    public void 個人資料でAI入力属性が空や未知値なら許可されない(string value)
    {
        AiInputExposure.IsAllowed(
            PrivateNote((AiInputExposure.AttributeKey, value)))
            .Should().BeFalse();
    }

    // 🔴 **陽性対照**: `doc_scope` を持たない既存の組織文書は**従来どおり**許可される。
    // ADR-0054 §結果「既存文書へ遡及付与しない」を壊さないことの検証であり、
    // **判定を否定形（`!= organization`）で書くとここが落ちる。**
    [Fact]
    public void doc_scopeを持たない組織文書は従来どおり許可される()
    {
        var attributes = Organization();
        attributes.Should().NotContainKey(DocumentScopes.Key);

        AiInputExposure.IsAllowed(attributes).Should().BeTrue();
    }

    // 陽性対照 2: `doc_scope=organization` の明示も許可される。
    [Fact]
    public void 組織文書は許可される()
    {
        AiInputExposure.IsAllowed(
            Organization((DocumentScopes.Key, DocumentScopes.Organization)))
            .Should().BeTrue();
    }

    // 組織文書の未知値は**許可のまま**（綴り間違い 1 つで組織文書が静かに RAG から落ちない）。
    // 個人資料と向きが違うことを対で固定する（[[IADR-0283]] 決定 2 の「未知値を無条件に拒否しない」）。
    [Fact]
    public void 組織文書でAI入力属性が未知値でも許可される()
    {
        AiInputExposure.IsAllowed(
            Organization((AiInputExposure.AttributeKey, "yes")))
            .Should().BeTrue();
    }

    // 明示的な `excluded` は**文書スコープによらず**効く（組織文書にも opt-out の口を残す）。
    [Fact]
    public void 明示的なexcludedは組織文書でも効く()
    {
        AiInputExposure.IsAllowed(
            Organization((AiInputExposure.AttributeKey, AiInputExposure.Excluded)))
            .Should().BeFalse();
    }

    // 値の大小文字は問わない（属性の書き手による揺れで判定が変わらない）。
    [Theory]
    [InlineData("INCLUDED", true)]
    [InlineData("Excluded", false)]
    public void 値の大小文字は判定に影響しない(string value, bool expected)
    {
        AiInputExposure.IsAllowed(
            PrivateNote((AiInputExposure.AttributeKey, value)))
            .Should().Be(expected);
    }

    // FR-19: トグル（bool）から属性値への写像。供給側と消費側が同じ語彙を引くことの担保。
    [Fact]
    public void トグルの真偽が属性値へ写る()
    {
        AiInputExposure.FromToggle(true).Should().Be(AiInputExposure.Included);
        AiInputExposure.FromToggle(false).Should().Be(AiInputExposure.Excluded);

        AiInputExposure.IsAllowed(
            PrivateNote((AiInputExposure.AttributeKey, AiInputExposure.FromToggle(true))))
            .Should().BeTrue();
        AiInputExposure.IsAllowed(
            PrivateNote((AiInputExposure.AttributeKey, AiInputExposure.FromToggle(false))))
            .Should().BeFalse();
    }

    // ADR-0054: 個人資料の判定は**集合帰属**である（否定で書かない）。
    [Fact]
    public void 個人資料の判定は集合帰属である()
    {
        DocumentScopes.IsPrivateNote(PrivateNote()).Should().BeTrue();
        DocumentScopes.IsPrivateNote(Organization()).Should().BeFalse();
        DocumentScopes.IsPrivateNote(
            Organization((DocumentScopes.Key, DocumentScopes.Organization))).Should().BeFalse();
    }

    // FR-21 ⑨: 述語として `RagContextPolicy` へ渡したときに、集合が正しく分かれる
    // （契約側の構造と本判定が噛み合っていることの結線試験）。
    [Fact]
    public void 述語としてRagContextPolicyへ渡すと集合が分かれる()
    {
        var excludedChunk = Guid.NewGuid();
        var results = new List<SearchResultDto>
        {
            new(Guid.NewGuid(), Guid.NewGuid(), "組織文書", "本文", 1.0f, null, Organization(), []),
            new(excludedChunk, Guid.NewGuid(), "個人資料", "本文", 0.9f, null,
                PrivateNote((AiInputExposure.AttributeKey, AiInputExposure.Excluded)), []),
            new(Guid.NewGuid(), Guid.NewGuid(), "個人資料2", "本文", 0.8f, null,
                PrivateNote((AiInputExposure.AttributeKey, AiInputExposure.Included)), []),
        };

        var selection = RagContextPolicy.Select(results, AiInputExposure.IsAllowed);

        selection.SearchResults.Should().HaveCount(3, "検索結果は絞らない（⑨ の前半）");
        selection.ContextChunks.Should().HaveCount(2);
        selection.ExcludedFromContextChunkIds.Should().Equal(excludedChunk);
    }
}
