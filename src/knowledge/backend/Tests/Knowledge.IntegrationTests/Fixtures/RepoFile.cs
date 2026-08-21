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
/// <see cref="FileNotFoundException"/> を投げていたので、**未解決時については**統一であって
/// 挙動の変更ではない（issue #891 本文は「fail 時の挙動が揃っていない」と書いていたが、
/// 実測では揃っていた。揃っていなかったのは返り値の型・前置の有無・メッセージの詳しさである）。
///
/// ⚠️ **1 点だけ挙動が増えている** —— 空・空白の <c>relative</c> に対する
/// <see cref="ArgumentException"/> は集約前の 6 実装のいずれにも無かった（従来は根まで辿って
/// <see cref="FileNotFoundException"/> になっていた）。現行の呼び出し元に空を渡すものは無い。
/// **「挙動の変更は無い」と丸めて書かない**（#898 の監査の指摘）。
///
/// 解決できなかったときに黙って既定値へ倒れてはならない —— 読めなかったファイルを
/// 「空」として扱うと、宣言を検査するはずのテストが**何も検査しないまま緑**になる。
/// </remarks>
internal static class RepoFile
{
    private const string ChartRoot = "deploy/helm/microservices-platform";

    /// <summary>リポジトリ根からの相対パスを絶対パスへ解決する。見つからなければ例外。</summary>
    /// <param name="because">
    /// 呼び出し側固有の「なぜここで止める必要があるか」。例外メッセージへ追記する。
    /// 🔴 **赤い CI ログを読む人が見るのは例外メッセージだけである。** 集約前、
    /// <c>Pipeline:ConfigPath</c> の解決失敗は長い理由を**例外メッセージに載せて**いた。
    /// 集約でそれをソースコメントへ移すと、**ログからは短い文しか読めなくなる**
    /// （#898 の監査の指摘。理由の置き場をソースへ移すのは診断としては後退である）。
    /// 共通化と両立させるため、理由は引数で受け取る。
    /// </param>
    internal static string Find(string relative, string? because = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        var message = $"{relative} をリポジトリルートから解決できませんでした。";
        if (!string.IsNullOrWhiteSpace(because)) message += $" {because}";
        throw new FileNotFoundException(message, relative);
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
