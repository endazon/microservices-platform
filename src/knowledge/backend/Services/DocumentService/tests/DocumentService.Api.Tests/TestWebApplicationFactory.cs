using DocumentService.Api.Foundation.Persistence;
using MassTransit;
using Wolverine;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Api.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    // NFR, #660: **各テストクラスで DB を分離するための一意名（InMemory）。**
    // xUnit はテストクラスごとに並列で走り、`IClassFixture` はクラスごとに別インスタンスを作る。
    // ところが DB 名が固定だと**ストアだけがプロセス内で共有され**、
    // 他クラスの書き込みが見えてしまう（`DocumentTest` で実際に発火した。#660）。
    private readonly string _dbName = $"DocumentTest_{Guid.NewGuid()}";

    // FR-21, ADR-0014/ADR-0015: 本文の格納先。**テストから格納内容を読める実装へ差し替える**
    // （縮退実装 `NullObjectStorageClient` は本文を保持しないため ⑦ が測れない）。
    public RecordingObjectStorageClient Storage { get; } = new();

    // FR-22: 通知の発火を記録するスタブ（HTTP 送出の HttpPrivateNoteNotifier を差し替える）。
    public RecordingPrivateNoteNotifier Notifier { get; } = new();

    // FR-20, ADR-0037 決定 9: 監査ログの記録スタブ（「誰が・いつ・何件」とタイトル不記載の検証用）。
    public RecordingAuditLogger Audit { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test"
            }));
        builder.ConfigureServices(services =>
        {
            // EF Core プロバイダーを InMemory へ差し替え
            // IDbContextOptionsConfiguration<T> をすべて削除し再登録
            ReplaceDbContext<DocumentDbContext>(services, _dbName);

            // FR-09, IADR-0044: 書き込みは admin/operator を要求する。Keycloak/JWT に依存せず
            // TestAuthHandler で認証し、既定で管理者ロールを付与する（既定スキームを Test に切替）。
            // 読み取りはロール不要のため、既定 admin でも非権限ロールでも到達できる。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // FR-21: オブジェクトストレージを記録用スタブへ差し替える。
            services.RemoveAll<IObjectStorageClient>();
            services.AddSingleton<IObjectStorageClient>(Storage);

            // FR-22: 通知の発火側を記録用スタブへ差し替える（HTTP 送出は結合テストの範囲外）。
            services.RemoveAll<DocumentService.Api.Foundation.Ports.IPrivateNoteNotifier>();
            services.AddSingleton<DocumentService.Api.Foundation.Ports.IPrivateNoteNotifier>(Notifier);

            // FR-20: 監査ログを記録用スタブへ差し替える。
            services.RemoveAll<Platform.Shared.Infrastructure.Foundation.Audit.IAuditLogger>();
            services.AddSingleton<Platform.Shared.Infrastructure.Foundation.Audit.IAuditLogger>(Audit);

            // MassTransit をテストハーネスへ差し替え
            services.RemoveAll<IBusControl>();
            services.AddMassTransitTestHarness();

            // ADR-0027 / E3a: DocumentDeleted の発行は Wolverine へ移った。
            // 実ブローカへ繋がずに「何を発行したか」だけを観測するため、IMessageBus を差し替える。
            // 🔴 **これが無いとテストが約 135 秒ハングする** —— Program.cs が UseWolverine +
            // UseRabbitMq を呼ぶため、テストホストの起動が実ブローカへの接続を試みる
            // （E1 の DataSourceService.Api.Tests と同じ作法）。
            services.DisableAllExternalWolverineTransports();
            services.RemoveAll<Wolverine.IMessageBus>();
            services.AddSingleton<RecordingMessageBus>();
            services.AddSingleton<Wolverine.IMessageBus>(sp => sp.GetRequiredService<RecordingMessageBus>());
        });
    }

    private static void ReplaceDbContext<TContext>(IServiceCollection services, string dbName)
        where TContext : DbContext
    {
        // 既存の DbContextOptions 関連ディスクリプタをすべて削除
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<TContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?.Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(TContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<TContext>(opt => opt.UseInMemoryDatabase(dbName));
    }
}
