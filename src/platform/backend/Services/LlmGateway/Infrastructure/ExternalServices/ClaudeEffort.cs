using Microsoft.Extensions.Options;

namespace LlmGateway.Infrastructure.ExternalServices;

// FR-11, ADR-0010, ADR-0025, IADR-0531 (#1875・planning#783): 用途別の effort（`output_config.effort`）の設定。
//
// 構成は `Llm:PurposeEffort`（用途 → effort）。例: `{ "rerank": "low" }`。**既定は空**であり、書かない用途は
// effort を送らない（＝提供元の既定。opus-5-5 / haiku-5-5 は medium、sonnet-5-5 は high）。
// 5.5 系は thinking を無効にできない（400）ので、思考の量を絞る手段は effort だけである。
public sealed class ClaudePurposeEffortOptions
{
    public const string SectionName = "Llm:PurposeEffort";

    // 用途 → effort。キーは呼び出し側が送る purpose 値（PurposeModels と同じ値域）。
    public Dictionary<string, string> Purposes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// IADR-0531 決定 3: effort を送ってよいモデルと値域。
public static class ClaudeEffort
{
    // 提供元が受け付ける effort の値（2026-10-10 確認）。
    public static readonly IReadOnlySet<string> Levels =
        new HashSet<string>(["low", "medium", "high", "xhigh", "max"], StringComparer.Ordinal);

    // 🔴 **effort を受け付けると確かめたモデルだけ**に送る。`claude-haiku-4-5` は effort を 400 で拒む
    // （切り戻しで用途を haiku-4-5 へ戻したときに、設定 1 つで全件 400 にしない）。ここに無いモデルへは送らない。
    // 一覧は利用許可集合（claude-managed の Models）のうち effort に対応するものに限る（sonnet-4-6 は xhigh を
    // 受けないため、値域が揃わないモデルは載せない）。
    public static readonly IReadOnlySet<string> CapableModels = new HashSet<string>(
        ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-opus-5", "claude-sonnet-5", "claude-opus-4-8"],
        StringComparer.OrdinalIgnoreCase);

    // 用途とモデルから、送る effort を決める。送らないときは null。
    public static string? Resolve(ClaudePurposeEffortOptions? options, string? purpose, string? model)
    {
        if (options is null || string.IsNullOrWhiteSpace(purpose) || string.IsNullOrWhiteSpace(model))
            return null;
        if (!CapableModels.Contains(model))
            return null;
        return options.Purposes.TryGetValue(purpose, out var effort) && !string.IsNullOrWhiteSpace(effort)
            ? effort.Trim().ToLowerInvariant()
            : null;
    }
}

// IADR-0531 決定 3: 値域の外の effort は**起動時に落とす**（ValidateOnStart）。実行時に送ると全件 400 になり、
// 呼び出し側には「上流の失敗」としか見えない。
public sealed class ClaudePurposeEffortOptionsValidator : IValidateOptions<ClaudePurposeEffortOptions>
{
    public ValidateOptionsResult Validate(string? name, ClaudePurposeEffortOptions options)
    {
        var errors = options.Purposes
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value) || !ClaudeEffort.Levels.Contains(kv.Value.Trim().ToLowerInvariant()))
            .Select(kv => $"{ClaudePurposeEffortOptions.SectionName}:{kv.Key} の値 '{kv.Value}' は effort の値域"
                + $"（{string.Join(" / ", ClaudeEffort.Levels)}）にありません。")
            .ToList();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

// IADR-0531 決定 3: 1 回の Anthropic 呼び出しに付ける effort を、SDK の外（HttpClient の層）へ渡す文脈。
// Anthropic.SDK 4.0.0 の MessageParameters は `output_config` を持たないため、要求本文への注入は
// AnthropicRequestShapingHandler が行う。呼び出し単位の値なので AsyncLocal で渡す（シングルトンの HttpClient を
// 共有したまま、並行する呼び出しの値が混ざらない）。
public static class AnthropicRequestContext
{
    private static readonly AsyncLocal<string?> CurrentEffort = new();

    public static string? Effort => CurrentEffort.Value;

    // 範囲の間だけ effort を設定する。null なら何も付けない。
    public static IDisposable UseEffort(string? effort)
    {
        var previous = CurrentEffort.Value;
        CurrentEffort.Value = effort;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => CurrentEffort.Value = previous;
    }
}
