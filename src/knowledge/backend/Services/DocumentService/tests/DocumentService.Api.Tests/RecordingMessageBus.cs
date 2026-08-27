using System.Collections.Concurrent;
using Wolverine;

namespace DocumentService.Api.Tests;

// ADR-0027（E3a。写しの元は #441 E1 の DataSourceService.Api.Tests）: 発行されたメッセージを
// 記録するだけの `IMessageBus` テストダブル。**3 つ目の複製である**（DataSource / Conversion に続く。
// 各テストプロジェクトは自己完結で共有ヘルパを持たないため、共通化は見送り複製を受容する ——
// 判断の記録は作業仕様書 20260828_edge-e3a-document-deleted.md §テスト）。
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
