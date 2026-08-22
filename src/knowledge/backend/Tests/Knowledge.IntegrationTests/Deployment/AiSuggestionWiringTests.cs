using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;

namespace Knowledge.IntegrationTests.Deployment;

// FR-18, ADR-0033 決定 10 (#914 / #911): **却下解除が「未配線」であることを機械で固定する。**
//
// ## なぜこのテストが要るのか
//
// #914 は却下解除の**判定ロジック**（`AiSuggestion.TryReinstate`）を実装した。しかし
// **発火させる経路が存在しない** —— 本文の変更を受け取るのは `DocumentUpdated` の購読であり、
// それは #911 の射程で、#911 は保留中だからである（GraphService には消費者が 1 つも無い）。
//
// 🔴 **散文で「未配線」と書くだけでは足りない。書いた本人以外は読まない。**
// この状態を放置すると、次の 2 つの事故が起きる。
//
//   1. **「実装済み」と誤認される** —— メソッドが在り、契約テストが緑なので、
//      「却下解除は動いている」と読まれる。実際には一度も呼ばれない
//   2. **配線されても受け入れ基準が更新されない** —— #911 が呼び出しを足したとき、
//      #914 の受け入れ基準（「呼び出し側は #911」）が古くなったことに誰も気付かない
//
// **そこで、呼び出し元が生まれた瞬間に落ちるテストを置く。** 落ちたら実装者は
// 「未配線という前提が変わった」ことを強制的に認識し、受け入れ基準の書き直しを迫られる。
//
// これは #920 の「**意図した不検出を、意図として固定する**」と同型である ——
// 欠陥と読まれることも、誰かが「ついでに」塞いで偽陽性を招くことも防ぐ。
//
// ## 測り方
//
// **本番ソースを走査する。** リフレクションでは呼び出し元（IL の call 命令）を見られないため。
// 走査対象は GraphService.Api の本番コードのみで、テストは含めない
// （テストからは当然呼ぶ —— それが契約テストである）。
[Trait("Category", "Deployment")]
public sealed class AiSuggestionWiringTests
{
    // 判定メソッドの名前。**変えたらここも変える**（変え忘れると走査が空振りする＝下の
    // 「アンカーが見つかること」の確認で落ちる）。
    private const string MethodName = "TryReinstate";

    // GraphService.Api の本番ソースの位置を決めるアンカー。
    private const string Anchor =
        "src/knowledge/backend/Services/GraphService/src/GraphService.Api/Program.cs";

    [Fact]
    public void TryReinstate_has_no_production_call_site_yet()
    {
        var programPath = RepoFile.Find(Anchor,
            "#914 の未配線を測るには GraphService.Api の本番ソースの位置が要る。");
        var productionDir = Path.GetDirectoryName(programPath)!;

        var sources = Directory
            .EnumerateFiles(productionDir, "*.cs", SearchOption.AllDirectories)
            // ビルド生成物と EF のマイグレーションは本番の「呼び出し」ではない。
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .ToList();

        sources.Should().NotBeEmpty(
            "本番ソースが 1 件も取れないなら、本テストは何も検査せずに緑を返す（fail-open）");

        // 🔴 **装置の検出力の確認。** 定義そのものは必ず在る。無いなら走査が壊れている
        // （名前を変えた・場所が変わった）ので、下の「呼び出し 0 件」は自明に成り立ってしまう。
        var definitions = sources
            .Where(p => File.ReadAllText(p).Contains($"public bool {MethodName}(", StringComparison.Ordinal))
            .ToList();
        definitions.Should().ContainSingle(
            $"{MethodName} の定義が 1 つ見つからないなら走査が壊れている（名前か場所が変わった）");

        var definitionPath = definitions[0];

        var callSites = sources
            .Where(p => !string.Equals(p, definitionPath, StringComparison.OrdinalIgnoreCase))
            .Where(p => File.ReadAllText(p).Contains(MethodName, StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(productionDir, p))
            .ToList();

        callSites.Should().BeEmpty(
            $"""
            {MethodName}（却下解除の判定）に本番コードからの呼び出し元ができた。
            これは #911 が DocumentUpdated の購読を配線したことを意味する。

            🔴 次の手順を踏むこと:
              1. 本テスト（AiSuggestionWiringTests）を削除する
              2. #914 の受け入れ基準「本文指紋が変われば pending へ戻る（呼び出し側は #911）」
                 から、**発火の受け入れ基準**を起こして #911 へ写す
              3. 本文の変更をどう判定しているかを確認する —— UpdatedAt での代用は
                 ADR-0033 決定 10 の「本文が変更されたことのみ」より広く解除を起こす

            呼び出し元: {string.Join(", ", callSites)}
            """);
    }
}
