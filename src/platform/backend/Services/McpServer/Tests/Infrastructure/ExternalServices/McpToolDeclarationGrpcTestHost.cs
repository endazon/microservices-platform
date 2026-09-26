using System.Net;
using System.Text;
using AwesomeAssertions;
using Grpc.Core;
using McpServer.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, NFR-09, NFR-16, ADR-0029, IADR-0379 決定 3・4, IADR-0462（2026-09-26 追記 / #1515）:
// 申告元の代わりに立てる最小のホスト。REST（`GET /internal/mcp-tools`）と gRPC（`platform.mcp.v1.McpToolDeclarations`）の
// 両面を持ち、gRPC 面は申告元と同じく `ServiceCaller` を要求する（**本番と同じ共通部品** `AddPlatformAuth` /
// `UsePlatformMiddleware` で組む）。収集器の往復・宛先ごとの輸送選択・失敗の畳み方を測るための器である。
//
// 🔴 **待受はループバック（127.0.0.1）に限る。** HTTP/1.1 と HTTP/2 専用（h2c）の 2 本を `IPAddress.Loopback` の
// 動的ポートへ開き、起動直後に全待受アドレスがループバックであることを確かめる（外れたら停止して落とす）。
// 0.0.0.0 では待ち受けない。
//
// REST と gRPC で**違う申告**（`RestService` / `GrpcService`）を返させ、どちらの輸送で集めたかを結果から読めるようにする。
//
// ［2026-09-27 追記 / #1516, IADR-0462 経路 ④-b］ツールの実行の代役も持てる。`execution` を渡したときだけ
// `platform.mcp.v1.McpToolExecution` を張る（渡さなければ張らない —— 実行口の無い本番の申告元と同じく `UNIMPLEMENTED` になる）。
// `legacyEndpoint` を渡すと、**旧い申告元**の形（REST の JSON に `endpoint`、gRPC の番号 4）で申告する。
internal sealed class McpToolDeclarationGrpcTestHost : IAsyncDisposable
{
    public const string Issuer = "https://test-issuer/realms/platform";
    public const string RestService = "probe-over-rest";
    public const string GrpcService = "probe-over-grpc";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("mcp-tools-grpc-test-signing-key-0123456789abcdef"));

    private readonly WebApplication _app;

    private McpToolDeclarationGrpcTestHost(WebApplication app, string httpAddress, string grpcAddress)
    {
        _app = app;
        HttpAddress = httpAddress;
        GrpcAddress = grpcAddress;
    }

    public string HttpAddress { get; }

    public string GrpcAddress { get; }

    // 申告の 5 項目がすべて異なる値を持つ見本（写し忘れた項目があれば一致しない）。
    public static McpToolDeclaration SampleTool { get; } = new(
        "probe.tool", "説明", """{"type":"object"}""", "probe:read", "internal");

    // `grpcService` を渡せば gRPC 面の申告のサービス名を差し替える（空の service 名を返させるため）。
    public static async Task<McpToolDeclarationGrpcTestHost> StartAsync(
        string grpcService = GrpcService, CancellationToken ct = default,
        StubExecution? execution = null, string? legacyEndpoint = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = Issuer,
        });
        ListenOptions? http = null;
        ListenOptions? h2c = null;
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0, o => { o.Protocols = HttpProtocols.Http1; http = o; });
            kestrel.Listen(IPAddress.Loopback, 0, o => { o.Protocols = HttpProtocols.Http2; h2c = o; });
        });

        builder.Services.AddPlatformAuth(builder.Configuration);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration { Issuer = Issuer });
            o.TokenValidationParameters.IssuerSigningKey = SigningKey;
            o.TokenValidationParameters.ValidIssuer = Issuer;
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(new StubDeclarations(grpcService, legacyEndpoint));
        if (execution is not null)
            builder.Services.AddSingleton(execution);

        var app = builder.Build();
        app.UsePlatformMiddleware();
        if (legacyEndpoint is null)
            app.MapGet("/internal/mcp-tools", () => Results.Ok(new ServiceToolDeclarations(RestService, [SampleTool])));
        else
            app.MapGet("/internal/mcp-tools", () => Results.Text(LegacyRestJson(legacyEndpoint), "application/json"));
        app.MapGrpcService<StubMcpToolDeclarationsService>();
        if (execution is not null)
            app.MapGrpcService<StubMcpToolExecutionService>();
        await app.StartAsync(ct);

        var addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
        var open = addresses.Where(a => !IsLoopback(a)).ToList();
        if (open.Count > 0)
        {
            await app.StopAsync(ct);
            throw new InvalidOperationException($"試験の待受がループバック以外に開いた: {string.Join(", ", open)}");
        }

        addresses.Should().HaveCount(2, "HTTP/1.1 と h2c の 2 本だけを開く");
        return new McpToolDeclarationGrpcTestHost(app,
            $"http://127.0.0.1:{http!.IPEndPoint!.Port}",
            $"http://127.0.0.1:{h2c!.IPEndPoint!.Port}");
    }

    private static bool IsLoopback(string address)
    {
        var host = BindingAddress.Parse(address).Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    // テスト用 IdP の代わりに JWT を発行する。realm_access.roles は KeycloakRolesClaimsTransformation が展開する。
    public static string IssueToken(string subject, IEnumerable<string> realmRoles)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["preferred_username"] = subject,
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = realmRoles.ToArray() },
            },
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static string ServiceToken() =>
        IssueToken("service-account-mcp-server", [PlatformAuthPolicies.ServiceRole]);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    internal sealed record StubDeclarations(string Service, string? LegacyEndpoint);

    // 旧い申告元の REST の JSON（6 項目。`endpoint` を持つ）。
    public static string LegacyRestJson(string endpoint) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            service = RestService,
            tools = new[]
            {
                new Dictionary<string, string>
                {
                    ["name"] = SampleTool.Name,
                    ["description"] = SampleTool.Description,
                    ["input_schema"] = SampleTool.InputSchema,
                    ["endpoint"] = endpoint,
                    ["required_scope"] = SampleTool.RequiredScope,
                    ["egress_class"] = SampleTool.EgressClass,
                },
            },
        });

    // 旧い申告元の gRPC の 1 ツール（番号 4 = 旧 `endpoint` を持つ）。今の生成型は番号 4 を知らないので、
    // 生のバイト列を組んで今の型で読む —— 番号 4 は未知のフィールドとして保持され、送り返すとワイヤに載る。
    public static Pb.McpToolDeclaration LegacyGrpcTool(string endpoint)
    {
        using var buffer = new MemoryStream();
        var output = new Google.Protobuf.CodedOutputStream(buffer);
        void Field(int number, string value)
        {
            output.WriteTag(number, Google.Protobuf.WireFormat.WireType.LengthDelimited);
            output.WriteString(value);
        }
        Field(1, SampleTool.Name);
        Field(2, SampleTool.Description);
        Field(3, SampleTool.InputSchema);
        Field(4, endpoint);
        Field(5, SampleTool.RequiredScope);
        Field(6, SampleTool.EgressClass);
        output.Flush();
        return Pb.McpToolDeclaration.Parser.ParseFrom(buffer.ToArray());
    }

    // 実行面の代役の振る舞い。受けた要求を記録し、`Respond` の結果を返す。
    internal sealed class StubExecution
    {
        public System.Collections.Concurrent.ConcurrentQueue<Pb.ExecuteMcpToolRequest> Received { get; } = new();

        public Func<Pb.ExecuteMcpToolRequest, CancellationToken, Task<Pb.McpToolResult>> Respond { get; init; } =
            (_, _) => Task.FromResult(new Pb.McpToolResult());
    }

    // 実行面の代役（受け口と同じく ServiceCaller を要求する —— #1611 の受け口が満たすべき形）。
    [Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
    internal sealed class StubMcpToolExecutionService(StubExecution stub) : Pb.McpToolExecution.McpToolExecutionBase
    {
        public override Task<Pb.McpToolResult> Execute(Pb.ExecuteMcpToolRequest request, ServerCallContext context)
        {
            stub.Received.Enqueue(request);
            return stub.Respond(request, context.CancellationToken);
        }
    }

    // 申告元の gRPC 面の代役（申告元と同じ ServiceCaller を要求する）。
    [Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
    internal sealed class StubMcpToolDeclarationsService(StubDeclarations stub)
        : Pb.McpToolDeclarations.McpToolDeclarationsBase
    {
        public override Task<Pb.ServiceToolDeclarations> Declare(Pb.DeclareMcpToolsRequest request, ServerCallContext context)
        {
            var message = new Pb.ServiceToolDeclarations { Service = stub.Service };
            message.Tools.Add(stub.LegacyEndpoint is { } legacy
                ? LegacyGrpcTool(legacy)
                : new Pb.McpToolDeclaration
                {
                    Name = SampleTool.Name,
                    Description = SampleTool.Description,
                    InputSchema = SampleTool.InputSchema,
                    RequiredScope = SampleTool.RequiredScope,
                    EgressClass = SampleTool.EgressClass,
                });
            return Task.FromResult(message);
        }
    }
}

// 固定のトークンを返す s2s トークン発行側（IdP を持たない試験用）。
internal sealed class FixedTokenProvider(string token) : IServiceTokenProvider
{
    public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromResult(token);
}

// 取得に失敗する s2s トークン発行側（`ServiceToken:ClientId` の注入漏れ等の代役）。
internal sealed class ThrowingTokenProvider : IServiceTokenProvider
{
    public ValueTask<string> GetTokenAsync(CancellationToken ct) =>
        throw new InvalidOperationException("ServiceToken:ClientId が未設定である（試験）。");
}

// ログを検査するためのダブル（新規パッケージを増やさないため手書きする）。
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message);

    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> OfLevel(LogLevel level)
    {
        lock (_entries) return [.. _entries.Where(e => e.Level == level)];
    }

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add(new Entry(logLevel, formatter(state, exception)));
    }
}
