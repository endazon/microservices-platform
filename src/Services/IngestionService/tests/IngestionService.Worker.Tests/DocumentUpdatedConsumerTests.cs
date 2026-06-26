using FluentAssertions;
using IngestionService.Worker.Consumers;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IngestionService.Worker.Tests;

public class DocumentUpdatedConsumerTests
{
    [Fact]
    public async Task Consumer_ShouldConsumeDocumentUpdatedMessage()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<DocumentUpdatedConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new DocumentUpdated(
            Guid.NewGuid(), "Test Doc", "active", DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<DocumentUpdated>()).Should().BeTrue();
        await harness.Stop();
    }
}
