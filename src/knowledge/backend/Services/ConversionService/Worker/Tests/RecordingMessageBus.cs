using System.Collections.Concurrent;
using Wolverine;

namespace ConversionService.Worker.Tests;

// ADR-0027（#441 E1）: 発行されたメッセージを記録するだけの `IMessageBus` テストダブル。
//
// 🔴 **`DataSourceService.Tests` の同名クラスと意図的な重複である。**
// サービスごとのテストプロジェクトは自己完結しており（相互の ProjectReference を持たない）、
// 共有テストヘルパのプロジェクトは存在しない。**それを新設するのは E1 の射程を超える構造変更**
// なので、ここでは重複を受け入れる。3 つ目が要るときに共通化を検討すること。
//
// MassTransit の `ITestHarness`（`harness.Published.Select<T>()`）に相当する観測点を、
// Wolverine 側で得るために置く。**本番の発行経路は `PublishAsync` だけ**なので、
// それ以外は `NotSupportedException` を投げる —— 使われたら黙って成功させず、**気づける形にする**。
//
// ⚠️ 実ブローカ越しの配送は本ダブルでは測れない（測るのは Knowledge.IntegrationTests の
// `BrokerRequired.SkipUnlessObtainable()` を使う試験である）。ここで固定するのは「何を発行したか」だけである。
public sealed class RecordingMessageBus : IMessageBus
{
    private readonly ConcurrentQueue<object> _published = new();

    public IReadOnlyList<T> PublishedOf<T>() => [.. _published.OfType<T>()];

    public string? TenantId { get; set; }

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message is not null) _published.Enqueue(message);
        return ValueTask.CompletedTask;
    }

    private static NotSupportedException Unused(string member) =>
        new($"RecordingMessageBus.{member} は使われない想定である（本番の発行経路は PublishAsync のみ）。"
            + " 呼ばれたなら、試験対象の発行経路が変わったということなので、ダブルではなく設計を見直すこと。");

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null) => throw Unused(nameof(SendAsync));

    public ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null) =>
        throw Unused(nameof(BroadcastToTopicAsync));

    public IDestinationEndpoint EndpointFor(string endpointName) => throw Unused(nameof(EndpointFor));

    public IDestinationEndpoint EndpointFor(Uri uri) => throw Unused(nameof(EndpointFor));

    public IReadOnlyList<Envelope> PreviewSubscriptions(object message) => throw Unused(nameof(PreviewSubscriptions));

    public IReadOnlyList<Envelope> PreviewSubscriptions(object message, DeliveryOptions? options) =>
        throw Unused(nameof(PreviewSubscriptions));

    public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        throw Unused(nameof(InvokeAsync));

    public Task InvokeAsync(object message, DeliveryOptions? options, CancellationToken cancellation = default,
        TimeSpan? timeout = null) => throw Unused(nameof(InvokeAsync));

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        throw Unused(nameof(InvokeAsync));

    public Task<T> InvokeAsync<T>(object message, DeliveryOptions? options, CancellationToken cancellation = default,
        TimeSpan? timeout = null) => throw Unused(nameof(InvokeAsync));

    public Task InvokeForTenantAsync(string tenantId, object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null) => throw Unused(nameof(InvokeForTenantAsync));

    public Task<T> InvokeForTenantAsync<T>(string tenantId, object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null) => throw Unused(nameof(InvokeForTenantAsync));

    public IAsyncEnumerable<T> StreamAsync<T>(object message, CancellationToken cancellation = default) =>
        throw Unused(nameof(StreamAsync));

    public IAsyncEnumerable<T> StreamAsync<T>(object message, DeliveryOptions? options,
        CancellationToken cancellation = default) => throw Unused(nameof(StreamAsync));

    public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages,
        CancellationToken cancellation = default, TimeSpan? timeout = null) => throw Unused(nameof(StreamAsync));

    public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages,
        DeliveryOptions? options, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        throw Unused(nameof(StreamAsync));
}
