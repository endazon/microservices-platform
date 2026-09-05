using System.Text.Json;
using AwesomeAssertions;
using DataSourceService.Infrastructure.ExternalServices;

namespace DataSourceService.Tests.Infrastructure.ExternalServices;

// FR-05, UC-04, ADR-0036, ADR-0074, Issue #752:
// 更新者の読み取りと**由来の分類**の単体テスト。
//
// 🔴 **本クラスが守るのは「取れなかった」と「取ったら空だった」を混ぜないことである。**
// どちらも `SourceItem.UpdatedBy` では null になり、`ResolveOwner` は同じく予約値へ倒す。
// **落ち方が同じでも由来を潰さない** —— 潰すと運用時に「項目名の設定を間違えている」のか
// 「ソース側が本当に空なのか」を区別できず、予約値の山を読み違える。
[Trait("TestKind", "Unit")]
public sealed class SourceUpdatedByTests
{
    private static Dictionary<string, JsonElement> Extra(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    // ---- 陽性 -------------------------------------------------------------

    [Fact]
    public void FromJson_CarriesTheRawValue_WhenTheFieldIsAString()
    {
        var value = SourceUpdatedBy.FromJson(Extra("""{"updatedBy":"hr\\tanaka"}"""), "updatedBy");

        value.Origin.Should().Be(SourceUpdatedByOrigin.Carried);
        // **素のまま載せる**（正規化しない）。突合の正規化は ResolveOwner 側の責務である。
        value.Value.Should().Be("hr\\tanaka");
    }

    [Fact]
    public void FromJson_UsesTheConfiguredFieldName()
    {
        // 実 Wiki / SaaS 製品ごとに項目名が違うため、名前は構成可能である。
        var extra = Extra("""{"lastModifiedBy":"alice","updatedBy":"bob"}""");

        SourceUpdatedBy.FromJson(extra, "lastModifiedBy").Value.Should().Be("alice");
    }

    [Fact]
    public void FromJson_MatchesCaseInsensitively_WhenTheExactNameMisses()
    {
        // JsonSerializerDefaults.Web が宣言済みプロパティに対して行っている突合と揃える。
        var value = SourceUpdatedBy.FromJson(Extra("""{"UpdatedBy":"alice"}"""), "updatedBy");

        value.Origin.Should().Be(SourceUpdatedByOrigin.Carried);
        value.Value.Should().Be("alice");
    }

    // ---- 陰性（3 つの「値が無い」を区別する） ------------------------------

    [Fact]
    public void FromJson_NotCarried_WhenTheFieldIsAbsent()
    {
        // 🔴 「取れなかった」。**項目を構成していないのは正常な状態であり、不備ではない。**
        var value = SourceUpdatedBy.FromJson(Extra("""{"title":"A"}"""), "updatedBy");

        value.Origin.Should().Be(SourceUpdatedByOrigin.NotCarried);
        value.Value.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"updatedBy":""}""")]
    [InlineData("""{"updatedBy":"   "}""")]
    [InlineData("""{"updatedBy":null}""")]
    public void FromJson_BlankAtSource_WhenTheFieldExistsButHasNoValue(string json)
    {
        // 🔴 「取ったら空だった」。**項目は在る** —— ソース側のデータ不備であって構成の不備ではない。
        var value = SourceUpdatedBy.FromJson(Extra(json), "updatedBy");

        value.Origin.Should().Be(SourceUpdatedByOrigin.BlankAtSource);
        value.Value.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"updatedBy":{"name":"alice"}}""")]
    [InlineData("""{"updatedBy":["alice"]}""")]
    [InlineData("""{"updatedBy":42}""")]
    [InlineData("""{"updatedBy":true}""")]
    public void FromJson_Unreadable_WhenTheFieldIsNotAString(string json)
    {
        // 構成した項目名が別物を指している兆候。**推測で文字列化しない。**
        var value = SourceUpdatedBy.FromJson(Extra(json), "updatedBy");

        value.Origin.Should().Be(SourceUpdatedByOrigin.Unreadable);
        value.Value.Should().BeNull();
    }

    [Fact]
    public void FromJson_NotCarried_WhenThereIsNoExtensionDataAtAll()
    {
        SourceUpdatedBy.FromJson(null, "updatedBy").Origin.Should().Be(SourceUpdatedByOrigin.NotCarried);
        SourceUpdatedBy.FromJson(Extra("{}"), "updatedBy").Origin.Should().Be(SourceUpdatedByOrigin.NotCarried);
    }

    // ---- DB 列由来 ---------------------------------------------------------

    [Fact]
    public void FromDbValue_CarriesStringsAndNumericIdentifiers()
    {
        SourceUpdatedBy.FromDbValue("tanaka").Value.Should().Be("tanaka");
        // 社員番号を整数列で持つソースは珍しくない。**値そのものであって推測ではない。**
        SourceUpdatedBy.FromDbValue(1024).Value.Should().Be("1024");
    }

    [Fact]
    public void FromDbValue_BlankAtSource_ForSqlNull()
    {
        // 🔴 列は在るのに値が NULL ＝「取ったら空だった」。列そのものが無い場合とは違う。
        SourceUpdatedBy.FromDbValue(DBNull.Value).Origin.Should().Be(SourceUpdatedByOrigin.BlankAtSource);
        SourceUpdatedBy.FromDbValue(null).Origin.Should().Be(SourceUpdatedByOrigin.BlankAtSource);
        SourceUpdatedBy.FromDbValue("  ").Origin.Should().Be(SourceUpdatedByOrigin.BlankAtSource);
    }

    [Fact]
    public void FromDbValue_Unreadable_ForValuesThatAreNotIdentifiers()
        => SourceUpdatedBy.FromDbValue(new byte[] { 1, 2 }).Origin
            .Should().Be(SourceUpdatedByOrigin.Unreadable);

    // ---- SQL 識別子の検証 --------------------------------------------------

    [Theory]
    [InlineData("updated_by")]
    [InlineData("UpdatedBy")]
    [InlineData("_col9")]
    public void IsSafeSqlIdentifier_AcceptsPlainIdentifiers(string identifier)
        => SourceUpdatedBy.IsSafeSqlIdentifier(identifier).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("9col")]
    [InlineData("a b")]
    [InlineData("a-b")]
    [InlineData("src.updated_by")]
    [InlineData("x FROM users; DROP TABLE t --")]
    [InlineData("\"quoted\"")]
    public void IsSafeSqlIdentifier_RejectsEverythingElse(string? identifier)
        // 🔴 `query` を自由に書ける経路が別に在ることは、識別子を無検査で連結してよい理由にならない。
        => SourceUpdatedBy.IsSafeSqlIdentifier(identifier).Should().BeFalse();

    // ---- 集計 --------------------------------------------------------------

    [Fact]
    public void Tally_DoesNotRaiseAnomaly_ForNotCarriedAlone()
    {
        var tally = new SourceUpdatedByTally();
        tally.Add(SourceUpdatedByOrigin.NotCarried);
        tally.Add(SourceUpdatedByOrigin.Carried);

        // 🔴 「項目を構成していない」で鳴らさない。鳴らすのは「在ったのに使えなかった」だけである。
        tally.HasAnomaly.Should().BeFalse();
        tally.NotCarried.Should().Be(1);
        tally.Carried.Should().Be(1);

        tally.Add(SourceUpdatedByOrigin.BlankAtSource);
        tally.HasAnomaly.Should().BeTrue();
    }
}
