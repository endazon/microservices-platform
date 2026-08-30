using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Platform.Shared.Infrastructure.Tests.Testing;

namespace Platform.Shared.Infrastructure.Tests.Composable.Adapters.Storage;

// FR-06, FR-19, ADR-0014, ADR-0057 決定 1, IADR-0296 (#901):
// オブジェクトストレージ未構成時の**縮退実装の契約**を固定する。
//
// 🔴 **本ファイルが塞ぐ穴。** 着手前の実測で NullObjectStorageClient は line 3/30（10%）だった。
// 共有側の既存テスト（PortSwapCompositionTests / ObjectStorageExtensionsTests）は
// `BeOfType<NullObjectStorageClient>()` の**型検査だけ**で、メソッドを 1 つも呼んでいない。
// リポジトリ全体をメソッド名で走査すると、実行しているのは
// ConversionService.Tests/ObjectStorageTests.cs の PutTextAsync / CanResolve / GetTextAsync のみで、
// **DeleteAsync / PutBytesAsync / GetBytesAsync / CreatePresignedGetUrl は全ユニットで実行 0 件**だった。
//
// とりわけ DeleteAsync は IADR-0296 / ADR-0057 決定 1 が 🔴 で
// 「**例外にしてはならない**」と明記した非自明な決定でありながら、試験が 1 件も無い。
// Get* が例外で Delete が成功という**向きの違い**は、根拠を知らない者には
// 一貫性の欠如に見える —— 将来の「整理」で最も戻されやすい形である。ここで対にして固定する。
public class NullObjectStorageClientTests
{
    private const string Bucket = "knowledge-normalized";

    private static (NullObjectStorageClient Sut, RecordingLogger<NullObjectStorageClient> Log) Build()
    {
        var log = new RecordingLogger<NullObjectStorageClient>();
        return (new NullObjectStorageClient(new ObjectStorageOptions { Bucket = Bucket }, log), log);
    }

    // ── 書き込み: 決定的 URI を返して警告する（永続化はしない） ──────────────

    [Fact]
    public async Task バイト列の保存も決定的URIを返す()
    {
        var (sut, log) = Build();

        var uri = await sut.PutBytesAsync("doc/assets/fig-1.png", [1, 2, 3], "image/png",
            TestContext.Current.CancellationToken);

        // PutTextAsync と同じ規則で組み立てる。ここが崩れると、同じ資産が
        // テキスト経路とバイト経路で違う URI を持つ。
        uri.Should().Be($"storage://{Bucket}/doc/assets/fig-1.png");
        log.OfLevel(LogLevel.Warning).Should().ContainSingle(
            "永続化していないことは運用に見えなければならない");
    }

    // ── 🔴 削除: 例外にしない（IADR-0296 / ADR-0057 決定 1） ──────────────────

    [Fact]
    public async Task 削除は例外を投げずに完走する()
    {
        var (sut, log) = Build();

        // 🔴 ここで NotSupportedException を投げると、個人資料の完全削除（FR-19）と
        //    文書削除（FR-06）が未構成環境で 500 になる。消えていないのではなく
        //    **最初から書かれていない**のだから、削除としては成功が正しい。
        var act = async () => await sut.DeleteAsync(
            $"storage://{Bucket}/doc/document.md", TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        log.OfLevel(LogLevel.Warning).Should().ContainSingle();
    }

    // ── 読み取り: 解決できない（プレースホルダーへ縮退させる） ────────────────

    // 🔴 Delete と**向きが逆**であることの対照条件。両方を 1 ファイルに置き、
    // 「一貫性が無いから揃えよう」という将来の整理を試験で止める。
    [Fact]
    public async Task テキスト取得もバイト取得も未対応として例外にする()
    {
        var (sut, _) = Build();
        var uri = $"storage://{Bucket}/doc/document.md";

        await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.GetTextAsync(uri, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.GetBytesAsync(uri, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void 署名付きURLは発行できない()
    {
        var (sut, _) = Build();

        // 「発行できたことにして壊れた URL を返す」と、閲覧側が原因不明の 404 を踏む。
        var act = () => sut.CreatePresignedGetUrl($"storage://{Bucket}/doc/document.md");

        act.Should().Throw<NotSupportedException>();
    }

    // 🔴 読み取り側のプレースホルダー縮退は、この 1 つの述語だけを条件にしている。
    // true を返すようになると、縮退が止まって解決不能な URI をそのまま辿る。
    [Theory]
    [InlineData("storage://knowledge-normalized/doc/document.md")]
    [InlineData("https://example.invalid/x")]
    [InlineData("")]
    [InlineData(null)]
    public void 常に解決不可を返す(string? uri)
    {
        var (sut, _) = Build();

        sut.CanResolve(uri).Should().BeFalse();
    }
}
