using DataSourceService.Api.Foundation.Services;
using AwesomeAssertions;

namespace DataSourceService.Api.Tests;

// FR-01, FR-05, UC-04, SC-06（Q14 / issue #537）, IADR-0053: 直近同期エラーのマスク。
// 例外メッセージは接続文字列・資格情報つき URL を運ぶため、**保存の時点で**伏せる
// （表示の時点ではない。DB に平文が残るとバックアップ・ログ・将来の別の読み口すべてが漏洩面になる）。
public class SyncErrorRedactorTests
{
    [Theory]
    // 接続文字列（キー=値）。キー名は残し値だけを伏せる（原因の切り分けには「どの項目か」が要る）。
    [InlineData("Npgsql: Host=db;Username=app;Password=hunter2;Database=x",
                "Npgsql: Host=db;Username=app;Password=***;Database=x")]
    [InlineData("auth failed (apiToken=abc123def)", "auth failed (apiToken=***)")]
    // HTTP 認証ヘッダはスキーム語を挟む。キー付きの形はスキーム語ごと値側へ畳んで伏せる
    // （2 度当てると *** *** に二重置換されるため）。
    [InlineData("header Authorization: Bearer xyz", "header Authorization: ***")]
    // キーを伴わない裸のスキームはスキーム語を残す（どの認証方式で落ちたかは切り分けに要る）。
    [InlineData("got 401 with Basic dXNlcjpwYXNz", "got 401 with Basic ***")]
    [InlineData("client_secret: s3cr3t", "client_secret: ***")]
    // 資格情報つき URI。**ユーザー名も残さない**（アカウント名自体が攻撃の手掛かりになる）。
    [InlineData("connect https://svc-account:p@ssw0rd@wiki.example.test/api failed",
                "connect https://***@wiki.example.test/api failed")]
    // 秘密を含まないメッセージは素通しする（過剰マスクは原因の切り分けを潰す）。
    [InlineData("discover boom", "discover boom")]
    [InlineData("smb://share/docs is unreachable", "smb://share/docs is unreachable")]
    public void Redact_MasksSecrets_ButKeepsDiagnosticContext(string input, string expected)
        => SyncErrorRedactor.Redact(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Redact_NullOrBlank_ReturnsNull(string? input)
        // 空文字を保存すると「エラーは在るがメッセージが無い」と読めてしまうため null に畳む。
        => SyncErrorRedactor.Redact(input).Should().BeNull();

    [Fact]
    public void Redact_LongMessage_IsTruncatedToMaxLength()
    {
        var redacted = SyncErrorRedactor.Redact(new string('x', SyncErrorRedactor.MaxLength * 2));
        redacted.Should().HaveLength(SyncErrorRedactor.MaxLength);
    }

    [Fact]
    public void Redact_SecretBeyondTruncationBoundary_IsStillMasked()
    {
        // 切り詰めは**マスクの後**に行う。順序が逆だと、上限より後ろにある秘密が
        // 「切り詰めたから消えた」ように見えて、上限を伸ばした瞬間に漏れる。
        var message = new string('x', SyncErrorRedactor.MaxLength - 10) + "Password=hunter2";

        var redacted = SyncErrorRedactor.Redact(message);

        redacted.Should().NotContain("hunter2");
    }
}
