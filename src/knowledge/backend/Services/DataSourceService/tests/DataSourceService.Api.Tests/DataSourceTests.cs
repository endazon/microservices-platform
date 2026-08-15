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

    // ---------------------------------------------------------------------
    // FR-05, UC-04, ADR-0036, #516: システム投入経路の owner / department
    // 計画確定（planning#344・2026-08-15）。IADR-0199。
    // ---------------------------------------------------------------------

    // #516: 既定属性が空でも、計画が必須と定める 3 属性がすべて付与される（欠落させない）
    [Fact]
    public void Create_WithoutAttributes_FillsAllRequiredAttributes()
    {
        var ds = DataSource.Create("fs", "filesystem", "smb://share");

        ds.DefaultAttributes["confidentiality"].Should().Be("internal");
        ds.DefaultAttributes["owner"].Should().Be("system");
        ds.DefaultAttributes["department"].Should().Be("unassigned");
    }

    // #516: データソース既定属性に department があればそれが使われ、予約値で上書きしない
    [Fact]
    public void Create_WithExplicitDepartment_DoesNotFallBackToReservedValue()
    {
        var ds = DataSource.Create("hr", "wiki", "https://wiki",
            defaultAttributes: new Dictionary<string, string> { ["department"] = "hr" });

        ds.DefaultAttributes["department"].Should().Be("hr");
        // owner は解決できないので予約値へ倒れる（SourceItem が更新者を運ばない。#752）
        ds.DefaultAttributes["owner"].Should().Be("system");
    }

    // #516: 明示指定した owner は保持する（予約値で上書きしない）
    [Fact]
    public void Create_WithExplicitOwner_PreservesValue()
    {
        var ds = DataSource.Create("hr", "wiki", "https://wiki",
            defaultAttributes: new Dictionary<string, string> { ["owner"] = "alice" });

        ds.DefaultAttributes["owner"].Should().Be("alice");
    }

    // #516: 空白のみの値は「未設定」と同じ扱いで予約値を入れる（confidentiality と同じ規約）
    [Theory]
    [InlineData("owner", "system")]
    [InlineData("department", "unassigned")]
    public void Create_WithBlankRequiredAttribute_FallsBackToReservedValue(string key, string expected)
    {
        var ds = DataSource.Create("fs", "filesystem", "smb://share",
            defaultAttributes: new Dictionary<string, string> { [key] = "   " });

        ds.DefaultAttributes[key].Should().Be(expected);
    }

    // #516: 3 属性すべて明示ならすべて素通しする
    [Fact]
    public void Create_WithAllRequiredAttributesExplicit_PreservesAll()
    {
        var ds = DataSource.Create("hr", "wiki", "https://wiki",
            defaultAttributes: new Dictionary<string, string>
            {
                ["confidentiality"] = "confidential",
                ["owner"] = "alice",
                ["department"] = "hr",
            });

        ds.DefaultAttributes["confidentiality"].Should().Be("confidential");
        ds.DefaultAttributes["owner"].Should().Be("alice");
        ds.DefaultAttributes["department"].Should().Be("hr");
    }

    // #516: Update / Patch / GetEffectiveAttributes でも同じ補完が働く（4 経路の一元化の退行防止）。
    // **1 箇所でも漏れると「登録時は付くが更新すると消える」という最も気づきにくい壊れ方になる。**
    [Fact]
    public void Update_FillsRequiredAttributes()
    {
        var ds = DataSource.Create("fs", "filesystem", "smb://share");

        ds.Update("fs", "filesystem", "smb://share",
            defaultAttributes: new Dictionary<string, string> { ["confidentiality"] = "internal" });

        ds.DefaultAttributes["owner"].Should().Be("system");
        ds.DefaultAttributes["department"].Should().Be("unassigned");
    }

    [Fact]
    public void Patch_FillsRequiredAttributes()
    {
        var ds = DataSource.Create("fs", "filesystem", "smb://share");

        ds.Patch(defaultAttributes: new Dictionary<string, string> { ["department"] = "sales" });

        ds.DefaultAttributes["department"].Should().Be("sales");
        ds.DefaultAttributes["owner"].Should().Be("system");
        ds.DefaultAttributes["confidentiality"].Should().Be("internal");
    }

    // #516, IADR-0019: 本対応マージ前から登録済みで owner / department を持たないデータソースでも、
    // 発行時に必ず補完される（最終防衛線）。
    //
    // **公開 API 経由では旧状態を作れない** —— `Create` / `Update` / `Patch` はいずれもフェイルセーフを
    // 通るため、それらで組み立てた時点で属性は既に埋まっている。**それを読んでも本検査にならない**
    // （`GetEffectiveAttributes` の補完を外しても緑のままになり、偽の安全網になる）。
    // したがって **EF の materialize と同じ経路**（private ctor ＋ private setter）を反射で再現し、
    // **補完を一度も通っていない旧行**を作って当てる。
    [Fact]
    public void GetEffectiveAttributes_FillsRequiredAttributesForLegacyRows()
    {
        var ds = MaterializeLegacyRow(new Dictionary<string, string> { ["confidentiality"] = "public" });

        // 前提を明示する: 旧行は補完を通っていない（この assert が落ちるなら再現方法が壊れている）
        ds.DefaultAttributes.Should().NotContainKey("owner");
        ds.DefaultAttributes.Should().NotContainKey("department");

        var effective = ds.GetEffectiveAttributes();

        effective["confidentiality"].Should().Be("public");
        effective["owner"].Should().Be("system");
        effective["department"].Should().Be("unassigned");
    }

    // EF Core が DB から復元するのと同じ形（private 既定コンストラクタ ＋ private setter）で
    // DataSource を組み立てる。**フェイルセーフを一度も通さない**ことが目的。
    private static DataSource MaterializeLegacyRow(Dictionary<string, string> defaultAttributes)
    {
        var ds = (DataSource)Activator.CreateInstance(typeof(DataSource), nonPublic: true)!;
        typeof(DataSource)
            .GetProperty(nameof(DataSource.DefaultAttributes))!
            .SetValue(ds, defaultAttributes);
        return ds;
    }

    // #516: lifecycle は**意図的に補完しない**（計画が取り込み経路での既定を裁定していない）。
    // 裁定が下りたらこのテストは「補完する」へ**反転させる**こと。
    // 環流: feedback/20260815_ingestion-lifecycle-default-unadjudicated.md
    [Fact]
    public void Create_DoesNotFillLifecycle_BecauseDefaultIsNotAdjudicated()
    {
        var ds = DataSource.Create("fs", "filesystem", "smb://share");

        ds.DefaultAttributes.Should().NotContainKey("lifecycle");
    }
}
