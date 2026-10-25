using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Tests.Features.BreakGlass;

// FR-19, UC-11, SC-03, 計画 ADR-0036 D-08, [[IADR-0610]], [[IADR-0611]] (#2046 の 2 本目):
// **gRPC の閲覧の 2 口（`GetDocument`・`GetDocumentContent`）は、呼び出し元が `allow_break_glass` を立てたときだけ
// 緊急アクセスの判定を含め、読めたときは `granted_by_break_glass` を立てる**ことを実 Kestrel の h2c で固定する。
//
// 🔴 既定（立てない）は緊急アクセスを含めない ＝ 閲覧の監査ログも作らない（BFF が書き込み・版・本人の確認の前段で
//   文書を引いても、閲覧の記録が増えない）。陽性（立てれば読める）と対で置く。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcBreakGlassViewTests
{
    private const string ServiceSubject = "service-account-bff";
    private readonly GrpcKestrelFactory _factory;

    public GrpcBreakGlassViewTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata ServiceBearer() => new()
    {
        { "Authorization", $"Bearer {GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole])}" },
    };

    private DocumentRead.DocumentReadClient Grpc() => new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static UserContext As(string user) => new() { UserId = user, Action = "read" };

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private sealed record Scenario(string Owner, string Nominee, Guid DocumentId);

    // 所有者の個人資料 1 件（本文つき）と、管理者 1 人を指名したいま有効な発動記録。
    private async Task<Scenario> SeedAsync()
    {
        var owner = Unique("owner");
        var nominee = Unique("admin");
        _factory.ApproverRoles.States[nominee] = ApproverAdminState.Admin;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = Document.Create($"note-{Guid.NewGuid():N}", originalUri: null, contentType: null,
            attributes: new Dictionary<string, string>
            {
                ["confidentiality"] = "restricted",
                ["doc_scope"] = "private-note",
                ["owner"] = owner,
            });
        var uri = StorageUri.Build(RecordingObjectStorageClient.Bucket, $"normalized/{doc.Id:N}.md");
        doc.SetMarkdownUri(uri);
        db.Documents.Add(doc);
        db.PrivateNotes.Add(PrivateNote.Create(doc.Id, owner, $"notes/{doc.Id:N}.md", 0, null, DateTimeOffset.UtcNow));
        db.BreakGlassGrants.Add(BreakGlassGrant.Activate(doc.Id, owner, nominee, "端末を紛失したため確認を依頼する",
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
        ((RecordingObjectStorageClient)_factory.Services.GetRequiredService<IObjectStorageClient>()).Texts[uri] = "# 本文";
        return new Scenario(owner, nominee, doc.Id);
    }

    private async Task<List<string>> ViewTargetsAsync(Guid documentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.BreakGlassAuditEntries
            .Where(a => a.DocumentId == documentId && a.Operation == BreakGlassOperations.View)
            .OrderBy(a => a.OccurredAt).Select(a => a.ViewTarget!).ToListAsync(Ct);
    }

    private Task<GetDocumentResponse> GetAsync(string user, Guid id, bool allow) => Grpc().GetDocumentAsync(
        new GetDocumentRequest { Id = id.ToString("D"), User = As(user), AllowBreakGlass = allow },
        ServiceBearer(), cancellationToken: Ct).ResponseAsync;

    private Task<GetDocumentContentResponse> GetContentAsync(string user, Guid id, bool allow) =>
        Grpc().GetDocumentContentAsync(
            new GetDocumentContentRequest { DocumentId = id.ToString("D"), User = As(user), AllowBreakGlass = allow },
            ServiceBearer(), cancellationToken: Ct).ResponseAsync;

    [Fact]
    public async Task 立てなければ指名された管理者も読めず_閲覧の監査ログも作らない()
    {
        var s = await SeedAsync();

        (await GetAsync(s.Nominee, s.DocumentId, allow: false)).Found.Should().BeFalse();
        (await GetContentAsync(s.Nominee, s.DocumentId, allow: false)).Found.Should().BeFalse();
        (await ViewTargetsAsync(s.DocumentId)).Should().BeEmpty("前段の確認で閲覧の記録を増やさない");

        // 陽性対照: 同じ時点・同じ資料で、立てれば読める。
        var granted = await GetAsync(s.Nominee, s.DocumentId, allow: true);
        granted.Found.Should().BeTrue();
        granted.GrantedByBreakGlass.Should().BeTrue("通常の判定では読めず、緊急アクセスで読めた");
        granted.Document.SharedWith.Should().BeEmpty("緊急アクセスの応答に共有先の写しを載せない");
        (await ViewTargetsAsync(s.DocumentId)).Should().Equal([BreakGlassViewTargets.Document]);
    }

    [Fact]
    public async Task 本文も立てたときだけ読め_本文として1行記録する()
    {
        var s = await SeedAsync();

        var content = await GetContentAsync(s.Nominee, s.DocumentId, allow: true);

        content.Found.Should().BeTrue();
        content.Markdown.Should().Be("# 本文");
        (await ViewTargetsAsync(s.DocumentId)).Should().Equal([BreakGlassViewTargets.Content]);
    }

    [Fact]
    public async Task 所有者は通常の判定で読め_緊急アクセスの印は立たず_記録もしない()
    {
        var s = await SeedAsync();

        var own = await GetAsync(s.Owner, s.DocumentId, allow: true);

        own.Found.Should().BeTrue();
        own.GrantedByBreakGlass.Should().BeFalse();
        (await ViewTargetsAsync(s.DocumentId)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 指名されていない人は立てても読めない(bool otherIsAdmin)
    {
        var s = await SeedAsync();
        var other = Unique("other");
        if (otherIsAdmin) _factory.ApproverRoles.States[other] = ApproverAdminState.Admin;

        (await GetAsync(other, s.DocumentId, allow: true)).Found.Should().BeFalse();
        (await GetContentAsync(other, s.DocumentId, allow: true)).Found.Should().BeFalse();
        (await GetAsync(s.Nominee, s.DocumentId, allow: true)).Found.Should().BeTrue("陽性対照");
    }
}
