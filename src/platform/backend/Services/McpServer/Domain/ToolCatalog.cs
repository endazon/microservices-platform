
namespace McpServer.Domain;

// FR-16, ADR-0024: 実効ツール（公開名 → 申告 + 公開構成）。
public sealed record PublishedTool(
    string PublishedName,
    string Service,
    McpToolDeclaration Declaration);

// FR-16, ADR-0024 §5: 公開宣言に対して申告が見つからない等の構成ドリフト。
public sealed record ToolCatalogDrift(string Kind, string Target, string Detail);

// FR-16, ADR-0024 §2・§5: サービスの自己申告と宣言的公開構成を突合し、実効ツール一覧を構成する。
//
// 🔴 **既定は非公開（許可リスト方式）である。** 申告があっても公開構成に無ければ公開しない。
// したがって突合は「構成を起点に申告を探す」向きで書く。申告を起点にすると、構成の書き漏れが
// 「公開されてしまう」側へ倒れる。
public sealed class ToolCatalog(ILogger<ToolCatalog> logger)
{
    private volatile CatalogSnapshot _snapshot =
        new(new Dictionary<string, PublishedTool>(StringComparer.Ordinal), [], 0);

    private sealed record CatalogSnapshot(
        IReadOnlyDictionary<string, PublishedTool> Tools,
        IReadOnlyList<ToolCatalogDrift> Drifts,
        int Version);

    public IReadOnlyList<PublishedTool> PublishedTools => [.. _snapshot.Tools.Values];

    public IReadOnlyList<ToolCatalogDrift> Drifts => _snapshot.Drifts;

    // カタログの版。突合のたびに内容が変わったときだけ進む（tools/list_changed の契機に使う）。
    public int Version => _snapshot.Version;

    // 公開名からツールを引く。**構成に無いツールは null を返す。**
    // 呼び出し側は「権限が無い」ではなく「不明なツール」として扱う（存在秘匿。ADR-0024 §3）。
    public PublishedTool? Find(string publishedName)
        => _snapshot.Tools.TryGetValue(publishedName, out var tool) ? tool : null;

    // 自己申告（サービス別）と公開構成を突合し、実効ツール一覧を差し替える。
    public void Refresh(
        ToolPublicationConfig config,
        IReadOnlyList<ServiceToolDeclarations> declarations)
    {
        var declared = declarations
            .SelectMany(d => d.Tools.Select(t => (Service: d.Service, Tool: t)))
            .ToDictionary(x => $"{x.Service}::{x.Tool.Name}", x => x, StringComparer.Ordinal);

        var tools = new Dictionary<string, PublishedTool>(StringComparer.Ordinal);
        var drifts = new List<ToolCatalogDrift>();

        foreach (var entry in config.Tools)
        {
            var key = $"{entry.Service}::{entry.Name}";
            if (!declared.TryGetValue(key, out var found))
            {
                // ADR-0024 §5: 公開宣言されたツールの申告が見つからない場合は構成ドリフト。
                drifts.Add(new ToolCatalogDrift(
                    "missing-declaration", entry.Name,
                    $"サービス '{entry.Service}' が '{entry.Name}' を申告していません。"));
                continue;
            }

            // ADR-0024 §5: egress_class は必須。欠けた申告は公開しない（安全側）。
            if (string.IsNullOrWhiteSpace(found.Tool.EgressClass))
            {
                drifts.Add(new ToolCatalogDrift(
                    "missing-egress-class", entry.Name,
                    $"'{entry.Name}' の申告に egress_class がありません。"));
                continue;
            }

            var publishedName = string.IsNullOrWhiteSpace(entry.PublishedName)
                ? entry.Name
                : entry.PublishedName;
            tools[publishedName] = new PublishedTool(publishedName, entry.Service, found.Tool);
        }

        var previous = _snapshot;
        var changed = previous.Tools.Count != tools.Count
            || tools.Any(kv => !previous.Tools.TryGetValue(kv.Key, out var old) || old != kv.Value);

        _snapshot = new CatalogSnapshot(tools, drifts, changed ? previous.Version + 1 : previous.Version);

        foreach (var drift in drifts)
            logger.LogWarning("MCP tool catalog drift: {Kind} {Target} — {Detail}",
                drift.Kind, drift.Target, drift.Detail);

        if (changed)
            logger.LogInformation(
                "MCP tool catalog refreshed: {Count} published tool(s), version {Version}",
                tools.Count, _snapshot.Version);
    }
}
