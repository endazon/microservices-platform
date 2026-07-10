using DataSourceService.Api.Foundation.Domain;
using FluentAssertions;

namespace DataSourceService.Api.Tests;

// FR-01, FR-05: データソースの既定 ABAC 属性（機密区分）付与ロジックの単体テスト
public class DataSourceTests
{
    // FR-05: 既定属性未指定なら confidentiality=internal を補完する（フェイルセーフ既定値）
    [Fact]
    public void Create_WithoutAttributes_DefaultsConfidentialityToInternal()
    {
        var ds = DataSource.Create("fs", "filesystem", "smb://share");

        ds.DefaultAttributes.Should().ContainKey("confidentiality")
            .WhoseValue.Should().Be("internal");
    }

    // FR-05: 明示指定した機密区分は保持し、既定値で上書きしない
    [Fact]
    public void Create_WithExplicitConfidentiality_PreservesValue()
    {
        var ds = DataSource.Create("hr", "wiki", "https://wiki",
            defaultAttributes: new Dictionary<string, string>
            {
                ["confidentiality"] = "confidential",
                ["department"] = "hr",
            });

        ds.DefaultAttributes["confidentiality"].Should().Be("confidential");
        ds.DefaultAttributes["department"].Should().Be("hr");
    }

    // FR-05: 機密区分が空文字なら既定値で補完する
    [Fact]
    public void Create_WithBlankConfidentiality_FallsBackToDefault()
    {
        var ds = DataSource.Create("fs", "filesystem", "smb://share",
            defaultAttributes: new Dictionary<string, string> { ["confidentiality"] = "  " });

        ds.DefaultAttributes["confidentiality"].Should().Be("internal");
    }

    // 呼び出し側の辞書を後から変更しても内部状態に影響しない（防御的コピー）
    [Fact]
    public void Create_CopiesProvidedAttributes()
    {
        var input = new Dictionary<string, string> { ["confidentiality"] = "public" };
        var ds = DataSource.Create("fs", "filesystem", "smb://share", defaultAttributes: input);

        input["confidentiality"] = "restricted";

        ds.DefaultAttributes["confidentiality"].Should().Be("public");
    }

    // FR-05, IADR-0019: GetEffectiveAttributes は明示指定の機密区分をそのまま返す
    [Fact]
    public void GetEffectiveAttributes_PreservesExplicitConfidentiality()
    {
        var ds = DataSource.Create("hr", "wiki", "https://wiki",
            defaultAttributes: new Dictionary<string, string>
            {
                ["confidentiality"] = "confidential",
                ["department"] = "hr",
            });

        var effective = ds.GetEffectiveAttributes();
        effective["confidentiality"].Should().Be("confidential");
        effective["department"].Should().Be("hr");
    }

    // FR-05, IADR-0019: GetEffectiveAttributes は呼び出しごとに防御的コピーを返し、返値変更が内部に波及しない
    [Fact]
    public void GetEffectiveAttributes_ReturnsDefensiveCopy()
    {
        var ds = DataSource.Create("fs", "filesystem", "smb://share");

        ds.GetEffectiveAttributes()["confidentiality"] = "public";

        ds.GetEffectiveAttributes()["confidentiality"].Should().Be("internal");
    }
}
