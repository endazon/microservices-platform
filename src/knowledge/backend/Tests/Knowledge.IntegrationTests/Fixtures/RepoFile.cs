namespace Knowledge.IntegrationTests.Fixtures;

/// <summary>
/// テスト実行ディレクトリからリポジトリ根へ遡り、相対パスでファイルを解決する。
/// </summary>
/// <remarks>
/// #891: 同じループが統合テストに **6 箇所**（＋公開ラッパ 1）あり、返り値がパスと内容に
/// 分かれ、chart の前置を内包するものと呼び出し側に任せるものが混在していた。
/// 独立に進化し得るため 1 実装へ畳んだ。
///
/// 🔴 **未解決は例外で止める（fail-closed）。** 集約前も 6 箇所すべてが
/// <see cref="FileNotFoundException"/> を投げていたので、これは統一であって挙動の変更ではない
/// （issue #891 本文は「fail 時の挙動が揃っていない」と書いていたが、実測では揃っていた。
/// 揃っていなかったのは返り値の型・前置の有無・メッセージの詳しさである）。
///
/// 解決できなかったときに黙って既定値へ倒れてはならない —— 読めなかったファイルを
/// 「空」として扱うと、宣言を検査するはずのテストが**何も検査しないまま緑**になる。
/// </remarks>
internal static class RepoFile
{
    private const string ChartRoot = "deploy/helm/microservices-platform";

    /// <summary>リポジトリ根からの相対パスを絶対パスへ解決する。見つからなければ例外。</summary>
    internal static string Find(string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"{relative} をリポジトリルートから解決できませんでした。", relative);
    }

    /// <summary>リポジトリ根からの相対パスでファイル内容を読む。</summary>
    internal static string Read(string relative) => File.ReadAllText(Find(relative));

    /// <summary>Helm chart 配下（deploy/helm/microservices-platform/）のファイル内容を読む。</summary>
    /// <remarks>
    /// 前置をここへ畳んだのは、集約前に 2 箇所（HpaPdbScalingTests / MeshMtlsTests）が
    /// **同じ前置を独立に持っていた**ためである。
    /// </remarks>
    internal static string ReadChart(string relative) =>
        Read(Path.Combine(ChartRoot, relative));
}
