using ConversionService.Features.ConversionJobs.Normalize;
using ConversionService.Domain;
using ConversionService.Infrastructure.Persistence;
using ConversionService.Domain.Ports;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;

namespace ConversionService.Tests.Features.ConversionJobs.Normalize;

// FR-12, UC-06, SC-07, IADR-0042: 変換ハンドラが成功／失敗を IConversionJobStore に記録すること、
// 失敗時も例外を再送出して再試行→デッドレターを保つことを検証する。
//
// 🔴 ADR-0027（#441 E1）: **再試行の駆動をテストの中で再現するのをやめた。**
// 旧テストは MassTransit の即時再試行（`UseMessageRetry(r => r.Immediate(n))`）でランタイムに
// n+1 回消費させ、その副作用としてデッドレター標識が立つのを見ていた。Wolverine 版で同じことを
// するには実時間の待ち（2s/10s/30s）かランタイム内部の差し替えが要る。
//
// 代わりに**鎖を 3 本に分けて、それぞれを直接測る**:
//   ① ランタイムが「何回目で諦めるか」 …… W1 の等価性テスト（`WolverineExtensions` 側）が測る。
//      実測で 2s/10s/30s の後、試行 4 で `MoveToErrorQueue` へ落ちることを確認済み。
//   ② ハンドラが「何回目を最後と見なすか」 …… **本ファイル**が `Envelope.Attempts` を直に与えて測る。
//      境界（上限 -1 / 上限）の両側を見るので、旧テストより**判定点が正確**である。
//   ③ 契約定数と上限の一致 …… 本ファイル末尾のテスト。
// ①②③ が揃って初めて「使い切ったらデッドレター」が言える。**どれか 1 本でも欠けると言えない。**
[Trait("TestKind", "Unit")]
public class RawDocumentFetchedConsumerJobTests
{
    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.docx", $"storage://{id}/raw",
            "application/pdf", new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["hr"],
            DateTimeOffset.UtcNow);

    private sealed class SucceedingNormalizer : INormalizationService
    {
        public Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw, CancellationToken ct = default) =>
            Task.FromResult(new NormalizationResult(Guid.NewGuid(), "storage://bucket/a.md", [], 1, 0,
                [new NormalizedFigure("fig-0", true, "mermaid", "flowchart TD; A-->B;", null, null, null)]));
    }

    private sealed class FailingNormalizer : INormalizationService
    {
        public Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw, CancellationToken ct = default) =>
            throw new InvalidOperationException("pandoc failed");
    }

    // ADR-0070 決定 3 / IADR-0356 (#1192): テキスト層の無い PDF。正規化は**成功**し、本文なしを運ぶ。
    private sealed class BodyAbsentNormalizer : INormalizationService
    {
        public Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw, CancellationToken ct = default) =>
            Task.FromResult(new NormalizationResult(Guid.NewGuid(), "storage://bucket/empty.md", [], 0, 0, [],
                BodyAbsent: true));
    }

    // IADR-0043: EF ストア ＋ EF InMemory DbContext。ハンドラの書き込みを同じ DB 名の別コンテキストから
    // 読み直せるようにするため、DB 名は一度だけ確定させる。
    private sealed class Harness(INormalizationService normalizer) : IAsyncDisposable
    {
        private readonly DbContextOptions<ConversionJobDbContext> _options =
            new DbContextOptionsBuilder<ConversionJobDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public RecordingDocumentNormalizedPublisher Publisher { get; } = new();

        private ConversionJobDbContext? _handlerDb;

        // 本番は 1 メッセージ 1 スコープである。試行ごとに新しい DbContext を作って同じ形にする。
        public Task HandleAsync(RawDocumentFetched ev, int attempts)
        {
            _handlerDb?.Dispose();
            _handlerDb = new ConversionJobDbContext(_options);
            var handler = new RawDocumentFetchedConsumer(
                normalizer, Publisher, new EfConversionJobStore(_handlerDb),
                NullLogger<RawDocumentFetchedConsumer>.Instance);
            return handler.Handle(ev, new Envelope { Attempts = attempts },
                TestContext.Current.CancellationToken);
        }

        public async Task<ConversionJobDto?> ReadJobAsync(Guid fetchId)
        {
            await using var db = new ConversionJobDbContext(_options);
            return await new EfConversionJobStore(db).GetAsync(fetchId,
                TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _handlerDb?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Consume_success_records_succeeded_job()
    {
        await using var harness = new Harness(new SucceedingNormalizer());
        var ev = Raw(Guid.NewGuid());

        await harness.HandleAsync(ev, attempts: 1);

        (await harness.ReadJobAsync(ev.FetchId))!.Status.Should().Be(ConversionJobStatus.Succeeded);
        harness.Publisher.Calls.Should().ContainSingle();
    }

    // FR-12, UC-06, SC-07, ADR-0070 決定 3 / IADR-0356 (#1192): テキスト層を持たない PDF は
    // **`succeeded` で確定し、`failed` にならず `deadLettered` も立たない**。内訳は `BodyAbsent` が運び、
    // 発行口へも同じ値が渡る（後続がメタデータ索引へ回す判断に使う）。
    // 🔴 従前はこの原本が `failed` ＋ `deadLettered=true` になっていた（#1192 の実測）。
    [Fact]
    public async Task Consume_pdf_without_text_layer_records_succeeded_job_with_body_absent()
    {
        await using var harness = new Harness(new BodyAbsentNormalizer());
        var ev = Raw(Guid.NewGuid());

        await harness.HandleAsync(ev, attempts: 1);

        var job = (await harness.ReadJobAsync(ev.FetchId))!;
        job.Status.Should().Be(ConversionJobStatus.Succeeded);
        job.BodyAbsent.Should().BeTrue();
        job.DeadLettered.Should().BeFalse();
        job.Error.Should().BeNull();
        job.MarkdownUri.Should().Be("storage://bucket/empty.md");
        harness.Publisher.Calls.Should().ContainSingle().Which.BodyAbsent.Should().BeTrue();
    }

    // 陽性対照: 本文ありの成功では標識は立たず、発行口へも false が渡る。
    [Fact]
    public async Task Consume_success_with_body_does_not_mark_body_absent()
    {
        await using var harness = new Harness(new SucceedingNormalizer());
        var ev = Raw(Guid.NewGuid());

        await harness.HandleAsync(ev, attempts: 1);

        (await harness.ReadJobAsync(ev.FetchId))!.BodyAbsent.Should().BeFalse();
        harness.Publisher.Calls.Should().ContainSingle().Which.BodyAbsent.Should().BeFalse();
    }

    [Fact]
    public async Task Consume_failure_records_failed_job_and_rethrows()
    {
        await using var harness = new Harness(new FailingNormalizer());
        var ev = Raw(Guid.NewGuid());

        // 例外を握り潰すとランタイムは成功と見なし、再試行もデッドレターも起きない。
        var act = () => harness.HandleAsync(ev, attempts: 1);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*pandoc failed*");

        var job = (await harness.ReadJobAsync(ev.FetchId))!;
        job.Status.Should().Be(ConversionJobStatus.Failed);
        job.Error.Should().Contain("pandoc failed");
        // FR-12, SC-07（AC-4）: 試行上限に達していない失敗にデッドレター標識は立たない。
        // 「失敗した」ことではなく「**再試行を使い切った**」ことが標識の意味である。
        job.Attempts.Should().BeLessThan(ConversionJobRetryPolicy.MaxAttempts);
        job.DeadLettered.Should().BeFalse();
        harness.Publisher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Consume_failure_on_last_attempt_marks_dead_lettered()
    {
        // FR-12, SC-07（AC-6/AC-7）: 自動再試行を使い切った継続失敗はデッドレターへ送られる。
        // 04_workflows/03_conversion-flow.md:65「継続失敗はデッドレターキューへ送り、管理者に通知する」。
        //
        // 🔴 **上限までの各回を実際に回す。** `job.Attempts` はストアが数える「ハンドラが走った回数」で
        // あり、`Envelope.Attempts` とは別物である（最終回だけ呼ぶと Attempts が 1 になり、AC-7 の
        // 「上限に達した」を測ったことにならない）。**境界の手前で標識が立たないことも毎回見る。**
        await using var harness = new Harness(new FailingNormalizer());
        var ev = Raw(Guid.NewGuid());

        for (var attempt = 1; attempt < WolverineExtensions.MaxAttempts; attempt++)
        {
            var notYet = () => harness.HandleAsync(ev, attempts: attempt);
            await notYet.Should().ThrowAsync<InvalidOperationException>();

            var inFlight = (await harness.ReadJobAsync(ev.FetchId))!;
            inFlight.DeadLettered.Should().BeFalse($"試行 {attempt} は上限未満である");
            inFlight.Attempts.Should().Be(attempt);
        }

        var last = () => harness.HandleAsync(ev, attempts: WolverineExtensions.MaxAttempts);
        await last.Should().ThrowAsync<InvalidOperationException>();

        var job = (await harness.ReadJobAsync(ev.FetchId))!;
        // AC-3: 状態値は 4 値のまま。デッドレターは failed の**内訳**である。
        job.Status.Should().Be(ConversionJobStatus.Failed);
        job.DeadLettered.Should().BeTrue();
        job.Attempts.Should().Be(ConversionJobRetryPolicy.MaxAttempts);
    }

    [Fact]
    public void MaxAttempts_contract_constant_matches_platform_retry_policy()
    {
        // FR-12, SC-07（AC-11）: 契約が公開する試行上限（ConversionJobRetryPolicy）と、
        // 実際に再試行を行う設定は同じ値でなければならない。
        // 契約プロジェクトから基盤プロジェクトを参照しない代わりに、両者の一致をここで束ねる
        // （IADR-0137 決定 3・決定 4）。**間隔を増減したらこのテストが落ちる。**
        //
        // ADR-0027（#441 E1）: 突き合わせ先を `MassTransitExtensions` から `WolverineExtensions` へ
        // 移した —— **本辺の再試行を実際に駆動するのは Wolverine 側だからである。**
        // MassTransit 側との値の一致は W1 の等価性テストが別に固定している（そちらを消さないこと）。
        ConversionJobRetryPolicy.MaxAttempts.Should().Be(WolverineExtensions.MaxAttempts);
    }
}
