using ConversionService.Worker.Consumers;
using ConversionService.Worker.Services;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConversionService.Worker.Tests;

// FR-12, UC-06: ConversionService Consumer ユニットテスト
public class RawDocumentFetchedConsumerTests
{
    [Fact]
    public async Task Consumer_ShouldConsumeRawDocumentFetchedMessage()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IConversionService>(
                new PandocConversionService(NullLogger<PandocConversionService>.Instance))
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<RawDocumentFetchedConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new RawDocumentFetched(
            Guid.NewGuid(), Guid.NewGuid(), "filesystem", "/docs/test.docx",
            "storage://bucket/raw/test.docx", "application/msword",
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ["knowledge-mgmt"],
            DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<RawDocumentFetched>()).Should().BeTrue();
        await harness.Stop();
    }
}
