using DataSourceService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace DataSourceService.Tests;

// #184: jsonb Dictionary 列の ValueComparer が hash/equals 契約（equals(a,b) ⟹ hash(a)==hash(b)）を満たすことを
// EF モデルメタデータ経由で検証する。参照ベース hash（v => v.GetHashCode()）の再導入を機械的に検出する
// （PR #180 claude-review 指摘の再発防止。ビルド・静的解析では検出できないため専用テストで担保する）。
public class JsonbValueComparerContractTests
{
    [Fact]
    public void DictionaryJsonbComparers_HashIsContentBased_NotReference()
    {
        using var ctx = new DataSourceDbContext(
            new DbContextOptionsBuilder<DataSourceDbContext>()
                .UseInMemoryDatabase(nameof(DictionaryJsonbComparers_HashIsContentBased_NotReference)).Options);

        var comparers = ctx.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(Dictionary<string, string>) && p.GetValueComparer() is not null)
            .Select(p => p.GetValueComparer()!)
            .ToList();

        comparers.Should().NotBeEmpty("jsonb Dictionary<string,string> 列に ValueComparer が設定されているはず");

        // 内容は同一だが参照が異なる 2 インスタンス。参照ベース hash（既定 GetHashCode）なら hash が食い違う。
        var a = new Dictionary<string, string> { ["confidentiality"] = "internal", ["team"] = "hr" };
        var b = new Dictionary<string, string> { ["confidentiality"] = "internal", ["team"] = "hr" };

        foreach (var cmp in comparers)
        {
            cmp.Equals(a, b).Should().BeTrue();
            cmp.GetHashCode(a).Should().Be(cmp.GetHashCode(b),
                "hash は equals と同じ内容ベースでなければならない（hash/equals 契約）");
        }
    }
}
