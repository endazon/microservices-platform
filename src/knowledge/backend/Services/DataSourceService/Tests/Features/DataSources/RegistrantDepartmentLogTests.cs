using System.Security.Claims;
using AwesomeAssertions;
using DataSourceService.Features.DataSources.Create;
using Microsoft.Extensions.Logging;

namespace DataSourceService.Tests.Features.DataSources;

// FR-05, UC-04, SC-06, IADR-0468 決定 6 (#754 監査): 部門を導けなかった理由を**区別して** 1 行だけ残す。
//
// 保存される値はどちらも予約値 `unassigned` だが、原因と直し方が違う:
//   - `group_paths` クレーム自体が無い → Warning（realm のマッパー未適用、または所属 0）
//   - クレームはあるが部門グループがちょうど 1 つではない → Information（設計どおりの「導かない」）
[Trait("TestKind", "Unit")]
public class RegistrantDepartmentLogTests
{
    private static ClaimsPrincipal UserWith(params string[] groupPaths) =>
        new(new ClaimsIdentity(
            groupPaths.Select(p => new Claim(CreateDataSourceEndpoint.GroupPathsClaim, p)), "test"));

    private static readonly Dictionary<string, string> NoDepartment = new() { ["confidentiality"] = "internal" };

    [Fact]
    public void MissingClaim_LogsOneWarningNamingTheRealmMapper_AndDerivesNothing()
    {
        var log = new RecordingLogger();

        var code = CreateDataSourceEndpoint.ResolveRegistrantDepartment(UserWith(), NoDepartment, log);

        code.Should().BeNull();
        log.Records.Should().ContainSingle();
        log.Records[0].Level.Should().Be(LogLevel.Warning);
        log.Records[0].Message.Should().Contain("group_paths").And.Contain("マッパー").And.Contain("unassigned");
    }

    [Theory]
    [InlineData("/clearance/restricted")]
    [InlineData("/department/engineering", "/department/sales")]
    public void ClaimPresentButNotExactlyOneDepartment_LogsOneInformation_DistinctFromMissingClaim(params string[] paths)
    {
        var log = new RecordingLogger();

        var code = CreateDataSourceEndpoint.ResolveRegistrantDepartment(UserWith(paths), NoDepartment, log);

        code.Should().BeNull();
        log.Records.Should().ContainSingle();
        // 🔴 Principle A: 「クレームが無い」と区別できること（水準も文言も違う）。
        log.Records[0].Level.Should().Be(LogLevel.Information);
        log.Records[0].Message.Should().NotContain("マッパー");
    }

    [Fact]
    public void ExactlyOneDepartment_DerivesIt_WithoutLogging()
    {
        var log = new RecordingLogger();

        var code = CreateDataSourceEndpoint.ResolveRegistrantDepartment(
            UserWith("/clearance/internal", "/department/hr"), NoDepartment, log);

        code.Should().Be("hr");
        log.Records.Should().BeEmpty();
    }

    // 部門を明示した登録は導く必要が無い。クレームが無くても警告しない（毎回の登録で誤った警告を出さない）。
    [Theory]
    [InlineData("sales", false)]
    [InlineData("unassigned", true)]
    [InlineData("  ", true)]
    public void ExplicitDepartment_SkipsDerivationAndLogging_ButReservedOrBlankStillCounts(string value, bool expectLog)
    {
        var log = new RecordingLogger();
        var requested = new Dictionary<string, string> { ["department"] = value };

        CreateDataSourceEndpoint.ResolveRegistrantDepartment(UserWith(), requested, log);

        log.Records.Should().HaveCount(expectLog ? 1 : 0);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add((logLevel, formatter(state, exception)));
    }
}
