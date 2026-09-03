using AwesomeAssertions;
using Knowledge.Contracts.Indexing;
using Xunit;

namespace Knowledge.Contracts.Tests;

// FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 3:
// 索引テキストから本文抜粋を導く射影。取り込み側と検索側が共有する唯一の点である。
public class DocumentBodyPresenceTests
{
    // 🔴 **変異試験**: `Excerpt` を `hasBody ? indexedText : string.Empty` から
    // `indexedText`（恒等関数）へ変異させると、**本文なしの点に載せたメタデータ（題名・タグ）が
    // 本文の抜粋として画面（SC-02）と LLM の文脈（FR-04）へ出る。** 本テストがその変異を殺す。
    [Fact]
    public void Excerpt_本文なしの点では索引テキストを返さない()
    {
        DocumentBodyPresence.Excerpt("2026 年度 経費精算マニュアル 経理", hasBody: false)
            .Should().BeEmpty();
    }

    // 陽性対照: 本文ありは素通しである（「常に空にする」実装だと上のテストだけでは緑になる）。
    [Fact]
    public void Excerpt_本文ありは索引テキストをそのまま返す()
    {
        DocumentBodyPresence.Excerpt("…精算の締め日は毎月 25 日とし…", hasBody: true)
            .Should().Be("…精算の締め日は毎月 25 日とし…");
    }

    // 索引に `text` が無い点でも null を返さない（呼び出し側の分岐を増やさない）。
    [Fact]
    public void Excerpt_索引テキストが無ければ空文字列になる()
    {
        DocumentBodyPresence.Excerpt(null, hasBody: true).Should().BeEmpty();
    }

    // 欠落は「本文あり」である（既存の点はすべて本文チャンクであり、backfill を要らなくする既定）。
    [Fact]
    public void 欠落時の既定は本文ありである()
    {
        DocumentBodyPresence.DefaultWhenAbsent.Should().BeTrue();
    }
}
