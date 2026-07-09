using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WikiService.Api.Foundation.Persistence;

namespace WikiService.Api.Tests;

// #184: jsonb Dictionary 列の ValueComparer が hash/equals 契約（equals(a,b) ⟹ hash(a)==hash(b)）を満たすことを
// EF モデルメタデータ経由で検証する。参照ベース hash（v => v.GetHashCode()）の再導入を機械的に検出する。
public class JsonbValueComparerContractTests
{
    [Fact]
    public void DictionaryJsonbComparers_HashIsContentBased_NotReference()
    {
        using var ctx = new WikiDbContext(
            new DbContextOptionsBuilder<WikiDbContext>()
                .UseInMemoryDatabase(nameof(DictionaryJsonbComparers_HashIsContentBased_NotReference)).Options);

        var comparers = ctx.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(Dictionary<string, string>) && p.GetValueComparer() is not null)
            .Select(p => p.GetValueComparer()!)
            .ToList();

        comparers.Should().NotBeEmpty("Attributes(jsonb) に ValueComparer が設定されているはず");

        var a = new Dictionary<string, string> { ["confidentiality"] = "internal" };
        var b = new Dictionary<string, string> { ["confidentiality"] = "internal" };

        foreach (var cmp in comparers)
        {
            cmp.Equals(a, b).Should().BeTrue();
            cmp.GetHashCode(a).Should().Be(cmp.GetHashCode(b),
                "hash は equals と同じ内容ベースでなければならない（hash/equals 契約）");
        }
    }
}
