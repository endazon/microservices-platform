namespace SampleService.Domain;

// テンプレート: エンティティ / 値オブジェクトを置く。外部ライブラリを using しないこと（ADR-0030）。
public sealed record SampleAggregate(Guid Id, string Name)
{
    public bool IsNamed => !string.IsNullOrWhiteSpace(Name);
}
