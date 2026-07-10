using System.Globalization;
using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.Extensions.Options;

namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;

// FR-15: 自己申告の収集・実効構成の組み立て・宣言との突合（ドリフト検出）を担うサービス。
// 構成情報 API（BFF エンドポイント）とドリフト定期検出（ホスト型サービス）の双方から利用される。
public interface IConfigInspectionService
{
    // 実効構成（段・イベント接続・ポート選択・コネクタ・構成バージョン）を組み立てて返す。
    Task<EffectiveConfigDto> GetEffectiveConfigAsync(CancellationToken ct = default);

    // 宣言（pipeline.json）と実効構成のドリフトを検出して返す。
    Task<DriftReportDto> GetDriftAsync(CancellationToken ct = default);

    // FR-15 (#139): 構成バージョン適用履歴（新しい順）を返す。正データ源は GitOps 層（IADR-0046）。
    Task<IReadOnlyList<ConfigVersionEntryDto>> GetVersionHistoryAsync(CancellationToken ct = default);
}

public sealed class ConfigInspectionService(
    IEffectiveConfigCollector collector,
    PipelineOptions declaration,
    IOptions<ConfigVersionOptions> versionOptions,
    TimeProvider timeProvider) : IConfigInspectionService
{
    private readonly ConfigVersionOptions _version = versionOptions.Value;

    public async Task<EffectiveConfigDto> GetEffectiveConfigAsync(CancellationToken ct = default)
    {
        var effective = await collector.CollectAsync(ct);
        return Assemble(declaration, effective, BuildVersion());
    }

    public async Task<DriftReportDto> GetDriftAsync(CancellationToken ct = default)
    {
        var effective = await collector.CollectAsync(ct);
        var findings = DriftDetector.Detect(declaration, effective);
        return new DriftReportDto(findings.Count > 0, timeProvider.GetUtcNow(), findings);
    }

    // FR-15 (#139), IADR-0046: 構成バージョン履歴（新しい順）を返す。
    // 正データ源は GitOps 層（Git/ArgoCD 適用履歴）で、現在バージョンと同じ注入経路で供給される
    // ConfigVersionOptions.History をそのまま surfacing する（API 側は履歴を永続化しない）。
    // 履歴未注入（dev/compose）時は現在バージョンの単一エントリへ縮退し、現在バージョンも空なら空一覧を返す。
    public Task<IReadOnlyList<ConfigVersionEntryDto>> GetVersionHistoryAsync(CancellationToken ct = default) =>
        Task.FromResult(BuildHistory());

    private IReadOnlyList<ConfigVersionEntryDto> BuildHistory()
    {
        if (_version.History.Count > 0)
        {
            // 注入された履歴を新しい順（AppliedAt 降順、日時不明は末尾）に整える。OrderByDescending は安定
            // ソートのため、AppliedAt が等しい／不明なエントリは注入順（＝GitOps が並べた順）を保つ。
            return _version.History
                .Select(h => new ConfigVersionEntryDto(
                    h.GitCommit, ParseApplied(h.AppliedAt), h.AppliedBy, h.HadDrift))
                .OrderByDescending(e => e.AppliedAt ?? DateTimeOffset.MinValue)
                .ToList();
        }

        // 履歴未注入時の縮退: 現在バージョンを単一エントリとして返す（その時点のドリフト有無は不明＝null。
        // 現在の実効ドリフトは /drift で別途取得できる）。現在バージョンも空なら履歴なし。
        var current = BuildVersion();
        if (current.GitCommit is null && current.AppliedAt is null && current.AppliedBy is null)
            return Array.Empty<ConfigVersionEntryDto>();
        return [new ConfigVersionEntryDto(current.GitCommit, current.AppliedAt, current.AppliedBy, null)];
    }

    // 宣言と自己申告から実効構成 DTO を組み立てる（純粋変換。テスト可能）。
    public static EffectiveConfigDto Assemble(
        PipelineOptions declaration, EffectiveCollection effective, ConfigVersionDto version)
    {
        // 段名 → 宣言順（イベント接続の並び・段の表示順に用いる）。
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < declaration.Steps.Count; i++)
            order[declaration.Steps[i].Name] = i;

        // 実効の段（サービス横断）。宣言順→段名で安定ソートする。
        var stages = new List<PipelineStageDto>();
        foreach (var svc in effective.Services)
        {
            foreach (var step in svc.Steps)
            {
                // サービス識別は宣言の service を優先（宣言と対応づく）。無ければ自己申告の Service。
                var declared = declaration.FindStep(step.Name);
                var service = declared?.Service ?? svc.Service;
                stages.Add(new PipelineStageDto(
                    step.Name, service, step.Consumer, step.Input, step.Outputs, step.Enabled));
            }
        }
        stages.Sort((a, b) =>
        {
            var ia = order.TryGetValue(a.Name, out var x) ? x : int.MaxValue;
            var ib = order.TryGetValue(b.Name, out var y) ? y : int.MaxValue;
            return ia != ib ? ia.CompareTo(ib) : string.CompareOrdinal(a.Name, b.Name);
        });

        var bindings = BuildEventBindings(declaration, stages);

        // ポート・コネクタはサービス横断で結合（重複は実装＋対象で一意化）。
        var ports = effective.Services
            .SelectMany(s => s.Ports)
            .GroupBy(p => (p.Port, p.Implementation, p.Target))
            .Select(g => g.First())
            .ToList();
        var connectors = effective.Services
            .SelectMany(s => s.Connectors)
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        return new EffectiveConfigDto(version, stages, bindings, ports, connectors);
    }

    // イベント接続（イベント型ごとの発行者・購読者）を宣言 sources ＋実効段の入出力から組み立てる。
    private static List<EventBindingDto> BuildEventBindings(
        PipelineOptions declaration, IReadOnlyList<PipelineStageDto> stages)
    {
        var events = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in declaration.Events) events.Add(e);
        foreach (var s in declaration.Sources) if (!string.IsNullOrEmpty(s.Event)) events.Add(s.Event);
        foreach (var st in stages)
        {
            if (!string.IsNullOrEmpty(st.Input)) events.Add(st.Input);
            foreach (var o in st.Outputs) events.Add(o);
        }

        var bindings = new List<EventBindingDto>();
        foreach (var ev in events)
        {
            var publishers = new List<string>();
            // 同期 API 起点の発行（宣言 sources）。
            publishers.AddRange(declaration.Sources
                .Where(s => string.Equals(s.Event, ev, StringComparison.Ordinal))
                .Select(s => s.Service));
            // 段の発行（有効な段の outputs）。
            publishers.AddRange(stages
                .Where(s => s.Enabled && s.Outputs.Contains(ev))
                .Select(s => s.Service));

            // 段の購読（有効な段の input）。
            var subscribers = stages
                .Where(s => s.Enabled && string.Equals(s.Input, ev, StringComparison.Ordinal))
                .Select(s => s.Service)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            bindings.Add(new EventBindingDto(
                ev, publishers.Distinct(StringComparer.Ordinal).ToList(), subscribers));
        }
        return bindings;
    }

    private ConfigVersionDto BuildVersion() =>
        new(_version.GitCommit, ParseApplied(_version.AppliedAt), _version.AppliedBy);

    // 適用日時文字列（ISO 8601、UTC 前提）を解釈する。空・不正は null（不明）とする。
    private static DateTimeOffset? ParseApplied(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }
        return null;
    }
}
