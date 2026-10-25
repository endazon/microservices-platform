using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain.Ports;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocumentService.Tests.Features.Tags.Rename;

// FR-06, FR-09, SC-09, ADR-0027 §決定, IADR-0608 (#2168): 改名の 409 は**改名の保存の失敗だけ**に写す。
//
// 改名の保存と再発行は 1 回の確定（`CommitAsync`）に載る。409（「既に辞書にあります」）へ写す範囲を確定全体へ広げると、
// 同名の競合ではない失敗（コミット・Outbox の INSERT）まで 409 に化けて原因が隠れる。ここでは確定の**保存の後**で
// `DbUpdateException` を投げる発行口を差し込み、それが 409 にならない（例外のまま出る）ことを固定する。
// 🔴 写す範囲を確定全体へ戻す変異（`when (renameSaveFailed)` を外す）で赤になる。
// 保存の失敗（同名の一意制約違反）が 409 になることは、実 PostgreSQL の `TagDictionaryUniquenessTests` 等が測る。
[Trait("TestKind", "Integration")]
public class RenameTagConflictScopeTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 関数（保存 → 発行を積む）を最後まで走らせてから、コミットの失敗を模して DbUpdateException を投げる発行口。
    private sealed class CommitFailingPublisher : IDocumentUpdatedPublisher
    {
        public async Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct = default)
        {
            await saveAndPublish(ct);
            throw new DbUpdateException("コミット（Outbox の INSERT）の失敗（試験の注入）");
        }

        public Task PublishUpdatedAsync(Guid documentId, string title, string status, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags, DateTimeOffset updatedAt,
            string? contentFingerprint = null, bool hasBody = true,
            string? originalPath = null, string? dataSourceName = null,
            List<string>? sharedWith = null, long? sequence = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task 確定の保存の後の失敗は409にしない()
    {
        using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDocumentUpdatedPublisher>();
            services.AddScoped<IDocumentUpdatedPublisher, CommitFailingPublisher>();
        }));
        var client = failing.CreateClient();
        var created = await client.PostAsJsonAsync("/tags", new CreateTagRequest($"確定失敗-{Guid.NewGuid():N}"), Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var tag = (await created.Content.ReadFromJsonAsync<TagDto>(Ct))!;

        HttpStatusCode? status = null;
        Exception? thrown = null;
        try
        {
            status = (await client.PutAsJsonAsync($"/tags/{tag.Id}",
                new RenameTagRequest($"確定失敗の新名-{Guid.NewGuid():N}"), Ct)).StatusCode;
        }
        catch (Exception ex)
        {
            // TestServer は端点の未処理例外を呼び出し側へそのまま投げる。
            thrown = ex;
        }

        status.Should().NotBe(HttpStatusCode.Conflict, "同名の競合ではない失敗を「既に辞書にあります」に化けさせない");
        if (thrown is not null)
            thrown.Should().BeOfType<DbUpdateException>().Which.Message.Should().Contain("試験の注入");
        else
            status.Should().Be(HttpStatusCode.InternalServerError);
    }
}
