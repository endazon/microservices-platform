using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConversionService.Tests;

// テスト共通ヘルパ: Npgsql 登録の DbContext を EF Core InMemory provider へ差し替える。
// 起動時 MigrateAsync は InMemory が非リレーショナル（IsRelational=false）のためスキップされる（IADR-0043）。
// ConversionJobEndpointTests / IntrospectionEndpointTests の重複実装を集約する。
internal static class TestDbContextReplacement
{
    public static void ReplaceDbContextWithInMemory<TContext>(this IServiceCollection services, string dbName)
        where TContext : DbContext
    {
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
