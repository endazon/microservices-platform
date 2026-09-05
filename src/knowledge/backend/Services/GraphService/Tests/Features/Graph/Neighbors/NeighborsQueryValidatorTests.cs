using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Features.Graph.Neighbors;

namespace GraphService.Tests.Features.Graph.Neighbors;

// FR-17, UC-10, SC-18, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
// IADR-0395: 近傍探索のクエリ引数の検証を手書きガード節から AbstractValidator へ移した際の
// **振る舞い同値**を固定する。
//
// 🔴 **本文が 2 欄（`error` ＋ `message`）である。** `ErrorCode` と `ErrorMessage` の**両方**を
// 見る —— 片方だけ見ると、機械語だけ・説明文だけが変わる退行が捕まらない。
[Trait("TestKind", "Unit")]
public class NeighborsQueryValidatorTests
{
    private readonly NeighborsQueryValidator _validator = new();

    // 陽性対照: 未指定は既定値（2）へ縮退して通る。
    // **これが無いと「常に落ちる検証器」でも陰性側のテストが全部緑になる。**
    [Fact]
    public void UnspecifiedHops_Passes()
    {
        var result = _validator.Validate(new NeighborsQuery(null, null));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 境界（陽性側）: 1 と上限ちょうどは通る。**境界を片側だけ見ると off-by-one を見逃す。**
    [Theory]
    [InlineData(1)]
    [InlineData(GraphTraversal.MaxHops)]
    public void HopsAtBoundary_Passes(int hops)
    {
        var result = _validator.Validate(new NeighborsQuery(hops, null));

        result.IsValid.Should().BeTrue();
    }

    // 陰性 1: 範囲外は落ち、**移送前と同じ 2 欄**になる。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(GraphTraversal.MaxHops + 1)]
    public void HopsOutOfRange_FailsWithOriginalCodeAndMessage(int hops)
    {
        var result = _validator.Validate(new NeighborsQuery(hops, null));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorCode.Should().Be(NeighborsQueryValidator.HopsOutOfRangeCode);
        result.Errors[0].ErrorMessage.Should().Be(NeighborsQueryValidator.HopsOutOfRangeMessage);
        NeighborsQueryValidator.HopsOutOfRangeCode.Should().Be("hops_out_of_range");
        NeighborsQueryValidator.HopsOutOfRangeMessage.Should().Be("hops は 1〜3 で指定する（既定 2）。");
    }

    // 陽性対照 2: 型フィルタが GUID のカンマ区切りなら通る（空白の混入も許す）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("6d9f2e6a-1a3b-4c5d-8e7f-0a1b2c3d4e5f")]
    [InlineData("6d9f2e6a-1a3b-4c5d-8e7f-0a1b2c3d4e5f, 7e8f9a0b-1c2d-3e4f-5a6b-7c8d9e0f1a2b")]
    public void ValidTypes_Pass(string? types)
    {
        var result = _validator.Validate(new NeighborsQuery(null, types));

        result.IsValid.Should().BeTrue();
    }

    // 🔴 陽性対照 3: 区切り文字だけの `types` は**不正ではない**（要素が 1 つも無い ＝ 絞らない）。
    // 移送前も 400 にしていない。端点側で `null`（絞らない）へ縮退する。
    [Fact]
    public void TypesWithOnlySeparators_Passes()
    {
        var result = _validator.Validate(new NeighborsQuery(null, ",,,"));

        result.IsValid.Should().BeTrue();
    }

    // 陰性 2: GUID として読めない要素があれば落ち、**移送前と同じ 2 欄**になる。
    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("6d9f2e6a-1a3b-4c5d-8e7f-0a1b2c3d4e5f,not-a-guid")]
    public void InvalidTypes_FailWithOriginalCodeAndMessage(string types)
    {
        var result = _validator.Validate(new NeighborsQuery(null, types));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorCode.Should().Be(NeighborsQueryValidator.EdgeTypeFilterInvalidCode);
        result.Errors[0].ErrorMessage.Should().Be(NeighborsQueryValidator.EdgeTypeFilterInvalidMessage);
        NeighborsQueryValidator.EdgeTypeFilterInvalidCode.Should().Be("edge_type_filter_invalid");
        NeighborsQueryValidator.EdgeTypeFilterInvalidMessage.Should()
            .Be("types は辺の型 ID（GUID）のカンマ区切りで指定する。");
    }

    // 🔴 **規則の宣言順が応答の契約の一部である。** 端点は `Errors[0]` を本文へ載せるため、
    // 両方違反したときにどれが出るかは宣言順で決まる。移送前は hops を先に見ていた。
    [Fact]
    public void BothInvalid_ReportsHopsFirst()
    {
        var result = _validator.Validate(new NeighborsQuery(GraphTraversal.MaxHops + 1, "not-a-guid"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
        result.Errors[0].ErrorCode.Should().Be(NeighborsQueryValidator.HopsOutOfRangeCode);
    }
}
