using System.Reflection;
using AwesomeAssertions;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.Messaging;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;

namespace IngestionService.Tests.Infrastructure.Messaging;

// FR-02, ADR-0027, IADR-0234 単位 E4 (#2134): IngestionCompleted の発行が Wolverine の経路で出ることを固定する。
//
// T-06（`DocumentUpdatedConsumerTests.Consumer_ShouldPublishIngestionCompleted`）は「取り込み完了で発行口が呼ばれ、件数 > 0」を
// ポートの引数で見る（変えない）。本ファイルはその先 —— 発行口が値をそのまま運ぶイベントを Wolverine へ出し、本番の配線で
// RabbitMQ の fan-out exchange へ経路が引かれること —— を見る。MassTransit の発行口の頃は、ここを見る試験が無かった。
[Trait("TestKind", "Integration")]
public class WolverineIngestionCompletedPublisherTests(IntrospectionEndpointTests.Factory factory)
    : IClassFixture<IntrospectionEndpointTests.Factory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 発行口は受け取った値をそのまま運ぶイベントを 1 件だけ `IMessageBus.PublishAsync` へ出す（Send ではない）。
    [Fact]
    public async Task 受け取った値をそのまま運ぶIngestionCompletedを1件発行する()
    {
        var bus = RecordingBus.Create();
        var documentId = Guid.NewGuid();
        var completedAt = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

        await new WolverineIngestionCompletedPublisher(bus).PublishCompletedAsync(documentId, 7, completedAt, Ct);

        var calls = RecordingBus.Of(bus).Calls;
        calls.Should().ContainSingle().Which.Method.Should().Be(nameof(IMessageBus.PublishAsync));
        calls[0].Message.Should().BeEquivalentTo(new IngestionCompleted(documentId, 7, completedAt));
    }

    // 本番の配線（`Program.cs`）: 発行口は Wolverine の実装であり、IngestionCompleted は RabbitMQ の exchange
    // `IngestionCompleted` へ経路が引かれる。🔴 `RoutePlatformEvent<IngestionCompleted>()` を外すと経路が無くなり赤になる
    // （共通既定が規約のローカル経路を切っているので、宣言が無い発行は送り先を持たない）。
    [Fact]
    public void 本番の配線ではRabbitMQのexchangeへ経路が引かれる()
    {
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IIngestionCompletedPublisher>()
                .Should().BeOfType<WolverineIngestionCompletedPublisher>();
        }

        var envelopes = factory.Services.GetRequiredService<IMessageBus>()
            .PreviewSubscriptions(new IngestionCompleted(Guid.NewGuid(), 1, DateTimeOffset.UtcNow));

        envelopes.Should().ContainSingle("発行先は fan-out exchange 1 つだけ（購読者は束ねる側が足す）");
        var destination = envelopes[0].Destination!;
        destination.Scheme.Should().Be("rabbitmq");
        destination.ToString().Should().Contain(WolverineExtensions.PlatformExchangeName<IngestionCompleted>());
    }

    // `IMessageBus` の呼び出しを記録する代役（使う面は PublishAsync / SendAsync だけ。他が呼ばれたら失敗させる）。
    public class RecordingBus : DispatchProxy
    {
        public List<(string Method, object? Message)> Calls { get; } = [];

        public static IMessageBus Create() => DispatchProxy.Create<IMessageBus, RecordingBus>();

        public static RecordingBus Of(IMessageBus bus) => (RecordingBus)(object)bus;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (name is nameof(IMessageBus.PublishAsync) or nameof(IMessageBus.SendAsync))
            {
                Calls.Add((name, args![0]));
                return ValueTask.CompletedTask;
            }
            throw new NotSupportedException($"IMessageBus.{name} は発行口から呼ばれない想定である。");
        }
    }
}
