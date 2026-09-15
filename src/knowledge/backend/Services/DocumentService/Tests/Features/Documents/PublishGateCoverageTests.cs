using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace DocumentService.Tests.Features.Documents;

// FR-19, ADR-0061 決定 1・2・4, [[IADR-0396]] 決定 4, [[IADR-0455]] 決定 1 (#1471):
// **`DocumentUpdated` を出す本番経路は、すべて発行の門を通る。**
//
// PR #1281 のレビュー修正（全経路を門へ寄せる）は squash マージの後に push されて develop に入らず、
// 10 経路が素の発行を直接呼んだまま残った。🔴 **挙動の試験はこれを検出できない** —— 組織文書では
// 門が常に通るため、どの経路の試験も「門を通ったか」の差を観測できない。だから経路の形そのものを固定する。
//
// `DocumentEndpoints.PublishUpdatedAsync` は `private` にしてあり、`DocumentEndpoints.` 付きで呼べば
// コンパイルで止まる。**この試験が止めるのは型で止まらない残り**である ——
// 経路がポートを直接叩く（`bus.PublishUpdatedAsync(`）形と、誰かが `private` を戻した場合。
[Trait("TestKind", "Unit")]
public class PublishGateCoverageTests
{
    private static readonly Regex DirectPublish = new(@"\bPublishUpdatedAsync\s*\(", RegexOptions.Compiled);

    // 素の発行が現れてよいファイル（サービスのルートからの相対パス・`/` 区切り）。
    // 🔴 **足すときは、その経路が門を通らない理由を IADR に書いてから足すこと。**
    private static readonly string[] Allowed =
    [
        "Domain/Ports/IDocumentUpdatedPublisher.cs", // ポートの宣言
        "Infrastructure/Messaging/WolverineDocumentUpdatedPublisher.cs", // アダプタの実装
        "Features/Documents/DocumentEndpoints.cs", // 門の内部実装
    ];

    [Fact]
    public void 本番経路は素の発行を直接呼ばない()
    {
        var hits = ScanProductionSources();

        // 🔴 陽性対照: 門の内部実装では一致が取れている。走査の根・除外・正規表現のどれかが壊れて
        // 0 件になっていても、下の陰性は緑になってしまう。
        hits.Should().Contain(h => h.File == "Features/Documents/DocumentEndpoints.cs",
            "走査が門の内部実装を見つけられないなら、走査そのものが壊れている");

        hits.Where(h => !Allowed.Contains(h.File))
            .Select(h => $"{h.File}:{h.Line}: {h.Text}")
            .Should().BeEmpty(
                "DocumentUpdated の発行は DocumentEndpoints の門（PublishUpdatedIfIndexableAsync / "
                + "PublishUpdatedIfIndexableOrWithdrawingAsync）を通す（IADR-0455 決定 1）");
    }

    private sealed record Hit(string File, int Line, string Text);

    private static List<Hit> ScanProductionSources()
    {
        var root = ServiceRoot();
        var hits = new List<Hit>();
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (IsExcluded(relative)) continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var text = lines[i].Trim();
                // 注記の中の名前は呼び出しではない（門の説明はこの名前を引く）。
                if (text.StartsWith("//", StringComparison.Ordinal)) continue;
                if (DirectPublish.IsMatch(text)) hits.Add(new Hit(relative, i + 1, text));
            }
        }
        return hits;
    }

    // テスト（偽ポートの実装を持つ）とビルド出力は本番経路ではない。
    private static bool IsExcluded(string relative) =>
        relative.StartsWith("Tests/", StringComparison.Ordinal)
        || relative.StartsWith("bin/", StringComparison.Ordinal)
        || relative.StartsWith("obj/", StringComparison.Ordinal);

    private static string ServiceRoot([CallerFilePath] string thisFile = "")
    {
        for (var dir = Directory.GetParent(thisFile); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("DocumentService.csproj").Any())
                return dir.FullName;
        }
        throw new InvalidOperationException($"DocumentService.csproj を {thisFile} の上位に見つけられなかった。");
    }
}
