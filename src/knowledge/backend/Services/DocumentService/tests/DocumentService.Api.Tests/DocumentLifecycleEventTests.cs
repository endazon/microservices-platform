using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace DocumentService.Api.Tests;

// FR-06, UC-03, Issue #88: 文書のアーカイブ（非公開化）・削除が下流（Wiki.js 同期）へ伝播すること。
//   - POST /documents/{id}/archive → status=archived の DocumentUpdated を発行。
//   - DELETE /documents/{id} → DocumentDeleted を発行（現状イベント未発行の設計ギャップを解消）。
public class DocumentLifecycleEventTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client() => factory.CreateClient();

    private async Task<DocumentDto> CreateAsync(string title)
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new { title, attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" }, tags = new List<string>() });
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
    }

    // アーカイブ: status=archived になり、版履歴が伸び、status=archived の DocumentUpdated が発行される。
    [Fact]
    public async Task Archive_SetsStatusAndPublishesArchivedEvent()
    {
        var doc = await CreateAsync("アーカイブ対象");

        var resp = await Client().PostAsync($"/documents/{doc.Id}/archive", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var archived = await resp.Content.ReadFromJsonAsync<DocumentDto>();
        archived!.Status.Should().Be("archived");
        archived.Version.Should().Be(doc.Version + 1);

        // E3b: DocumentUpdated の発行は Wolverine（RecordingMessageBus で観測する）。
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        bus.PublishedOf<DocumentUpdated>().Should()
            .Contain(e => e.DocumentId == doc.Id && e.Status == "archived");
    }

    [Fact]
    public async Task Archive_Returns404_WhenDocumentMissing()
    {
        var resp = await Client().PostAsync($"/documents/{Guid.NewGuid()}/archive", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 削除: 204 とともに DocumentDeleted を発行し、下流（Wiki.js）の実体撤去を可能にする。
    [Fact]
    public async Task Delete_PublishesDocumentDeleted()
    {
        var doc = await CreateAsync("削除対象");

        var resp = await Client().DeleteAsync($"/documents/{doc.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        // E3a: DocumentDeleted の発行は Wolverine（RecordingMessageBus で観測する）。
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        bus.PublishedOf<DocumentDeleted>().Should().Contain(e => e.DocumentId == doc.Id);
    }

    // 存在しない文書の削除は 404 のままイベントを発行しない。
    [Fact]
    public async Task Delete_DoesNotPublish_WhenDocumentMissing()
    {
        var missingId = Guid.NewGuid();
        var resp = await Client().DeleteAsync($"/documents/{missingId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // E3a: DocumentDeleted の発行は Wolverine（RecordingMessageBus で観測する）。
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        bus.PublishedOf<DocumentDeleted>().Should().NotContain(e => e.DocumentId == missingId);
    }
}
