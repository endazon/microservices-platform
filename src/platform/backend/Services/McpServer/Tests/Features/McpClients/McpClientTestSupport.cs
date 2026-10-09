using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Features.McpClients;
using McpServer.Infrastructure.ExternalServices;
using McpServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServer.Tests.Features.McpClients;

// FR-16, UC-09, SC-12, 計画 ADR-0134 決定 2・フォローアップ 4・5, [[IADR-0516]]（2026-10-09 追記 / #1845）:
// 無人の MCP クライアントの client secret を登録・再発行の応答で一度だけ返し、SC-12 の管理操作を監査記録に残す。
//
// IdP はプロセス内の口（`InMemoryServiceAccountProvisioner`）。ホストの**全ロガー**を `CapturingLoggerProvider` で捕まえ、
// 監査（本物の `AuditLogger` が書く `Audit=true` の構造化ログ）と、アプリケーションのログに値が出ないことを同じ記録から確かめる。


/// <summary>ホストの全ログ（整形済みの本文・構造化の値・例外）を捕まえる器つきのホスト。</summary>
public sealed class AuditCapturingFactory : TestWebApplicationFactory
{
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public sealed record Entry(
        string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, string?> Values, string? Exception);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => [.. _entries];

    /// <summary>`AuditLogger` が書いた監査の行（`Audit=true`）。</summary>
    public IReadOnlyList<Entry> Audits => [.. _entries.Where(e => e.Values.TryGetValue("Audit", out var a) && a == "True")];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<Entry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value?.ToString())
                : [];
            entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), values, exception?.ToString()));
        }
    }
}

internal static class McpClientTestRequests
{
    public const string AdminName = "test-user";

    public static HttpClient Registrar(this TestWebApplicationFactory factory, string? roles = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(StubRegistrarAttributeResolver.ClearanceHeader, "public,internal");
        if (roles is not null) client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    public static RegisterMcpClientRequest ServiceAccount(string clientId)
        => new(clientId, clientId, "service-account", new Dictionary<string, string> { ["clearance"] = "public" });

    public static RegisterMcpClientRequest Interactive(string clientId)
        => new(clientId, clientId, "interactive", RedirectUris: ["https://agent.example.test/cb"]);

    public static InMemoryServiceAccountProvisioner Idp(this TestWebApplicationFactory factory)
        => (InMemoryServiceAccountProvisioner)factory.Services.GetRequiredService<IServiceAccountProvisioner>();

    public static async Task AddRegistryRow(this TestWebApplicationFactory factory, string clientId, McpClientKind kind, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();
        db.Clients.Add(McpClient.Register(clientId, "旧い行", kind,
            new Dictionary<string, string>(), EgressTier.StandardExternal, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
    }
}
