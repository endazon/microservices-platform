using System.Reflection;
using AwesomeAssertions;
using JasperFx.CodeGeneration.Model;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;
using Wolverine.RabbitMQ;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// ADR-0027 移行チェックリスト 手順 3・4・5 が共通ヘルパで実際に効いていることを固定する。
//
// 🔴 **どの試験も「適用前の既定値」を先に assert する。** 手順 4・5 は「設定してもしなくても
// 起動し、ビルドもテストも通る」種類の設定であり、適用後の値だけを見ると **ヘルパが何もしなくても
// 既定値がたまたま目的の値なら緑になる**（＝変異が当たらない試験になる）。#883 で
// 「置換が無言で no-op になり、証明したい命題を何も証明していなかった」実例を踏んでいる。
public class WolverineExtensionsTests
{
    // 手順 4 が変える状態は internal プロパティにしか現れないため、リフレクションで観測する。
    // 名前が消えれば GetProperty が null を返し、下の assert が落ちる（版更新で静かに no-op 化しない）。
    private static PropertyInfo LocalRoutingDisabledProperty =>
        typeof(WolverineOptions).GetProperty(
            "LocalRoutingConventionDisabled",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "WolverineOptions.LocalRoutingConventionDisabled が見つかりません。"
            + " Wolverine の版更新で手順 4 の観測点が変わった可能性があります（ADR-0027 手順 4）。");

    [Fact]
    public void 手順4_適用前は規約ローカルルーティングが有効である()
    {
        // 変異が当たることの前提条件。既定が既に true なら手順 4 の試験は何も証明しない。
        LocalRoutingDisabledProperty.GetValue(new WolverineOptions()).Should().Be(false);
    }

    [Fact]
    public void 手順4_共通ヘルパが規約ローカルルーティングを無効化する()
    {
        var options = new WolverineOptions();

        options.UsePlatformMessagingDefaults();

        LocalRoutingDisabledProperty.GetValue(options).Should().Be(true);
    }

    [Fact]
    public void 手順5_適用前の既定はサービスロケーション禁止である()
    {
        // 既定が NotAllowed だからこそ手順 5 が要る（internal 実装型に依存するハンドラが
        // 最初のメッセージ受信時に落ちる）。ここが AlwaysAllowed に変わったら手順 5 は不要になる。
        new WolverineOptions().ServiceLocationPolicy.Should().Be(ServiceLocationPolicy.NotAllowed);
    }

    [Fact]
    public void 手順5_共通ヘルパがサービスロケーションを常時許可へ変える()
    {
        var options = new WolverineOptions();

        options.UsePlatformMessagingDefaults();

        options.ServiceLocationPolicy.Should().Be(ServiceLocationPolicy.AlwaysAllowed);
    }

    [Fact]
    public void 手順3_リスニングキュー名にサービス名を前置する()
    {
        WolverineExtensions.PlatformQueueName("wiki-service", "DocumentUpdated")
            .Should().Be("wiki-service.DocumentUpdated");
    }

    // 実際に設定されたキュー名を Wolverine の公開 API から読む。
    //
    // 🔴 **純粋関数（PlatformQueueName）の試験だけでは足りない。** #897 の監査が実測で示したとおり、
    // 適用点が `ListenToRabbitQueue(queueName)` へ退化しても——つまり前置が丸ごと消えても——
    // ビルドも 13 件のテストも検査器もすべて緑のままだった。**封じ込めるべきは名前の作り方ではなく
    // 適用点である**（IADR-0233 決定 1）以上、適用点そのものを観測しなければ守れていない。
    private static string[] RabbitQueueNamesOf(WolverineOptions options) =>
        [.. options.Transports
            .Single(t => t.Protocol == "rabbitmq")
            .Endpoints()
            .Select(e => e.EndpointName)];

    [Fact]
    public void 手順3_適用点が実際に前置つきのキューを購読する()
    {
        var options = new WolverineOptions();
        options.UseRabbitMq();

        options.ListenToPlatformQueue("wiki-service", "DocumentUpdated");

        RabbitQueueNamesOf(options).Should().Contain("wiki-service.DocumentUpdated");
    }

    [Fact]
    public void 手順3_適用点を通せば同一イベントの2購読者が別々のキューになる()
    {
        // 手順 3 が防ぐ退行そのもの。キューが 1 つに潰れると competing consumer になり、
        // 丁度 1 つだけが受信する（＝業務イベントが片方へ届かない）。
        var options = new WolverineOptions();
        options.UseRabbitMq();

        options.ListenToPlatformQueue("ingestion-service", "DocumentUpdated");
        options.ListenToPlatformQueue("wiki-service", "DocumentUpdated");

        RabbitQueueNamesOf(options).Should().Contain(
            ["ingestion-service.DocumentUpdated", "wiki-service.DocumentUpdated"]);
    }

    [Fact]
    public void 手順3_同一イベントを購読する2サービスのキュー名が分かれる()
    {
        // 手順 3 の目的そのもの。正本の pipeline.json では DocumentUpdated を
        // ingestion-service（段 ingest）と wiki-service（段 wiki-sync）が購読する。
        // キュー名が同じになると competing consumer へ退行し、丁度 1 つだけが受信する。
        var ingest = WolverineExtensions.PlatformQueueName("ingestion-service", "DocumentUpdated");
        var wikiSync = WolverineExtensions.PlatformQueueName("wiki-service", "DocumentUpdated");

        ingest.Should().NotBe(wikiSync);
    }

    [Theory]
    [InlineData("", "DocumentUpdated")]
    [InlineData("  ", "DocumentUpdated")]
    [InlineData("wiki-service", "")]
    [InlineData("wiki-service", "  ")]
    public void 手順3_サービス名かキュー名が空なら例外にする(string serviceName, string queueName)
    {
        // 空のサービス名を黙って受けると "-DocumentUpdated" のような前置なしのキュー名ができ、
        // 手順 3 を満たしていないのに満たしたつもりになる。
        var act = () => WolverineExtensions.PlatformQueueName(serviceName, queueName);

        act.Should().Throw<ArgumentException>();
    }
}
