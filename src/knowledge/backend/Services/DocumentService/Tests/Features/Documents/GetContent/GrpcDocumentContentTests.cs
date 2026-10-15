using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.GetContent;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Tests.Features.Documents.GetContent;

// FR-06, UC-03, SC-03, ADR-0014, ADR-0029, ADR-0119 決定 3, [[IADR-0540]], [[IADR-0544]] (#1905):
// 本文の読み取りの gRPC 面（`DocumentRead.GetDocumentContent`）を**実 Kestrel の h2c ポート**で往復して固定する。
//
// 受け入れ基準（作業仕様書 20261015_1905_bff-document-content の写像）:
//   AC-1 本文を返す / AC-2 本文の実体が無い（NoSuchKey）は「無い」の応答（案内文にしない）/ AC-3 権限外は found=false /
//   AC-4 上限を超える本文は返さない（切り詰めない）/ AC-5 「引けなかった」（ストレージ未構成・一時障害）は status。
// 陽性（AC-1）と陰性（AC-2〜5）を同じ器で対にする —— 「本文が返らない」だけでは器が壊れているのか区別できない。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcDocumentContentTests : IDisposable
{
    private const string ServiceSubject = "service-account-bff";
    private readonly GrpcKestrelFactory _factory;

    public GrpcDocumentContentTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    public void Dispose() => Storage.GetTextThrows = null;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RecordingObjectStorageClient Storage =>
        (RecordingObjectStorageClient)_factory.Services.GetRequiredService<IObjectStorageClient>();

    private static Metadata ServiceBearer() => new()
    {
        { "Authorization", $"Bearer {GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole])}" },
    };

    // 受信上限は BFF の D-1 チャネルの既定（64 MiB。`DocumentReadGrpcClient.DefaultMaxReceiveMessageSize`）に揃える。
    // grpc-dotnet の既定 4 MB のままだと、上限ちょうどの本文（16 MiB）を受け取れない。
    private const int BffReceiveLimit = 64 * 1024 * 1024;

    private DocumentRead.DocumentReadClient Grpc() => new(GrpcChannel.ForAddress(
        _factory.GrpcAddress, new GrpcChannelOptions { MaxReceiveMessageSize = BffReceiveLimit }));

    private static UserContext As(string user) => new() { UserId = user, Action = "read" };

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    // 台帳へ入れ、本文があれば器のストレージへ置く（`CanResolve` が真になる）。
    private async Task<(Document Doc, string? Uri)> SeedAsync(
        string? body, Dictionary<string, string>? attributes = null, string? markdownUri = "storage")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = Document.Create($"content-{Guid.NewGuid():N}", null, null,
            attributes ?? new Dictionary<string, string> { ["confidentiality"] = "internal" });
        string? uri = markdownUri == "storage"
            ? StorageUri.Build(RecordingObjectStorageClient.Bucket, $"normalized/{doc.Id:N}.md")
            : markdownUri;
        if (uri is not null)
            doc.SetMarkdownUri(uri);
        db.Documents.Add(doc);
        await db.SaveChangesAsync(Ct);
        if (body is not null && uri is not null)
            Storage.Texts[uri] = body;
        return (doc, uri);
    }

    private Task<GetDocumentContentResponse> ReadAsync(Guid id, UserContext? user = null) =>
        Grpc().GetDocumentContentAsync(
            new GetDocumentContentRequest { DocumentId = id.ToString("D"), User = user },
            ServiceBearer(), cancellationToken: Ct).ResponseAsync;

    // AC-1（陽性）: 本文をそのまま返す（案内文ではない・1 文字も変えない）。
    [Fact]
    public async Task 本文を返す()
    {
        const string body = "# 日次報告\n\n本文の 1 行目。\n日本語と記号 <>&\" を含む。";
        var (doc, _) = await SeedAsync(body);

        var resp = await ReadAsync(doc.Id);

        resp.Found.Should().BeTrue();
        resp.State.Should().Be(DocumentContentState.Available);
        resp.Markdown.Should().Be(body);
    }

    // AC-2: オブジェクトが無い（S3 の NoSuchKey）は MISSING。🔴 案内文の本文にしない（#1905 の本質）。
    // SDK の専用の型と、汎用の型＋エラーコードの両方を拾う（[[IADR-0540]] 決定 1 と同じ判定）。
    [Theory]
    [InlineData("type")]
    [InlineData("code")]
    public async Task 本文のオブジェクトが無いときはMISSINGで案内文を返さない(string shape)
    {
        var (doc, uri) = await SeedAsync("この本文は読まれない");
        Storage.GetTextThrows = u => u == uri
            ? shape == "type"
                ? new NoSuchKeyException("The specified key does not exist.")
                : new AmazonS3Exception("not found") { ErrorCode = "NoSuchKey", StatusCode = HttpStatusCode.NotFound }
            : null;

        var resp = await ReadAsync(doc.Id);

        resp.Found.Should().BeTrue("文書は読める。無いのは本文の実体である");
        resp.State.Should().Be(DocumentContentState.Missing);
        resp.Markdown.Should().BeEmpty("案内文を本文として返さない");
    }

    // AC-5: バケットが無い（NoSuchBucket・同じ 404）は「無い」ではなく「引けなかった」（status）。
    // 🔴 状態コード 404 だけで判定すると構成の不調が「本文が無い」に化ける（[[IADR-0540]] 決定 1）。
    [Fact]
    public async Task バケットが無いのはMISSINGではなくstatusで返す()
    {
        var (doc, uri) = await SeedAsync("この本文は読まれない");
        Storage.GetTextThrows = u => u == uri
            ? new AmazonS3Exception("no bucket") { ErrorCode = "NoSuchBucket", StatusCode = HttpStatusCode.NotFound }
            : null;

        var act = () => ReadAsync(doc.Id);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().NotBe(StatusCode.OK);
    }

    // AC-5: ストレージが構成されていない（`CanResolve` が偽）は FAILED_PRECONDITION。案内文へ縮退しない。
    [Fact]
    public async Task ストレージが構成されていなければFAILED_PRECONDITION()
    {
        var (doc, _) = await SeedAsync(body: null); // 器のストレージに置かない → CanResolve が偽

        var act = () => ReadAsync(doc.Id);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    // AC-2: 本文の参照が storage:// でない（未設定・外部 URL）は NOT_STORED。外部 URL を取りに行かない。
    [Theory]
    [InlineData(null)]
    [InlineData("https://example.invalid/original.md")]
    public async Task 本文の参照がストレージでなければNOT_STORED(string? markdownUri)
    {
        var (doc, _) = await SeedAsync(body: null, markdownUri: markdownUri);

        var resp = await ReadAsync(doc.Id);

        resp.Found.Should().BeTrue();
        resp.State.Should().Be(DocumentContentState.NotStored);
        resp.Markdown.Should().BeEmpty();
    }

    // AC-4: 上限を超える本文は TOO_LARGE で、本文を返さない（切り詰めない）。大きさと上限を返す。
    // 境界: ちょうど上限は返す（陽性対照）。
    [Fact]
    public async Task 上限を超える本文は返さず大きさと上限を返す()
    {
        var (over, _) = await SeedAsync(new string('a', (int)DocumentContentUseCase.MaxBodyBytes + 1));
        var (atLimit, _) = await SeedAsync(new string('a', (int)DocumentContentUseCase.MaxBodyBytes));

        var tooLarge = await ReadAsync(over.Id);
        var fits = await ReadAsync(atLimit.Id);

        tooLarge.State.Should().Be(DocumentContentState.TooLarge);
        tooLarge.Markdown.Should().BeEmpty("切り詰めた本文を返さない");
        tooLarge.SizeBytes.Should().Be(DocumentContentUseCase.MaxBodyBytes + 1);
        tooLarge.LimitBytes.Should().Be(DocumentContentUseCase.MaxBodyBytes);
        fits.State.Should().Be(DocumentContentState.Available, "陽性対照: ちょうど上限は返す");
        fits.Markdown.Length.Should().Be((int)DocumentContentUseCase.MaxBodyBytes);
    }

    // 上限はバイトで数える（文字数ではない）。日本語は 1 文字 3 バイトなので、文字数が上限の 1/3 を超えれば超過になる。
    [Fact]
    public async Task 上限はUTF8のバイトで数える()
    {
        var chars = (int)(DocumentContentUseCase.MaxBodyBytes / 3) + 1;
        var (doc, _) = await SeedAsync(new string('あ', chars));

        var resp = await ReadAsync(doc.Id);

        resp.State.Should().Be(DocumentContentState.TooLarge);
        resp.SizeBytes.Should().Be(chars * 3L);
    }

    // AC-3: 権限外（他人の共有していない個人資料）は found=false（`GetDocument` と同じ判定。存在秘匿）。
    // 陽性対照: 所有者には本文が返る。
    [Fact]
    public async Task 他人の個人資料の本文は返らず所有者には返る()
    {
        var owner = Unique("owner");
        var (note, _) = await SeedAsync("# 個人資料の本文", new Dictionary<string, string>
        {
            ["confidentiality"] = "restricted",
            ["doc_scope"] = "private-note",
            ["owner"] = owner,
        });

        var stranger = await ReadAsync(note.Id, As(Unique("stranger")));
        var machine = await ReadAsync(note.Id);
        var self = await ReadAsync(note.Id, As(owner));

        stranger.Found.Should().BeFalse("他人には「無い」と同じ");
        stranger.Markdown.Should().BeEmpty();
        machine.Found.Should().BeFalse("機械の主体には個人資料は返らない（ADR-0034 決定 9）");
        self.Found.Should().BeTrue("陽性対照: 所有者には返る");
        self.Markdown.Should().Be("# 個人資料の本文");
    }

    // 台帳に無い文書は found=false（NOT_FOUND を投げない）。綴り誤りは INVALID_ARGUMENT。
    [Fact]
    public async Task 無い文書はfound_falseで綴り誤りはINVALID_ARGUMENT()
    {
        (await ReadAsync(Guid.NewGuid())).Found.Should().BeFalse();

        var act = () => Grpc().GetDocumentContentAsync(
            new GetDocumentContentRequest { DocumentId = "not-a-guid" }, ServiceBearer(), cancellationToken: Ct).ResponseAsync;
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // s2s の門（ServiceCaller）はこの rpc にも効く（面の属性で掛かる）。トークンの無い呼び出しは拒否。
    [Fact]
    public async Task トークンの無い呼び出しは拒否される()
    {
        var (doc, _) = await SeedAsync("# 本文");

        var act = () => Grpc().GetDocumentContentAsync(
            new GetDocumentContentRequest { DocumentId = doc.Id.ToString("D") }, cancellationToken: Ct).ResponseAsync;

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }
}
