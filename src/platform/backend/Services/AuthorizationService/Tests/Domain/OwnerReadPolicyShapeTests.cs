using System.Text.Json;
using System.Text.RegularExpressions;
using AuthorizationService.Domain;
using AuthorizationService.Tests.Features.Authz.ResolveScope;
using AwesomeAssertions;

namespace AuthorizationService.Tests.Domain;

// FR-05, FR-19, NFR-09, 計画 ADR-0121 決定 1・2, [[IADR-0480]], [[IADR-0481]] (#1665):
// **所有者の読み取りのポリシーの存在の判定**（issue のやること 1）。形が近いが違うポリシーを「在る」と誤認しない。
[Trait("TestKind", "Unit")]
public class OwnerReadPolicyShapeTests
{
    private static Dictionary<string, List<string>> Cond(params (string Key, string[] Values)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Values.ToList());

    private static AbacPolicy Policy(
        string action, Dictionary<string, List<string>>? user, Dictionary<string, List<string>>? doc, bool active = true)
    {
        var p = AbacPolicy.Create("p", action, user, doc);
        if (!active) p.SetActive(false);
        return p;
    }

    private static AbacPolicy Canonical(bool active = true)
        => Policy(PolicyAction.Read, [], Cond(("owner", ["${current_user}"])), active);

    // T-39: 陽性対照。正規の形（seed と運用の手順の本文と同じ）は在る。
    [Fact]
    public void 正規の形は在ると数える()
    {
        OwnerReadPolicyShape.Matches(Canonical()).Should().BeTrue();
        // 利用者の条件を省いた（null）ものも、保存時に空辞書になるので同じ形である。
        OwnerReadPolicyShape.Matches(Policy(PolicyAction.Read, null, Cond(("owner", ["${current_user}"])))).Should().BeTrue();
    }

    // T-39: 同じ `${current_user}` の重複は束縛で 1 値になるので同値として認める。
    [Fact]
    public void 束縛変数の重複は同じ形として数える()
        => OwnerReadPolicyShape.Matches(
                Policy(PolicyAction.Read, [], Cond(("owner", ["${current_user}", "${current_user}"]))))
            .Should().BeTrue();

    public static TheoryData<string, AbacPolicy> NearMisses() => new()
    {
        { "無効", Canonical(active: false) },
        { "write", Policy(PolicyAction.Write, [], Cond(("owner", ["${current_user}"]))) },
        { "analyze", Policy(PolicyAction.Analyze, [], Cond(("owner", ["${current_user}"]))) },
        { "大文字の Read（評価器は序数で比べる）", Policy("Read", [], Cond(("owner", ["${current_user}"]))) },
        { "利用者の条件あり", Policy(PolicyAction.Read, Cond(("clearance", ["internal"])), Cond(("owner", ["${current_user}"]))) },
        { "利用者の条件のキーだけ（値が空）", Policy(PolicyAction.Read, Cond(("clearance", [])), Cond(("owner", ["${current_user}"]))) },
        { "文書の条件が空（全件許可）", Policy(PolicyAction.Read, [], []) },
        { "文書の条件に別のキーを足した", Policy(PolicyAction.Read, [], Cond(("owner", ["${current_user}"]), ("confidentiality", ["internal"]))) },
        { "キーが Owner", Policy(PolicyAction.Read, [], Cond(("Owner", ["${current_user}"]))) },
        { "キーが author", Policy(PolicyAction.Read, [], Cond(("author", ["${current_user}"]))) },
        { "値に利用者名が混ざる（広い）", Policy(PolicyAction.Read, [], Cond(("owner", ["${current_user}", "alice"]))) },
        { "値に current_groups が混ざる", Policy(PolicyAction.Read, [], Cond(("owner", ["${current_user}", "${current_groups}"]))) },
        { "値が空", Policy(PolicyAction.Read, [], Cond(("owner", []))) },
        { "値がリテラルの利用者名", Policy(PolicyAction.Read, [], Cond(("owner", ["alice"]))) },
        { "束縛変数の綴り違い", Policy(PolicyAction.Read, [], Cond(("owner", ["${Current_User}"]))) },
        { "共有先の分岐", Policy(PolicyAction.Read, [], Cond(("shared_with", ["${current_user}", "${current_groups}"]))) },
    };

    // T-40: 否定の試験。形が近いが違うポリシーは在ると数えない。
    [Theory]
    [MemberData(nameof(NearMisses))]
    public void 形が近いが違うポリシーは在ると数えない(string _, AbacPolicy policy)
        => OwnerReadPolicyShape.Matches(policy).Should().BeFalse();

    [Fact]
    public void 件数は形の合う有効なものだけを数える()
    {
        var policies = new[]
        {
            Canonical(),
            Canonical(active: false),
            Policy(PolicyAction.Read, [], Cond(("owner", ["alice"]))),
            Canonical(),
        };
        OwnerReadPolicyShape.CountActive(policies).Should().Be(2);
        OwnerReadPolicyShape.CountActive([]).Should().Be(0);
    }

    // T-67（#1676）: **投入の正本 2 つを判定器に通す。** dev seed（`deploy/local/abac-seed/policies.json`）と、
    // 運用仕様書 §所有者の読み取りのポリシーの投入 の JSON 本文は、どちらも「在る」と判定される（seed はちょうど 1 件）。
    // 🔴 どちらも**ファイルから読む**（書き写さない）。本文が形の判定から外れると、手順どおりに投入しても門が開かず、
    // 消失の警報が鳴り続ける —— しかも誤りとして表に出ない。
    [Fact]
    public void DevSeedの所有者の読み取りのポリシーは在ると数える()
    {
        var policies = SeedScopeFixture.SeedPolicies()
            .Select(p => AbacPolicy.Create(p.Name, p.Action, p.UserConditions, p.DocumentConditions))
            .ToList();
        policies.Should().NotBeEmpty("seed を読んでいる");

        OwnerReadPolicyShape.CountActive(policies).Should().Be(1, "seed の所有者の読み取りのポリシーは 1 本");
    }

    [Fact]
    public void 運用仕様書の投入の本文は在ると数える()
    {
        var body = OperationsOwnerReadBody();
        var policy = AbacPolicy.Create(
            body.GetProperty("name").GetString()!,
            body.GetProperty("action").GetString()!,
            body.GetProperty("userConditions").Deserialize<Dictionary<string, List<string>>>(),
            body.GetProperty("documentConditions").Deserialize<Dictionary<string, List<string>>>());

        OwnerReadPolicyShape.Matches(policy).Should().BeTrue();
    }

    // 運用仕様書の §所有者の読み取りのポリシーの投入（次の `### ` まで）にある JSON のコードブロック。1 つだけであることも確かめる。
    private static JsonElement OperationsOwnerReadBody()
    {
        const string Heading = "### 所有者の読み取りのポリシーの投入";
        var text = File.ReadAllText(SeedScopeFixture.RepoFile("docs/operations/operations.md"));
        var start = text.IndexOf(Heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"運用仕様書に「{Heading}」の節がある");
        var end = text.IndexOf("\n### ", start + Heading.Length, StringComparison.Ordinal);
        var section = end < 0 ? text[start..] : text[start..end];

        var blocks = Regex.Matches(section, @"```json\s*\n(?<body>.*?)```", RegexOptions.Singleline);
        blocks.Should().ContainSingle("節の投入の本文（JSON）は 1 つ");
        return JsonDocument.Parse(blocks[0].Groups["body"].Value).RootElement.Clone();
    }
}
