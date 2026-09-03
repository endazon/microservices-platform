using AwesomeAssertions;
using DataSourceService.Domain;

namespace DataSourceService.Tests.Domain;

// FR-05, UC-04, SC-06, ADR-0036, ADR-0074 決定 1・4 (#1194): 写像表の正規化・検証・解決。
// **HTTP も EF も要らない純粋な判断**なので、ここで直接固定する。
public class OwnerMappingTableTests
{
    [Fact]
    public void Normalize_TrimsBothSides_ButDoesNotFoldCase()
    {
        var result = OwnerMappingTable.Normalize(new Dictionary<string, string>
        {
            ["  hr\\Tanaka  "] = "  alice  ",
        });

        result.Should().ContainKey("hr\\Tanaka").WhoseValue.Should().Be("alice");
        // 🔴 大小文字を畳まない。別名前空間の識別子どうしを実装の裁量で同一視することは
        // 「推測で埋める」ことに他ならない（09_datasource-connectors）。
        result.Should().NotContainKey("hr\\tanaka");
    }

    [Fact]
    public void ValidateShape_RejectsBlankTarget_AndBlankSourceKey()
    {
        OwnerMappingTable.ValidateShape(new Dictionary<string, string> { ["src"] = " " })
            .Should().ContainSingle();
        OwnerMappingTable.ValidateShape(new Dictionary<string, string> { [" "] = "alice" })
            .Should().ContainSingle();
        // 陽性対照: 正しい対は 1 件も誤りを出さない。
        OwnerMappingTable.ValidateShape(new Dictionary<string, string> { ["src"] = "alice" })
            .Should().BeEmpty();
    }

    [Fact]
    public void ValidateTargetsExist_NamesEveryMissingTarget_Once()
    {
        var mappings = new Dictionary<string, string>
        {
            ["a"] = "ghost",
            ["b"] = "ghost",
            ["c"] = "alice",
            ["d"] = "spectre",
        };
        var known = new HashSet<string>(StringComparer.Ordinal) { "alice", "bob" };

        var errors = OwnerMappingTable.ValidateTargetsExist(mappings, known);

        errors.Should().ContainSingle();
        errors[0].Should().Contain("ghost").And.Contain("spectre");
        // 同じ値を 2 度並べない（対の数ではなく、実在しない値の数を伝える）。
        errors[0].Split("ghost").Length.Should().Be(2);
    }

    [Fact]
    public void ValidateTargetsExist_IsCaseSensitive()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "alice" };
        // `owner` の突合は `DocumentBodyIntake.CanWrite` が Ordinal で行う。
        // ここで大小文字を許すと、保存も検証も通るのに一致しない写像ができる。
        OwnerMappingTable.ValidateTargetsExist(
            new Dictionary<string, string> { ["a"] = "Alice" }, known).Should().ContainSingle();
        OwnerMappingTable.ValidateTargetsExist(
            new Dictionary<string, string> { ["a"] = "alice" }, known).Should().BeEmpty();
    }

    [Fact]
    public void ResolveOwner_ReturnsMappedUser_WhenTheTableHits()
    {
        var source = DataSource.Create("s", "db", "", ownerMappings:
            new Dictionary<string, string> { ["hr\\tanaka"] = "alice" });

        source.ResolveOwner("hr\\tanaka").Should().Be("alice");
        // 前後空白はソース側の値にも付き得る（DB の char 列など）。
        source.ResolveOwner("  hr\\tanaka  ").Should().Be("alice");
    }

    [Fact]
    public void ResolveOwner_ReturnsNull_WhenTheTableMisses_NeverTheRawIdentifier()
    {
        var source = DataSource.Create("s", "db", "", ownerMappings:
            new Dictionary<string, string> { ["hr\\tanaka"] = "alice" });

        // 🔴 **生の識別子を返さない。** 返すと別名前空間の値がそのまま `owner` になる。
        source.ResolveOwner("hr\\suzuki").Should().BeNull();
        source.ResolveOwner(null).Should().BeNull();
        source.ResolveOwner("  ").Should().BeNull();
    }

    [Fact]
    public void ResolveOwner_ReturnsNull_WhenNoTableIsConfigured()
    {
        // 陰性。写像表が空のソースは従来どおり（＝予約値へ倒れる）。
        DataSource.Create("s", "filesystem", "").ResolveOwner("root").Should().BeNull();
    }

    [Fact]
    public void Patch_AndDefaultAttributes_DoNotOverwriteEachOther()
    {
        var source = DataSource.Create("s", "db", "",
            defaultAttributes: new Dictionary<string, string> { ["confidentiality"] = "restricted" },
            ownerMappings: new Dictionary<string, string> { ["a"] = "alice" });

        source.Patch(defaultAttributes: new Dictionary<string, string> { ["confidentiality"] = "internal" });
        source.OwnerMappings.Should().ContainKey("a");

        source.Patch(ownerMappings: new Dictionary<string, string> { ["b"] = "bob" });
        source.DefaultAttributes["confidentiality"].Should().Be("internal");
        source.OwnerMappings.Should().ContainKey("b").And.NotContainKey("a");
    }

    [Fact]
    public void Update_KeepsOwnerMappings_WhenOmitted_AndClearsThem_WhenEmptyGiven()
    {
        var source = DataSource.Create("s", "db", "", ownerMappings:
            new Dictionary<string, string> { ["a"] = "alice" });

        source.Update("s2", "db", "", config: [], defaultAttributes: []);
        source.OwnerMappings.Should().ContainKey("a", "後から足した項目を全置換の巻き添えで消さない");

        source.Update("s3", "db", "", config: [], defaultAttributes: [], ownerMappings: []);
        source.OwnerMappings.Should().BeEmpty("{} は「空にする」の明示である");
    }
}
