using AwesomeAssertions;
using DocumentService.Domain.Ports;

namespace DocumentService.Tests;

// FR-19, [[IADR-0474]] の #1583 追記, #1598: 試験のスタブ自身の約束の自己試験。
// 🔴 **列の最後の答えに例外を置かない**（尽きた後はその答えを返し続け、同じクラスの後続の試験の周期の
// 1 巡目の判定を落とす。#1583 の CI で実測）。約束はコメントだけでは守られないので、宣言の時点で強制する。
public sealed class StubOwnerRetentionDirectoryTests
{
    [Fact]
    public void 列の最後に例外を投げる答えを置くと宣言の時点で失敗する()
    {
        var stub = new StubOwnerRetentionDirectory();

        var act = () => stub.DeclareSequence("owner",
            StubOwnerRetentionDirectory.Departed,
            () => throw new TimeoutException("fake"));

        act.Should().Throw<ArgumentException>().WithInnerException<TimeoutException>();
    }

    // 陽性対照: 例外を投げる答えが途中に在るだけなら宣言でき、宣言時の評価は列を消費しない（1 回目は先頭の答え）。
    [Fact]
    public async Task 途中の答えが例外を投げるだけなら宣言でき列は先頭から答える()
    {
        var stub = new StubOwnerRetentionDirectory();

        stub.DeclareSequence("owner",
            StubOwnerRetentionDirectory.Departed,
            () => throw new TimeoutException("fake"),
            () => null);

        (await stub.GetAsync("owner", TestContext.Current.CancellationToken))
            .Should().Be(StubOwnerRetentionDirectory.Departed());
        var second = () => stub.GetAsync("owner", TestContext.Current.CancellationToken);
        await second.Should().ThrowAsync<TimeoutException>();
        (await stub.GetAsync("owner", TestContext.Current.CancellationToken)).Should().BeNull();
        (await stub.GetAsync("owner", TestContext.Current.CancellationToken)).Should().BeNull("最後の答えを返し続ける");
    }
}
