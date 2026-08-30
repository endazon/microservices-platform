using ConversionService.Infrastructure.Messaging;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ConversionService.Tests;

// FR-12 / ADR-0027（#441 E1）: 発行口の引数 → DocumentNormalized の写像を固定する。
//
// 🔴 **本ファイルは E1 で新設した。** 旧構成では `RawDocumentFetchedConsumerTests` が
// ハーネス経由で発行済みイベントそのものを見ていたため、写像も一緒に測れていた。
// E1 で発行を `IDocumentNormalizedPublisher` へ切り出し、ハンドラ側のテストは
// **発行口へ渡した引数**しか見なくなった —— そのままだと**写像が入れ替わっても誰も気づかない**。
// 抽象を挟んだことで生じた穴なので、抽象と同じ PR で塞ぐ。
//
// ⚠️ **辺 DocumentNormalized のトランスポートは MassTransit のままである**（E2 の射程）。
// よってここは MassTransit のテストハーネスで測るのが正しい。E2 でこの辺が動いたら、
// 本ファイルも一緒に動かすこと。
public class MassTransitDocumentNormalizedPublisherTests
{
    [Fact]
    public async Task 引数はDocumentNormalizedの各フィールドへ写される()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var publisher = new MassTransitDocumentNormalizedPublisher(harness.Bus);
            var documentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var sourceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

            await publisher.PublishNormalizedAsync(
                documentId, sourceId,
                title: "設計メモ",
                markdownUri: "storage://normalized/doc.md",
                assetUris: ["storage://normalized/assets/fig-1.png"],
                attributes: new Dictionary<string, string> { ["confidentiality"] = "internal" },
                tags: ["knowledge-mgmt"],
                ct: TestContext.Current.CancellationToken);

            (await harness.Published.Any<DocumentNormalized>(TestContext.Current.CancellationToken))
                .Should().BeTrue();
            var ev = harness.Published.Select<DocumentNormalized>(TestContext.Current.CancellationToken)
                .First().Context.Message;

            // 🔴 同じ型（Guid どうし・string どうし・コレクションどうし）の取り違えは
            // 「どれか 1 つ」を見るだけでは捕まらないので、**全フィールドを見る**。
            ev.DocumentId.Should().Be(documentId);
            ev.SourceId.Should().Be(sourceId);
            ev.Title.Should().Be("設計メモ");
            ev.MarkdownUri.Should().Be("storage://normalized/doc.md");
            ev.AssetUris.Should().ContainSingle().Which.Should().Be("storage://normalized/assets/fig-1.png");
            ev.Attributes.Should().ContainKey("confidentiality").WhoseValue.Should().Be("internal");
            ev.Tags.Should().ContainSingle().Which.Should().Be("knowledge-mgmt");
            ev.NormalizedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        }
        finally
        {
            await harness.Stop(TestContext.Current.CancellationToken);
        }
    }
}
