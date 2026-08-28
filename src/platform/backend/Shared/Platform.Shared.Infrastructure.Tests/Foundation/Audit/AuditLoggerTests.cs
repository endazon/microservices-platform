using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Tests.Testing;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Audit;

// FR-15, ADR-0004, IADR-0216 決定 2 (#901): 監査ログの**出口の形**を固定する。
//
// 🔴 **本ファイルが塞ぐ穴。** 着手前の実測で AuditLogger は line 0/6・branch 0/2 だった。
// リポジトリ全体を型名（AuditLogger / IAuditLogger）とメソッド名の 2 軸で走査したところ、
// **実体を new して Record を呼ぶテストは Platform.Bff.Tests/PlatformLoggingTests.cs の 1 件だけ**で、
// NotificationService / DocumentService のテストは RecordingAuditLogger へ**差し替えている**
// （実装は 1 行も通らない）。共有ライブラリ側には専用テストが 1 件も無い。
//
// この経路が壊れても **例外も警告も出ない。** ログは出続け、監査だけが
// 可観測性基盤から抽出できなくなる —— docs/security/security.md が約束する
// 「Audit=true を構造化プロパティに付与し、可観測性基盤で監査として抽出可能」が静かに失われる。
//
// 整形済み文字列ではなく **State の key/value** で表明する。整形結果だけを見ると、
// 抽出キーの喪失（プロパティ名の変更・欠落）を見逃す。
public class AuditLoggerTests
{
    private static (AuditLogger Sut, RecordingLogger<AuditLogger> Log) Build()
    {
        var log = new RecordingLogger<AuditLogger>();
        return (new AuditLogger(log), log);
    }

    private static object? PropertyOf(RecordingLogger<AuditLogger>.Entry entry, string key) =>
        entry.State.FirstOrDefault(p => p.Key == key).Value;

    [Fact]
    public void 監査は_Information_で1件だけ出る()
    {
        var (sut, log) = Build();

        sut.Record("config.read", "tester", "granted");

        // レベルが Debug/Trace へ落ちると既定の収集構成から外れ、監査が届かなくなる。
        var entry = log.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Information);
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
        log.OfLevel(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public void 抽出キーが構造化プロパティとして揃う()
    {
        var (sut, log) = Build();

        sut.Record("config.read", "tester", "granted", "unit-test");

        var entry = log.Entries.Should().ContainSingle().Which;
        PropertyOf(entry, "AuditAction").Should().Be("config.read");
        PropertyOf(entry, "AuditSubject").Should().Be("tester");
        PropertyOf(entry, "AuditOutcome").Should().Be("granted");
        PropertyOf(entry, "AuditDetail").Should().Be("unit-test");
    }

    // 🔴 docs/security/security.md が約束する抽出条件そのもの。
    // これが落ちると「監査ログを可観測性基盤で抽出できる」という約束だけが静かに嘘になる。
    [Fact]
    public void Audit_フラグが_true_で付く()
    {
        var (sut, log) = Build();

        sut.Record("config.read", "tester", "denied");

        PropertyOf(log.Entries.Single(), "Audit").Should().Be(true);
    }

    // detail 省略時の縮退（`detail ?? string.Empty`）。null のままだと構造化ログ側で
    // プロパティが欠落し、「detail が無い監査」と「監査でない」の区別が付かなくなる。
    [Fact]
    public void detail省略時は空文字になる_nullにしない()
    {
        var (sut, log) = Build();

        sut.Record("config.read", "tester", "granted");

        PropertyOf(log.Entries.Single(), "AuditDetail").Should().Be(string.Empty);
    }

    // 上の対照条件。これが無いと「常に空文字を入れる」実装でも通る。
    [Fact]
    public void detail指定時はその値が載る()
    {
        var (sut, log) = Build();

        sut.Record("config.read", "tester", "granted", "対象=pipeline.json");

        PropertyOf(log.Entries.Single(), "AuditDetail").Should().Be("対象=pipeline.json");
    }

    // 許可と拒否が同じ形で記録される（outcome だけが違う）。
    // 拒否だけ落とす実装は「監査はあるが拒否が見えない」という最も危険な壊れ方になる。
    [Theory]
    [InlineData("granted")]
    [InlineData("denied")]
    public void 許可も拒否も同じ形で記録する(string outcome)
    {
        var (sut, log) = Build();

        sut.Record("config.read", "tester", outcome);

        var entry = log.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Information);
        PropertyOf(entry, "AuditOutcome").Should().Be(outcome);
        PropertyOf(entry, "Audit").Should().Be(true);
    }
}
