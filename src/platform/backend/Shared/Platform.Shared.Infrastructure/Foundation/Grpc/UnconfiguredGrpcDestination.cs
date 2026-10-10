using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Platform.Shared.Infrastructure.Foundation.Grpc;

// NFR-09, NFR-16, ADR-0029, ADR-0075, 計画 ADR-0089 決定 1, [[IADR-0533]] 決定 2 (#1255, #1517):
// **宛先が構成されていない east-west gRPC 経路を「宛先へ届かない」と同じ枝へ倒す。**
//
// REST の並走を撤去したので、経路の輸送は gRPC だけである。宛先（`Services:<Name>Grpc`）が構成されていない
// 配備で取り得る形は 3 つあった（[[IADR-0533]] 決定 2）:
//   1. 起動を止める —— 単体試験・`dotnet run` の各ホストが宛先を与えないと起動できなくなる
//      （`WebApplicationFactory.ConfigureAppConfiguration` は組み立て時の読み取りに間に合わない。[[IADR-0379]] §結果）。
//   2. コードに既定アドレスを持つ —— 配備の Service 名が compose と helm で違う宛先がある（`llm-gateway` / `llmgateway-service`）。
//      既定が外れると、名前解決は通るがポートが無い形で沈黙する（#342 の REST と同型）。
//   3. **宛先へ届かないのと同じ `UNAVAILABLE` を返す（採用）** —— gRPC だけの既存の経路（文書 → 認可・データソース → 部門照会・
//      MCP のツール実行）が既に採っている「構成が無ければ縮退」と同じ向きであり、各実装の縮退の枝（deny-by-default・
//      申告なし・提案 0 件など）を 1 行も変えずに使える。理由はステータスの詳細に構成キーの名前で載せる。
//
// 🔴 **TryAdd で登録する。** 宛先が構成されていれば各 `Add…GrpcClient` が本物の生成クライアントを先に登録しており、
// ここは何もしない。呼ぶ順序は「`Add…GrpcClient` の後」である。
public sealed class UnconfiguredGrpcDestination(string addressKey) : CallInvoker
{
    public string AddressKey { get; } = addressKey;

    private RpcException Unavailable() => new(new Status(
        StatusCode.Unavailable,
        $"east-west gRPC の宛先が構成されていません（{AddressKey}）。REST の並走は撤去済みであり、代わりの輸送はありません。"));

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw Unavailable();

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        var failed = Task.FromException<TResponse>(Unavailable());
        return new AsyncUnaryCall<TResponse>(
            failed, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }

    // 本物のチャネルと同じく、失敗は応答の読み出し（MoveNext）で現れる（呼び出しの生成では投げない）。
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        new(new FailingReader<TResponse>(Unavailable()),
            Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

    private sealed class FailingReader<T>(RpcException error) : IAsyncStreamReader<T>
    {
        public T Current => throw new InvalidOperationException("応答はありません。");

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(error);
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw Unavailable();

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw Unavailable();
}

public static class UnconfiguredGrpcDestinationExtensions
{
    /// <summary>
    /// 生成クライアント <typeparamref name="TClient"/> が未登録（＝宛先 <paramref name="addressKey"/> が構成されていない）なら、
    /// 常に <c>UNAVAILABLE</c> を返す呼び出し器の上に組んで登録する。登録済みなら何もしない。
    /// </summary>
    public static IServiceCollection TryAddUnconfiguredGrpcClient<TClient>(
        this IServiceCollection services, string addressKey, Func<CallInvoker, TClient> create)
        where TClient : class
    {
        services.TryAddSingleton(_ => create(new UnconfiguredGrpcDestination(addressKey)));
        return services;
    }
}
