namespace SampleService.Domain;

// テンプレート: エンティティ / 値オブジェクトを置く。**Domain/ は Features/・Infrastructure/・
// Common/Behaviors を using せず、外部ライブラリにも依存しない**（ADR-0030 選定基準 3 /
// IADR-0282 決定 2。唯一の例外は Platform.Shared.Kernel —— Result 型と DDD 基底型）。
public sealed record SampleAggregate(Guid Id, string Name)
{
    public bool IsNamed => !string.IsNullOrWhiteSpace(Name);
}
