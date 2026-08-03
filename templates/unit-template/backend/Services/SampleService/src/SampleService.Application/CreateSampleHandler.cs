using SampleService.Contracts;
using SampleService.Domain;

namespace SampleService.Application;

// テンプレート: ローカルディスパッチも Wolverine ハンドラに統一する（ADR-0030。MediatR・独自 Dispatcher は使わない）。
// ハンドラは規約ベースで解決されるため、インタフェース実装や属性は不要である。
public sealed record CreateSample(string Name);

public static class CreateSampleHandler
{
    public static SampleCreated Handle(CreateSample command, TimeProvider clock)
    {
        var aggregate = new SampleAggregate(Guid.NewGuid(), command.Name);
        return new SampleCreated(aggregate.Id, aggregate.Name, clock.GetUtcNow());
    }
}
