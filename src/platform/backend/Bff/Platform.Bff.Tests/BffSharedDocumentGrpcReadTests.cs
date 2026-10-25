using AwesomeAssertions;
using Google.Protobuf;
using Grpc.Core;
using Knowledge.Bff.Endpoints.Documents;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace Platform.Bff.Tests;

// FR-06, FR-19, UC-03, SC-03, NFR-09, ADR-0029, ADR-0036 D-06・D-08, ADR-0075, ADR-0098 決定 1・フォローアップ 5,
// [[IADR-0402]], [[IADR-0447]] 決定 4, [[IADR-0450]] (#1898):
// **BFF の単体判定（共有先ベースの分岐）と共有先の表示が、文書の読み取りを gRPC で受ける構成でも効く。**
//
// #1898 の回帰: gRPC の `DocumentSummary` が `shared_with` を運ばず、BFF が受ける `DocumentDto.SharedWith` が
// 常に null だった。当時の REST 経路の同じ判定は `BffSharedDocumentReadTests` が固定していたが、**gRPC 経路だけが
// 共有された相手を 404 へ倒し、所有者への応答から `sharedWith` を消していた**（［2026-10-10 / [[IADR-0533]]］REST の並走は撤去した）。
//
// スタブは文書サービスの応答を `DocumentReadGrpcMapping.ToProto` で組み、**バイト列へ直列化して読み戻してから**
// 返す（線上で落ちる項目を、生成コードの既定値まで含めて再現する）。
// 🔴 陽性（共有先には 200）と陰性（共有先外・静的分岐では 404）を対で置く。
public class BffSharedDocumentGrpcReadTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffSharedDocumentGrpcReadTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        _factory.ScopeBranches = null;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
    }

    private static readonly Guid NoteId = Guid.Parse("dddddddd-1898-4898-8898-dddddddddddd");

    // 個人資料の実物と同じ属性（`PrivateNoteDefaults` と同じ 3 つ）＋ 共有先。
    private static DocumentDto SharedNote(List<string>? sharedWith) => new()
    {
        Id = NoteId,
        Title = "共有された個人メモ（gRPC）",
        Status = "published",
        MarkdownUri = "storage://bucket/note.md",
        Version = 1,
        Attributes = new Dictionary<string, string>
        {
            ["doc_scope"] = "private-note",
            ["owner"] = "someone-else",
            ["confidentiality"] = "restricted",
        },
        Tags = [],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        SharedWith = sharedWith,
    };

    private static AccessScopeBranch SharedWithBranch(params string[] subjects)
        => new("共有先ベース",
            [new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, [.. subjects])]);

    private static AccessScopeBranch OwnerBranch(string boundOwner)
        => new("owner", [new AttributeFilter("owner", [boundOwner])]);

    private static AccessScopeBranch StaticBranch()
        => new("静的属性ベース",
            [new AttributeFilter("confidentiality", ["public", "internal", "confidential", "restricted"])]);

    private Task<HttpResponseMessage> GetNoteOverGrpcAsync()
        => GrpcClient().GetAsync($"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);

    // AC-4（陽性）: 共有先に自分が含まれる個人資料は、gRPC 経路でも `shared_with` 分岐で読める。
    // 🔴 **変異試験**: 写像から `SharedWith` を落とすと（#1898 の回帰）、像に `shared_with` が載らず 404 になる。
    [Fact]
    public async Task 共有された相手は_gRPC_経路でも個人資料を開ける()
    {
        _factory.ScopeBranches = [SharedWithBranch("alice")];
        _factory.StubDocument = SharedNote(["alice"]);

        (await GetNoteOverGrpcAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 集合値は交差で判定する（共有先が複数のとき、どれか 1 つが交われば読める）。
    [Theory]
    [InlineData("alice", HttpStatusCode.OK)]
    [InlineData("g-1", HttpStatusCode.OK)]
    [InlineData("g-2", HttpStatusCode.NotFound)]
    public async Task 共有先が複数のとき_gRPC_経路でも交差で判定する(string subject, HttpStatusCode expected)
    {
        _factory.ScopeBranches = [SharedWithBranch(subject)];
        _factory.StubDocument = SharedNote(["alice", "g-1"]);

        (await GetNoteOverGrpcAsync()).StatusCode.Should().Be(expected);
    }

    // AC-4（陰性）: 共有先が自分と交わらなければ 404（存在秘匿）。
    [Fact]
    public async Task 共有されていない相手は_gRPC_経路で個人資料を開けない()
    {
        _factory.ScopeBranches = [SharedWithBranch("alice")];
        _factory.StubDocument = SharedNote(["bob"]);

        (await GetNoteOverGrpcAsync()).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 🔴 陰性対照（ADR-0036 D-08）: 静的属性の分岐では、共有された個人資料も gRPC 経路で読めない。
    [Fact]
    public async Task 静的属性の分岐では_gRPC_経路でも共有された個人資料を読めない()
    {
        _factory.ScopeBranches = [StaticBranch()];
        _factory.StubDocument = SharedNote(["alice"]);

        (await GetNoteOverGrpcAsync()).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 共有先の表示: 所有者（`owner` 分岐で許可）への gRPC 経路の応答には `sharedWith` が順序どおり載る。
    // 🔴 #1898 の回帰では、ここが常に null（共有先の表示と管理が壊れる）だった。
    [Fact]
    public async Task 所有者への_gRPC_経路の応答に共有先の写しが載る()
    {
        _factory.ScopeBranches = [OwnerBranch("someone-else")];
        _factory.StubDocument = SharedNote(["alice", "11111111-1111-1111-1111-111111111111"]);

        var body = await GrpcClient().GetFromJsonAsync<DocumentDto>(
            $"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);

        body!.SharedWith.Should().Equal("alice", "11111111-1111-1111-1111-111111111111");
    }

    // 陰性: 共有先ベースの分岐だけで読めた相手には、gRPC 経路でも `sharedWith` を項目ごと落とす（[[IADR-0450]]）。
    [Fact]
    public async Task 共有された相手への_gRPC_経路の応答には共有先の写しを返さない()
    {
        _factory.ScopeBranches = [SharedWithBranch("alice")];
        _factory.StubDocument = SharedNote(["alice", "bob"]);

        var body = await GrpcClient().GetFromJsonAsync<DocumentDto>(
            $"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);

        body!.SharedWith.Should().BeNull();
        body.Id.Should().Be(NoteId, "陽性対照: 読めること自体は変わらない");
    }

    // ［2026-10-10 / #1255・[[IADR-0533]]］AC-3「REST 経路と gRPC 経路が同じ応答を返す」の試験は外した。
    // REST の並走を撤去したので比べる相手が無い（器の既定のクライアントも gRPC の橋渡しになった）。
    // 共有先が gRPC で運ばれることは上の `所有者への_gRPC_経路の応答に共有先の写しが載る` が固定している。

    // gRPC クライアントを DI へ差し込んだテスト用ホスト（`BffDocumentGrpcTests` と同じ作法。実チャネルは張らない）。
    private HttpClient GrpcClient() =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton(new DocumentReadGrpcClient(
                new Pb.DocumentRead.DocumentReadClient(new WireRoundTripInvoker(_factory))))))
            .CreateClient();

    // 器の HTTP スタブと同じ状態（`StubDocument` / `StubDocumentList`）から応答を組み、**線上を一度通す**。
    // 本ファイルで要るのは文書の 2 口だけである（版の 2 口は判定の像が同じ `AuthzView` を通るので省く）。
    private sealed class WireRoundTripInvoker(BffTestFactory factory) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            IMessage response = method.Name switch
            {
                nameof(Pb.DocumentRead.DocumentReadClient.GetDocument) => GetDocument((Pb.GetDocumentRequest)(object)request!),
                nameof(Pb.DocumentRead.DocumentReadClient.ListDocuments) => ListDocuments(),
                _ => throw new NotSupportedException(method.Name),
            };
            // 線上を通す（直列化 → 読み戻し）。
            var onWire = (TResponse)response.Descriptor.Parser.ParseFrom(response.ToByteArray());
            return new AsyncUnaryCall<TResponse>(Task.FromResult(onWire), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        private Pb.GetDocumentResponse GetDocument(Pb.GetDocumentRequest request)
        {
            var doc = factory.StubDocumentList.Concat([factory.StubDocument])
                .FirstOrDefault(d => d.Id == Guid.Parse(request.Id));
            return doc is null
                ? new Pb.GetDocumentResponse { Found = false }
                : new Pb.GetDocumentResponse { Found = true, Document = DocumentReadGrpcMapping.ToProto(doc) };
        }

        private Pb.ListDocumentsResponse ListDocuments()
        {
            var resp = new Pb.ListDocumentsResponse();
            resp.Documents.AddRange(factory.StubDocumentList.Select(DocumentReadGrpcMapping.ToProto));
            return resp;
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
    }
}
