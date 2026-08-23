using System.Security.Claims;
using AwesomeAssertions;
using McpServer.Api.Foundation.Contracts;
using McpServer.Api.Foundation.Domain;
using McpServer.Api.Foundation.Persistence;
using McpServer.Api.Foundation.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServer.Api.Tests;

// NFR, FR-16 (#1009): ログへ出す要求由来の文字列から改行・制御文字を除去する。
//
// 🔴 **ツール名は JSON-RPC の本文（`params.name`）由来である。** ヘッダではないので Kestrel の
// 制御文字検査を通らず、改行を含んだまま届く。本番の `Program.cs` は `ClearProviders` を呼んで
// おらず既定の Console プロバイダが有効なので、**行指向のログへ未加工で落とすと偽の監査行を
// 注入できる**（ログ偽造。CWE-117）。
//
// **公開の振る舞い（InvokeAsync が実際に書いたログ）で検査する** —— LlmGateway が
// 同じ理由の sanitize を私有のままにして経路越しに固定しているのと同じ作法である。
public class LogForgingSanitizationTests
{
    // 実際に書かれたログ行を捕まえる。整形済みの本文を見るのが要点で、
    // 構造化の引数だけを見ると「出力の時点でどう見えるか」を検査できない。
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private static async Task<List<string>> InvokeUnknownToolAsync(string requestedToolName)
    {
        var db = new McpDbContext(new DbContextOptionsBuilder<McpDbContext>()
            .UseInMemoryDatabase($"McpLogForging_{Guid.NewGuid()}").Options);
        var client = McpClient.Register(
            "claude-desktop", "有人エージェント", McpClientKind.Interactive,
            null, EgressTier.SelfHosted, DateTimeOffset.UtcNow);
        db.Clients.Add(client);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // カタログは空のまま。どんな名前も「不明なツール」経路へ落ちる。
        var logger = new CapturingLogger<ToolInvocationService>();
        var service = new ToolInvocationService(
            new McpSubjectResolver(db),
            new ToolCatalog(NullLogger<ToolCatalog>.Instance),
            new ThrowingInvoker(),
            new ServiceAccountDocumentFilter(NullLogger<ServiceAccountDocumentFilter>.Instance),
            new EgressPolicy(),
            logger);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("azp", "claude-desktop")], "test"));

        var outcome = await service.InvokeAsync(
            principal, requestedToolName, "{}", TestContext.Current.CancellationToken);

        outcome.Ok.Should().BeFalse();
        return logger.Messages;
    }

    // 未知ツールの経路しか通らないので、呼ばれたら設計違反である。
    private sealed class ThrowingInvoker : IToolInvoker
    {
        public Task<McpToolResult> InvokeAsync(
            PublishedTool tool, ToolInvocationScope scope, string argumentsJson, CancellationToken ct)
            => throw new InvalidOperationException("未知ツールでは下流を呼ばない");
    }

    // 陰性: 改行を注入しても、ログは 1 行に収まる（偽の監査行が増えない）。
    [Theory]
    [InlineData("search\nRejected unknown MCP tool admin for client trusted")]
    [InlineData("search\r\n2026-08-23 INFO 偽の監査行")]
    [InlineData("search\rINFO forged")]
    public async Task Unknown_tool_name_cannot_inject_a_forged_log_line(string forged)
    {
        var messages = await InvokeUnknownToolAsync(forged);

        messages.Should().ContainSingle();
        messages[0].Should().NotContain("\n").And.NotContain("\r");
    }

    // 陰性: 要求由来の名前でログを溢れさせない。
    [Fact]
    public async Task Overlong_tool_name_is_truncated_in_the_log()
    {
        var messages = await InvokeUnknownToolAsync(new string('a', 5_000));

        messages.Should().ContainSingle();
        messages[0].Length.Should().BeLessThan(400);
        messages[0].Should().Contain("…");
    }

    // 🔴 陽性対照: **正常な名前はログにそのまま出る。**
    // これが無いと「名前をログに書かない」実装でも上の 4 件が緑になる。
    [Fact]
    public async Task Ordinary_tool_name_still_appears_in_the_log()
    {
        var messages = await InvokeUnknownToolAsync("search_documents");

        messages.Should().ContainSingle();
        messages[0].Should().Contain("search_documents");
    }
}
