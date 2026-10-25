using AwesomeAssertions;
using JasperFx;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// FR-06, ADR-0027 §決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」,
// IADR-0604 (#2042): 共通ヘルパ `UsePlatformTransactionalOutbox` が Outbox の構成を入れることを固定する。
//
// 🔴 **各試験は「適用前には無い」を先に確かめる**（同じディレクトリの `WolverineExtensionsTests` の作法）。
// 適用後だけを見ると、既定で入っているものに対してヘルパが何もしなくても緑になる。
//
// ⚠️ 耐久送出（`UseDurableOutboxOnAllSendingEndpoints`）は送出先の組み立て時（起動時）に効くため、ここでは観測できない。
// 効いていることは Knowledge.IntegrationTests の `DocumentDeletedOutboxTests`（実 PostgreSQL ＋ 実 RabbitMQ）が、
// 「確定の後に送出が失われても Outbox の表に残り再送される」で測る（外すと表に 1 行も書かれず赤になる）。
public class WolverineTransactionalOutboxTests
{
    private const string ConnectionString = "Host=localhost;Database=outbox_test";

    private static bool Has(WolverineOptions options, Type serviceType) =>
        options.Services.Any(d => d.ServiceType == serviceType);

    [Fact]
    public void 適用前はOutboxの発行口もメッセージの保存先も無い()
    {
        var options = new WolverineOptions();

        Has(options, typeof(IDbContextOutbox<>)).Should().BeFalse();
        Has(options, typeof(IMessageStore)).Should().BeFalse();
    }

    [Fact]
    public void 共通ヘルパはEF連携の発行口を登録する()
    {
        var options = new WolverineOptions();

        options.UsePlatformTransactionalOutbox(ConnectionString);

        // DbContext と同じスコープで解決され、DB の変更と発行を 1 トランザクションで確定する口。
        Has(options, typeof(IDbContextOutbox<>)).Should().BeTrue();
        Has(options, typeof(IDbContextOutbox)).Should().BeTrue();
    }

    [Fact]
    public void 共通ヘルパはメッセージの保存先を渡した接続文字列と専用スキーマのPostgreSQLにする()
    {
        var options = new WolverineOptions();

        options.UsePlatformTransactionalOutbox(ConnectionString);

        Has(options, typeof(IMessageStore)).Should().BeTrue();
        var settings = options.Services
            .Where(d => d.ServiceType == typeof(DatabaseSettings))
            .Select(d => d.ImplementationInstance)
            .OfType<DatabaseSettings>()
            .Single();
        // 🔴 接続文字列は DbContext と同じものでなければならない（envelope の INSERT は DbContext の接続で走る）。
        // ヘルパが接続文字列を書き換えないことを固定する。
        settings.ConnectionString.Should().Be(ConnectionString);
        settings.SchemaName.Should().Be(WolverineExtensions.OutboxSchemaName);
        WolverineExtensions.OutboxSchemaName.Should().Be("wolverine");
    }

    [Fact]
    public void 共通ヘルパは表を起動時に作るか追随させる()
    {
        // 既定は JasperFx のプロファイルに依存する（未設定なら CreateOrUpdate だが、プロファイルが None を与えると表が作られない）。
        // 既定と同値のままだとヘルパが何もしなくても緑になるので、None から始める。
        var options = new WolverineOptions { AutoBuildMessageStorageOnStartup = AutoCreate.None };

        options.UsePlatformTransactionalOutbox(ConnectionString);

        options.AutoBuildMessageStorageOnStartup.Should().Be(AutoCreate.CreateOrUpdate);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void 共通ヘルパは空の接続文字列を受けない(string connectionString)
    {
        var act = () => new WolverineOptions().UsePlatformTransactionalOutbox(connectionString);

        act.Should().Throw<ArgumentException>();
    }
}
