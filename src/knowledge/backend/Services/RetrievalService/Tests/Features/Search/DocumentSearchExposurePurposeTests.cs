using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using RetrievalService.Domain.Ports;
using RetrievalService.Tests.Grpc;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace RetrievalService.Tests.Features.Search;

// FR-19, FR-21 ⑨, FR-04, NFR-09, ADR-0061 決定 3, 計画 ADR-0086 決定 1, [[IADR-0426]] 追記 1, [[IADR-0512]] (#1752):
// **gRPC `DocumentSearch/Search` の `purpose`（露出の用途）を、実 Kestrel ＋ 本物の JwtBearer で往復して固定する。**
//
// 🔴 同じ索引の点・同じ権威スコープで `purpose` だけを変えて対にする（陽性と陰性を同じ器で）。
// 🔴 信頼されない呼び出し元は `AI_INPUT` を付けても用途を読む前に PERMISSION_DENIED（スコープも解決しない）。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class DocumentSearchExposurePurposeTests
{
    private readonly GrpcKestrelFactory _factory;

    public DocumentSearchExposurePurposeTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    // AI 分析の実トークンの形（`azp` だけ。既定の許可集合 `aianalysis-service`）。
    private static string RelayToken(string clientId = "aianalysis-service") =>
        GrpcKestrelFactory.IssueToken(Guid.NewGuid().ToString("D"), [PlatformAuthPolicies.ServiceRole],
            azp: clientId, withUsername: false);

    private Pb.DocumentSearch.DocumentSearchClient Grpc() => new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static Pb.SearchRequest Request(string userId, Pb.ExposurePurpose purpose) => new()
    {
        Query = "検索",
        TopK = 50,
        User = new Pb.UserContext { UserId = userId, Action = "read" },
        Purpose = purpose,
    };

    // 「横断検索に含める」OFF・「AI の入力に含める」ON の個人資料と、その逆の個人資料を 1 点ずつ入れ、
    // 権威スコープをこの試験の点だけに向ける（器の索引は他の試験と共有される）。
    private async Task<(Guid AiOnly, Guid SearchOnly)> SeedAsync()
    {
        var dept = $"purpose-{Guid.NewGuid():N}"[..16];
        var aiOnly = await UpsertNoteAsync(dept, search: false, ai: true);
        var searchOnly = await UpsertNoteAsync(dept, search: true, ai: false);
        _factory.Authoritative = new AccessScopeResponse(
            "alice", [new AttributeFilter("dept", [dept])], Granted: true);
        return (aiOnly, searchOnly);
    }

    private async Task<Guid> UpsertNoteAsync(string dept, bool search, bool ai)
    {
        var documentId = Guid.NewGuid();
        var attributes = DocumentExposure.Project(includeInSearch: search, includeInGraph: false, includeInAi: ai);
        attributes["dept"] = dept;
        attributes[DocumentScopes.Key] = DocumentScopes.PrivateNote;
        attributes["owner"] = "alice";
        await _factory.Index.UpsertAsync(new ChunkPayload(
            Guid.NewGuid(), documentId, $"doc:{dept}", "検索 個人資料の本文", new float[1536], "s3://b/x.md",
            attributes, [], null, true));
        return documentId;
    }

    private async Task<List<string>> DocumentIdsAsync(Pb.ExposurePurpose purpose)
    {
        var resp = await Grpc().SearchAsync(Request("alice", purpose), Bearer(RelayToken()), cancellationToken: Ct);
        return [.. resp.Results.Select(r => r.DocumentId)];
    }

    // AC1・AC2: AI 分析が用途 AI 入力で引くと、「AI の入力に含める」で選ばれる。
    [Fact]
    public async Task 用途AI入力の検索は横断検索OFFかつAI入力ONの個人資料を返しAI入力OFFを返さない()
    {
        var (aiOnly, searchOnly) = await SeedAsync();

        var ids = await DocumentIdsAsync(Pb.ExposurePurpose.AiInput);

        ids.Should().Equal([aiOnly.ToString()],
            "FR-19 の 3 トグルは独立である —— RAG の候補は「AI の入力に含める」で選び、「横断検索に含める」では選ばない");
        ids.Should().NotContain(searchOnly.ToString());
    }

    // AC3: 用途を言わない（旧い呼び出し元）・横断検索を言う・知らない値は、従来どおり「横断検索に含める」で落とす。
    [Theory]
    [InlineData(Pb.ExposurePurpose.Unspecified)]
    [InlineData(Pb.ExposurePurpose.Search)]
    [InlineData((Pb.ExposurePurpose)99)]
    public async Task 用途AI入力以外の検索は従来どおり横断検索OFFの個人資料を返さない(Pb.ExposurePurpose purpose)
    {
        var (aiOnly, searchOnly) = await SeedAsync();

        var ids = await DocumentIdsAsync(purpose);

        ids.Should().Equal([searchOnly.ToString()], "陽性対照: 同じ点・同じスコープで、横断検索 ON の資料は返る");
        ids.Should().NotContain(aiOnly.ToString(), "取り違えは一覧に出てはならない資料が出ない側へ倒す");
    }

    // AC4: 用途は利用者文脈と同じ信頼の下で運ばれる。信頼されない呼び出し元は AI 入力を名乗っても通らない。
    [Fact]
    public async Task 信頼されない呼び出し元が用途AI入力を付けてもPERMISSION_DENIEDでスコープを解決しない()
    {
        await SeedAsync();
        var user = $"mallory-{Guid.NewGuid():N}";

        var act = async () => await Grpc().SearchAsync(
            Request(user, Pb.ExposurePurpose.AiInput), Bearer(RelayToken("graph-service")), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        _factory.ResolvedFor.Should().NotContain(r => r.UserId == user);
    }
}
