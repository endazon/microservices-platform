using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Tests.Features.Authz.ResolveScope;

// FR-05, ADR-0121 フォローアップ 6 (#1664): dev seed（`deploy/local/abac-seed/policies.json`）と、
// seed を入れた端点の応答の期待値（`Fixtures/owner-read-seed-scopes.json`）を読む。
// **どちらもリポジトリの実ファイルを読む**（書き写さない）。
internal static class SeedScopeFixture
{
    internal sealed record SeedPolicy(
        string Name, string Action,
        Dictionary<string, List<string>> UserConditions,
        Dictionary<string, List<string>> DocumentConditions);

    internal sealed record Subject(string UserId, Dictionary<string, string> Attributes);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<SeedPolicy> SeedPolicies()
    {
        var root = JsonNode.Parse(File.ReadAllText(RepoFile("deploy/local/abac-seed/policies.json")))!;
        return [.. root["policies"]!.AsArray().Select(p => new SeedPolicy(
            (string)p!["name"]!,
            (string)p["action"]!,
            Conditions(p["userConditions"]),
            Conditions(p["documentConditions"])))];
    }

    public static IReadOnlyList<Subject> Subjects()
        => [.. FixtureSubjects().Select(s => new Subject(
            (string)s!["userId"]!,
            s["attributes"]!.Deserialize<Dictionary<string, string>>(Web)!))];

    public static AccessScopeResponse ScopeOf(string userId)
        => FixtureSubjects().Single(s => (string)s!["userId"]! == userId)!["scope"]!
            .Deserialize<AccessScopeResponse>(Web)!;

    // 分岐の順序・値の順序を無視した正規形（DB の返す順は定まらない）。
    public static string Normalize(AccessScopeResponse scope)
    {
        static string Filters(IEnumerable<AttributeFilter> filters) => string.Join(" & ", filters
            .Select(f => $"{f.Key}∈{{{string.Join(",", f.AllowedValues.Order(StringComparer.Ordinal))}}}")
            .Order(StringComparer.Ordinal));

        var branches = (scope.Branches ?? [])
            .Select(b => $"[{b.Name}] {Filters(b.Filters)}")
            .Order(StringComparer.Ordinal);
        return $"user={scope.UserId} granted={scope.Granted}\n"
            + $"allowed: {Filters(scope.AllowedFilters)}\n"
            + string.Join("\n", branches);
    }

    private static JsonArray FixtureSubjects()
        => JsonNode.Parse(File.ReadAllText(RepoFile(
            "src/platform/backend/Services/AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json")))!
            ["subjects"]!.AsArray();

    private static Dictionary<string, List<string>> Conditions(JsonNode? node)
        => node is null ? [] : node.Deserialize<Dictionary<string, List<string>>>(Web)!;

    internal static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "docker-compose.yml")) && File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"リポジトリの {relative} が見つからない（走査の起点: {AppContext.BaseDirectory}）");
    }
}
