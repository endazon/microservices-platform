using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, ADR-0018, ADR-0027, IADR-0239 決定 2 (#901): 自己申告の**登録ヘルパ**を固定する。
//
// 🔴 **本ファイルが塞ぐ穴。** AddPlatformIntrospection / IntrospectionBuilder を実際に実行する
// テストは、Docker 必須の Knowledge.IntegrationTests（既定で skip）と、
// PartialMigrationSafetyValveTests の reflection 1 件しか無かった。
// つまり **Docker が無い CI 経路では 1 行も実行されない**。
//
// 自己申告は「宣言と実装が一致しているか」を外から検証するための唯一の観測点である
// （ドリフト検出はこれを集めて突合する）。**ここが黙って間違うと、検証そのものが嘘になる** ——
// ドリフト検出は「一致している」と報告し続ける。
//
// 🔴 対象は Wolverine 段・ポート・コネクタの申告に限る。**MassTransit 版の AddStep<TConsumer> は
// 触らない**（C3 / Wolverine 移行チェーンの射程であり、並行トラックと衝突する）。
public class IntrospectionRegistrationTests
{
    private sealed record ConvertRequested(string DocumentId);

    // 入力イベント型を型で申告する正しい Wolverine 段。
    private sealed class ConvertStep : IPipelineStep<ConvertRequested>
    {
        public static string StepName => "convert";
    }

    // 🔴 IPipelineStep<TIn> を実装し忘れた段（＝入力イベント型を導出できない）。
    private sealed class UndeclaredInputStep : IPipelineStep
    {
        public static string StepName => "undeclared";
    }

    private static PipelineOptions Declaration(params PipelineStepOptions[] steps) =>
        new() { Steps = [.. steps] };

    private static PipelineStepOptions Step(
        string name, bool enabled = true, params string[] outputs) =>
        new() { Name = name, Service = "conversion-service", Input = "ConvertRequested", Outputs = [.. outputs], Enabled = enabled };

    private static ServiceIntrospectionDto Resolve(
        string service, PipelineOptions pipeline, Action<IntrospectionBuilder>? configure)
    {
        var services = new ServiceCollection();
        services.AddPlatformIntrospection(service, pipeline, configure);
        return services.BuildServiceProvider().GetRequiredService<ServiceIntrospectionDto>();
    }

    // ── 登録そのもの ──────────────────────────────────────────────────────────

    // FR-15: 自己申告は singleton として解決できる（MapPlatformIntrospection が
    // GetRequiredService で取り出すため、登録が漏れるとエンドポイントが 500 になる）。
    [Fact]
    public void 自己申告はサービス名を伴うシングルトンとして登録される()
    {
        var services = new ServiceCollection();

        services.AddPlatformIntrospection("conversion-service", Declaration(), configure: null);

        var provider = services.BuildServiceProvider();
        var report = provider.GetRequiredService<ServiceIntrospectionDto>();
        report.Service.Should().Be("conversion-service");
        report.Steps.Should().BeEmpty();
        report.Ports.Should().BeEmpty();
        report.Connectors.Should().BeEmpty();
        provider.GetRequiredService<ServiceIntrospectionDto>().Should().BeSameAs(report,
            "自己申告は起動時に確定する不変値であり、要求ごとに作り直さない");
    }

    // ── 段の申告（宣言からの解決）────────────────────────────────────────────

    // FR-15: 段名・実装型の完全名・入力イベント型名を申告し、
    // 有効状態と出力は**宣言（pipeline.json）から**解決する。
    [Fact]
    public void Wolverine段は宣言から有効状態と出力を解決して申告する()
    {
        var report = Resolve("conversion-service",
            Declaration(Step("convert", enabled: true, "DocumentNormalized", "ThumbnailRequested")),
            b => b.AddWolverineStep<ConvertStep>());

        var step = report.Steps.Should().ContainSingle().Which;
        step.Name.Should().Be("convert");
        step.Consumer.Should().Be(typeof(ConvertStep).FullName);
        step.Input.Should().Be(nameof(ConvertRequested),
            "入力イベント型は IPipelineStep<TIn> から導出する（宣言との突合の基準）");
        step.Outputs.Should().BeEquivalentTo(["DocumentNormalized", "ThumbnailRequested"]);
        step.Enabled.Should().BeTrue();
    }

    // FR-15: 宣言で無効化された段は無効として申告する（上の試験の対照条件）。
    // 常に true を申告する実装だと、無効な段が「動いているはず」として突合され、
    // ドリフト検出が実態と逆の結論を出す。
    [Fact]
    public void 宣言で無効化された段は無効として申告する()
    {
        var report = Resolve("conversion-service",
            Declaration(Step("convert", enabled: false)),
            b => b.AddWolverineStep<ConvertStep>());

        report.Steps.Should().ContainSingle().Which.Enabled.Should().BeFalse();
    }

    // FR-15: 宣言に無い段は既定登録＝有効として申告する（AddPlatformPipelineStep の登録規則 1 と整合）。
    // 宣言が空のローカル・テスト環境で「全段が無効」と申告されると、実効構成が空になる。
    [Fact]
    public void 宣言に無い段は既定で有効かつ出力なしとして申告する()
    {
        var report = Resolve("conversion-service", Declaration(),
            b => b.AddWolverineStep<ConvertStep>());

        var step = report.Steps.Should().ContainSingle().Which;
        step.Enabled.Should().BeTrue("宣言なし＝既定配線で動作する（登録規則 1）");
        step.Outputs.Should().BeEmpty();
    }

    // FR-15: 宣言の出力が空なら空のまま申告する（null 合体で宣言側を握り潰さない）。
    [Fact]
    public void 宣言の出力が空なら空のまま申告する()
    {
        var report = Resolve("conversion-service",
            Declaration(Step("convert", enabled: true)),
            b => b.AddWolverineStep<ConvertStep>());

        report.Steps.Should().ContainSingle().Which.Outputs.Should().BeEmpty();
    }

    // ── 🔴 誤設定は起動時に落とす（IADR-0239 決定 2）────────────────────────

    // 🔴 ADR-0027, IADR-0239 決定 2: **入力イベント型を導出できない段は起動を止める。**
    //
    // MassTransit 版は導出できないと `?? string.Empty` で **空文字を自己申告する**。
    // 空文字の input は宣言（"ConvertRequested"）と一致しないが、それが判るのは
    // ドリフト検出が走る実行時であり、しかも警告 1 行で流れる。
    // Wolverine 段では**起動時に落とす** —— 誤設定を deploy 前に止めるための防壁である。
    //
    // ここが `?? string.Empty` へ退行すると、**壊れたことに気づく手段が無くなる**
    // （起動は成功し、自己申告も返り、ドリフト警告だけが増える）。
    [Fact]
    public void 入力イベント型を導出できない段は起動時に例外で止まる()
    {
        var services = new ServiceCollection();

        var act = () => services.AddPlatformIntrospection(
            "conversion-service", Declaration(), b => b.AddWolverineStep<UndeclaredInputStep>());

        act.Should().Throw<InvalidOperationException>(
                "空文字で自己申告するとドリフト検出が実行時まで気づけない（IADR-0239 決定 2）")
            .WithMessage("*undeclared*")
            .And.Message.Should().Contain(typeof(UndeclaredInputStep).FullName!,
                "どの実装が壊れているかを名指ししないと、起動失敗から原因へ辿れない");
    }

    // ── ポート・コネクタの申告 ────────────────────────────────────────────────

    // FR-15: 選択中のポート実装と接続先を申告する（ポート差し替えの実効値の観測点）。
    [Fact]
    public void ポートは実装名と接続先を伴って申告される()
    {
        var report = Resolve("conversion-service", Declaration(), b => b
            .AddPort("object-storage", "S3ObjectStorageClient", "minio:9000")
            .AddPort("vector-store", "QdrantIngestionVectorStore"));

        report.Ports.Should().HaveCount(2);
        report.Ports[0].Should().BeEquivalentTo(
            new PortSelectionDto("object-storage", "S3ObjectStorageClient", "minio:9000"));
        report.Ports[1].Target.Should().BeNull("接続先を持たないポートもある（既定 null）");
    }

    // FR-15: コネクタは有効・無効の**両方**を申告する（09_datasource-connectors の管理情報）。
    // 無効なものを落とすと「設定されていない」と区別できなくなる。
    [Fact]
    public void コネクタは無効なものも状態つきで申告される()
    {
        var report = Resolve("datasource-service", Declaration(), b => b
            .AddConnector("obsidian", enabled: true)
            .AddConnector("confluence", enabled: false));

        report.Connectors.Should().BeEquivalentTo([
            new ConnectorDto("obsidian", true),
            new ConnectorDto("confluence", false),
        ]);
    }

    // FR-15: ビルダは連鎖でき、複数段の申告順を保つ。
    [Fact]
    public void ビルダは連鎖でき申告の順序を保つ()
    {
        var report = Resolve("conversion-service",
            Declaration(Step("convert"), Step("undeclared")),
            b => b.AddWolverineStep<ConvertStep>().AddPort("p", "impl").AddConnector("c", true));

        report.Steps.Should().ContainSingle().Which.Name.Should().Be("convert");
        report.Ports.Should().ContainSingle();
        report.Connectors.Should().ContainSingle();
    }

    // FR-15: configure を省略しても登録は成立する（段を持たないサービスも自己申告を返す）。
    [Fact]
    public void 構成デリゲート省略時も空の自己申告として登録される()
    {
        var report = Resolve("llm-gateway", Declaration(Step("convert")), configure: null);

        report.Service.Should().Be("llm-gateway");
        report.Steps.Should().BeEmpty("宣言に段があっても、申告するのは自分がホストする段だけである");
    }
}
