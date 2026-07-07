using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Pipeline;

namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, ADR-0018: 宣言（Git 上の pipeline.json）と実効構成（自己申告の集約）を突合し、
// 不一致（ドリフト）を検出する純粋関数。10_composability-design.md §6 のドリフト種別に対応する。
// 判定粒度は「段の存在・有効状態・購読バインディング（input/consumer）」とし、キュー名の相違は
// 情報レベルに留めて誤検知を抑制する（未決事項: ドリフト判定粒度・誤検知抑制）。
public static class DriftDetector
{
    // ドリフト種別（DriftFindingDto.Kind）。
    public const string MissingApply = "MissingApply";                 // 宣言で有効だが実効に無い（適用漏れ・起動失敗）
    public const string UndeclaredSubscription = "UndeclaredSubscription"; // 宣言に無い購読が実効に存在
    public const string StaleStage = "StaleStage";                     // 宣言で無効だが実効で稼働（古い段の残留）
    public const string BindingMismatch = "BindingMismatch";           // 段名一致だが input/consumer が不一致
    public const string Unverifiable = "Unverifiable";                 // 宣言の段の担当サービスが到達不能で検証不能

    public const string SeverityWarning = "Warning";
    public const string SeverityInfo = "Info";

    // 宣言 declaration と実効 effective を突合し、ドリフトの一覧を返す（空＝一致）。
    public static IReadOnlyList<DriftFindingDto> Detect(
        PipelineOptions declaration, EffectiveCollection effective)
    {
        var findings = new List<DriftFindingDto>();

        // 実効の段（段名→(サービス, 実効段)）。段名は宣言側で一意（CI 検証 V2）。
        var effectiveSteps = new Dictionary<string, (string Service, StepIntrospectionDto Step)>(
            StringComparer.Ordinal);
        foreach (var svc in effective.Services)
        {
            foreach (var step in svc.Steps)
            {
                // 同名の重複は後勝ちで良い（宣言側で一意性が保証されるため通常発生しない）。
                effectiveSteps[step.Name] = (svc.Service, step);
            }
        }

        var declaredNames = new HashSet<string>(
            declaration.Steps.Select(s => s.Name), StringComparer.Ordinal);

        // 1) 宣言側からの検査（適用漏れ・古い段の残留・バインディング不一致）。
        foreach (var decl in declaration.Steps)
        {
            var hosted = effectiveSteps.TryGetValue(decl.Name, out var eff);

            if (decl.Enabled)
            {
                if (!hosted || !eff.Step.Enabled)
                {
                    // 担当サービスが到達不能なら「検証不能」に留め、適用漏れとは断定しない（誤検知抑制）。
                    if (!IsServiceReachable(decl.Service, effective))
                    {
                        findings.Add(new DriftFindingDto(
                            Unverifiable, SeverityInfo, decl.Name,
                            $"宣言で有効な段 '{decl.Name}' の担当サービス '{decl.Service}' が到達不能で実効を検証できません。"));
                    }
                    else
                    {
                        findings.Add(new DriftFindingDto(
                            MissingApply, SeverityWarning, decl.Name,
                            $"宣言で有効な段 '{decl.Name}' が実効構成に存在しません（適用漏れ・起動失敗の疑い）。"));
                    }
                    continue;
                }

                // 段名一致・有効。購読バインディング（input/consumer）の一致を検査する。
                if (!string.Equals(decl.Input, eff.Step.Input, StringComparison.Ordinal)
                    || !string.Equals(decl.Consumer, eff.Step.Consumer, StringComparison.Ordinal))
                {
                    findings.Add(new DriftFindingDto(
                        BindingMismatch, SeverityWarning, decl.Name,
                        $"段 '{decl.Name}' の宣言（input={decl.Input}, consumer={decl.Consumer}）が " +
                        $"実効（input={eff.Step.Input}, consumer={eff.Step.Consumer}）と一致しません。"));
                }
            }
            else
            {
                // 宣言で無効だが実効で稼働している＝古い段の残留。
                if (hosted && eff.Step.Enabled)
                {
                    findings.Add(new DriftFindingDto(
                        StaleStage, SeverityWarning, decl.Name,
                        $"宣言で無効な段 '{decl.Name}' が実効構成で稼働しています（古い段の残留・適用漏れ）。"));
                }
            }
        }

        // 2) 実効側からの検査（宣言に無い購読）。
        foreach (var kv in effectiveSteps)
        {
            var (name, value) = (kv.Key, kv.Value);
            if (value.Step.Enabled && !declaredNames.Contains(name))
            {
                findings.Add(new DriftFindingDto(
                    UndeclaredSubscription, SeverityWarning, name,
                    $"宣言に無い購読 '{name}'（サービス '{value.Service}'）が実効構成に存在します。"));
            }
        }

        return findings;
    }

    // 担当サービスが到達済み（自己申告を返した）か。未設定・到達不能はいずれも「検証不能」扱い。
    private static bool IsServiceReachable(string service, EffectiveCollection effective) =>
        effective.ReachableServices.Contains(service);
}
