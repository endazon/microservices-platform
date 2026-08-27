namespace SampleService.Features.Samples.Create;

// テンプレート: スライスが発行するイベント。Wolverine で発行 / 購読する（ADR-0027）。
// **他サービスが購読する契約になったら、ユニットの Shared（<Unit>.Contracts）へ移す**
// （サービス個別の Contracts プロジェクトは置かない —— IADR-0282 決定 1）。
public sealed record SampleCreated(Guid Id, string Name, DateTimeOffset OccurredAt);
