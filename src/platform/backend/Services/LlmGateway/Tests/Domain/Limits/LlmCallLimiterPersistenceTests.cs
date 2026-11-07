using System.Diagnostics.Metrics;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Limits;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using LlmGateway.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LlmGateway.Tests.Domain.Limits;

// NFR-18, NFR-21, ADR-0044, IADR-0698: 当日の費用の枠を置き場に残し、再起動を越えて引き継ぐ。
// 「再起動」は、同じ置き場のパスで判定器と置き場を作り直すことで模す（前の置き場は Dispose ＝ プロセスの終わり）。
// 🔴 置き場を読めない・書けないときは段 2 で送らない（fail-closed）ことを、故障を注入する置き場で固定する。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Unit")]
public sealed class LlmCallLimiterPersistenceTests : IDisposable
{
    private const string Caller = "ai-stock-trading-llm-caller";
    private const string Model = "test-model";

    // 入力 1000 文字・出力 1000 トークン（入力 3・出力 15 / 百万トークン）の見込み額。
    private const decimal Estimate1000 = 0.018m;

    private readonly ServiceProvider _meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly LlmRoutingOptions _routing = new();
    private readonly ModelPriceTable _prices;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llm-spend-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _stores = [];

    public LlmCallLimiterPersistenceTests()
    {
        _routing.PurposeModels["trade-decision"] = Model;
        var pricing = new ModelPricingOptions();
        // 合成のモデル名と任意の単価（実価格ではない）。
        pricing.Models[Model] = [new ModelPriceEntry { InputPerMillionTokens = 3.0m, OutputPerMillionTokens = 15.0m }];
        _prices = new ModelPriceTable(
            new TestClaudeProviders.StaticOptionsMonitor<ModelPricingOptions>(pricing), NullLogger<ModelPriceTable>.Instance);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        _meterServices.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string StatePath => Path.Combine(_dir, "spend.json");

    private FileLlmSpendStore FileStore(string? path = null)
    {
        var s = new FileLlmSpendStore(path ?? StatePath);
        _stores.Add(s);
        return s;
    }

    private LlmCallLimiter Limiter(ILlmSpendStore store, decimal cap, LlmCallLimitMode mode = LlmCallLimitMode.Enforce)
        => TestCallLimiters.Create(
            _meterServices.GetRequiredService<IMeterFactory>(), _routing, _prices, _time,
            new LlmCallLimitOptions { Mode = mode, Default = { DailyCostLimit = cap, RequestsPerMinute = 100_000, PurposeRequestsPerMinute = 100_000 } },
            store);

    private static LlmCallAdmission Admit(LlmCallLimiter l) => l.Admit(Caller, "trade-decision", Model, 1000, 1000);

    // ---- 再起動を越えて引き継ぐ -------------------------------------------------------------------

    [Fact]
    public void 新しいインスタンスが前回の消費を引き継ぎ_枠が0に戻らない()
    {
        var first = FileStore();
        var before = Limiter(first, cap: Estimate1000 * 2);
        Admit(before).Denied.Should().BeFalse();
        Admit(before).Denied.Should().BeFalse();
        Admit(before).Exceeded.Should().Equal(LlmCallLimiter.KindCallerCost);
        first.Dispose(); // プロセスの終わり

        var after = Limiter(FileStore(), cap: Estimate1000 * 2);

        var a = Admit(after);
        a.Denied.Should().BeTrue("前回の 2 件の予約で当日の枠は埋まっている");
        a.Exceeded.Should().Equal(LlmCallLimiter.KindCallerCost);
    }

    [Fact]
    public void 置き換えた実額を引き継ぐ_予約のままでは残らない()
    {
        var first = FileStore();
        var before = Limiter(first, cap: Estimate1000 * 2);
        // 見込み 0.018 の予約を、実額（入力 1000・出力 0 ＝ 0.003）へ置き換える。
        Admit(before).Settle(Model, 1000, 0);
        first.Dispose();

        var after = Limiter(FileStore(), cap: Estimate1000 * 2);

        // 0.003 ＋ 0.018 ＋ 0.018 ＝ 0.039 ＞ 0.036。2 件目までは通る（予約のままなら 1 件目で 0.036 に達して 2 件目は止まる）。
        Admit(after).Denied.Should().BeFalse();
        Admit(after).Denied.Should().BeTrue();
    }

    [Fact]
    public void 戻した見込みは引き継がない()
    {
        var first = FileStore();
        var before = Limiter(first, cap: Estimate1000);
        Admit(before).Release();
        first.Dispose();

        Admit(Limiter(FileStore(), cap: Estimate1000)).Denied.Should().BeFalse();
    }

    [Fact]
    public void 前の日の消費は新しい日の枠に入らない()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 7, 23, 59, 0, TimeSpan.Zero));
        var first = FileStore();
        Admit(Limiter(first, cap: Estimate1000)).Denied.Should().BeFalse();
        first.Dispose();

        // 同じ日のうちに作り直せば引き継ぐ。
        var sameDay = FileStore();
        Admit(Limiter(sameDay, cap: Estimate1000)).Denied.Should().BeTrue();
        sameDay.Dispose();

        // UTC の 0 時を越えて作り直すと、前の日の値は入らない。
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 8, 0, 0, 1, TimeSpan.Zero));
        Admit(Limiter(FileStore(), cap: Estimate1000)).Denied.Should().BeFalse();
    }

    [Fact]
    public void 日を跨いで書いた置き場は新しい日の値だけを持つ()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 7, 23, 59, 0, TimeSpan.Zero));
        var store = FileStore();
        var l = Limiter(store, cap: Estimate1000 * 10);
        Admit(l);
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 8, 0, 0, 1, TimeSpan.Zero));
        Admit(l);
        store.Dispose();

        var snap = new FileLlmSpendStore(StatePath);
        _stores.Add(snap);
        var s = snap.Load()!;
        s.Day.Should().Be(new DateOnly(2026, 11, 8));
        s.Totals["c|" + Caller].Should().Be(Estimate1000);
    }

    // ---- 置き場の障害で送らない（fail-closed） -----------------------------------------------------

    [Fact]
    public void 前回の値を読めるまで段2では送らず_読めたら判定へ戻る()
    {
        var store = new FaultyStore { FailLoad = true };
        var l = Limiter(store, cap: Estimate1000 * 10);

        l.TryLoadState().Should().BeFalse();
        var a = Admit(l);
        a.Denied.Should().BeTrue("前回の値が読めない間は費用を数えられない");
        a.Exceeded.Should().Equal(LlmCallLimiter.KindCostState);
        store.Saved.Should().BeEmpty("読む前に書くと前回の値を消す");

        store.FailLoad = false;
        store.Persisted = new LlmSpendSnapshot(DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime),
            new Dictionary<string, decimal> { ["c|" + Caller] = Estimate1000 * 9 });

        Admit(l).Denied.Should().BeFalse("前回の 9 件分 ＋ 1 件 ＝ 10 件分で枠の中");
        Admit(l).Exceeded.Should().Equal(LlmCallLimiter.KindCallerCost);
    }

    [Fact]
    public void 予約を書けなければ段2では送らず_予約も残さない()
    {
        var store = new FaultyStore { FailSave = true };
        var l = Limiter(store, cap: Estimate1000);

        var a = Admit(l);
        a.Denied.Should().BeTrue();
        a.Exceeded.Should().Equal(LlmCallLimiter.KindCostState);

        // 置き場が戻れば、止めた 1 件は枠を使っていないので通る。
        store.FailSave = false;
        Admit(l).Denied.Should().BeFalse();
        store.Saved[^1].Totals["c|" + Caller].Should().Be(Estimate1000);
    }

    [Fact]
    public void 段1では置き場の障害でも送り_種類を報告する()
    {
        var store = new FaultyStore { FailLoad = true };
        var l = Limiter(store, cap: Estimate1000, mode: LlmCallLimitMode.Report);

        var a = Admit(l);
        a.Denied.Should().BeFalse();
        a.Exceeded.Should().Equal(LlmCallLimiter.KindCostState);
    }

    [Fact]
    public void 読む前に段1で数えた分は_読んだ前回の値に足す()
    {
        var store = new FaultyStore { FailLoad = true };
        var l = Limiter(store, cap: Estimate1000 * 3, mode: LlmCallLimitMode.Report);
        Admit(l);

        store.FailLoad = false;
        store.Persisted = new LlmSpendSnapshot(DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime),
            new Dictionary<string, decimal> { ["c|" + Caller] = Estimate1000 });
        Admit(l).Exceeded.Should().BeEmpty();

        store.Saved[^1].Totals["c|" + Caller].Should().Be(Estimate1000 * 3);
    }

    [Fact]
    public void 置き場のファイルが壊れていれば段2では送らない()
    {
        File.WriteAllText(StatePath, "{ not json");

        var a = Admit(Limiter(FileStore(), cap: Estimate1000 * 10));

        a.Denied.Should().BeTrue();
        a.Exceeded.Should().Equal(LlmCallLimiter.KindCostState);
        File.ReadAllText(StatePath).Should().Be("{ not json", "壊れたファイルを空で上書きしない");
    }

    [Theory]
    [InlineData("{\"version\":2,\"day\":\"2026-11-07\",\"totals\":{}}")]
    [InlineData("{\"version\":1,\"day\":\"11/07/2026\",\"totals\":{}}")]
    [InlineData("{\"version\":1,\"day\":\"2026-11-07\",\"totals\":{\"c|x\":-1}}")]
    [InlineData("{\"version\":1,\"day\":\"2026-11-07\",\"totals\":{\"x\":1}}")]
    public void 形の違うファイルは読めないとして扱う(string content)
    {
        File.WriteAllText(StatePath, content);

        FileStore().Invoking(s => s.Load()).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void 置き場のディレクトリが無ければ作らずに段2では送らない()
    {
        var missing = Path.Combine(_dir, "not-mounted", "spend.json");

        Admit(Limiter(FileStore(missing), cap: Estimate1000 * 10)).Exceeded.Should().Equal(LlmCallLimiter.KindCostState);
        Directory.Exists(Path.GetDirectoryName(missing)).Should().BeFalse("付いていないボリュームの代わりに消える場所へ書かない");
    }

    [Fact]
    public void 同じ置き場を別の書き手が持つ間は送らず_離したら引き継ぐ()
    {
        var holder = FileStore();
        Admit(Limiter(holder, cap: Estimate1000 * 2)).Denied.Should().BeFalse();

        // 新旧の Pod が重なった形（同じ置き場を 2 つ目の書き手が開く）。
        var second = Limiter(FileStore(), cap: Estimate1000 * 2);
        Admit(second).Exceeded.Should().Equal(LlmCallLimiter.KindCostState);

        holder.Dispose();
        Admit(second).Denied.Should().BeFalse("前の書き手の 1 件 ＋ 1 件で枠の中");
        Admit(second).Exceeded.Should().Equal(LlmCallLimiter.KindCallerCost);
    }

    // ---- 同時要求 --------------------------------------------------------------------------------

    [Fact]
    public void 同時の要求でも枠を越えて送らず_置き場の値も枠を越えない()
    {
        const int Capacity = 20;
        var l = Limiter(FileStore(), cap: Estimate1000 * Capacity);

        var admitted = 0;
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            if (!Admit(l).Denied) Interlocked.Increment(ref admitted);
        });

        admitted.Should().Be(Capacity);
        foreach (var s in _stores) s.Dispose();
        var persisted = new FileLlmSpendStore(StatePath);
        _stores.Add(persisted);
        persisted.Load()!.Totals["c|" + Caller].Should().Be(Estimate1000 * Capacity);
    }

    // 故障を注入する置き場（読み・書きのそれぞれを止められる）。
    private sealed class FaultyStore : ILlmSpendStore
    {
        public bool FailLoad { get; set; }
        public bool FailSave { get; set; }
        public LlmSpendSnapshot? Persisted { get; set; }
        public List<LlmSpendSnapshot> Saved { get; } = [];

        public bool IsDurable => true;

        public LlmSpendSnapshot? Load() => FailLoad ? throw new IOException("injected") : Persisted;

        public void Save(LlmSpendSnapshot snapshot)
        {
            if (FailSave) throw new IOException("injected");
            Saved.Add(snapshot);
            Persisted = snapshot;
        }
    }
}
