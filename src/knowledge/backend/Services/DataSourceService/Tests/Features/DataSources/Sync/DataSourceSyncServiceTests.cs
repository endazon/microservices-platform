using DataSourceService.Infrastructure.ExternalServices;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using DataSourceService.Features.DataSources;
using DataSourceService.Features.DataSources.Sync;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataSourceService.Tests.Features.DataSources.Sync;

// FR-01, UC-04, IADR-0051, claude-review #220（🔴）: 増分 watermark（LastSyncedAt）は完全成功時のみ前進し、
// discover 失敗・一部 fetch 失敗時は前進しないこと（＝失敗ファイルが次回再試行対象に残る）を検証する。
public sealed class DataSourceSyncServiceTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public async Task Sync_DiscoverFailure_DoesNotAdvanceWatermark_AndTracksFailure()
    {
        using var scope = factory.Services.CreateScope();
        var svc = BuildService(scope, new DiscoverThrowingConnector());
        var source = DataSource.Create("boom", "filesystem", "");
        source.LastSyncedAt.Should().BeNull();

        var result = await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        result.ConnectorAvailable.Should().BeTrue();
        result.DiscoverSucceeded.Should().BeFalse();
        result.ShouldAdvanceWatermark.Should().BeFalse();
        source.LastSyncedAt.Should().BeNull("discover 失敗時は watermark を進めず次回再試行できるようにする");
        // SC-06（Q14 / #537）: 計数はエンティティに載る（永続化され SC-06 が読む）。
        source.ConsecutiveFailureCount.Should().Be(1);
        source.LastSyncError.Should().NotBeNull();
        source.LastSyncErrorAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Sync_PartialFetchFailure_DoesNotAdvanceWatermark()
    {
        using var scope = factory.Services.CreateScope();
        var svc = BuildService(scope, new FetchThrowingConnector());
        var source = DataSource.Create("partial", "filesystem", "");

        var result = await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        result.Failed.Should().Be(1);
        result.ShouldAdvanceWatermark.Should().BeFalse();
        source.LastSyncedAt.Should().BeNull("一部取得失敗時も watermark を進めない（失敗分を次回再試行）");
    }

    [Fact]
    public async Task Sync_FullSuccess_AdvancesWatermark()
    {
        using var scope = factory.Services.CreateScope();
        var dir = CreateTempDirWithFile("ok.md", "ok");
        var svc = BuildService(scope, new FileSystemConnector(NullLogger<FileSystemConnector>.Instance));
        var source = DataSource.Create("share", "filesystem", "",
            new Dictionary<string, string> { ["rootPath"] = dir });

        var result = await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        result.Fetched.Should().Be(1);
        result.Failed.Should().Be(0);
        result.ShouldAdvanceWatermark.Should().BeTrue();
        source.LastSyncedAt.Should().NotBeNull("完全成功時は watermark を前進させる");
    }

    // FR-01, UC-04 例外フロー, SC-06（Q14 / #537）: 継続失敗のしきい値は**再試行上限に達した時点**である
    // （計画 §SC-06。hi-fi の「3/5」は「5 回中 3 回目」という進捗表示であって、しきい値 3 の意味ではない）。
    // 従前の実装は独自に 3 を持っており、計画が「実装が決めることになる」として排した状態だった。
    [Fact]
    public async Task Sync_RepeatedFailures_ReachAlertThresholdAtRetryLimit()
    {
        using var scope = factory.Services.CreateScope();
        var svc = BuildService(scope, new DiscoverThrowingConnector());
        var source = DataSource.Create("flaky", "filesystem", "");

        // しきい値の 1 つ手前までは未到達である。
        for (var i = 1; i < DataSourceSyncService.AlertThreshold; i++)
        {
            await svc.SyncAsync(source, TestContext.Current.CancellationToken);
            source.ConsecutiveFailureCount.Should().Be(i);
            source.ConsecutiveFailureCount.Should().BeLessThan(DataSourceSyncService.AlertThreshold);
        }

        await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        source.ConsecutiveFailureCount.Should().Be(DataSourceSyncService.AlertThreshold);
        DataSourceSyncService.AlertThreshold.Should().Be(DataSourceSyncHealth.DefaultRetryLimit,
            "しきい値と再試行上限は同一の定数である（2 つ持つと片方が黙って古くなる）");
    }

    // SC-06（Q14 / #537）: 完全成功で健全性が初期状態へ戻る。**直近エラーも消す** ——
    // 残すと「正常なのに ⚠ の材料が残っている」状態になり、画面の判断が割れる。
    [Fact]
    public async Task Sync_SuccessAfterFailures_ClearsHealth()
    {
        using var scope = factory.Services.CreateScope();
        var dir = CreateTempDirWithFile("ok.md", "ok");
        var source = DataSource.Create("recovering", "filesystem", "",
            new Dictionary<string, string> { ["rootPath"] = dir });

        await BuildService(scope, new DiscoverThrowingConnector()).SyncAsync(source, TestContext.Current.CancellationToken);
        source.ConsecutiveFailureCount.Should().Be(1);
        source.LastSyncError.Should().NotBeNull();

        await BuildService(scope, new FileSystemConnector(NullLogger<FileSystemConnector>.Instance))
            .SyncAsync(source, TestContext.Current.CancellationToken);

        source.ConsecutiveFailureCount.Should().Be(0);
        source.LastSyncError.Should().BeNull();
        source.LastSyncErrorAt.Should().BeNull();
    }

    // FR-05, IADR-0053: 直近エラーは**保存の時点で**マスクされる（表示の時点ではない）。
    [Fact]
    public async Task Sync_Failure_StoresRedactedError()
    {
        using var scope = factory.Services.CreateScope();
        var svc = BuildService(scope, new SecretLeakingConnector());
        var source = DataSource.Create("leaky", "filesystem", "");

        await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        source.LastSyncError.Should().NotBeNull();
        source.LastSyncError.Should().NotContain("hunter2", "接続文字列の秘密が平文で保存されてはならない");
        source.LastSyncError.Should().Contain("***");
    }

    // FR-01, UC-04, SC-05, SC-09, #637: **取り込み経路はタグを生成しない**
    // （計画確定・2026-08-09。利用者裁定 planning#304）。
    //
    // 従前は**親フォルダ名をタグへ写していた**（`BuildTags`）。**その挙動にはテストが 1 件も無く**、
    // 削除しても既存 473 件は 1 件も落ちなかった（実測）。**だからここで固定する。**
    //
    // フォルダ名をタグにすると**ファイルサーバーのディレクトリ名がそのまま辞書になる**うえ、
    // 使用件数が登録の瞬間に 1 件以上となり、SC-09 の削除拒否で**管理者が消せなくなる**。
    [Fact]
    public async Task Sync_DoesNotTurnFolderNameIntoTag()
    {
        using var scope = factory.Services.CreateScope();
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        var dir = CreateTempDirWithFile("ok.md", "ok");
        var svc = BuildService(scope, new FileSystemConnector(NullLogger<FileSystemConnector>.Instance));
        var source = DataSource.Create("share", "filesystem", "",
            new Dictionary<string, string> { ["rootPath"] = dir });

        await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        var published = bus.PublishedOf<RawDocumentFetched>()
            .Where(m => m.SourceId == source.Id)
            .ToList();

        published.Should().NotBeEmpty("同期は原本取得イベントを発行する");
        published.Should().OnlyContain(m => m.Tags.Count == 0,
            "取り込み経路はタグを生成しない。ソースのメタ（フォルダ等）は ABAC 基本属性側で運ぶ");
        // 属性側は従来どおり運ばれている（タグを止めただけで、メタが失われたのではない）。
        published.Should().OnlyContain(m => m.Attributes.Count > 0,
            "ソースのメタは ABAC 基本属性として運ばれ続ける");
    }

    // FR-05, UC-04, #752 段 1: **配管を入れても挙動が変わらないこと**を固定する。
    //
    // 本段は属性の解決を「ソース単位で 1 回」から「アイテムごと」へ移した。**どのコネクタも
    // `UpdatedBy` を載せないので、発行される属性は移行前と同一でなければならない。**
    // これが崩れると、値を載せる前の段階で既に退行していることになる。
    [Fact]
    public async Task Sync_WhenConnectorCarriesNoUpdater_AttributesAreUnchanged()
    {
        using var scope = factory.Services.CreateScope();
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        var svc = BuildService(scope, new TwoItemConnector(updatedBy: null));
        var source = DataSource.Create("share", "filesystem", "");

        await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        var published = bus.PublishedOf<RawDocumentFetched>()
            .Where(m => m.SourceId == source.Id)
            .ToList();

        published.Should().HaveCount(2);
        // 予約値へ倒れる（コネクタが更新者を運ばないため。計画「解決できないとき」）。
        published.Should().OnlyContain(m => m.Attributes[DataSource.OwnerKey] == DataSource.UnresolvedOwner,
            "更新者を運ばないコネクタでは owner は予約値のままである（配管を入れても変わらない）");
        // アイテムごとに解決するようになっても、2 件が同じ内容を受け取ることは変わらない。
        published[0].Attributes.Should().BeEquivalentTo(published[1].Attributes,
            "アイテム単位の上書きが 1 件も無いなら、全アイテムが同じ属性を受け取る");
    }

    // FR-05, UC-04, #752 段 1: アイテムが更新者を運んできたら、**予約値より優先**して載る。
    //
    // 🔴 現時点でこれを満たすコネクタは無い。4 実装のうち `filesystem` / `wiki` / `saas` の 3 本は
    // 構造上取れず、`db` の 1 本は載せられるが**載せてはならない** —— 解決順（① Keycloak 検索 →
    // ② 写像表 → 予約値。裁定 2026-08-16）が**実装されていない**ため、生の値がそのまま `owner` に
    // なるからである（#752。詳細は `DataSourceSyncService.PerItemAttributes` の注記）。
    // **経路が生きていることだけ**をスタブで固定する。
    [Fact]
    public async Task Sync_WhenItemCarriesUpdater_OwnerBeatsReservedValue()
    {
        using var scope = factory.Services.CreateScope();
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        var svc = BuildService(scope, new TwoItemConnector(updatedBy: "alice"));
        var source = DataSource.Create("share", "filesystem", "");

        await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        var published = bus.PublishedOf<RawDocumentFetched>()
            .Where(m => m.SourceId == source.Id)
            .ToList();

        published.Should().HaveCount(2);
        published.Should().OnlyContain(m => m.Attributes[DataSource.OwnerKey] == "alice",
            "アイテムが運んできた更新者は予約値 system より優先する");
    }

    // FR-05, UC-04, #752 段 1: **明示指定はアイテム単位の値にも負けない。**
    //
    // 既存規約「明示指定は上書きしない」（`Create_WithExplicitOwner_PreservesValue`）を、
    // 新しい経路が破っていないことを固定する。**優先順位は 明示 > アイテム > 予約値**である。
    [Fact]
    public async Task Sync_WhenSourceHasExplicitOwner_ItemUpdaterDoesNotOverrideIt()
    {
        using var scope = factory.Services.CreateScope();
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        var svc = BuildService(scope, new TwoItemConnector(updatedBy: "alice"));
        // 🔴 名前付き引数で渡す。4 番目の位置引数は `config` であり `defaultAttributes` ではない
        // （位置で渡して 1 度取り違えた。属性ではなく接続設定に入り、テストが誤って落ちた）。
        var source = DataSource.Create("share", "filesystem", "",
            defaultAttributes: new Dictionary<string, string> { [DataSource.OwnerKey] = "explicit-owner" });

        await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        var published = bus.PublishedOf<RawDocumentFetched>()
            .Where(m => m.SourceId == source.Id)
            .ToList();

        published.Should().HaveCount(2);
        published.Should().OnlyContain(m => m.Attributes[DataSource.OwnerKey] == "explicit-owner",
            "データソースの明示指定はアイテム単位の更新者より強い");
    }

    // FR-05, UC-04, #752 段 1: **アイテムごとに違う更新者が、混ざらずにそれぞれへ載る。**
    //
    // これが「ソース単位で 1 回だけ計算していた」構造では原理的に不可能だった振る舞いであり、
    // 本段が解いた問題そのものである。
    [Fact]
    public async Task Sync_WhenItemsCarryDifferentUpdaters_EachKeepsItsOwn()
    {
        using var scope = factory.Services.CreateScope();
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        var svc = BuildService(scope, new PerItemUpdaterConnector());
        var source = DataSource.Create("share", "filesystem", "");

        await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        var published = bus.PublishedOf<RawDocumentFetched>()
            .Where(m => m.SourceId == source.Id)
            .OrderBy(m => m.OriginalPath)
            .ToList();

        published.Should().HaveCount(2);
        published[0].Attributes[DataSource.OwnerKey].Should().Be("alice");
        published[1].Attributes[DataSource.OwnerKey].Should().Be("bob");
    }

    private static DataSourceSyncService BuildService(
        IServiceScope scope, IDataSourceConnector connector)
    {
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var bus = scope.ServiceProvider.GetRequiredService<RecordingMessageBus>();
        var registry = new ConnectorRegistry([connector]);
        return new DataSourceSyncService(registry, storage, bus,
            NullLogger<DataSourceSyncService>.Instance);
    }

    private string CreateTempDirWithFile(string fileName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kp-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        _tempDirs.Add(dir);
        return dir;
    }

    // discover が失敗するコネクタ（一時的な接続断・権限エラーを模す）。
    private sealed class DiscoverThrowingConnector : IDataSourceConnector
    {
        public string SourceType => "filesystem";
        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(DataSource s, DateTimeOffset? since, CancellationToken ct)
            => throw new IOException("discover boom");
        public Task<RawContent> FetchAsync(DataSource s, SourceItem item, CancellationToken ct)
            => throw new NotSupportedException();
    }

    // 接続文字列を含む例外を投げるコネクタ（資格情報が例外メッセージへ漏れる実例を模す）。
    private sealed class SecretLeakingConnector : IDataSourceConnector
    {
        public string SourceType => "filesystem";
        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(DataSource s, DateTimeOffset? since, CancellationToken ct)
            => throw new IOException("connect failed: Host=db;Username=app;Password=hunter2");
        public Task<RawContent> FetchAsync(DataSource s, SourceItem item, CancellationToken ct)
            => throw new NotSupportedException();
    }

    // discover は 1 件返すが fetch で失敗するコネクタ（一部取得失敗を模す）。
    private sealed class FetchThrowingConnector : IDataSourceConnector
    {
        public string SourceType => "filesystem";
        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(DataSource s, DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceItem>>([new SourceItem("/x/a.md", DateTimeOffset.UtcNow, 1)]);
        public Task<RawContent> FetchAsync(DataSource s, SourceItem item, CancellationToken ct)
            => throw new IOException("fetch boom");
    }

    // 2 件を返し、両方に同じ更新者（または null）を載せるスタブ。
    private sealed class TwoItemConnector(string? updatedBy) : IDataSourceConnector
    {
        public string SourceType => "filesystem";
        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(DataSource s, DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceItem>>(
            [
                new SourceItem("/x/a.md", DateTimeOffset.UtcNow, 1, updatedBy),
                new SourceItem("/x/b.md", DateTimeOffset.UtcNow, 1, updatedBy),
            ]);
        public Task<RawContent> FetchAsync(DataSource s, SourceItem item, CancellationToken ct)
            => Task.FromResult(new RawContent([1], "text/markdown"));
    }

    // アイテムごとに**違う**更新者を載せるスタブ（ソース単位の計算では表現できない状態）。
    private sealed class PerItemUpdaterConnector : IDataSourceConnector
    {
        public string SourceType => "filesystem";
        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(DataSource s, DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceItem>>(
            [
                new SourceItem("/x/a.md", DateTimeOffset.UtcNow, 1, "alice"),
                new SourceItem("/x/b.md", DateTimeOffset.UtcNow, 1, "bob"),
            ]);
        public Task<RawContent> FetchAsync(DataSource s, SourceItem item, CancellationToken ct)
            => Task.FromResult(new RawContent([1], "text/markdown"));
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
