using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace DocumentService.Tests.Features.BreakGlass;

// FR-19, UC-11, 計画 ADR-0036 D-08, [[IADR-0610]] (#2046): 緊急アクセスの試験の器。
//   - 時計は偽の時計（`FakeTimeProvider`）。15 分の失効は**壁時計を待たずに**時刻を進めて測る。
//   - 「いま platform-admin か」は利用者名簿の代役（`StubApproverRoleDirectory`。既定は NotAdmin）。
// 器はクラスで共有されるので、試験は利用者名・資料を毎回新しく作る（他の試験の行と混ざらない）。
public sealed class BreakGlassWebApplicationFactory : TestWebApplicationFactory
{
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));

    public StubApproverRoleDirectory AdminDirectory { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IApproverRoleDirectory>();
            services.AddSingleton<IApproverRoleDirectory>(AdminDirectory);
        });
    }
}

internal static class BreakGlassSeed
{
    internal static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    // 個人資料 1 件（台帳の行と文書の行）。作成の口は本文の投入などを伴うので、台帳へ直接入れる。
    internal static async Task<Document> PrivateNoteAsync(TestWebApplicationFactory factory, string owner,
        CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = Document.Create($"note-{Guid.NewGuid():N}", originalUri: null, contentType: null,
            attributes: new Dictionary<string, string>
            {
                ["confidentiality"] = "restricted",
                ["doc_scope"] = "private-note",
                ["owner"] = owner,
            });
        db.Documents.Add(doc);
        db.PrivateNotes.Add(PrivateNote.Create(doc.Id, owner, $"notes/{doc.Id:N}.md", 0, null,
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
        return doc;
    }
}
