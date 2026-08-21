using AwesomeAssertions;

namespace Knowledge.IntegrationTests.Fixtures;

// #891: 集約先 RepoFile のふるまいを**コミットされた回帰試験**として固定する。
//
// 🔴 **集約 PR の変異試験（A1〜A4・B）は一度きりのローカル実測であり、回帰試験ではなかった。**
// #898 の AI レビューの 🟡 指摘。**指摘は正しい。** 6 つの呼び出し元テストが「たまたま」
// 検知する形に頼っていると、fail-closed の挙動や chart 前置が壊れたときに
// **何が壊れたのか**が分からない（呼び出し元は YAML の内容を検査しており、
// 解決の失敗はその手前で起きる）。
//
// 本リポジトリで繰り返し記録してきた形そのものである ——
// **「測った」と「測り続ける」は違う。** 一度きりの実測は次の変更を守らない。
public class RepoFileTests
{
    [Fact]
    public void Find_解決できないパスはFileNotFoundExceptionで止まる()
    {
        // 🔴 fail-closed である理由: 解決できなかったファイルを黙って空として扱うと、
        // 宣言を検査するはずのテストが**何も検査しないまま緑**になる。
        var act = () => RepoFile.Find("no/such/file-891-regression.yaml");

        act.Should().Throw<FileNotFoundException>()
            .Which.FileName.Should().Be("no/such/file-891-regression.yaml");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Find_空や空白のパスはArgumentExceptionで止まる(string relative)
    {
        var act = () => RepoFile.Find(relative);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Find_実在するファイルは絶対パスを返す()
    {
        // 正本の pipeline.json は必ず在る（本番が読む宣言そのもの）。
        var path = RepoFile.Find(
            Path.Combine("deploy", "helm", "microservices-platform", "files", "pipeline.json"));

        Path.IsPathRooted(path).Should().BeTrue();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Read_実在するファイルの内容を返す()
    {
        var json = RepoFile.Read(
            Path.Combine("deploy", "helm", "microservices-platform", "files", "pipeline.json"));

        json.Should().Contain("\"steps\"");
    }

    [Fact]
    public void Read_解決できないパスはFileNotFoundExceptionで止まる()
    {
        var act = () => RepoFile.Read("no/such/file-891-regression.yaml");

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ReadChart_chart配下を前置して読む()
    {
        // 🔴 **前置が効いていることを、前置なしでは解決できないパスで示す。**
        // "files/pipeline.json" は chart 配下にしか無く、リポジトリ根には無い。
        // 前置が落ちれば下の Read が FileNotFoundException を投げるので、
        // この 2 つを並べると「前置が効いた」ことの証明になる。
        var viaChart = RepoFile.ReadChart(Path.Combine("files", "pipeline.json"));
        viaChart.Should().Contain("\"steps\"");

        var withoutPrefix = () => RepoFile.Read(Path.Combine("files", "pipeline.json"));
        withoutPrefix.Should().Throw<FileNotFoundException>(
            "前置なしでは解決できないパスを選んでいる。ここが解決できてしまうと"
            + " ReadChart の試験は前置の有無を区別できていない");
    }

    [Fact]
    public void ReadChart_解決できないときは前置つきのパスを例外へ載せる()
    {
        // 直せるメッセージであること。前置後のパスが出ないと、どこを探したのか分からない。
        var act = () => RepoFile.ReadChart("no-such-chart-file-891.yaml");

        act.Should().Throw<FileNotFoundException>()
            .Which.FileName.Should().Be(
                Path.Combine("deploy/helm/microservices-platform", "no-such-chart-file-891.yaml"));
    }
}
