using AwesomeAssertions;
using GraphService.Infrastructure.ExternalServices;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-18, NFR-09, NFR-16, SC-09, ADR-0029, ADR-0043, ADR-0063 決定 2, ADR-0075,
// [[IADR-0364]] 決定 2, [[IADR-0379]] 決定 4・5, [[IADR-0402]] 決定 6, [[IADR-0412]] (#1255):
// タグ辞書読み取りの gRPC 実装が、**REST 実装（`HttpTagDictionaryReader`）と同じ意味論**であり、
// 変わるのは輸送だけであることを固定する。
//
// 🔴 **この経路の不変条件は「`null`（引けなかった）と空集合（辞書が空）を混ぜない」**である
// （`ITagDictionaryReader` の注記 / 決定 3）。混ぜると
// 「辞書が空だからタグを付けない」と「引けなかったからタグを付けない」が区別できなくなり、
// 下流の fail-closed（`TagDictionaryEnforcementTests`）が**静かに意味を失う**。
// したがって**両方向を対で**置く —— 片方だけでは片方の変異しか殺せない。
[Trait("TestKind", "Unit")]
public class GrpcTagDictionaryReaderTests
{
    // 🔴 T-01 陽性対照。正常応答は集合になる。
    // これが無いと、以下の陰性はすべて「常に null を返す」実装でも緑になる。
    [Fact]
    public async Task 正常応答を集合へ写す()
    {
        var fake = new FakeClient(Response("経理", "人事"));

        var names = await Reader(fake).ReadNamesAsync(TestContext.Current.CancellationToken);

        names.Should().BeEquivalentTo(["経理", "人事"]);
    }

    // 🔴 T-02: **正常応答の空リストは空集合であって `null` ではない**（決定 3 の対の片方）。
    // ここを `null` へ倒すと「辞書が空」が「引けなかった」に化ける。
    [Fact]
    public async Task 空の応答は空集合であって引けなかったではない()
    {
        var fake = new FakeClient(Response());

        var names = await Reader(fake).ReadNamesAsync(TestContext.Current.CancellationToken);

        names.Should().NotBeNull("空リストは正常応答である");
        names.Should().BeEmpty();
    }

    // 🔴 T-03: **輸送の失敗はすべて `null`**（決定 3 の対のもう片方）。**空集合へ縮退しない。**
    // REST 版の「到達できない・非 2xx」と同じ値である。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task 輸送の失敗は引けなかったへ倒す(StatusCode status)
    {
        var fake = new FakeClient(new RpcException(new Status(status, "失敗")));

        var names = await Reader(fake).ReadNamesAsync(TestContext.Current.CancellationToken);

        names.Should().BeNull("空集合へ縮退すると「辞書が空」と区別できなくなる");
    }

    // 🔴 T-04: s2s トークンの取得失敗も**同じ縮退**である（`RpcException` にはならない）。
    [Fact]
    public async Task s2sトークン取得失敗も引けなかったである()
    {
        var fake = new FakeClient(new InvalidOperationException("ServiceToken:ClientId が未設定です。"));

        var names = await Reader(fake).ReadNamesAsync(TestContext.Current.CancellationToken);

        names.Should().BeNull();
    }

    // 🔴 T-05: **呼び出し元のキャンセルだけは伝播する**（REST 版と同じ姿勢）。
    [Fact]
    public async Task 呼び出し元のキャンセルは伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var fake = new FakeClient(new OperationCanceledException(cts.Token));

        var act = async () => await Reader(fake).ReadNamesAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // 🔴 T-06: **利用者の資格情報を面へ載せない**（[[IADR-0379]] 決定 4）。読む主体は本サービス自身であり、
    // メタデータに載るのは**チャネルに付いた s2s だけ**である。呼び出しごとのヘッダは 1 本も足さない。
    [Fact]
    public async Task 呼び出しごとのヘッダを足さない()
    {
        var fake = new FakeClient(Response("経理"));

        await Reader(fake).ReadNamesAsync(TestContext.Current.CancellationToken);

        (fake.LastOptions.Headers ?? []).Should().BeEmpty();
    }

    // 空白だけの名前を落とすのは REST 版と同じ（`HttpTagDictionaryReader`）。
    [Fact]
    public async Task 空白だけの名前を落とす()
    {
        var fake = new FakeClient(Response("経理", "  ", ""));

        (await Reader(fake).ReadNamesAsync(TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(["経理"]);
    }

    // 🔴 T-09 / T-10: **切替は構成の有無だけである**（登録関数を陽性・陰性の対で固定する。
    // DI の分岐は試験ホストからは観測できないので拡張メソッドを直接叩く）。
    [Fact]
    public void 宛先が未設定なら生成クライアントを登録しない()
    {
        var services = new ServiceCollection()
            .AddTagDictionaryGrpcClient(new ConfigurationBuilder().Build());

        services.Should().NotContain(
            d => d.ServiceType == typeof(Pb.TagDictionary.TagDictionaryClient),
            "未設定なら何も登録しない（REST のまま）");
    }

    [Fact]
    public void 宛先が構成されていれば生成クライアントを登録する()
    {
        var services = new ServiceCollection().AddTagDictionaryGrpcClient(Configured());

        services.Should().Contain(
            d => d.ServiceType == typeof(Pb.TagDictionary.TagDictionaryClient),
            "★ 陽性対照 —— 登録されないのは関数が壊れているからではない");
    }

    // 🔴 T-14: **チャネルは宛先ごとに 1 本**（[[IADR-0402]] 決定 6）。読み取りと書き込みは同じ
    // DocumentService を宛先に持つので、**どちらを先に登録してもチャネルは 1 本**でなければならない。
    // 🔴 **登録順を入れ替えた 2 通りを両方置く** —— 片方だけだと、片側が `TryAdd` を失っても緑のままになる。
    [Fact]
    public void 同じ宛先へチャネルを2本張らない()
    {
        var config = Configured();

        var readerFirst = new ServiceCollection()
            .AddTagDictionaryGrpcClient(config)
            .AddDocumentTagWriteGrpcClient(config);
        var writerFirst = new ServiceCollection()
            .AddDocumentTagWriteGrpcClient(config)
            .AddTagDictionaryGrpcClient(config);

        ChannelRegistrations(readerFirst).Should().Be(1, "読み取りが先でも 1 本である");
        ChannelRegistrations(writerFirst).Should().Be(1, "書き込みが先でも 1 本である");
    }

    // 🔴 読み取りと書き込みは**同じ鍵**で切り替わる —— 1 つの宛先を 2 つの鍵で切り替えると、
    // 片方だけ gRPC へ倒れた状態が作れてしまう。
    [Fact]
    public void 読み書きは同じ構成キーで切り替わる()
    {
        TagDictionaryGrpcClientExtensions.AddressKey
            .Should().Be(DocumentTagWriteGrpcClientExtensions.AddressKey);
        TagDictionaryGrpcClientExtensions.ChannelKey
            .Should().Be(DocumentTagWriteGrpcClientExtensions.ChannelKey);
    }

    // ── 器 ────────────────────────────────────────────────────────

    private static IConfiguration Configured() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TagDictionaryGrpcClientExtensions.AddressKey] = "http://document-service:8081",
            ["ServiceToken:TokenEndpoint"] = "http://keycloak/token",
            ["ServiceToken:ClientId"] = "graph-service",
            ["ServiceToken:ClientSecret"] = "secret",
        }).Build();

    private static int ChannelRegistrations(IServiceCollection services) =>
        services.Count(d => d.ServiceType == typeof(GrpcChannel)
                         && Equals(d.ServiceKey, TagDictionaryGrpcClientExtensions.ChannelKey));

    private static GrpcTagDictionaryReader Reader(Pb.TagDictionary.TagDictionaryClient client) =>
        new(client, NullLogger<GrpcTagDictionaryReader>.Instance);

    private static Pb.ListNamesResponse Response(params string[] names)
    {
        var response = new Pb.ListNamesResponse();
        response.Names.AddRange(names);
        return response;
    }

    private sealed class FakeClient : Pb.TagDictionary.TagDictionaryClient
    {
        private readonly Task<Pb.ListNamesResponse> _response;

        public FakeClient(Pb.ListNamesResponse response) => _response = Task.FromResult(response);

        public FakeClient(Exception exception) =>
            _response = Task.FromException<Pb.ListNamesResponse>(exception);

        public CallOptions LastOptions { get; private set; }

        public override AsyncUnaryCall<Pb.ListNamesResponse> ListNamesAsync(
            Pb.ListNamesRequest request, CallOptions options)
        {
            LastOptions = options;
            return new AsyncUnaryCall<Pb.ListNamesResponse>(
                _response, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }
}
