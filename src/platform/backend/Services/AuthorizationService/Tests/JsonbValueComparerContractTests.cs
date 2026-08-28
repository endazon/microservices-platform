using AuthorizationService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Tests;

// #184: jsonb Dictionary 列の ValueComparer が hash/equals 契約（equals(a,b) ⟹ hash(a)==hash(b)）を満たすことを
// EF モデルメタデータ経由で検証する。参照ベース hash（v => v.GetHashCode()）の再導入を機械的に検出する。
// AuthorizationDbContext の対象は AbacPolicy の条件（Dictionary<string, List<string>>）。
public class JsonbValueComparerContractTests
{
    [Fact]
    public void DictionaryJsonbComparers_HashIsContentBased_NotReference()
    {
        using var ctx = new AuthorizationDbContext(
            new DbContextOptionsBuilder<AuthorizationDbContext>()
                .UseInMemoryDatabase(nameof(DictionaryJsonbComparers_HashIsContentBased_NotReference)).Options);

        var comparers = ctx.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(Dictionary<string, List<string>>) && p.GetValueComparer() is not null)
            .Select(p => p.GetValueComparer()!)
            .ToList();

        comparers.Should().NotBeEmpty("AbacPolicy の条件（Dictionary<string,List<string>>）に ValueComparer が設定されているはず");

        // 内容は同一だが参照が異なる 2 インスタンス（内側 List も別インスタンス）。参照ベース hash なら食い違う。
        var a = new Dictionary<string, List<string>> { ["dept"] = new() { "hr", "legal" } };
        var b = new Dictionary<string, List<string>> { ["dept"] = new() { "hr", "legal" } };

        foreach (var cmp in comparers)
        {
            cmp.Equals(a, b).Should().BeTrue();
            cmp.GetHashCode(a).Should().Be(cmp.GetHashCode(b),
                "hash は equals と同じ内容ベースでなければならない（hash/equals 契約）");
        }
    }
}
