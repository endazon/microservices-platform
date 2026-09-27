using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Platform.Bff.Foundation.Endpoints;

namespace Platform.Bff.Tests;

// SC-22, ADR-0095, IADR-0453 フォローアップ 4 の 2026-09-27 追記, IADR-0456 決定 4 (#1472):
// **監査の抽出条件を約束する文書（docs/security/security.md「監査ログ」）の SC-22 の行が、
// 境界層が実際に記録する action と outcome をすべて挙げていること**を固定する。
//
// 🔴 本リポジトリに監査の抽出クエリは無い。抽出を書く人は文書の行を見て action と outcome を列挙する。
// 文書が action を 1 つ書き漏らすと、その action の `failed`（同期の依頼が通らない＝書けたのに Pod へ届かない）が
// 抽出から黙って落ちる —— `secret.item.sync` は即時同期の導入時に足されたが、文書の行に無かった（2026-09-27 に検出）。
public class SecretItemAuditDocTests
{
    private static readonly string SecurityDocPath = RepoPaths.Resolve("docs/security/security.md");
    private static readonly string EndpointsSourcePath = RepoPaths.Resolve(
        "src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/SecretItemBffEndpoints.cs");

    // 監査ログの表のうち、`/bff/secrets` 系の行（1 行だけのはず）。
    private static string SecretItemsAuditRow()
    {
        var rows = File.ReadAllLines(SecurityDocPath)
            .Where(l => l.StartsWith('|') && l.Contains("`/bff/secrets`", StringComparison.Ordinal))
            .ToArray();
        rows.Should().ContainSingle("監査ログの表に秘密情報の一覧・投入の行が 1 行だけあること");
        return rows[0];
    }

    // 境界層が名乗る監査の action（`*Action` 定数）。反射で拾うので、定数を足せば本試験が文書への追記を求める。
    private static string[] DeclaredActions() =>
        typeof(SecretItemBffEndpoints)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Action", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void Audit_row_lists_every_action_the_endpoints_record()
    {
        var actions = DeclaredActions();
        // 陽性対照: 反射が定数を拾えていること（0 件で緑にならない）。
        actions.Should().Contain(["secret.item.list", "secret.item.update", "secret.item.sync"]);

        var row = SecretItemsAuditRow();
        foreach (var action in actions)
            row.Should().Contain($"`{action}`", $"監査の抽出を書く人が {action} を列挙できること");
    }

    [Fact]
    public void Audit_row_lists_every_outcome_the_endpoints_record()
    {
        // outcome は呼び出しごとのリテラルで、定数が無い。境界層の実装から `audit.Record(…, "<outcome>"` を拾う。
        var source = File.ReadAllText(EndpointsSourcePath);
        var matches = Regex.Matches(source, """audit\.Record\(\s*[^,]+,\s*[^,]+,\s*"(?<outcome>[a-z-]+)"\s*""");
        // outcome をリテラル以外で渡す呼び出しを黙って読み飛ばさない（呼び出しの数と拾えた数を合わせる）。
        matches.Count.Should().Be(
            Regex.Matches(source, @"audit\.Record\(").Count,
            "監査の記録はすべて outcome をリテラルで渡し、本試験がそれを読めること");

        var outcomes = matches
            .Select(m => m.Groups["outcome"].Value)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        // 陽性対照: 3 値すべてを拾えていること（正規表現の取りこぼしで緑にならない）。
        outcomes.Should().Equal("denied", "failed", "granted");

        var row = SecretItemsAuditRow();
        foreach (var outcome in outcomes)
            row.Should().Contain($"`{outcome}`", $"監査の抽出が outcome={outcome} を落とさないこと");
    }
}
