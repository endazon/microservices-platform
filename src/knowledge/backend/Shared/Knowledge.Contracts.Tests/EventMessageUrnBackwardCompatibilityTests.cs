using FluentAssertions;
using Knowledge.Contracts.Events;
using MassTransit;
using Xunit;

namespace Knowledge.Contracts.Tests;

// FR-14, IADR-0059, Issue #229: イベント契約を platform の Shared.Contracts から Knowledge.Contracts へ
// 移設しても、MassTransit のメッセージ URN が旧値（KnowledgePlatform.Shared.Contracts.Events:*）から
// 変わらないことを固定する後方互換の回帰ガード。URN は RabbitMQ の交換機名・ルーティングに直結するため、
// 変わると既発行メッセージやローリングデプロイ中の相互運用が壊れる（[MessageUrn] 固定の意図）。
public class EventMessageUrnBackwardCompatibilityTests
{
    [Theory]
    [InlineData(typeof(RawDocumentFetched), "urn:message:KnowledgePlatform.Shared.Contracts.Events:RawDocumentFetched")]
    [InlineData(typeof(DocumentNormalized), "urn:message:KnowledgePlatform.Shared.Contracts.Events:DocumentNormalized")]
    [InlineData(typeof(DocumentUpdated), "urn:message:KnowledgePlatform.Shared.Contracts.Events:DocumentUpdated")]
    [InlineData(typeof(DocumentDeleted), "urn:message:KnowledgePlatform.Shared.Contracts.Events:DocumentDeleted")]
    [InlineData(typeof(IngestionRequested), "urn:message:KnowledgePlatform.Shared.Contracts.Events:IngestionRequested")]
    [InlineData(typeof(IngestionCompleted), "urn:message:KnowledgePlatform.Shared.Contracts.Events:IngestionCompleted")]
    public void Event_message_urn_is_pinned_to_original_platform_namespace(Type eventType, string expectedUrn)
    {
        MessageUrn.ForType(eventType).ToString().Should().Be(expectedUrn);
    }
}
