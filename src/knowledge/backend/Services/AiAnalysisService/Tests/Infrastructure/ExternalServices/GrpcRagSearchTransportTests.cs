using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace AiAnalysisService.Tests.Infrastructure.ExternalServices;

// FR-03, FR-04, FR-05, FR-07, NFR-09, NFR-16, UC-01, UC-02, SC-01, SC-08, ADR-0004, ADR-0029,
// ADR-0034 決定 1, ADR-0075, 計画 ADR-0086 決定 1, ADR-0087 決定 2, ADR-0089 決定 1,
// [[IADR-0253]] 決定 2, [[IADR-0379]] 決定 4・5, [[IADR-0410]], [[IADR-0415]], [[IADR-0416]],
// [[IADR-0425]] (#1255):
// RAG の検索の gRPC 輸送が、**REST 輸送と同じ枝・同じ副作用**であり、
// **利用者の資格情報を面へ載せず本文で運ぶ**ことを固定する。
//
// 🔴 ここが本スライスの不変条件そのものである —— 輸送を替えたときに
// **利用者トークンが面へ漏れる**か**故障が「該当なし」に化ける**かのどちらかへ倒れると、
// 前者は confused deputy、後者は「文書が 1 件も無い」という嘘になる。
[Trait("TestKind", "Unit")]
public class GrpcRagSearchTransportTests
{
    private static RagSearchQuery Query(
        IReadOnlyDictionary<string, List<string>>? narrowTo = null, int topK = 5) =>
        new("質問", topK, new AccessScope([], GrantsAccess: true), "alice",
            new Dictionary<string, string> { ["clearance"] = "internal", ["department"] = "hr" },
            narrowTo);

    private static GrpcRagSearchTransport Transport(FakeClient client) =>
        new(client, NullLogger<GrpcRagSearchTransport>.Instance);

    // 🔴 T-01 陽性対照。**利用者文脈は本文で運ばれる**（`user_id` / 属性 / `action`）。
    // これが無いと、以下の陰性はすべて「何も送らない」実装でも緑になる。
    [Fact]
    public async Task 利用者文脈を本文で運ぶ()
    {
        var fake = new FakeClient(Response());

        await Transport(fake).SearchAsync(Query(), TestContext.Current.CancellationToken);

        fake.LastRequest.Should().NotBeNull();
        fake.LastRequest!.User.UserId.Should().Be("alice");
        fake.LastRequest.User.Action.Should().Be("read", "action は既定へ丸めない");
        fake.LastRequest.User.UserAttributes.Should().Contain(
            new KeyValuePair<string, string>("clearance", "internal"));
        fake.LastRequest.User.UserAttributes.Should().Contain(
            new KeyValuePair<string, string>("department", "hr"));
        fake.LastRequest.Query.Should().Be("質問");
        fake.LastRequest.TopK.Should().Be(5);
    }

    // 🔴 T-02: **利用者の JWT をメタデータへ載せない**（confused deputy の防止。
    // 計画 `ADR-0086` 決定 1 / [[IADR-0379]] 決定 4）。載るのは**チャネルに付いた s2s だけ**であり、
    // 呼び出しごとのヘッダは 1 本も足さない。
    [Fact]
    public async Task 利用者のトークンをメタデータへ載せない()
    {
        var fake = new FakeClient(Response());

        await Transport(fake).SearchAsync(Query(), TestContext.Current.CancellationToken);

        (fake.LastOptions.Headers ?? []).Should().BeEmpty(
            "利用者の資格情報は面を通らない —— 通ると呼び出し先が「利用者が直接呼んだ」と区別できない");
    }

    // 🔴 T-03: **解決済みスコープを送る口が無い。** 送るのは利用者が指定した絞り込み
    //（交差前の値）だけであり、交差は呼び出し先が同じ `ScopeNarrowing` で行う
    // （[[IADR-0415]] / [[IADR-0416]]）。
    [Fact]
    public async Task 絞り込みは交差前の値で運ばれスコープは送らない()
    {
        var fake = new FakeClient(Response());
        var narrowTo = new Dictionary<string, List<string>> { ["dept"] = ["sales"] };

        await Transport(fake).SearchAsync(Query(narrowTo), TestContext.Current.CancellationToken);

        fake.LastRequest!.NarrowTo.Should().ContainKey("dept");
        fake.LastRequest.NarrowTo["dept"].Values.Should().Equal(["sales"]);
        Pb.SearchRequest.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name)
            .Should().NotContain("scope",
                "呼び出し先が「本文で渡された scope を信じる」口を開かない");
    }

    // ★ T-03 の陰性対照。絞り込みが無ければ**空で送る**（勝手に実効スコープを詰めない）。
    [Fact]
    public async Task 絞り込みが無ければ空で送る()
    {
        var fake = new FakeClient(Response());

        await Transport(fake).SearchAsync(Query(), TestContext.Current.CancellationToken);

        fake.LastRequest!.NarrowTo.Should().BeEmpty();
    }

    // 🔴 T-04: **応答の写しは既定へ頼らない**（`docs/api/east-west-grpc.md` §proto3 の「未指定」を写す）。
    // `has_body` は DTO の既定が `true`・proto3 の既定が `false` で向きが逆であり、
    // `updated_at` / `markdown_uri` は未設定と既定値を取り違えると意味が静かに変わる。
    [Fact]
    public async Task 応答の未指定と既定を取り違えずに写す()
    {
        var updatedAt = new DateTimeOffset(2026, 9, 11, 1, 2, 3, TimeSpan.Zero);
        var withBody = Result(hasBody: true, markdownUri: "s3://b/x.md", updatedAt: updatedAt);
        var bodyless = Result(hasBody: false, markdownUri: null, updatedAt: null);
        var fake = new FakeClient(Response(withBody, bodyless));

        var results = await Transport(fake).SearchAsync(Query(), TestContext.Current.CancellationToken);

        results.Should().HaveCount(2);
        results[0].HasBody.Should().BeTrue("★ 陽性対照 —— 既定の向きが逆でも写せている");
        results[0].MarkdownUri.Should().Be("s3://b/x.md");
        results[0].UpdatedAt.Should().Be(updatedAt);
        results[0].Tags.Should().Equal(["tag-a"]);
        results[0].Attributes.Should().Contain(new KeyValuePair<string, string>("dept", "sales"));
        results[1].HasBody.Should().BeFalse("本文を持たない点は false のまま届く");
        results[1].MarkdownUri.Should().BeNull("未設定（null）と空文字は別物である");
        results[1].UpdatedAt.Should().BeNull("未設定は「まだ索引に無い」であり既定値で埋めない");
    }

    // 🔴 T-05: **検索の失敗は回答そのものを落とさない**（REST 輸送の非 2xx / 不達と同値）。
    // 例外を上げると、現在は「関連文書が見つかりませんでした」へ倒れる場面が north-south の 500 になる。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task 検索の失敗は空へ縮退する(StatusCode status)
    {
        var fake = new FakeClient(Response()) { Exception = new RpcException(new Status(status, "失敗")) };

        var act = async () => await Transport(fake).SearchAsync(
            Query(), TestContext.Current.CancellationToken);

        (await act.Should().NotThrowAsync()).Subject.Should().BeEmpty();
    }

    // 🔴 T-06: s2s トークンの取得失敗（`InvalidOperationException`）も**同じ縮退**である。
    // **配線漏れがまさにこの枝に出る**ので、枝を分けない代わりに必ず警告を出す（実装の 🔴 を参照）。
    [Fact]
    public async Task s2sトークン取得失敗も空へ縮退する()
    {
        var fake = new FakeClient(Response())
        {
            Exception = new InvalidOperationException("ServiceToken:ClientId が未設定です。"),
        };

        var act = async () => await Transport(fake).SearchAsync(
            Query(), TestContext.Current.CancellationToken);

        (await act.Should().NotThrowAsync()).Subject.Should().BeEmpty();
    }

    // 🔴 T-07: **切替は構成の有無だけである。** `Services:RetrievalServiceGrpc` が無ければ
    // 生成クライアントを**1 つも登録しない** —— 登録の有無で `Program.cs` が REST 輸送と
    // gRPC 輸送を選ぶ（並走中の正は REST。戻すのは構成を外すだけでコードは変えない）。
    [Fact]
    public void 宛先が未設定なら生成クライアントを登録しない()
    {
        var services = new ServiceCollection()
            .AddRetrievalSearchGrpcClient(new ConfigurationBuilder().Build());

        services.Should().NotContain(
            d => d.ServiceType == typeof(Pb.DocumentSearch.DocumentSearchClient),
            "未設定なら何も登録しない（REST のまま）");
    }

    [Fact]
    public void 宛先が構成されていれば生成クライアントを登録する()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RetrievalSearchGrpcClientExtensions.AddressKey] = "http://retrieval-service:8081",
        }).Build();

        var services = new ServiceCollection().AddRetrievalSearchGrpcClient(config);

        services.Should().Contain(
            d => d.ServiceType == typeof(Pb.DocumentSearch.DocumentSearchClient),
            "★ 陽性対照 —— 登録されないのは関数が壊れているからではない");
    }

    // 🔴 T-08: **宛先ごとにチャネルを分ける。** 本サービスは既に認可サービス宛のチャネルを
    // **キー無し**で持つので、3 本目をキー無しで足すと検索が認可サービスへ繋がる。
    [Fact]
    public void 検索のチャネルはキー付きで登録される()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RetrievalSearchGrpcClientExtensions.AddressKey] = "http://retrieval-service:8081",
        }).Build();

        var services = new ServiceCollection().AddRetrievalSearchGrpcClient(config);

        var channels = services
            .Where(d => d.ServiceType == typeof(Grpc.Net.Client.GrpcChannel)).ToList();

        channels.Should().Contain(
            d => Equals(d.ServiceKey, RetrievalSearchGrpcClientExtensions.ChannelKey));
        channels.Should().NotContain(
            d => d.ServiceKey == null,
            "キー無しのチャネルは認可サービス宛である（上書きすると検索がそちらへ繋がる）");
    }

    // ── 器 ────────────────────────────────────────────────────────

    private static Pb.SearchResult Result(
        bool hasBody = true, string? markdownUri = "s3://b/x.md", DateTimeOffset? updatedAt = null)
    {
        var result = new Pb.SearchResult
        {
            ChunkId = Guid.NewGuid().ToString(),
            DocumentId = Guid.NewGuid().ToString(),
            DocumentTitle = "文書",
            Text = hasBody ? "本文" : string.Empty,
            Score = 0.9f,
            HasBody = hasBody,
            Tags = { "tag-a" },
            Attributes = { { "dept", "sales" } },
        };
        if (markdownUri is not null) result.MarkdownUri = markdownUri;
        if (updatedAt is { } at) result.UpdatedAt = Timestamp.FromDateTimeOffset(at);
        return result;
    }

    private static Pb.SearchResponse Response(params Pb.SearchResult[] results)
    {
        var response = new Pb.SearchResponse();
        response.Results.AddRange(results);
        return response;
    }

    private sealed class FakeClient(Pb.SearchResponse response) : Pb.DocumentSearch.DocumentSearchClient
    {
        public Pb.SearchRequest? LastRequest { get; private set; }
        public CallOptions LastOptions { get; private set; }
        public Exception? Exception { get; init; }

        public override AsyncUnaryCall<Pb.SearchResponse> SearchAsync(
            Pb.SearchRequest request, CallOptions options)
        {
            LastRequest = request;
            LastOptions = options;
            var task = Exception is null
                ? Task.FromResult(response)
                : Task.FromException<Pb.SearchResponse>(Exception);
            return new AsyncUnaryCall<Pb.SearchResponse>(
                task, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }
}
