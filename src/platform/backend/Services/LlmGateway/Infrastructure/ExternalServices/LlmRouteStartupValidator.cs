using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Options;

namespace LlmGateway.Infrastructure.ExternalServices;

// NFR-18, FR-11, ADR-0010, ADR-0044, IADR-0674 (#2411): 経路の設定の起動時検査。
//
// 用途のモデルの割当・上流の宛先・単価表の誤りは、従前は**最初の呼び出しまで表に出なかった**
// （未知のモデルは黙って既定のモデルへ落ち、空の宛先は呼び出しの例外になり、単価の欠落は金額の計上から黙って抜ける）。
// 起動時に 3 種を引き、段に従って扱う:
//   - 段 1（`Llm:RouteValidation:Mode` 未設定・`Warn`）: 起動を止めない。Program.cs が見つけた誤りを 1 件 1 行の Warning で出す。
//   - 段 2（`Fail`）: 誤りがあれば起動を止める（ValidateOnStart。IADR-0641 の鍵の検査と同じ機構）。
// 🔴 段の値が不正なら起動を止める（「止めたつもりが警告だけ」へ黙って倒さない）。
public sealed class LlmRouteStartupValidator(
    IConfiguration configuration,
    IOptionsMonitor<EmbeddingRoutingOptions> embedding,
    IOptionsMonitor<ModelPricingOptions> pricing,
    TimeProvider time) : IValidateOptions<LlmRoutingOptions>
{
    public ValidateOptionsResult Validate(string? name, LlmRoutingOptions options)
    {
        if (!LlmRouteConfigurationCheck.TryReadMode(configuration, out var mode))
            return ValidateOptionsResult.Fail(
                $"{LlmRouteConfigurationCheck.ModeConfigKey} の値が不正です（{configuration[LlmRouteConfigurationCheck.ModeConfigKey]}）。"
                + "Warn か Fail を指定してください。");
        if (mode != LlmRouteValidationMode.Fail)
            return ValidateOptionsResult.Success;

        var findings = LlmRouteConfigurationCheck.Find(
            options, embedding.CurrentValue, pricing.CurrentValue, configuration, time.GetUtcNow());
        return findings.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(findings.Select(f => f.Message));
    }
}

public enum LlmRouteValidationMode
{
    // 段 1: 警告だけ（起動を止めない）。
    Warn,

    // 段 2: 誤りがあれば起動を止める。
    Fail,
}

// 見つけた誤り。Kind は 3 値（unknown_model / destination / missing_price）で閉じる。
public sealed record LlmRouteFinding(string Kind, string Message);

// 経路の設定の誤りを引く純粋な関数（段に依らない。段 1 の警告と段 2 の拒否が同じ結果を使う）。
public static class LlmRouteConfigurationCheck
{
    public const string ModeConfigKey = "Llm:RouteValidation:Mode";

    public const string KindUnknownModel = "unknown_model";
    public const string KindDestination = "destination";
    public const string KindMissingPrice = "missing_price";

    // 生成のプロバイダ → 上流の宛先の設定キー。claude は宛先を AnthropicClientFactory が本番の URL に固定する（null）。
    private static readonly IReadOnlyDictionary<string, string?> CompletionDestinations = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["claude"] = null,
        ["selfhosted"] = "Llm:SelfHosted:BaseUrl",
        ["copilot"] = "Llm:Copilot:BaseUrl",
    };

    // 外部へ送り、費用が発生するプロバイダ（単価が要る）。セルフホストは社外の費用を持たない。
    private static readonly IReadOnlySet<string> PricedProviders = new HashSet<string>(["claude", "copilot"], StringComparer.Ordinal);

    // 埋め込みのプロバイダ → 上流の宛先の設定キー。決定的ローカル埋め込みはプロセス内計算で宛先を持たない（null）。
    private static readonly IReadOnlyDictionary<string, string?> EmbeddingDestinations = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["voyage"] = "Embedding:Voyage:BaseUrl",
        ["selfhosted-embedding"] = "Embedding:SelfHosted:BaseUrl",
        ["deterministic-embedding"] = null,
    };

    public static bool TryReadMode(IConfiguration configuration, out LlmRouteValidationMode mode)
    {
        var raw = configuration[ModeConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            mode = LlmRouteValidationMode.Warn;
            return true;
        }
        return Enum.TryParse(raw.Trim(), ignoreCase: true, out mode) && Enum.IsDefined(mode);
    }

    public static IReadOnlyList<LlmRouteFinding> Find(
        LlmRoutingOptions routing, EmbeddingRoutingOptions embedding, ModelPricingOptions pricing,
        IConfiguration configuration, DateTimeOffset now)
    {
        var findings = new List<LlmRouteFinding>();
        var declared = new HashSet<string>(routing.Endpoints.SelectMany(e => e.Models), StringComparer.Ordinal);

        // ── 未知のモデル（宣言されたどのエンドポイントの Models にも無い）。
        foreach (var (purpose, model) in routing.PurposeModels)
        {
            if (string.IsNullOrWhiteSpace(model) || !declared.Contains(model))
                findings.Add(new(KindUnknownModel,
                    $"Llm:Routing:PurposeModels:{purpose} のモデル '{model}' は、どのエンドポイントの Models にもありません（呼び出しは既定のモデルへ落ちます）。"));
        }
        foreach (var (purpose, models) in routing.PurposeFallbackModels)
        {
            foreach (var model in models.Where(m => !declared.Contains(m)))
                findings.Add(new(KindUnknownModel,
                    $"Llm:Routing:PurposeFallbackModels:{purpose} のモデル '{model}' は、どのエンドポイントの Models にもありません。"));
        }
        foreach (var e in routing.Endpoints.Where(e => !string.IsNullOrWhiteSpace(e.DefaultModel)))
        {
            if (!e.Models.Contains(e.DefaultModel, StringComparer.Ordinal))
                findings.Add(new(KindUnknownModel,
                    $"エンドポイント '{e.Name}' の DefaultModel '{e.DefaultModel}' が、そのエンドポイントの Models にありません。"));
        }

        // ── 空の宛先・未知のプロバイダ（有効なエンドポイントだけ）。
        foreach (var e in routing.Endpoints.Where(e => e.Enabled))
        {
            if (!CompletionDestinations.TryGetValue(e.Provider, out var key))
                findings.Add(new(KindDestination,
                    $"エンドポイント '{e.Name}' の Provider '{e.Provider}' は未知です（{string.Join(", ", CompletionDestinations.Keys)}）。"));
            else if (key is not null && !IsAbsoluteHttpUrl(configuration[key]))
                findings.Add(new(KindDestination,
                    $"エンドポイント '{e.Name}' が有効なのに、上流の宛先 {key} が空か、http(s) の絶対 URL ではありません。"));
        }
        foreach (var e in embedding.Endpoints.Where(e => e.Enabled))
        {
            if (!EmbeddingDestinations.TryGetValue(e.Provider, out var key))
                findings.Add(new(KindDestination,
                    $"埋め込みのエンドポイント '{e.Name}' の Provider '{e.Provider}' は未知です（{string.Join(", ", EmbeddingDestinations.Keys)}）。"));
            else if (key is not null && !IsAbsoluteHttpUrl(configuration[key]))
                findings.Add(new(KindDestination,
                    $"埋め込みのエンドポイント '{e.Name}' が有効なのに、上流の宛先 {key} が空か、http(s) の絶対 URL ではありません。"));
        }

        // ── 単価の欠落（有効な外部エンドポイントのモデルに、起動時点で有効な単価が無い）。
        var priced = routing.Endpoints
            .Where(e => e.Enabled && PricedProviders.Contains(e.Provider))
            .SelectMany(e => e.Models)
            .Distinct(StringComparer.Ordinal);
        foreach (var model in priced)
        {
            if (!pricing.Models.TryGetValue(model, out var entries) || !entries.Any(x => x.Covers(now)))
                findings.Add(new(KindMissingPrice,
                    $"モデル '{model}' に {now:yyyy-MM-dd} 時点で有効な単価がありません（Llm:Pricing:Models:{model}）。費用が計上されません。"));
        }

        return findings;
    }

    private static bool IsAbsoluteHttpUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
