using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Wolverine;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Pipeline;

// FR-14, ADR-0018 (#444): 「接続先コンポーネントの差し替え」＝ポート実装を構成だけで交換できること、
// および交換しても宣言的パイプラインが壊れないこと（issue #444 の退行防止 3 項目目）。
//
// 🔴 **差し替えられると書いてあることと、差し替えが効くことは別である。**
// 本試験はまず「構成を変えると解決される実装が実際に入れ替わる」ことを確かめ、
// そのうえで「入れ替えても段の登録・実効構成の組み立てが 1 つも変わらない」ことを確かめる。
// 前半が無いと、後半は**そもそも何も差し替えていない**まま緑になる。
public class PortSwapCompositionTests
{
    private const string StepName = "convert";

    public sealed record SampleEvent(string Id);

    // ポート（IObjectStorageClient）に依存する段。差し替えの影響を受ける側の代表である。
    public sealed class StorageBackedStep(IObjectStorageClient storage) : IPipelineStep<SampleEvent>
    {
        public static string StepName => PortSwapCompositionTests.StepName;

        public IObjectStorageClient Storage { get; } = storage;

        public void Handle(SampleEvent message) { }
    }

    private static PipelineOptions Declaration() => new()
    {
        Events = ["SampleEvent"],
        Steps =
        [
            new PipelineStepOptions
            {
                Name = StepName,
                Service = "conversion-service",
                Consumer = typeof(StorageBackedStep).FullName!,
                Input = nameof(SampleEvent),
                Outputs = [],
                Enabled = true,
            },
        ],
    };

    // 構成が未設定なら縮退実装、揃っていれば S3 実装。ADR-0018「ポート実装の選択を構成で切替」。
    private static IServiceCollection Compose(bool configured)
    {
        var settings = new Dictionary<string, string?>();
        if (configured)
        {
            settings["ObjectStorage:Endpoint"] = "http://minio.invalid:9000";
            settings["ObjectStorage:AccessKey"] = "test-access-key";
            settings["ObjectStorage:SecretKey"] = "test-secret-key";
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformObjectStorage(configuration);
        services.AddSingleton<StorageBackedStep>();
        return services;
    }

    // FR-14 (#444): 前提の検証。**構成だけでポート実装が実際に入れ替わる。**
    // ここが崩れていると、以降の「壊れない」試験は何も差し替えないまま通る。
    [Fact]
    public void 構成だけでポート実装が入れ替わる()
    {
        using var degraded = Compose(configured: false).BuildServiceProvider();
        using var real = Compose(configured: true).BuildServiceProvider();

        degraded.GetRequiredService<IObjectStorageClient>()
            .Should().BeOfType<NullObjectStorageClient>();
        real.GetRequiredService<IObjectStorageClient>()
            .Should().BeOfType<S3ObjectStorageClient>();
    }

    // FR-14 (#444): ポート実装を差し替えても、段はポートの差し替えを受け取って解決できる
    // （コア改修なしで組み替えられる、の実体）。
    [Fact]
    public void ポートを差し替えても段は解決でき注入される実装だけが変わる()
    {
        using var degraded = Compose(configured: false).BuildServiceProvider();
        using var real = Compose(configured: true).BuildServiceProvider();

        degraded.GetRequiredService<StorageBackedStep>().Storage
            .Should().BeOfType<NullObjectStorageClient>();
        real.GetRequiredService<StorageBackedStep>().Storage
            .Should().BeOfType<S3ObjectStorageClient>();
    }

    // FR-14 (#444): ポートを差し替えても**宣言的な段登録の結果が変わらない**
    // （既存パイプラインが壊れない）。段の登録は宣言だけで決まり、ポート選択に依存しない。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ポートを差し替えても宣言どおりに段が登録される(bool configured)
    {
        _ = Compose(configured);
        var options = new WolverineOptions();

        var step = options.AddPlatformWolverineStep<StorageBackedStep>(Declaration());

        step.Should().NotBeNull("宣言に基づく登録はポートの選択に依存しない");
        step!.Name.Should().Be(StepName);
        step.Input.Should().Be(nameof(SampleEvent));
        step.Enabled.Should().BeTrue();
    }

    // FR-15 (#444): ポートを差し替えても実効構成の段・イベント接続は変わらない
    // （変わるのはポート選択の申告だけである）。
    [Fact]
    public void ポートを差し替えても実効構成の段とイベント接続は変わらない()
    {
        var declaration = Declaration();

        var degraded = AssembleWithPort(declaration, nameof(NullObjectStorageClient));
        var real = AssembleWithPort(declaration, nameof(S3ObjectStorageClient));

        real.Pipeline.Should().BeEquivalentTo(degraded.Pipeline);
        real.EventBindings.Should().BeEquivalentTo(degraded.EventBindings);
        real.Ports.Should().NotBeEquivalentTo(degraded.Ports,
            "差し替えたポート実装は実効構成のポート選択に現れる");
    }

    private static EffectiveConfigDto AssembleWithPort(
        PipelineOptions declaration, string implementation)
    {
        var service = new ServiceIntrospectionDto(
            "conversion-service",
            [
                new StepIntrospectionDto(
                    StepName, typeof(StorageBackedStep).FullName!, nameof(SampleEvent), [], true),
            ],
            [new PortSelectionDto("object-storage", implementation, null)],
            []);

        var collection = new EffectiveCollection(
            [service],
            new HashSet<string> { "conversion-service" },
            new HashSet<string>());

        return ConfigInspectionService.Assemble(
            declaration, collection, new ConfigVersionDto(null, null, null));
    }
}
