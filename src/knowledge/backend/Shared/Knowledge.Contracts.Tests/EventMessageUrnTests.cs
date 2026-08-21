using AwesomeAssertions;
using Knowledge.Contracts.Events;
using MassTransit;
using Xunit;

namespace Knowledge.Contracts.Tests;

// FR-14, IADR-0059 / IADR-0062, Issue #227/#229: イベントの MassTransit URN を固定する回帰ガード。
// URN は各イベント型の名前空間（Knowledge.Contracts.Events）から導出される正準値で一貫させる
// （後方互換は持たせない＝旧 KnowledgePlatform.Shared.Contracts.Events への [MessageUrn] 固定は撤廃済み）。
// 送受信は同一の型定義（Knowledge.Contracts）を共有するため URN は自ずと一致する。本テストはその正準 URN が
// 意図せず変わらないことを固定する。
public class EventMessageUrnTests
{
    [Theory]
    [InlineData(typeof(RawDocumentFetched), "urn:message:Knowledge.Contracts.Events:RawDocumentFetched")]
    [InlineData(typeof(DocumentNormalized), "urn:message:Knowledge.Contracts.Events:DocumentNormalized")]
    [InlineData(typeof(DocumentUpdated), "urn:message:Knowledge.Contracts.Events:DocumentUpdated")]
    [InlineData(typeof(DocumentDeleted), "urn:message:Knowledge.Contracts.Events:DocumentDeleted")]
    [InlineData(typeof(IngestionRequested), "urn:message:Knowledge.Contracts.Events:IngestionRequested")]
    [InlineData(typeof(IngestionCompleted), "urn:message:Knowledge.Contracts.Events:IngestionCompleted")]
    public void Event_message_urn_derives_from_current_namespace(Type eventType, string expectedUrn)
    {
        MessageUrn.ForType(eventType).ToString().Should().Be(expectedUrn);
    }
}
