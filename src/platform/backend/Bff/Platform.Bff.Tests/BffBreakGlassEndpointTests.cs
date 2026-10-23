using AwesomeAssertions;
using Google.Protobuf;
using Grpc.Core;
using Knowledge.Bff.Endpoints.Documents;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace Platform.Bff.Tests;

// FR-19, UC-11, SC-19, SC-03, 計画 ADR-0036 D-08, [[IADR-0610]], [[IADR-0611]] (#2046 の 2 本目):
// **緊急アクセスの BFF の口の認可**と、**SC-03 の詳細・本文が後段の判定をそのまま通す**ことを固定する
// （planning#841 の裁定 2026-10-23: 本人同意型の管理者の閲覧）。
//
// 後段（文書サービス）は本物の判定の形で再現する（`FakeDocumentService`）:
//   - REST の発動・終了・状態は**所有者だけ**（他人は 404）。監査ログは管理者は全件・他は本人の行だけ。
//   - gRPC の `GetDocument` / `GetDocumentContent` は、通常の判定（組織文書・所有者）で読めなければ、
//     **`allow_break_glass` が立っているときだけ**「指名 × 有効 × いま管理者」を見て許し、閲覧を 1 行記録する。
//
// 🔴 陰性（404 / 403）は必ず陽性対照と対で置く（「常に 404」の実装でも陰性だけは緑になる）。
// 🔴 **二重の守り**: BFF の本人の確認は、後段が誤って誰にでも許す（`BackendAcceptsAnyone`）状態でも他人を止めることで測る。
//   後段の判定へ委ねていることは、BFF の確認を通った要求の 404 / 409 / 400 / 503 が本文ごと届くことで測る。
public class BffBreakGlassEndpointTests : IClassFixture<BffTestFactory>, IDisposable
{
    private const string Owner = "alice";
    private const string Stranger = "carol";        // 管理者でない第三者
    private const string OtherAdmin = "dave";       // 指名されていない管理者
    private const string Nominee = "eve";           // 指名された管理者
    private const string Machine = "service-account-ops";

    private static readonly Guid Note = Guid.Parse("b1a55b1a-2046-4046-8046-000000000001");
    private static readonly Guid OtherNote = Guid.Parse("b1a55b1a-2046-4046-8046-000000000002");
    private static readonly Guid OrgDoc = Guid.Parse("b1a55b1a-2046-4046-8046-000000000003");

    private readonly BffTestFactory _factory;
    private readonly FakeDocumentService _backend = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;

    public BffBreakGlassEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        // 管理者の ABAC は静的属性の分岐しか持たない（計画のとおり）。BFF の `IsReadable` はこれで個人資料を通さない。
        _factory.ScopeBranches = [new AccessScopeBranch("静的属性ベース",
            [new AttributeFilter("confidentiality", ["public", "internal", "confidential", "restricted"])])];
        _factory.WriteScopeGranted = true;
        _factory.WriteScopeFilters = [];
        _host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton(new DocumentReadGrpcClient(new Pb.DocumentRead.DocumentReadClient(_backend)));
            s.AddHttpClient("DocumentService").ConfigurePrimaryHttpMessageHandler(() => _backend.Rest);
        }));
    }

    public void Dispose() => _host.Dispose();

    private HttpClient As(string user, string roles = "viewer", bool machine = false)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UsernameHeader, user);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        if (machine)
            client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, user["service-account-".Length..]);
        // 本番では BFF セッションのアクセストークンが同じ位置に入る（後段は主体をここからしか採らない）。
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user);
        return client;
    }

    private HttpClient AsAdmin(string user) => As(user, "platform-admin");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Path(Guid id, string suffix = "") => $"/bff/private-notes/{id}/break-glass{suffix}";

    private static readonly ActivateBreakGlassRequest Request = new("端末を紛失したため確認を依頼する", Nominee);

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path)
        => client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(Request) : null,
        }, Ct);

    public static TheoryData<string, string> OwnerRoutes => new()
    {
        { "POST", "" },
        { "POST", "/end" },
        { "GET", "" },
    };

    // ── 1. 無認証は 401（`/bff/*` の不変条件）──────────────────────────────

    [Theory]
    [InlineData("POST", "/bff/private-notes/b1a55b1a-2046-4046-8046-000000000001/break-glass")]
    [InlineData("POST", "/bff/private-notes/b1a55b1a-2046-4046-8046-000000000001/break-glass/end")]
    [InlineData("GET", "/bff/private-notes/b1a55b1a-2046-4046-8046-000000000001/break-glass")]
    [InlineData("GET", "/bff/break-glass/audit")]
    public async Task 無認証は401で後段へ届かない(string method, string path)
    {
        var client = As(Owner);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        (await Send(client, method, path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _backend.RestCalls.Should().BeEmpty();
    }

    // ── 2. 所有者本人は発動・終了・状態を中継でき、資格情報が後段へ届く（陽性）──────────────

    [Theory]
    [MemberData(nameof(OwnerRoutes))]
    public async Task 所有者本人は中継され_本人の資格情報が後段へ届く(string method, string suffix)
    {
        var res = await Send(As(Owner), method, Path(Note, suffix));

        res.StatusCode.Should().Be(method == "POST" && suffix == "" ? HttpStatusCode.Created : HttpStatusCode.OK,
            await res.Content.ReadAsStringAsync(Ct));
        _backend.RestCalls.Should().ContainSingle()
            .Which.Should().Be((method, $"/private-notes/{Note}/break-glass{suffix}", $"Bearer {Owner}"));
    }

    [Fact]
    public async Task 発動の要求の本文は詰め替えずに後段へ届く()
    {
        await Send(As(Owner), "POST", Path(Note));

        _backend.LastActivateRequest.Should().Be(Request);
    }

    // ── 3. 第三者・指名外の管理者・指名された管理者・機械は、発動・終了・状態のいずれも 404（後段へ送らない）──

    [Theory]
    [MemberData(nameof(OwnerRoutes))]
    public async Task 第三者は404で後段へ送らない_後段が誤って許す状態でも止まる(string method, string suffix)
    {
        _backend.BackendAcceptsAnyone = true; // 後段の判定が壊れた状態（BFF の確認だけで止まることを測る）

        (await Send(As(Stranger), method, Path(Note, suffix))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(As(Stranger), method, Path(OtherNote, suffix))).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "他人の資料も無い資料と同じ（存在秘匿）");
        _backend.RestCalls.Should().BeEmpty();

        // 陽性対照: 同じ状態で所有者本人は届く。
        (await Send(As(Owner), method, Path(Note, suffix))).IsSuccessStatusCode.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(OwnerRoutes))]
    public async Task 指名されていない管理者は404で後段へ送らない(string method, string suffix)
    {
        _backend.BackendAcceptsAnyone = true;

        (await Send(AsAdmin(OtherAdmin), method, Path(Note, suffix))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _backend.RestCalls.Should().BeEmpty("管理者ロールでは開かない（D-08: 第三者の発動経路を設けない）");
    }

    [Theory]
    [MemberData(nameof(OwnerRoutes))]
    public async Task 指名された管理者も本人の口は404で_閲覧の記録も増えない(string method, string suffix)
    {
        _backend.BackendAcceptsAnyone = true;
        _backend.Grant(Note, Nominee);

        (await Send(AsAdmin(Nominee), method, Path(Note, suffix))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _backend.RestCalls.Should().BeEmpty("終える・延ばすのは本人だけ");
        _backend.ViewAudit.Should().BeEmpty("本人の確認は緊急アクセスを含めない形で引く");
        _backend.GrpcCalls.Should().OnlyContain(c => !c.AllowBreakGlass);
    }

    [Theory]
    [MemberData(nameof(OwnerRoutes))]
    public async Task 機械の主体は所有者を名乗っても404で後段へ送らない(string method, string suffix)
    {
        _backend.BackendAcceptsAnyone = true;
        var machine = As(Machine, "platform-admin", machine: true);
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Owner);

        (await Send(machine, method, Path(Note, suffix))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _backend.RestCalls.Should().BeEmpty();
        _backend.GrpcCalls.Should().BeEmpty("機械は本人の確認に進まない");
    }

    [Fact]
    public async Task 組織文書は本人の口の対象ではなく404()
    {
        _backend.BackendAcceptsAnyone = true;

        (await Send(As(Owner), "POST", Path(OrgDoc))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _backend.RestCalls.Should().BeEmpty();
    }

    // ── 4. 後段の判定に委ねる（BFF の確認を通った要求は後段の応答が本文ごと届く）──────────────

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task 後段の拒否は本文ごと透過する(HttpStatusCode status)
    {
        _backend.ForcedStatus = status;

        var res = await Send(As(Owner), "POST", Path(Note));

        res.StatusCode.Should().Be(status);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("downstream-decision", "後段の判定の本文を詰め替えない");
    }

    [Fact]
    public async Task 本人の確認で後段に届かなければ502で_無いことにしない()
    {
        _backend.GrpcUnavailable = true;

        (await Send(As(Owner), "GET", Path(Note))).StatusCode.Should().Be(HttpStatusCode.BadGateway);
        _backend.RestCalls.Should().BeEmpty();
    }

    // ── 5. 発動だけ write スコープで前段を絞る（終了・状態は止めない）─────────────────────

    [Fact]
    public async Task 書き込みのポリシーが無ければ発動は403_終了と状態は通る()
    {
        _factory.WriteScopeGranted = false;
        try
        {
            (await Send(As(Owner), "POST", Path(Note))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            _backend.RestCalls.Should().BeEmpty();

            (await Send(As(Owner), "POST", Path(Note, "/end"))).StatusCode.Should().Be(HttpStatusCode.OK,
                "終了は露出を縮める向き。ポリシーの有無で止めない");
            (await Send(As(Owner), "GET", Path(Note))).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            _factory.WriteScopeGranted = true;
        }
    }

    // ── 6. 監査ログ: 人の主体だけ。誰の行まで読めるかは後段が決める ─────────────────────

    [Fact]
    public async Task 監査ログは管理者には全件_本人には自分の行だけが届く()
    {
        _backend.AuditRows.Add(Row(Note, Owner));
        _backend.AuditRows.Add(Row(OtherNote, "bob"));

        var admin = await AsAdmin(OtherAdmin).GetFromJsonAsync<List<BreakGlassAuditEntryDto>>("/bff/break-glass/audit", Ct);
        var own = await As(Owner).GetFromJsonAsync<List<BreakGlassAuditEntryDto>>("/bff/break-glass/audit", Ct);
        var stranger = await As(Stranger).GetFromJsonAsync<List<BreakGlassAuditEntryDto>>("/bff/break-glass/audit", Ct);

        admin!.Select(r => r.DocumentId).Should().BeEquivalentTo([Note, OtherNote]);
        own!.Select(r => r.DocumentId).Should().Equal(Note);
        stranger.Should().BeEmpty();
        _backend.RestCalls.Select(c => c.Authorization).Should().Equal(
            $"Bearer {OtherAdmin}", $"Bearer {Owner}", $"Bearer {Stranger}");
    }

    [Fact]
    public async Task 監査ログの絞り込みは型で受けて組み立て直して届く()
    {
        await As(Owner).GetAsync($"/bff/break-glass/audit?documentId={Note}&limit=20&owner=bob", Ct);

        _backend.RestCalls.Should().ContainSingle()
            .Which.Path.Should().Be($"/break-glass/audit?documentId={Note:D}&limit=20", "知らないクエリは後段へ渡さない");
        (await As(Owner).GetAsync("/bff/break-glass/audit?documentId=not-a-guid", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 機械の主体は監査ログを読めない()
    {
        (await As(Machine, "platform-admin", machine: true).GetAsync("/bff/break-glass/audit", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _backend.RestCalls.Should().BeEmpty();
        (await AsAdmin(OtherAdmin).GetAsync("/bff/break-glass/audit", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "陽性対照");
    }

    // ── 7. SC-03 の詳細・本文は後段の判定（指名 × 有効 × いま管理者）をそのまま通す ────────────

    [Fact]
    public async Task 指名された管理者は有効な間_SC03の詳細と本文を開ける()
    {
        _backend.Grant(Note, Nominee);
        var admin = AsAdmin(Nominee);

        var detail = await admin.GetAsync($"/bff/documents/{Note}", Ct);
        var content = await admin.GetAsync($"/bff/documents/{Note}/content", Ct);

        detail.StatusCode.Should().Be(HttpStatusCode.OK, "BFF の ABAC（静的属性の分岐）では読めないが、後段が許した");
        (await detail.Content.ReadFromJsonAsync<DocumentDto>(Ct))!.Id.Should().Be(Note);
        content.StatusCode.Should().Be(HttpStatusCode.OK);
        (await content.Content.ReadFromJsonAsync<DocumentContentDto>(Ct))!.Markdown.Should().Be(FakeDocumentService.Body);
        _backend.ViewAudit.Should().Equal(
            (Note, Nominee, "document"), (Note, Nominee, "document"), (Note, Nominee, "content"));
    }

    [Fact]
    public async Task BFFは拒否を足さない_ABACの許可が無くても後段が許せば開ける()
    {
        _factory.SearchScopeGranted = false;
        try
        {
            _backend.Grant(Note, Nominee);

            (await AsAdmin(Nominee).GetAsync($"/bff/documents/{Note}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            // 陰性対照: 通常の判定で読めるもの（所有者）は従来どおり BFF の ABAC を AND で重ねる。
            (await As(Owner).GetAsync($"/bff/documents/{Note}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            _factory.SearchScopeGranted = true;
        }
    }

    [Theory]
    [InlineData(OtherAdmin, true)]   // 指名されていない管理者
    [InlineData(Stranger, false)]    // 管理者でない第三者
    [InlineData(Nominee, true)]      // 発動が無い（終了・失効後）
    public async Task BFFは許可を足さない_後段が許さなければSC03は404(string user, bool admin)
    {
        if (user != Nominee) _backend.Grant(Note, Nominee);
        var client = admin ? AsAdmin(user) : As(user);

        (await client.GetAsync($"/bff/documents/{Note}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/bff/documents/{Note}/content", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _backend.ViewAudit.Should().BeEmpty();
    }

    [Fact]
    public async Task 機械の主体はSC03でも緊急アクセスで開かない()
    {
        _backend.Grant(Note, Nominee);
        var machine = As(Machine, "platform-admin", machine: true);

        (await machine.GetAsync($"/bff/documents/{Note}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _backend.GrpcCalls.Should().OnlyContain(c => c.User == null, "機械は利用者を名指さない（後段は機械として判定する）");
    }

    [Fact]
    public async Task 版と書き込みの前段は緊急アクセスを含めず_閲覧の記録を作らない()
    {
        _backend.Grant(Note, Nominee);
        var admin = AsAdmin(Nominee);

        (await admin.GetAsync($"/bff/documents/{Note}/versions", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/bff/documents/{Note}/versions/1", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PutAsJsonAsync($"/bff/documents/{Note}", new { title = "x" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        _backend.GrpcCalls.Should().OnlyContain(c => !c.AllowBreakGlass);
        _backend.ViewAudit.Should().BeEmpty();
        _backend.RestCalls.Should().BeEmpty("書き込みは後段へ送らない");
        (await admin.GetAsync($"/bff/documents/{Note}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "陽性対照");
    }

    [Fact]
    public async Task 所有者が通常の判定で開いた本文は緊急アクセスを頼まない()
    {
        _factory.ScopeBranches = [new AccessScopeBranch("owner", [new AttributeFilter("owner", [Owner])])];

        (await As(Owner).GetAsync($"/bff/documents/{Note}/content", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        _backend.GrpcCalls.Where(c => c.Method == "GetDocumentContent").Should().OnlyContain(c => !c.AllowBreakGlass);
    }

    private static BreakGlassAuditEntryDto Row(Guid doc, string owner) => new(
        Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, BreakGlassOperations.Activate, owner, doc, owner,
        Nominee, "理由", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15), null);

    // ── 後段（文書サービス）の代役。判定の形は 1 本目の実装と同じ（所有者・指名・いま管理者・閲覧の記録）──────────
    private sealed class FakeDocumentService : CallInvoker
    {
        public const string Body = "# 緊急アクセスの対象";

        private static readonly HashSet<string> Admins = [OtherAdmin, Nominee];

        private readonly Dictionary<Guid, DocumentDto> _docs = new()
        {
            [Note] = PrivateNote(Note, Owner),
            [OtherNote] = PrivateNote(OtherNote, "bob"),
            [OrgDoc] = new DocumentDto
            {
                Id = OrgDoc,
                Title = "組織文書",
                Status = "published",
                MarkdownUri = "storage://b/org.md",
                Attributes = new() { ["confidentiality"] = "internal", ["owner"] = Owner },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };

        private readonly ConcurrentDictionary<(Guid, string), bool> _grants = new();

        public FakeDocumentService() => Rest = new RestHandler(this);

        public HttpMessageHandler Rest { get; }
        public bool BackendAcceptsAnyone { get; set; }
        public bool GrpcUnavailable { get; set; }
        public HttpStatusCode? ForcedStatus { get; set; }
        public ActivateBreakGlassRequest? LastActivateRequest { get; private set; }
        public List<BreakGlassAuditEntryDto> AuditRows { get; } = [];
        public ConcurrentQueue<(string Method, string Path, string? Authorization)> RestCalls { get; } = new();
        public ConcurrentQueue<(string Method, string? User, bool AllowBreakGlass)> GrpcCalls { get; } = new();
        public ConcurrentQueue<(Guid Doc, string User, string Target)> ViewAudit { get; } = new();

        public void Grant(Guid doc, string nominee) => _grants[(doc, nominee)] = true;

        private static DocumentDto PrivateNote(Guid id, string owner) => new()
        {
            Id = id,
            Title = "個人メモ",
            Status = "published",
            MarkdownUri = "storage://b/note.md",
            Attributes = new()
            {
                ["doc_scope"] = "private-note",
                ["owner"] = owner,
                ["confidentiality"] = "restricted",
            },
            SharedWith = ["frank"],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        // 通常の判定（組織文書は誰でも・個人資料は所有者だけ）→ 読めなければ、頼まれたときだけ緊急アクセス。
        private (DocumentDto Doc, bool ViaBreakGlass)? View(Guid id, Pb.UserContext? user, bool allow, string target)
        {
            if (!_docs.TryGetValue(id, out var doc)) return null;
            var name = user?.UserId;
            var isPrivate = doc.Attributes.GetValueOrDefault("doc_scope") == "private-note";
            if (!isPrivate || (name is not null && doc.Attributes["owner"] == name))
                return (doc, false);
            if (!allow || name is null || !_grants.ContainsKey((id, name)) || !Admins.Contains(name))
                return null;
            ViewAudit.Enqueue((id, name, target));
            return (doc with { SharedWith = null }, true);
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            if (GrpcUnavailable)
                throw new RpcException(new Status(StatusCode.Unavailable, "stub: unavailable"));

            IMessage response;
            switch (request)
            {
                case Pb.GetDocumentRequest r:
                    {
                        GrpcCalls.Enqueue((method.Name, r.User?.UserId, r.AllowBreakGlass));
                        var v = View(Guid.Parse(r.Id), r.User, r.AllowBreakGlass, "document");
                        response = v is { } x
                            ? new Pb.GetDocumentResponse
                            {
                                Found = true,
                                Document = DocumentReadGrpcMapping.ToProto(x.Doc),
                                GrantedByBreakGlass = x.ViaBreakGlass,
                            }
                            : new Pb.GetDocumentResponse { Found = false };
                        break;
                    }
                case Pb.GetDocumentContentRequest r:
                    {
                        GrpcCalls.Enqueue((method.Name, r.User?.UserId, r.AllowBreakGlass));
                        var v = View(Guid.Parse(r.DocumentId), r.User, r.AllowBreakGlass, "content");
                        response = v is null
                            ? new Pb.GetDocumentContentResponse { Found = false }
                            : new Pb.GetDocumentContentResponse
                            {
                                Found = true,
                                State = Pb.DocumentContentState.Available,
                                Markdown = Body,
                            };
                        break;
                    }
                case Pb.ListVersionsRequest r:
                    {
                        GrpcCalls.Enqueue((method.Name, r.User?.UserId, false));
                        var v = View(Guid.Parse(r.DocumentId), r.User, allow: false, "versions");
                        response = new Pb.ListVersionsResponse { Found = v is not null };
                        break;
                    }
                case Pb.GetVersionRequest r:
                    {
                        GrpcCalls.Enqueue((method.Name, r.User?.UserId, false));
                        response = new Pb.GetVersionResponse { Found = false };
                        break;
                    }
                default:
                    throw new NotSupportedException(method.Name);
            }

            // 線上を一度通す（直列化 → 読み戻し）。新しい項目が線上で落ちないことも測る。
            var onWire = (TResponse)response.Descriptor.Parser.ParseFrom(response.ToByteArray());
            return new AsyncUnaryCall<TResponse>(Task.FromResult(onWire), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();

        // REST（`/private-notes/{id}/break-glass*`・`/break-glass/audit`）。主体はトークンからしか採らない。
        private sealed class RestHandler(FakeDocumentService owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.PathAndQuery;
                var auth = request.Headers.Authorization?.ToString();
                owner.RestCalls.Enqueue((request.Method.Method, path, auth));
                var subject = auth?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? auth["Bearer ".Length..] : null;
                if (subject is null)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                if (path.StartsWith("/break-glass/audit", StringComparison.Ordinal))
                {
                    var rows = Admins.Contains(subject)
                        ? owner.AuditRows
                        : owner.AuditRows.Where(r => r.OwnerUserId == subject).ToList();
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(rows) };
                }

                if (owner.ForcedStatus is { } forced)
                    return new HttpResponseMessage(forced) { Content = JsonContent.Create(new { title = "downstream-decision" }) };

                var id = Guid.Parse(path.Split('/')[2]);
                var isOwner = owner._docs.TryGetValue(id, out var doc) && doc.Attributes["owner"] == subject;
                if (!isOwner && !owner.BackendAcceptsAnyone)
                    return new HttpResponseMessage(HttpStatusCode.NotFound);

                var now = DateTimeOffset.UtcNow;
                var grant = new BreakGlassGrantDto(Guid.NewGuid(), id, Nominee, "理由", now, now.AddMinutes(15), null,
                    now.AddMinutes(15), true);
                if (request.Method == HttpMethod.Post && path.EndsWith("/break-glass", StringComparison.Ordinal))
                {
                    owner.LastActivateRequest = await request.Content!.ReadFromJsonAsync<ActivateBreakGlassRequest>(ct);
                    return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(grant) };
                }
                if (request.Method == HttpMethod.Post)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(grant with { Active = false }) };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new BreakGlassStatusResponse(grant)),
                };
            }
        }
    }
}
