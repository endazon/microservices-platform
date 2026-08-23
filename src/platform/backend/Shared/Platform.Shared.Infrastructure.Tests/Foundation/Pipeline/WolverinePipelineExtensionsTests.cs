using System.Reflection;
using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Wolverine;
using Wolverine.Configuration;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Pipeline;

// FR-14, ADR-0018 / ADR-0027: Wolverine 版の段登録経路（W2。IADR-0234 決定 3）。
//
// 🔴 **本試験群の要点は規則 5 である。** MassTransit 経路は入力型を導出できないと
// `inputType is not null &&` で **照合ごと飛ばす**。Wolverine 段には `IConsumer<TIn>` が無いため、
// その形を持ち込むと input 照合が永久に素通りし、**起動もテストも緑のまま宣言と実装がずれる**。
// 「落ちること」だけでなく **「落ちた理由が入力型の導出失敗であること」** をメッセージまで見る ——
// 別の規則で落ちたのを「fail-fast が効いた」と誤読しないため。
//
// 🔴 **MassTransit の型を using で取り込まない**（残件 ratchet が 13 → 14 へ増える。IADR-0233 決定 6）。
public class WolverinePipelineExtensionsTests
{
    private const string StepName = "convert";

    // 試験用のイベントと段。実装は `IConsumer` を一切知らない —— それが W2 の要件である。
    public sealed record SampleEvent(string Id);

    public sealed record OtherEvent(string Id);

    public sealed class GoodStep : IPipelineStep<SampleEvent>
    {
        public static string StepName => WolverinePipelineExtensionsTests.StepName;

        public void Handle(SampleEvent message) { }
    }

    // 規則 5 用: 入力型を型から導出できない段（素の IPipelineStep しか実装しない）。
    public sealed class NoInputTypeStep : IPipelineStep
    {
        public static string StepName => WolverinePipelineExtensionsTests.StepName;

        public void Handle(SampleEvent message) { }
    }

    // 規則 6 用: 入力型は申告するが、それを受け取るハンドラメソッドが無い段。
    public sealed class NoHandlerMethodStep : IPipelineStep<SampleEvent>
    {
        public static string StepName => WolverinePipelineExtensionsTests.StepName;

        // Wolverine のハンドラ名ではない。かつ引数の型も違う。
        public void Process(OtherEvent message) { }
    }

    // #1004（#441 E1 の追試）用のダミー段とイベント。
    //
    // 🔴 **型名をわざと `Consumer` で終わらせている。** Wolverine の規約探索（`HandlerQuery`）は
    // 既定で型名サフィックス `Handler` / `Consumer` を含み判定に使う（decompile で確認。作業仕様書
    // `.ai-context/specs/20260823_issue-1004_pipeline-enabled-false-gate.md` 参照）。上の `GoodStep` 等は
    // どれもこのサフィックスを持たないため、**既存試験は一度も規約探索に拾われる型を対象にしていない**。
    // 本ファイルの実際の生産段（`RawDocumentFetchedConsumer` 等）と同じ経路を再現するには、
    // ダミー側もこの命名規則に合わせる必要がある。
    public sealed record GateProbeEvent(string Id);

    public sealed class GateProbeConsumer : IPipelineStep<GateProbeEvent>
    {
        public const string GateStepName = "gate-probe";
        public static string StepName => GateStepName;

        public void Handle(GateProbeEvent message) { }
    }

    private static PipelineOptions GateProbeDeclared(bool enabled)
        => new()
        {
            Steps =
            [
                new PipelineStepOptions
                {
                    Name = GateProbeConsumer.GateStepName,
                    Service = "conversion-service",
                    Consumer = typeof(GateProbeConsumer).FullName!,
                    Input = nameof(GateProbeEvent),
                    Outputs = [],
                    Enabled = enabled,
                },
            ],
        };

    // `HandlerDiscovery.FindCalls` は internal だが、**「明示登録（ExplicitTypes）と規約探索
    // （HandlerQuery.Find）を合成して、実際に登録される (型, ハンドラメソッド) の組」を計算する
    // まさにその関数**である（decompile で確認）。`RegisteredTypes`（ExplicitTypes だけを読む既存
    // ヘルパ）では規約探索側の除外が効いているかを見られないため、本試験群だけこちらを使う。
    private static (Type Type, MethodInfo Method)[] FindCalls(WolverineOptions options)
    {
        var method = typeof(HandlerDiscovery).GetMethod(
            "FindCalls", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "HandlerDiscovery.FindCalls が見つかりません。"
                + " Wolverine の版更新で登録候補の計算箇所が変わった可能性があります。");
        var result = method.Invoke(options.Discovery, [options]);
        return (ValueTuple<Type, MethodInfo>[])result!;
    }

    [Fact]
    public void 規約探索の前提_GateProbeConsumerは明示登録なしでも規約探索に拾われる()
    {
        // FR-14, NFR: 後続の 2 試験が成立する前提の検証。ここが崩れていると
        // 「規約探索が拾わない型を除外した」だけの空証明になり、規則 8 の検出力が無いまま緑になる。
        var options = new WolverineOptions();
        options.Discovery.IncludeAssembly(typeof(GateProbeConsumer).Assembly);

        var calls = FindCalls(options);

        calls.Select(c => c.Type).Should().Contain(typeof(GateProbeConsumer),
            "型名サフィックス 'Consumer' により Wolverine の規約探索が既定で拾うはずである"
            + "（本試験群はこの前提の上で規則 8 の除外を測る）");
    }

    [Fact]
    public void 規則8_規約探索が効いている条件でも無効化した段は探索結果に現れない()
    {
        // FR-14, NFR: #441 E1 で見つけた本番欠陥の直接追試（#1004）。
        // 🔴 **`RegisteredTypes`（ExplicitTypes のみ）ではなく `FindCalls`（規約探索込みの合成結果）を見る。**
        // 「IncludeType を呼ばない」だけでは段は無効にならない —— 規約探索は明示登録と独立に
        // アセンブリを走査するため、`AddPlatformWolverineStep` が規則 8 で `CustomizeHandlerDiscovery`
        // の Excludes を通じて明示的に除外していなければ、enabled:false でも購読が生える。
        var options = new WolverineOptions();
        options.AddPlatformWolverineStep<GateProbeConsumer>(GateProbeDeclared(enabled: false));
        // 🔴 走査対象の決定は環境依存（#441 E1: 同じコードが Windows では拾わず Linux の CI では拾った）。
        // ここで明示的にアセンブリを足し、どの環境でも「規約探索が対象を見つけ得る」側へ固定する。
        options.Discovery.IncludeAssembly(typeof(GateProbeConsumer).Assembly);

        var calls = FindCalls(options);

        calls.Select(c => c.Type).Should().NotContain(typeof(GateProbeConsumer),
            "pipeline.json の enabled:false は、明示登録だけでなく規約探索からも段を除外しなければならない");
    }

    [Fact]
    public void 規則9_規約探索が効いている条件で有効な段は探索結果に現れる()
    {
        // FR-14, NFR: 規則 8 試験の対照条件。これが無いと「常に見えない」壊れた実装でも
        // 規則 8 試験だけは緑になり得る。
        var options = new WolverineOptions();
        options.AddPlatformWolverineStep<GateProbeConsumer>(GateProbeDeclared(enabled: true));
        options.Discovery.IncludeAssembly(typeof(GateProbeConsumer).Assembly);

        var calls = FindCalls(options);

        calls.Select(c => c.Type).Should().Contain(typeof(GateProbeConsumer));
    }

    private static PipelineOptions Declared(
        string name = StepName,
        string? consumer = null,
        string input = nameof(SampleEvent),
        bool enabled = true,
        string? queue = null)
        => new()
        {
            Steps =
            [
                new PipelineStepOptions
                {
                    Name = name,
                    Service = "conversion-service",
                    Consumer = consumer ?? typeof(GoodStep).FullName!,
                    Input = input,
                    Outputs = ["DocumentNormalized"],
                    Enabled = enabled,
                    Queue = queue,
                },
            ],
        };

    // 登録の観測点。`HandlerDiscovery.ExplicitTypes` は internal のためリフレクションで読む。
    // 名前が消えれば下の throw で落ちる（版更新で静かに no-op 化しない）。
    private static Type[] RegisteredTypes(WolverineOptions options)
    {
        var property = typeof(Wolverine.Configuration.HandlerDiscovery).GetProperty(
            "ExplicitTypes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "HandlerDiscovery.ExplicitTypes が見つかりません。"
                + " Wolverine の版更新で登録の観測点が変わった可能性があります。");
        return [.. (IEnumerable<Type>)property.GetValue(options.Discovery)!];
    }

    // 実装側の一覧も internal のためリフレクションで読む。
    private static string[] DeclaredHandlerMethodNames()
    {
        var field = typeof(WolverinePipelineExtensions).GetField(
            "HandlerMethodNames", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "WolverinePipelineExtensions.HandlerMethodNames が見つかりません。");
        return (string[])field.GetValue(null)!;
    }

    [Fact]
    public void 規則1_宣言が無ければ段は既定で登録される()
    {
        var options = new WolverineOptions();

        var step = options.AddPlatformWolverineStep<GoodStep>(new PipelineOptions());

        step.Should().BeNull("宣言が無いので返す段宣言も無い");
        RegisteredTypes(options).Should().Contain(typeof(GoodStep));
    }

    [Fact]
    public void 規則2_宣言があるのに段が未宣言なら起動失敗する()
    {
        var options = new WolverineOptions();

        var act = () => options.AddPlatformWolverineStep<GoodStep>(Declared(name: "other-step"));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{StepName}*");
    }

    [Fact]
    public void 規則3_consumer宣言が空なら起動失敗する()
    {
        var options = new WolverineOptions();

        var act = () => options.AddPlatformWolverineStep<GoodStep>(Declared(consumer: ""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*consumer*");
    }

    [Fact]
    public void 規則3_consumer宣言が実装と不一致なら起動失敗する()
    {
        var options = new WolverineOptions();

        var act = () => options.AddPlatformWolverineStep<GoodStep>(
            Declared(consumer: "Some.Other.Handler"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*consumer*");
    }

    [Fact]
    public void 規則4_input宣言が空なら起動失敗する()
    {
        var options = new WolverineOptions();

        var act = () => options.AddPlatformWolverineStep<GoodStep>(Declared(input: ""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*input*");
    }

    [Fact]
    public void 規則5_入力型を導出できないなら起動失敗する()
    {
        // 🔴 **本試験群の中心。** MassTransit 経路はここで照合を飛ばして登録まで進む。
        // Wolverine 経路は「導出できないこと自体」を起動失敗にする。
        var options = new WolverineOptions();
        var pipeline = Declared(consumer: typeof(NoInputTypeStep).FullName!);

        var act = () => options.AddPlatformWolverineStep<NoInputTypeStep>(pipeline);

        act.Should().Throw<InvalidOperationException>()
            // 🔴 **落ちた理由まで見る。** 別の規則で落ちたのを「効いた」と誤読しないため。
            .WithMessage("*IPipelineStep<TIn>*")
            .And.Message.Should().Contain("入力イベント型を導出できません");
    }

    [Fact]
    public void 規則5_入力型を導出できない段は登録もされない()
    {
        // 起動失敗した段が登録だけ済んでいる、という中途半端な状態を作らない。
        var options = new WolverineOptions();
        var pipeline = Declared(consumer: typeof(NoInputTypeStep).FullName!);

        var act = () => options.AddPlatformWolverineStep<NoInputTypeStep>(pipeline);

        act.Should().Throw<InvalidOperationException>();
        RegisteredTypes(options).Should().NotContain(typeof(NoInputTypeStep));
    }

    [Fact]
    public void 規則6_入力型を受けるハンドラメソッドが無ければ起動失敗する()
    {
        // IPipelineStep<TIn> は自己申告にすぎない。これが無いと規則 7 は
        // 「宣言 対 別の宣言」の照合になり、実装と突き合わせたことにならない。
        var options = new WolverineOptions();
        var pipeline = Declared(consumer: typeof(NoHandlerMethodStep).FullName!);

        var act = () => options.AddPlatformWolverineStep<NoHandlerMethodStep>(pipeline);

        act.Should().Throw<InvalidOperationException>()
            .And.Message.Should().Contain("ハンドラメソッド");
    }

    [Fact]
    public void 規則7_input宣言が実装の購読イベントと不一致なら起動失敗する()
    {
        var options = new WolverineOptions();

        var act = () => options.AddPlatformWolverineStep<GoodStep>(
            Declared(input: nameof(OtherEvent)));

        act.Should().Throw<InvalidOperationException>()
            .And.Message.Should().Contain(nameof(SampleEvent));
    }

    [Fact]
    public void 規則7_input宣言の大文字小文字が違えば起動失敗する()
    {
        // 🔴 **変異試験 D が開けた穴を塞ぐ試験。** 照合を OrdinalIgnoreCase へ緩める変異は、
        // 当初 37 件すべて緑のまま素通りした（＝検出力ゼロ）。`input` は C# の型名であり
        // 大文字小文字は意味を持つので、ここは Ordinal でなければならない。
        var options = new WolverineOptions();

        var act = () => options.AddPlatformWolverineStep<GoodStep>(
            Declared(input: nameof(SampleEvent).ToLowerInvariant()));

        act.Should().Throw<InvalidOperationException>()
            .And.Message.Should().Contain(nameof(SampleEvent));
    }

    [Fact]
    public void 規則8_無効化した段は登録されない()
    {
        var options = new WolverineOptions();

        var step = options.AddPlatformWolverineStep<GoodStep>(Declared(enabled: false));

        step.Should().NotBeNull();
        RegisteredTypes(options).Should().NotContain(typeof(GoodStep));
    }

    [Fact]
    public void 規則9_有効な段は登録され宣言が返る()
    {
        var options = new WolverineOptions();

        var step = options.AddPlatformWolverineStep<GoodStep>(Declared(queue: "DocumentUpdated"));

        RegisteredTypes(options).Should().Contain(typeof(GoodStep));
        // queue 宣言は戻り値で呼び出し側へ渡す（黙って無視すると「宣言したのに効かない」形になる）。
        step!.Queue.Should().Be("DocumentUpdated");
    }

    [Fact]
    public void 型制約にMassTransitのIConsumerを持たない()
    {
        // W2 の要件そのもの。MassTransit の型を取り込まずに制約名で照合する
        // （PartialMigrationSafetyValveTests と同じ作法）。
        var method = typeof(WolverinePipelineExtensions)
            .GetMethod(nameof(WolverinePipelineExtensions.AddPlatformWolverineStep),
                BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();

        var constraints = method!.GetGenericArguments()[0]
            .GetGenericParameterConstraints().Select(c => c.FullName!).ToArray();

        constraints.Should().Contain(typeof(IPipelineStep).FullName!);
        constraints.Should().NotContain("MassTransit.IConsumer");
    }

    [Fact]
    public void ハンドラメソッド名の一覧がWolverineの実装と一致する()
    {
        // 🔴 複写した一覧が版更新でずれたらここで落ちる。
        // ずれたまま気づかないと「Wolverine は受け付けるのに本経路だけが拒否する」偽の起動失敗になる。
        var field = typeof(Wolverine.Configuration.HandlerDiscovery)
            .GetField("_validMethods", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "HandlerDiscovery._validMethods が見つかりません。"
                + " Wolverine の版更新でハンドラ名の観測点が変わった可能性があります。");

        var actual = (string[])field.GetValue(new WolverineOptions().Discovery)!;

        DeclaredHandlerMethodNames().Should().BeEquivalentTo(actual);
    }
}
