using System.Net;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace RetrievalService.Tests.Grpc;

// NFR-16, ADR-0029, [[IADR-0379]] 決定 3 (#1637): 生成クライアントを**本物のチャネル**で往復させるための最小の gRPC サーバー。
//
// 🔴 呼び出し元の取り消しの対照は、偽のクライアントへ素の `OperationCanceledException` を注入しても測れない ——
// 本物のチャネルは取り消しを `RpcException(Cancelled)` で投げる（`ThrowOperationCanceledOnCancellation` は既定の false）。
// そこで受け口の偽物（生成された `*Base` の派生）だけをこのサーバーに載せ、クライアント側は本番と同じ
// `GrpcChannel` ＋ 生成クライアントで呼ぶ。
//
// 🔴 **待受は 127.0.0.1 の動的ポート（h2c）に限る。** 起動直後に全待受アドレスがループバックであることを確かめ、
// 外れたら止めて落とす（各サービスの `GrpcTestConfiguration.LoopbackOnlyGuard` と同じ主張）。
// 構成を読まない空の器（`CreateEmptyBuilder`）で起こすので、`ASPNETCORE_URLS` 等の環境変数は待受に効かない。
internal sealed class LoopbackGrpcServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LoopbackGrpcServer(WebApplication app, int port)
    {
        _app = app;
        Channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
    }

    /// <summary>本番と同じ既定値（`ThrowOperationCanceledOnCancellation = false`）のチャネル。</summary>
    public GrpcChannel Channel { get; }

    public static Task<LoopbackGrpcServer> StartAsync<TService>(TService service, CancellationToken ct)
        where TService : class
        => StartAsync(service, _ => { }, ct);

    // #1746 監査 F1: 受け口の器へ追加の登録（例: `IHttpContextAccessor`）を足す口。
    public static Task<LoopbackGrpcServer> StartAsync<TService>(
        TService service, Action<IServiceCollection> extra, CancellationToken ct)
        where TService : class
        => StartCoreAsync(
            services => { services.AddGrpc(); services.AddSingleton(service); extra(services); },
            app => app.MapGrpcService<TService>(),
            ct);

    // NFR-16 (#1646): 生成された `*Base` を持たない第三者の gRPC（Qdrant の公式クライアントはサーバー側を同梱しない）の
    // 受け口の偽物を載せる口。全経路を 1 つの `RequestDelegate` で受ける —— 受け口は要求を受け取ったことを知らせて待つか、
    // trailers-only の gRPC 応答（`grpc-status` をヘッダに載せる）を返すだけでよい。クライアント側は本物のチャネルのまま。
    public static Task<LoopbackGrpcServer> StartRawAsync(RequestDelegate handler, CancellationToken ct)
        => StartCoreAsync(_ => { }, app => app.Map("/{**path}", handler), ct);

    private static async Task<LoopbackGrpcServer> StartCoreAsync(
        Action<IServiceCollection> register, Action<WebApplication> map, CancellationToken ct)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        ListenOptions? h2c = null;
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, o => { o.Protocols = HttpProtocols.Http2; h2c = o; }));
        builder.Services.AddRoutingCore();
        register(builder.Services);

        var app = builder.Build();
        map(app);
        await app.StartAsync(ct);

        var addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
        var open = addresses.Where(a => !IsLoopback(a)).ToList();
        if (open.Count > 0 || h2c?.IPEndPoint is null)
        {
            await app.StopAsync(ct);
            await app.DisposeAsync();
            throw new InvalidOperationException(
                $"試験の待受がループバック以外に開いた（または開かなかった）: {string.Join(", ", addresses)}");
        }

        // bind 後の実ポートは ListenOptions.IPEndPoint に書き戻される。
        return new LoopbackGrpcServer(app, h2c.IPEndPoint.Port);
    }

    private static bool IsLoopback(string address)
    {
        var host = BindingAddress.Parse(address).Host;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    public async ValueTask DisposeAsync()
    {
        Channel.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
