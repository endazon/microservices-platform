namespace SampleService.Contracts;

// テンプレート: 公開イベント契約。Wolverine で発行 / 購読する（ADR-0027）。
public sealed record SampleCreated(Guid Id, string Name, DateTimeOffset OccurredAt);
