using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Shared.Contracts.Dtos;

namespace DocumentService.Tests;

// FR-05, FR-19, NFR-09, 計画 ADR-0121 決定 1・5 (#1664, #1615): **dev seed を入れた認可サービスの応答の期待値**を読む。
// 正は `AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json`（認可サービスの試験が実物の応答と突き合わせる）。
// 🔴 期待値を試験へ書き写さない —— seed が変わったときに、こちらだけが作り物のまま緑で残る。
public static class OwnerReadSeedScopes
{
    private const string Relative =
        "src/platform/backend/Services/AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json";

    public static AccessScopeResponse ScopeOf(string userId)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, Relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            return JsonNode.Parse(File.ReadAllText(path))!["subjects"]!.AsArray()
                .Single(s => (string)s!["userId"]! == userId)!["scope"]!
                .Deserialize<AccessScopeResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
        throw new FileNotFoundException($"リポジトリの {Relative} が見つからない（走査の起点: {AppContext.BaseDirectory}）");
    }

    /// <summary>その主体の分岐の並び（`IDocumentReadScopeSource` が返す形）。</summary>
    public static IReadOnlyList<IReadOnlyList<AttributeFilter>> BranchesOf(string userId)
        => [.. ScopeOf(userId).Branches!.Select(b => (IReadOnlyList<AttributeFilter>)b.Filters)];
}
