using System.Diagnostics.Metrics;
using AwesomeAssertions;
using GraphService.Common.Observability;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Features.AiSuggestions.Generate;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Features.AiSuggestions.Generate;

// FR-18, SC-03, SC-09, ADR-0063 決定 2, IADR-0364 決定 2 (#1014): **生成段のタグ辞書の値域強制。**
//
// 「辞書外の値を持つ提案は生成しない」を、LLM が辞書外を返した場合について固定する。
// 🔴 **陰性は陽性対照と対で置く** —— 辞書内の値が提案になることを同じクラスで見る。
// 「タグ提案を一切作らない」実装なら陰性は緑のままだが、陽性対照で落ちる。
//
// 🔴 **変異試験の対象である**（#1014 受け入れ基準 3）。`AiSuggestionGenerator.PersistAsync` の
// `dictionary.Contains(value)` を外すと `Out_of_dictionary_tag_is_not_persisted` が落ちる。
[Trait("TestKind", "Unit")]
public class TagDictionaryEnforcementTests
{
    private static AccessScopeResponse InternalOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

    private static GraphDbContext NewDb()
        => new(new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"tagdict_{Guid.NewGuid():N}").Options);

    private static GraphDocument Doc(Guid id, string title)
        => GraphDocument.Create(id, title,
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            null, DateTimeOffset.UnixEpoch);

    private sealed class StubSimilarity(params SimilarityCandidate[] candidates) : ISimilarityCandidateSource
    {
        public Task<IReadOnlyList<SimilarityCandidate>> FindSimilarAsync(
            Guid originDocumentId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SimilarityCandidate>>(candidates);
    }

    // 封（プロンプト）を捕まえつつ、固定の提案を返す LLM。
    private sealed class CapturingLlm(IReadOnlyList<LlmSuggestionProposal> proposals) : ISuggestionLlmClient
    {
        public SuggestionPrompt? LastPrompt { get; private set; }

        public Task<IReadOnlyList<LlmSuggestionProposal>> ProposeAsync(
            SuggestionPrompt prompt, CancellationToken ct = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(proposals);
        }
    }

    private sealed class StubDictionary(IReadOnlySet<string>? names) : ITagDictionaryReader
    {
        public Task<IReadOnlySet<string>?> ReadNamesAsync(CancellationToken ct = default)
            => Task.FromResult(names);
    }

    private static IReadOnlySet<string> Dictionary(params string[] names)
        => names.ToHashSet(StringComparer.Ordinal);

    private static LlmSuggestionProposal Tag(string value)
        => new(SuggestionKind.Tag, null, null, value, "根拠");

    private static async Task<(Guid Origin, Guid Visible)> SeedAsync(GraphDbContext db)
    {
        var origin = Guid.NewGuid();
        var visible = Guid.NewGuid();
        db.Documents.Add(Doc(origin, "起点"));
        db.Documents.Add(Doc(visible, "候補"));
        db.EdgeTypes.Add(EdgeType.Create(EdgeTypeSeed.DefaultTypeName, EdgeTypeLayer.Core, true, isSeed: true));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (origin, visible);
    }

    private static (AiSuggestionGenerator Generator, CapturingLlm Llm, MetricsProbe Probe) Build(
        GraphDbContext db, Guid visible, IReadOnlySet<string>? dictionary,
        params LlmSuggestionProposal[] proposals)
    {
        var factory = new TestMeterFactory();
        var metrics = new TagSuggestionDropMetrics(factory);
        var probe = new MetricsProbe(factory.CreatedMeterName!);
        var llm = new CapturingLlm(proposals);
        var generator = new AiSuggestionGenerator(
            new EfGraphStore(db), new StubSimilarity(new SimilarityCandidate(visible, 0.9)),
            llm, new StubDictionary(dictionary), metrics, db, TimeProvider.System);
        return (generator, llm, probe);
    }

    // 🔴 陰性: 辞書に無い値は提案にならない。落とした件数が数えられる。
    [Fact]
    public async Task Out_of_dictionary_tag_is_not_persisted()
    {
        using var db = NewDb();
        var (origin, visible) = await SeedAsync(db);
        var (generator, _, probe) = Build(db, visible, Dictionary("経理", "規程"), Tag("極秘プロジェクト"));

        var created = await generator.GenerateAsync(origin, InternalOnly(), TestContext.Current.CancellationToken);

        created.Should().BeEmpty("辞書外の値を持つ提案は生成しない（ADR-0063 決定 2）");
        db.AiSuggestions.Should().BeEmpty();
        probe.Dropped(TagSuggestionDropMetrics.OutOfDictionary).Should().Be(1);
    }

    // 陽性対照: 辞書内の値は提案になる（上の陰性が「タグを一切作らない」で通っていないこと）。
    [Fact]
    public async Task In_dictionary_tag_is_persisted_as_pending()
    {
        using var db = NewDb();
        var (origin, visible) = await SeedAsync(db);
        var (generator, _, probe) = Build(db, visible, Dictionary("経理", "規程"), Tag("経理"));

        var created = await generator.GenerateAsync(origin, InternalOnly(), TestContext.Current.CancellationToken);

        created.Should().ContainSingle(s => s.Kind == SuggestionKind.Tag && s.TagValue == "経理");
        created![0].State.Should().Be(SuggestionState.Pending);
        probe.Dropped(TagSuggestionDropMetrics.OutOfDictionary).Should().Be(0, "0 が正常");
    }

    // 同じ応答に辞書内・辞書外が混ざっても、辞書内だけが残る（順序に依らない）。
    [Fact]
    public async Task Mixed_response_keeps_only_dictionary_values()
    {
        using var db = NewDb();
        var (origin, visible) = await SeedAsync(db);
        var (generator, _, probe) = Build(db, visible, Dictionary("経理", "規程"),
            Tag("極秘"), Tag("規程"), Tag("人事評価"), Tag("経理"));

        var created = await generator.GenerateAsync(origin, InternalOnly(), TestContext.Current.CancellationToken);

        created!.Select(s => s.TagValue).Should().BeEquivalentTo(["規程", "経理"]);
        probe.Dropped(TagSuggestionDropMetrics.OutOfDictionary).Should().Be(2);
    }

    // 🔴 **比較は Ordinal である**（DocumentService の `TagResolver.ToIdsAsync` と同じ）。大小文字違いを
    // 通すと、生成段で通した値が承認段で「辞書に無い」と落ちる。
    [Fact]
    public async Task Matching_is_ordinal_like_the_dictionary_owner()
    {
        using var db = NewDb();
        var (origin, visible) = await SeedAsync(db);
        var (generator, _, _) = Build(db, visible, Dictionary("Finance"), Tag("finance"), Tag(" Finance "));

        var created = await generator.GenerateAsync(origin, InternalOnly(), TestContext.Current.CancellationToken);

        // Trim は辞書側の正規化（`Tag.Normalize`）と同じなので通る。大小文字は通らない。
        created!.Select(s => s.TagValue).Should().BeEquivalentTo(["Finance"]);
    }

    // 🔴 fail-closed: 辞書が引けなければタグ提案を 1 件も作らない。**リンク提案は影響を受けない**（陽性対照）。
    [Fact]
    public async Task Unavailable_dictionary_drops_every_tag_but_keeps_links()
    {
        using var db = NewDb();
        var (origin, visible) = await SeedAsync(db);
        var (generator, _, probe) = Build(db, visible, dictionary: null,
            Tag("経理"),
            new LlmSuggestionProposal(SuggestionKind.Link, visible, EdgeTypeSeed.DefaultTypeName, null, "似ている"));

        var created = await generator.GenerateAsync(origin, InternalOnly(), TestContext.Current.CancellationToken);

        created.Should().ContainSingle(s => s.Kind == SuggestionKind.Link, "リンク提案は辞書と無関係");
        created.Should().NotContain(s => s.Kind == SuggestionKind.Tag, "辞書が分からないときは作らない");
        probe.Dropped(TagSuggestionDropMetrics.DictionaryUnavailable).Should().Be(1);
    }

    // 辞書は LLM に**選ばせる値集合**として封に入る（辺の型と同じ形）。引けなければ「提案しない」と指示する。
    [Fact]
    public async Task Dictionary_names_are_passed_to_the_llm_as_the_allowed_value_set()
    {
        using var db = NewDb();
        var (origin, visible) = await SeedAsync(db);
        var (withDictionary, llm, _) = Build(db, visible, Dictionary("経理", "規程"));
        await withDictionary.GenerateAsync(origin, InternalOnly(), TestContext.Current.CancellationToken);
        var rendered = llm.LastPrompt!.Render();
        rendered.Should().Contain("## タグ").And.Contain("経理").And.Contain("規程");
        rendered.Should().Contain("一覧に無いタグを提案してはならない");

        var (withoutDictionary, llm2, _) = Build(db, visible, dictionary: null);
        await withoutDictionary.GenerateAsync(origin, InternalOnly(), TestContext.Current.CancellationToken);
        llm2.LastPrompt!.TagNames.Should().BeEmpty();
        llm2.LastPrompt.Render().Should().Contain("タグ候補は提案しない");
    }

    // ── 計測の道具（LinkEdgeSyncTests と同じ作法） ─────────────────────────

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];
        public string? CreatedMeterName { get; private set; }

        public Meter Create(MeterOptions options)
        {
            CreatedMeterName = $"{options.Name}.test-{Guid.NewGuid():N}";
            var meter = new Meter(CreatedMeterName, options.Version, options.Tags, scope: this);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var m in _meters) m.Dispose();
            _meters.Clear();
        }
    }

    private sealed class MetricsProbe
    {
        private readonly Dictionary<string, long> _byReason = [];

        public MetricsProbe(string meterName)
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == meterName
                        && instrument.Name == TagSuggestionDropMetrics.DroppedCounterName)
                        l.EnableMeasurementEvents(instrument);
                },
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var reason = "?";
                foreach (var t in tags)
                    if (t.Key == TagSuggestionDropMetrics.ReasonTag) reason = t.Value?.ToString() ?? "?";
                lock (_byReason) _byReason[reason] = _byReason.GetValueOrDefault(reason) + value;
            });
            listener.Start();
        }

        public long Dropped(string reason)
        {
            lock (_byReason) return _byReason.GetValueOrDefault(reason);
        }
    }
}
