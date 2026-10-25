using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents;

// FR-06, FR-19, UC-03, SC-03, NFR-09, ADR-0029, ADR-0036 D-06, ADR-0075, ADR-0098 決定 1, ADR-0119 決定 3,
// [[IADR-0379]], [[IADR-0402]], [[IADR-0447]] (#1898):
// **文書の読み取りの gRPC 応答が共有先（`shared_with`）を運ぶ**ことを、実 Kestrel の h2c ポートで固定する。
//
// #1898 の回帰: `DocumentSummary` に `shared_with` が無く、写像も写していなかった。サーバは REST と同じ
// `DocumentReadUseCase` で `SharedWith` を埋めていたが、**線へ載る直前の 1 点で落ちていた**。
//
// 🔴 陽性（共有先には返り、`shared_with` が載る）と陰性（共有されていない相手には found=false）を対で置く。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcDocumentReadSharedWithTests
{
    private const string ServiceSubject = "service-account-bff";
    private readonly GrpcKestrelFactory _factory;

    public GrpcDocumentReadSharedWithTests(GrpcKestrelFactory factory)
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

    // 台帳へ直接入れる（作成の口は個人資料を拒むため）。共有は利用者の種別で与える。
    private async Task<Document> SeedAsync(Dictionary<string, string> attributes, params string[] sharedUsers)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = Document.Create($"shared-{Guid.NewGuid():N}", null, null, attributes);
        db.Documents.Add(doc);
        foreach (var user in sharedUsers)
            db.DocumentShares.Add(DocumentShare.Create(doc.Id, ShareSubjectType.User, user, grantedBy: "seed"));
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    private static Dictionary<string, string> PrivateNote(string owner) => new()
    {
        ["confidentiality"] = "restricted",
        ["doc_scope"] = "private-note",
        ["owner"] = owner,
    };

    private static Dictionary<string, string> Organization() => new()
    {
        ["confidentiality"] = "internal",
        ["doc_scope"] = "organization",
    };

    // AC-4（陽性）: 共有先の利用者は gRPC で個人資料を読め、応答に `shared_with` が載る（一覧・個別とも）。
    // 🔴 **変異試験**: 写像から `SharedWith` を落とすと `SharedWith` が空になって落ちる（#1898 の回帰そのもの）。
    [Fact]
    public async Task 共有先の利用者は_gRPC_で個人資料を読め_共有先が応答に載る()
    {
        var owner = Unique("owner");
        var colleague = Unique("colleague");
        var note = await SeedAsync(PrivateNote(owner), colleague);
        var id = note.Id.ToString("D");

        var get = await Grpc().GetDocumentAsync(
            new GetDocumentRequest { Id = id, User = As(colleague) }, ServiceBearer(), cancellationToken: Ct);
        get.Found.Should().BeTrue("陽性対照: 共有先には返る");
        get.Document.SharedWith.Should().Equal(colleague);

        var list = await Grpc().ListDocumentsAsync(
            new ListDocumentsRequest { User = As(colleague) }, ServiceBearer(), cancellationToken: Ct);
        list.Documents.Should().ContainSingle(d => d.Id == id)
            .Which.SharedWith.Should().Equal(colleague);
    }

    // AC-4（陰性）: 共有されていない相手には存在ごと見えない（一覧に居ない・個別は found=false）。
    [Fact]
    public async Task 共有されていない相手には_gRPC_で個人資料が見えない()
    {
        var note = await SeedAsync(PrivateNote(Unique("owner")), Unique("colleague"));
        var id = note.Id.ToString("D");
        var stranger = As(Unique("stranger"));

        var get = await Grpc().GetDocumentAsync(
            new GetDocumentRequest { Id = id, User = stranger }, ServiceBearer(), cancellationToken: Ct);
        get.Found.Should().BeFalse();

        var list = await Grpc().ListDocumentsAsync(
            new ListDocumentsRequest { User = stranger }, ServiceBearer(), cancellationToken: Ct);
        list.Documents.Should().NotContain(d => d.Id == id);
    }

    // 所有者への応答にも共有先が載る（所有者の SC-03 / SC-19 で共有先を表示・管理する入力）。複数件は順序どおり。
    [Fact]
    public async Task 所有者への_gRPC_応答に複数の共有先が載る()
    {
        var owner = Unique("owner");
        var a = Unique("a");
        var b = Unique("b");
        var note = await SeedAsync(PrivateNote(owner), a, b);

        var get = await Grpc().GetDocumentAsync(
            new GetDocumentRequest { Id = note.Id.ToString("D"), User = As(owner) }, ServiceBearer(), cancellationToken: Ct);

        get.Found.Should().BeTrue();
        DocumentReadGrpcMapping.ToDto(get.Document).SharedWith.Should().BeEquivalentTo([a, b]);
    }

    // AC-3: **REST と gRPC が同じ文書に同じ応答を返す**（共有先を含む）。同じ主体（機械）で測る。
    // 共有なしの文書も並べ、null（共有なし）が両経路で null であることを併せて固定する。
    [Fact]
    public async Task 共有先を含めて_REST_と_gRPC_の応答が一致する()
    {
        var shared = await SeedAsync(Organization(), Unique("x"), Unique("y"));
        var unshared = await SeedAsync(Organization());

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]));

        foreach (var doc in new[] { shared, unshared })
        {
            var rest = (await http.GetFromJsonAsync<DocumentDto>($"/documents/{doc.Id}", Ct))!;
            var grpc = await Grpc().GetDocumentAsync(
                new GetDocumentRequest { Id = doc.Id.ToString("D") }, ServiceBearer(), cancellationToken: Ct);

            DocumentReadGrpcMapping.ToDto(grpc.Document)
                .Should().BeEquivalentTo(rest, o => o.WithStrictOrdering(), "輸送を替えても応答の意味は変わらない");
        }

        // 陽性対照: 比べた文書の片方は実際に共有先を持っている（両方 null どうしの一致で緑にならない）。
        (await http.GetFromJsonAsync<DocumentDto>($"/documents/{shared.Id}", Ct))!
            .SharedWith.Should().HaveCount(2);
        (await http.GetFromJsonAsync<DocumentDto>($"/documents/{unshared.Id}", Ct))!
            .SharedWith.Should().BeNull();
    }
}
