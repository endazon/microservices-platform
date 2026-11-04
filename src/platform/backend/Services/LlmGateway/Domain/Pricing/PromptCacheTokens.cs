namespace LlmGateway.Domain.Pricing;

// FR-10, ADR-0044 決定 3, IADR-0671 (#2396): 1 要求の入力トークン（プロンプト全体）の**うち**、キャッシュへ書いた分と読んだ分。
// 単価が入力と違う（書き 1.25 倍・読み 0.05〜0.1 倍。提供元の公表値）ので、費用と計器はこの 2 つを分けて数える。
// 既定（0, 0）はキャッシュを使わない要求であり、従前の換算と同じになる。
public readonly record struct PromptCacheTokens(long Write, long Read)
{
    public bool IsEmpty => Write <= 0 && Read <= 0;
}
