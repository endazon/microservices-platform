using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphService.Tests.Infrastructure.Persistence;

// FR-18, ADR-0051 決定 1・2, IADR-0380 (#1244): 類似度候補の既定の供給元（語の共起）。
//
// 🔴 本クラスの主眼は 2 つある。
//   1. **供給元が実際に候補を返すこと**（#1244 の欠陥「常に空」を赤にする陽性）
//   2. **件数・存在をログへ出さないこと**（ADR-0051 決定 2。差し替え前の既定アダプタが守っていた作法）
[Trait("TestKind", "Unit")]
public class TermOverlapSimilarityCandidateSourceTests
{
    private static readonly Guid Origin = Guid.Parse("10000000-0000-0000-0000-00000000000a");
    private static readonly Guid Similar = Guid.Parse("10000000-0000-0000-0000-00000000000b");
    private static readonly Guid Unrelated = Guid.Parse("10000000-0000-0000-0000-00000000000c");
    private static readonly Guid Hidden = Guid.Parse("10000000-0000-0000-0000-00000000000d");

    private static GraphDbContext NewDb()
        => new(new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"sim_{Guid.NewGuid():N}").Options);

    private static GraphDocument Doc(Guid id, string title, string conf = "internal")
        => GraphDocument.Create(id, title,
            new Dictionary<string, string> { ["confidentiality"] = conf }, "fp", DateTimeOffset.UnixEpoch);

    private static GraphDocumentTermProfile Profile(Guid id, string title, string body)
        => GraphDocumentTermProfile.Create(id, TermProfile.Extract(title, body), "fp", DateTimeOffset.UnixEpoch);

    // ログの構造化値を捕まえる（NullLogger では「出していない」ことを測れない）。
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var pairs = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Entries.Add((formatter(state, exception), pairs));
        }
    }

    private static (TermOverlapSimilarityCandidateSource Source, CapturingLogger<TermOverlapSimilarityCandidateSource> Log)
        Build(GraphDbContext db, double minScore = 0.1)
    {
        var log = new CapturingLogger<TermOverlapSimilarityCandidateSource>();
        var source = new TermOverlapSimilarityCandidateSource(
            db, Options.Create(new AiSuggestionSimilarityOptions { MinScore = minScore }), log);
        return (source, log);
    }

    // 起点・似た文書（本文入り）・無関係（本文入り）・スコープ外だが似ている文書。
    private static async Task SeedAsync(GraphDbContext db, CancellationToken ct)
    {
        db.Documents.AddRange(
            Doc(Origin, "知識グラフの ABAC 判定設計"),
            Doc(Similar, "グラフ探索の認可レビュー"),
            Doc(Unrelated, "Quarterly budget planning"),
            Doc(Hidden, "極秘: ABAC 判定の監査結果", conf: "restricted"));
        db.TermProfiles.AddRange(
            Profile(Origin, "知識グラフの ABAC 判定設計",
                "ホップごとに認可述語を評価し、不許可ノードでは探索を打ち切る。属性の複製はイベントで追随する。"),
            Profile(Similar, "グラフ探索の認可レビュー",
                "探索はホップごとに認可述語を評価する。不許可ノードは打ち切り、属性の複製で判定する。"),
            Profile(Unrelated, "Quarterly budget planning",
                "Revenue forecast and headcount plan for the fiscal year. Travel expenses are capped."),
            Profile(Hidden, "極秘: ABAC 判定の監査結果",
                "ホップごとの認可述語の評価と不許可ノードの打ち切りを監査した。属性の複製の追随も確認した。"));
        await db.SaveChangesAsync(ct);
    }

    // T-41 陽性: 実文書 2 件から候補が返る。**これが赤なら #1244 の再発である。**
    [Fact]
    public async Task Returns_the_document_sharing_the_body_terms()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        await SeedAsync(db, ct);
        var (source, _) = Build(db);

        var candidates = await source.FindSimilarAsync(Origin, 50, ct);

        candidates.Should().NotBeEmpty();
        candidates.Select(c => c.DocumentId).Should().Contain(Similar);
        candidates.Should().BeInDescendingOrder(c => c.Score);
    }

    // T-42 陰性: 語彙を共有しない文書は候補に入らない（T-41 と同じ母集合）。
    [Fact]
    public async Task Does_not_return_the_unrelated_document()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        await SeedAsync(db, ct);
        var (source, _) = Build(db);

        var candidates = await source.FindSimilarAsync(Origin, 50, ct);

        candidates.Select(c => c.DocumentId).Should().NotContain(Unrelated);
        candidates.Select(c => c.DocumentId).Should().NotContain(Origin, "起点自身は候補にしない");
    }

    // ADR-0051 決定 1: **供給元はスコープを跨ぐ。** スコープ外の似た文書も返る —— 絞るのは候補列挙の段であり、
    // 本クラスの責務ではない（絞りが効くことは AiSuggestionGenerationTests G-12 が実供給元で固定する）。
    [Fact]
    public async Task Crosses_abac_scope_by_design()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        await SeedAsync(db, ct);
        var (source, _) = Build(db);

        var candidates = await source.FindSimilarAsync(Origin, 50, ct);

        candidates.Select(c => c.DocumentId).Should().Contain(Hidden,
            "類似度の算出は全文書横断でよい（ADR-0051 決定 1）。ここで絞ると列挙の段のテストが空振りする");
    }

    // T-45 縮退: 出現数の行が無い文書は表題から候補になる（陽性）／表題も語を持たなければ候補にならない。
    [Fact]
    public async Task Falls_back_to_the_title_when_no_term_profile_is_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        var titleOnly = Guid.NewGuid();
        var blank = Guid.NewGuid();
        db.Documents.AddRange(
            Doc(Origin, "ABAC 判定の設計"),
            Doc(titleOnly, "ABAC 判定のレビュー"),
            Doc(blank, "   "));
        await db.SaveChangesAsync(ct);
        var (source, _) = Build(db, minScore: 0.0);

        var candidates = await source.FindSimilarAsync(Origin, 50, ct);

        candidates.Select(c => c.DocumentId).Should().Contain(titleOnly, "出現数の行が無くても表題で候補になる");
        candidates.Select(c => c.DocumentId).Should().NotContain(blank);
    }

    // T-46 🔴 ADR-0051 決定 2: **ログは起点の ID しか出さない。** 件数・候補 ID・存在を出さない。
    //
    // 陽性対照: 起点 ID は構造化値として現れる（これが無いと「何も記録していない」実装でも通る）。
    [Fact]
    public async Task Logs_only_the_origin_id_and_never_candidate_counts_or_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        await SeedAsync(db, ct);
        var (source, log) = Build(db);

        var candidates = await source.FindSimilarAsync(Origin, 50, ct);
        candidates.Should().NotBeEmpty("空振り防止: 候補があるときにこそ件数を出さないことを測る");

        log.Entries.Should().NotBeEmpty("陽性対照: 起点は記録される");
        foreach (var (message, state) in log.Entries)
        {
            state.Select(kv => kv.Key).Where(k => k != "{OriginalFormat}")
                .Should().BeEquivalentTo(["OriginDocumentId"],
                    "構造化値は起点 ID だけ（件数・候補 ID・存在を運ぶ欄が無い）");
            message.Should().Contain(Origin.ToString());
            foreach (var c in candidates)
                message.Should().NotContain(c.DocumentId.ToString(), "候補の ID を出さない");
            message.Should().NotContain(candidates.Count.ToString(), "候補の件数を出さない");
        }
    }
}
