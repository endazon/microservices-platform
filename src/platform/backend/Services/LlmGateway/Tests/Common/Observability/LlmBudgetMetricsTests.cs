using System.Diagnostics.Metrics;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Pricing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Common.Observability;

// T-2, FR-11, NFR-21, ADR-0044, IADR-0466 決定 3 (#1111): 月次予算の上限のゲージ。
//
// 🔴 **陰性対照が本体である。** 未設定のあいだゲージが 1 件でも値を出すと、上限アラートの式が
// 評価対象を持ち、**所有者が金額を決めていないのに統制が動く**（計画が禁じた「実装側で金額を決める」と
// 同じ結果になる）。未設定 → 0 件、を単体とホストの両方で固定する。
//
// 共有 Meter（`LlmCompletionMetrics.MeterName`）へ発行するのでコレクションへ加入する（[[IADR-0394]] の加入規則）。
// 購読は Meter の**インスタンス**で絞る（主たる防護）。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class LlmBudgetMetricsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private sealed record Measured(string Instrument, double Value, Dictionary<string, string> Tags);

    // 指定した Meter インスタンスの観測計器を 1 回だけ収集する。
    private static List<Measured> Collect(Meter meter)
    {
        var items = new List<Measured>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) =>
        {
            var dict = new Dictionary<string, string>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value?.ToString() ?? string.Empty;
            items.Add(new Measured(i.Name, v, dict));
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return items;
    }

    private static (LlmBudgetMetrics Metrics, Meter Meter) NewMetrics(LlmBudgetOptions budget, string currency = "USD")
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var meterFactory = services.BuildServiceProvider().GetRequiredService<IMeterFactory>();
        var prices = new ModelPriceTable(
            new Static<ModelPricingOptions>(new ModelPricingOptions { Currency = currency }),
            NullLogger<ModelPriceTable>.Instance);
        var metrics = new LlmBudgetMetrics(meterFactory, new Static<LlmBudgetOptions>(budget), prices);
        return (metrics, meterFactory.Create(LlmBudgetMetrics.MeterName));
    }

    // T-2a: 設定した用途だけが、用途（小文字）と単価表の通貨のタグ付きで出る。
    // タグ名は llm.cost.total と同じ軸でなければルールの `on (llm_purpose, llm_currency)` が突き合わない。
    [Fact]
    public void 設定した用途だけが用途と通貨のタグ付きで出る()
    {
        var budget = new LlmBudgetOptions();
        budget.MonthlyLimits["RAG-Answer"] = 12.5m;
        budget.MonthlyLimits["trade-decision"] = 30m;
        var (_, meter) = NewMetrics(budget, currency: "JPY");

        var items = Collect(meter);

        items.Should().HaveCount(2).And.OnlyContain(m => m.Instrument == LlmBudgetMetrics.LimitGaugeName);
        items.Should().ContainSingle(m => m.Tags[LlmCompletionMetrics.PurposeTag] == "rag-answer" && m.Value == 12.5);
        items.Should().ContainSingle(m => m.Tags[LlmCompletionMetrics.PurposeTag] == "trade-decision" && m.Value == 30);
        items.Should().OnlyContain(m => m.Tags[LlmUsageMetrics.CurrencyTag] == "JPY" && m.Tags.Count == 2);
    }

    // T-2b: 🔴 **陰性対照 —— 未設定なら 1 件も出さない。** 系列が無いのでアラートは発火しない（不活性）。
    [Fact]
    public void 未設定なら1件も出さない()
    {
        var (_, meter) = NewMetrics(new LlmBudgetOptions());

        Collect(meter).Should().BeEmpty();
    }

    // T-2c: 名前は scripts.repo.test.js がルールの式（`llm_budget_monthly_limit`）と突き合わせる前提である。
    [Fact]
    public void 計器名を固定する()
        => LlmBudgetMetrics.LimitGaugeName.Should().Be("llm.budget.monthly_limit");

    // T-2d: ホストは**起動時に**ゲージを登録する（補完の呼び出しを待たない）。
    // 待つと「金額を設定したのに系列が出ない」時間が生まれ、所有者の確認手順が成り立たない。
    [Fact]
    public void ホストは起動時に設定済みの上限をゲージとして出す()
    {
        using var host = WithBudget(("rag-answer", "42.5"));
        _ = host.Services; // 起動（要求は 1 件も送らない）

        var items = Collect(HostMeter(host));

        items.Where(m => m.Instrument == LlmBudgetMetrics.LimitGaugeName).Should().ContainSingle()
            .Which.Should().Match<Measured>(m =>
                m.Value == 42.5
                && m.Tags[LlmCompletionMetrics.PurposeTag] == "rag-answer"
                && m.Tags[LlmUsageMetrics.CurrencyTag] == "USD");
    }

    // T-2e: 🔴 **陰性対照（ホスト）—— 既定の設定（appsettings.json は空）では計器はあるが値は 1 件も無い。**
    // 本リポジトリの既定が「金額なし」であることの固定でもある。
    [Fact]
    public void 既定の設定ではゲージは値を1件も出さない()
    {
        _ = factory.Services;

        var meter = HostMeter(factory);
        var published = new List<string>();
        using (var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter, meter)) published.Add(instrument.Name);
            }
        })
        {
            listener.Start();
        }

        published.Should().Contain(LlmBudgetMetrics.LimitGaugeName); // 計器はある（陽性対照）
        Collect(meter).Where(m => m.Instrument == LlmBudgetMetrics.LimitGaugeName).Should().BeEmpty();
    }

    // T-2f: 未知の用途は起動時に落ちる（ValidateOnStart。黙って読み飛ばさない）。
    //
    // ［2026-09-26 / #1598］🔴 **`host.Services` が投げる例外では測らない**（GraphService の #1582・#1594 と同じ競合）。
    // 起動検証はエントリポイントのスレッドで落ち、`app.Run()` の後始末が host を破棄する。テストスレッドの
    // `DeferredHost.StartAsync` が破棄の**後**に着くと、破棄済みの ServiceProvider を引いて `ObjectDisposedException` になり、
    // 着順しだいで届く例外が変わる（従前の書き方は ODE を受けると `OptionsValidationException` が見つからず赤になる）。
    // そこで **起動検証そのものを包み、投げた例外を host の破棄より前に記録する**。
    // ① 起動しない（`host.Services` が投げる）と ② 落ちた理由が起動検証である（ODE を合格扱いにしない）の 2 点を測る。
    // 本サービスの `ValidateOnStart` は 3 件あり、複数が落ちると `AggregateException` にまとまるので、記録は平らにして探す。
    [Fact]
    public async Task 未知の用途を設定するとホストは起動しない()
    {
        var startupValidation = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = WithBudget(
            services => RecordingStartupValidator.Install(services, startupValidation),
            ("no-such-purpose", "1"));

        var act = () => host.Services;

        act.Should().Throw<Exception>("未知の用途で host が起動してはならない");
        var failure = await startupValidation.Task.WaitAsync(
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        failure.Should().NotBeNull("起動を落としたのは起動検証（ValidateOnStart）である");
        Flatten(failure!).Should().Contain(x => x is OptionsValidationException && x.Message.Contains("no-such-purpose"));
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithBudget(params (string Purpose, string Limit)[] limits)
        => WithBudget(_ => { }, limits);

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithBudget(
        Action<IServiceCollection> configureServices, params (string Purpose, string Limit)[] limits)
        => factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(limits.ToDictionary(
                    l => $"{LlmBudgetOptions.SectionName}:MonthlyLimits:{l.Purpose}", l => (string?)l.Limit)));
            b.ConfigureServices(configureServices);
        });

    // #1598（#1594 の `SimilaritySourceWiringTests` と同じ包み。ユニットが別なので試験の補助は共有しない）:
    // Program が `ValidateOnStart` で登録した起動検証を包み、結果（通れば null・落ちればその例外）を記録してから元どおり投げる。
    // 🔴 `ValidateOnStart` を 1 つ外しても `IStartupValidator` の登録は残る（本サービスは他に 2 件ある。GraphService でも
    // 唯一の 1 件を外して登録が残ることを #1598 で実測した）ので、そうした変異は下の「未登録」の枝ではなく、起動が通って
    // 上の `Throw` か記録の検査で赤になる。この枝は**登録そのものが無い構成**への備えであり、記録されないまま待ち続けるより先に、
    // 理由の分かる形で赤にする。
    private sealed class RecordingStartupValidator(IStartupValidator inner, TaskCompletionSource<Exception?> sink)
        : IStartupValidator
    {
        public static void Install(IServiceCollection services, TaskCompletionSource<Exception?> sink)
        {
            var original = services.SingleOrDefault(d => d.ServiceType == typeof(IStartupValidator));
            if (original?.ImplementationType is not { } type)
            {
                var missing = new InvalidOperationException("起動検証（ValidateOnStart）が登録されていない");
                sink.TrySetResult(missing);
                throw missing;
            }
            services.Remove(original);
            services.AddTransient<IStartupValidator>(sp =>
                new RecordingStartupValidator((IStartupValidator)ActivatorUtilities.CreateInstance(sp, type), sink));
        }

        public void Validate()
        {
            try
            {
                inner.Validate();
                sink.TrySetResult(null);
            }
            catch (Exception ex)
            {
                sink.TrySetResult(ex);
                throw;
            }
        }
    }

    // ホストの IMeterFactory は同じ名前の Meter を同じインスタンスで返す（キャッシュ）。
    private static Meter HostMeter(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
        => host.Services.GetRequiredService<IMeterFactory>().Create(LlmBudgetMetrics.MeterName);

    private static IEnumerable<Exception> Flatten(Exception e)
    {
        yield return e;
        if (e is AggregateException agg)
            foreach (var inner in agg.InnerExceptions.SelectMany(Flatten)) yield return inner;
        else if (e.InnerException is { } inner)
            foreach (var x in Flatten(inner)) yield return x;
    }

    private sealed class Static<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
