using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// NFR (#179 / IADR-0045): BFF 文書書き込みの「スコープ確認 GET → 実処理」2 往復の実測ハーネス。
//
// DocumentBffEndpoints.ForwardIfInScope は書き込み（更新/公開/アーカイブ/削除）のたびに
//   (1) ResolveAsync → AuthorizationService /authz/scope（ABAC スコープ解決）
//   (2) GET /documents/{id}（対象文書の取得＝スコープ確認）
//   (3) 実処理（PUT/POST/DELETE）を DocumentService へ転送
// を行う。(2)+(3) が DocumentService への 2 往復。#179 は「実測を取ってから最適化に着手する」方針。
//
// 本ハーネスは既存の WebApplicationFactory（in-process TestServer）に、後段呼び出しの回数を数え
// 疑似 RTT（Task.Delay）を注入する計測用ハンドラを差し込み、次を実測する:
//   - 書き込み経路あたりの後段往復回数（決定的・回帰ガード）。
//   - 疑似 RTT を変えたときの 1 リクエストのレイテンシ（p50/p95）。
//     更新 PUT（後段 doc 2 往復）と新規作成 POST（後段 doc 1 往復）を同一条件で比較し、
//     プリフライト GET 1 往復の限界コストを分離する。
//
// 注意: TestServer はソケットを介さない in-process 呼び出しのため「ネットワーク RTT」は Task.Delay で
// モデル化した値。BFF 自身の処理（シリアライズ・スコープ突合）は実 CPU 時間として計測される。
// Windows の既定タイマ分解能（~15.6ms）により小さな RTT の実測は丸められる点に留意（RttMs 列は要求値）。
public sealed class MeasuringBffFactory : BffTestFactory
{
    // 後段 1 往復あたりに注入する疑似 RTT（ms）。0 で BFF 自身のオーバヘッドのみを計測。
    public int BackendRttMs { get; set; }

    // 後段呼び出し回数。外部へは読み取り専用で公開し、加算/リセットは Interlocked で行う
    // （Interlocked の ref にはフィールドが必要なため、backing は private フィールドで保持する）。
    private int _authzCalls;
    private int _docGetCalls;
    private int _docWriteCalls;

    public int AuthzCalls => Volatile.Read(ref _authzCalls);
    public int DocGetCalls => Volatile.Read(ref _docGetCalls);
    public int DocWriteCalls => Volatile.Read(ref _docWriteCalls);

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _authzCalls, 0);
        Interlocked.Exchange(ref _docGetCalls, 0);
        Interlocked.Exchange(ref _docWriteCalls, 0);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // 後から登録した ConfigurePrimaryHttpMessageHandler が最終的な PrimaryHandler を上書きする。
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient("AuthorizationService")
                .ConfigurePrimaryHttpMessageHandler(() => new MeasuringHandler(this, "authz"));
            services.AddHttpClient("DocumentService")
                .ConfigurePrimaryHttpMessageHandler(() => new MeasuringHandler(this, "document"));
        });
    }

    private sealed class MeasuringHandler(MeasuringBffFactory owner, string service) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (owner.BackendRttMs > 0)
                await Task.Delay(owner.BackendRttMs, cancellationToken);

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (service == "authz")
            {
                Interlocked.Increment(ref owner._authzCalls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new AccessScopeResponse("tester", owner.ScopeFilters, owner.SearchScopeGranted))
                };
            }

            // DocumentService: GET=スコープ確認のプリフライト、それ以外=実書き込み。
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref owner._docGetCalls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(owner.StubDocument)
                };
            }

            Interlocked.Increment(ref owner._docWriteCalls);
            var code = request.Method == HttpMethod.Post && path == "/documents"
                ? HttpStatusCode.Created
                : HttpStatusCode.OK;
            return new HttpResponseMessage(code) { Content = JsonContent.Create(owner.StubDocument) };
        }
    }
}

// 命名について: 本クラスは同ディレクトリの `*Tests`（例 BffDocumentEndpointTests）命名規約から外し
// `*Benchmark` としている。決定的な回帰ガード 1 件に加えレイテンシ計測ハーネスを主目的とするため、
// 通常のエンドポイント単体テストと区別する意図。`*Tests` で検索するレビュワー向けの補足。
public sealed class BffDocumentWriteRoundtripBenchmark(ITestOutputHelper output)
{
    private static string DetailPath => $"/bff/documents/{BffTestFactory.StubDocumentId}";

    // レイテンシ・スイープは時間がかかるため既定で無効。環境変数 RUN_BFF_BENCH=1 で有効化する。
    private static bool BenchEnabled => Environment.GetEnvironmentVariable("RUN_BFF_BENCH") == "1";

    // 決定的な往復構造の検証（常時実行・回帰ガード）。
    // 更新 PUT: 後段 = 1 authz + 2 document（GET プリフライト + 書き込み）。
    // 新規作成 POST: 後段 = 1 authz + 1 document（プリフライト GET なし）。
    [Fact]
    public async Task Write_path_roundtrip_counts_are_measured()
    {
        using var factory = new MeasuringBffFactory { SearchScopeGranted = true, ScopeFilters = [] };
        var client = factory.CreateClient();

        // 更新（PUT）= 現行の 2 往復書き込み経路。
        factory.ResetCounters();
        var put = await client.PutAsJsonAsync(DetailPath,
            new { title = "改訂", attributes = new { confidentiality = "internal" }, tags = Array.Empty<string>(), expectedVersion = 3 });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.AuthzCalls.Should().Be(1, "スコープ解決は 1 回");
        factory.DocGetCalls.Should().Be(1, "スコープ確認のプリフライト GET が 1 回");
        factory.DocWriteCalls.Should().Be(1, "実書き込みが 1 回");

        // 新規作成（POST）= プリフライト GET を持たない単一往復書き込み（最適化後の往復数の代理）。
        factory.ResetCounters();
        var post = await client.PostAsJsonAsync("/bff/documents",
            new { title = "新規", attributes = new { confidentiality = "internal" }, tags = new[] { "hr" } });
        post.StatusCode.Should().Be(HttpStatusCode.Created);
        factory.AuthzCalls.Should().Be(1);
        factory.DocGetCalls.Should().Be(0, "作成はプリフライト GET を行わない");
        factory.DocWriteCalls.Should().Be(1);

        output.WriteLine("=== BFF write-path backend roundtrips (measured) ===");
        output.WriteLine("PUT  /bff/documents/{id}        : authz=1, doc GET=1, doc write=1  (= 2 DocumentService roundtrips)");
        output.WriteLine("POST /bff/documents (create)    : authz=1, doc GET=0, doc write=1  (= 1 DocumentService roundtrip)");
    }

    // レイテンシ・スイープ。RUN_BFF_BENCH=1 のときのみ実行。
    // 未設定時は **真の Skipped** 扱いにする（no-op の passed 表示を避ける）。
    // #455 A-2: xUnit v3 標準の Assert.SkipUnless へ移した。従前は Xunit.SkippableFact の
    // [SkippableFact] + Skip.IfNot を使っていたが、同パッケージに v3 対応版が無い
    // （最新 1.5.61 も xunit.extensibility.execution v2 に依存する）。
    // 🔴 v2 のうちに外してはならなかった —— v2 には動的スキップが無く、先に外すと
    // 「真の Skipped」が「何もしない Passed」へ退化する。
    [Fact]
    public async Task Benchmark_write_latency_by_backend_rtt()
    {
        Assert.SkipUnless(BenchEnabled, "set RUN_BFF_BENCH=1 to run the latency sweep.");

        var n = int.TryParse(Environment.GetEnvironmentVariable("BENCH_N"), out var envN) ? envN : 200;
        var rtts = (Environment.GetEnvironmentVariable("BENCH_RTTS") ?? "0,5,20")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).ToArray();
        const int warmup = 30;

        using var factory = new MeasuringBffFactory { SearchScopeGranted = true, ScopeFilters = [] };
        var client = factory.CreateClient();

        output.WriteLine($"# BFF write-path latency (in-process TestServer, modeled backend RTT)");
        output.WriteLine($"# iterations/measure={n}, warmup={warmup}, OS={Environment.OSVersion}");
        output.WriteLine("");
        output.WriteLine("| RttMs | op            | doc RT | p50 ms | p95 ms | p99 ms | mean ms |");
        output.WriteLine("|------:|---------------|-------:|-------:|-------:|-------:|--------:|");

        foreach (var rtt in rtts)
        {
            factory.BackendRttMs = rtt;

            var put = await MeasureAsync(n, warmup, () => client.PutAsJsonAsync(DetailPath,
                new { title = "改訂", attributes = new { confidentiality = "internal" }, tags = Array.Empty<string>(), expectedVersion = 3 }));
            Emit(rtt, "PUT update", 2, put);

            var post = await MeasureAsync(n, warmup, () => client.PostAsJsonAsync("/bff/documents",
                new { title = "新規", attributes = new { confidentiality = "internal" }, tags = new[] { "hr" } }));
            Emit(rtt, "POST create", 1, post);
        }

        output.WriteLine("");
        output.WriteLine("# 「PUT update (2 doc RT)」-「POST create (1 doc RT)」の差 ≒ プリフライト GET 1 往復の限界コスト。");
    }

    private static async Task<double[]> MeasureAsync(int n, int warmup, Func<Task<HttpResponseMessage>> op)
    {
        for (var i = 0; i < warmup; i++)
            (await op()).Dispose();

        var samples = new double[n];
        var sw = new Stopwatch();
        for (var i = 0; i < n; i++)
        {
            sw.Restart();
            using var resp = await op();
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples;
    }

    private void Emit(int rtt, string op, int docRt, double[] sorted)
    {
        output.WriteLine(
            $"| {rtt,5} | {op,-13} | {docRt,6} | {Pct(sorted, 50),6:F2} | {Pct(sorted, 95),6:F2} | {Pct(sorted, 99),6:F2} | {sorted.Average(),7:F2} |");
    }

    private static double Pct(double[] sorted, int p)
    {
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
