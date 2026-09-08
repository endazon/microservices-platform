using AuthorizationService.Features.Authz.ResolveScope;
using AwesomeAssertions;

namespace AuthorizationService.Tests.Features.Authz.ResolveScope;

// FR-05, NFR-09, 計画 ADR-0088, [[IADR-0398]] 決定 1(b), [[IADR-0413]] 決定 8 (#1333):
// 🔴 **deny の監査ログへ偽の行を混ぜられないこと。**
//
// `userId` は**呼び出し元が本文で渡す検証されていない値**である
// （`ResolveScopeValidator` は `action` の値域しか持たず、gRPC 面は validator を通らない）。
// 🔴 **本 PR の前提が「呼び出し元の本文を信じない」であるのに、その値を改行ごと
// ログへ流していた** —— PR #1334 のレビュー（CodeQL）が捕まえた私の欠陥である。
//
// 判定に使う値は生のまま（IdP へ問い合わせる鍵であり、妙な値なら「居ない」で deny へ倒れる）。
// **削るのはログへ出す表現だけである。**
[Trait("TestKind", "Unit")]
public class ScopeUserAttributeLogSafetyTests
{
    // 🔴 T-01: **改行を混ぜてログ行を捏造できない。**
    [Theory]
    [InlineData("alice\nWARN 偽の行")]
    [InlineData("alice\r\nWARN 偽の行")]
    [InlineData("alice\rWARN 偽の行")]
    public void A_user_id_cannot_forge_a_new_log_line(string hostile)
    {
        var logged = ScopeUserAttributeSource.ForLog(hostile);

        logged.Should().NotContain("\n").And.NotContain("\r");
        logged.Should().Be("aliceWARN 偽の行", "制御文字だけを落とし、残りは読めるまま残す");
    }

    // 🔴 T-02: TAB や NUL といった他の制御文字も落とす
    // （構造化ログの区切りや C 文字列の終端に効く）。
    //
    // 🔴 **文字コードを数値で組み立てている。** ここでエスケープ表記を書いたところ
    // **実バイトの制御文字**になってファイルへ入り、git と grep がこのファイルを
    // バイナリ扱いした（#956 と同型。この PR で実際に起き、`check-nul-bytes` が止めた）。
    // 数値なら、途中の道具が何をしても壊れない。
    [Fact]
    public void Other_control_characters_are_dropped_too()
        => ScopeUserAttributeSource.ForLog("a" + (char)9 + "b" + (char)0 + "c").Should().Be("abc");

    // 🔴 T-03: **長大な値で 1 行を埋められない**（改行が無くても妨害は成り立つ）。
    [Fact]
    public void An_enormous_value_is_truncated()
        => ScopeUserAttributeSource.ForLog(new string('x', 5000)).Length.Should().Be(256);

    // 陽性対照: 普通の利用者名は**そのまま**出る。
    // これが無いと、上の 3 つは「常に空文字を返す実装」でも緑になる。
    [Theory]
    [InlineData("tanaka.taro")]
    [InlineData("佐藤 花子")]
    [InlineData("user+alias@example.test")]
    public void An_ordinary_user_name_survives_unchanged(string name)
        => ScopeUserAttributeSource.ForLog(name).Should().Be(name);

    // 空・null は「読めない値が出た」ことが分かる形にする（空欄で消さない）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n\r\t")]
    public void An_unreadable_value_is_still_visible_in_the_log(string? value)
        => ScopeUserAttributeSource.ForLog(value).Should().Be("(空)");
}
