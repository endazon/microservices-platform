using ConversionService.Worker.Consumers;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ConversionService.Worker.Tests;

public class RawDocumentFetchedConsumerTests
{
    [Fact]
    public async Task Consumer_ShouldConsumeRawDocumentFetchedMessage()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<RawDocumentFetchedConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new RawDocumentFetched(
            Guid.NewGuid(), "filesystem", "/docs/test.docx",
            "s3://bucket/raw/test.docx", DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<RawDocumentFetched>()).Should().BeTrue();
        await harness.Stop();
    }
}
