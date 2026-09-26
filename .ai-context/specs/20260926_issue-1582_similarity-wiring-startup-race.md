---
title: GraphService の SimilaritySourceWiringTests.Unknown_source_fails_at_startup が稀に ObjectDisposedException で落ちる競合を、起動検証の例外を捕まえる形で決定的にする（#1582）
type: spec
status: done
related_ids: [FR-18, FR-17, ADR-0051, IADR-0380]
author: claude
created: 2026-09-26
updated: 2026-09-26
related_specs: []
issue: "#1582"
---

# 仕様書: Unknown_source_fails_at_startup の起動失敗を決定的に捕まえる（#1582）

> 本仕様書は実装着手前に作成する。

## 起点

- FR-18（AI 提案。T-50「未知の値は起動が落ちる」）。issue の表題は GraphService の機能要求 FR-17 を挙げるが、試験の写像先は
  `docs/tests/FR-18_ai-suggestions.md` の T-50 である。
- 関連: ADR-0051、IADR-0380（類似度の供給元の切り替えと `ValidateOnStart`）
- 事象: PR #1579 の CI（backend-build (knowledge) 1 回目）で、期待した起動時の設定エラーではなく `ObjectDisposedException` が返った。再実行で通った。

## 原因（CI ログのスタックで確定）

`WebApplicationFactory` はトップレベル文の `Program` を `DeferredHostBuilder` 経由で動かす。

1. テストスレッドの `factory.Services` → `CreateHost` → `DeferredHostBuilder.Build()` は、アプリが `builder.Build()` を呼んだ時点で戻る。
2. エントリポイントのスレッドはそのまま `app.Run()` へ進み、`Host.StartAsync` の起動検証（`ValidateOnStart`）で
   `OptionsValidationException` を投げ、`RunAsync` の `finally` で**host（ServiceProvider）を破棄**する。
3. テストスレッドは `DeferredHost.StartAsync` で `_host.Services.GetRequiredService<IHostApplicationLifetime>()` を呼ぶ。
   - 2 の破棄より**先**に着けば、エントリポイントの完了（例外）を待って期待どおりの例外を受け取る。
   - 2 の破棄より**後**に着くと、破棄済みの ServiceProvider で `ObjectDisposedException`（CI のスタックと一致：
     `DeferredHost.StartAsync` → `GetRequiredService` → `ThrowObjectDisposedException`）。

つまり 2 本のスレッドの着順で、テストに届く例外が変わる。試験の側の問題であり、本番の起動検証は正しく落ちている。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| AC-1 | 未知の供給元（`qdrant`）で**起動が落ちる**ことを、着順に依らず測る | テストスレッドの `Start` を遅らせて破棄を先に終わらせても緑（下の再現） |
| AC-2 | 落ちた理由が**起動検証**（`OptionsValidationException`・キー `AiSuggestions:Similarity:Source`）であることを測る（ODE を合格扱いにしない） | 起動検証の例外そのものを捕まえて検査する |
| AC-3 | 30 回以上ループで実行し、前後の合格数を記録する | 本仕様書「検証」 |

## 設計

- 試験のホストの `IStartupValidator`（`ValidateOnStart` が登録する起動検証）を、元の検証を呼んで**例外を記録してから投げ直す**包みに差し替える
  （`WithWebHostBuilder` の `ConfigureServices`。Program の登録の後に走る）。起動検証はエントリポイントのスレッドで host の破棄より前に走るので、
  テストスレッドがどちらの例外を受け取っても、記録は必ず先に済んでいる。
- 試験は ① `factory.Services` が例外を投げること（起動しない）と ② 記録された起動検証の例外が `OptionsValidationException` で
  キーを含むことの 2 点を測る。ODE は ① を満たすだけで、② で起動検証の失敗を別に確かめる。
- 本番コード（`Program.cs`）は変えない。`TestWebApplicationFactory` も変えない（他の試験に影響させない）。

## 母集合

- 軸 1（同じ形 —— `WithWebHostBuilder` の host を `() => …Services` で起動させて例外を待つ）: `git grep -n "=> factory.Services\|=> f.Services\|\.Services;$"`（`src/**/*Tests.cs`）
  → `GraphService…/SimilaritySourceWiringTests.cs:123`（**本件・反映**）、`LlmGateway/Tests/Common/Observability/LlmBudgetMetricsTests.cs:141`
  （T-2f「未知の用途を設定するとホストは起動しない」。**同じ潜在競合を持つが platform ユニットの別試験であり、1 issue = 1 PR のため本 PR では直さない**。
  報告で別 issue 化を提案する）。他の行はプロパティの読み出し（`factory.Services.GetRequiredService<…>()`）で、起動失敗を待たない（**除外**）。
- 軸 2（起動失敗を語る試験）: `git grep -ln "OptionsValidationException\|fails_at_startup\|起動が落ち\|起動を落と"`（試験）→ 上の 2 件に加え
  `DashboardService…/DashboardEndpointTests.cs`・`UsageRetentionTests.cs`（「起動を落とさず既定へ倒す」側の試験で、起動は成功する。**除外**）。
  ［2026-09-26 追記 / #1582］当初の走査はパス指定（`'src/**/Tests/**/*.cs' 'src/**/*.Tests/**/*.cs'`）が `Platform.Bff.Tests` の直下を拾わず、
  `src/platform/backend/Bff/Platform.Bff.Tests/PlatformLoggingTests.cs` を落としていた（AI レビューが指摘）。`'src/**/*Tests.cs'` を足して引き直すと 5 件で、
  追加の 1 件は「OTLP 先が不在でも起動が落ちない」側の試験であり起動は成功する（**除外**。上の Dashboard の 2 件と同じ理由）。
- 文書: `docs/tests/FR-18_ai-suggestions.md` の T-50（「未知の値は起動が落ちる」）は測る内容が変わらないので**変更なし**。

## 検証

- 再現（修正前）: 一時的な試験（コミットしない）でテストスレッドの `host.Start()` の前に 10 秒待たせると、元の書き方は 3/3 で
  `ObjectDisposedException`（CI と同じスタック）。遅延なしのループでは手元で再現しなかった（40/40 合格）。
- 修正前（実測）: 遅延を入れた一時試験で元の書き方は **0/5 合格**（5 件とも `ObjectDisposedException`）。遅延なしでは `dotnet test` を 4 並列で
  40 回回して **40/40 合格**（手元では自然には再現しなかった。CI の失敗は 1 回きり）。
- 修正後（実測）: 同じ 10 秒の遅延を入れても新しい書き方は **5/5 合格**。遅延なしで本物の試験を `dotnet test` 4 並列で 32 回回して **32/32 合格**。
  GraphService.Tests 全件 637/637。
- 変異: Program から `.ValidateOnStart()` を外す → 赤／述語を `o => true` にする → 赤（どちらも `Unknown_source_fails_at_startup`）。
- `dotnet test`（GraphService.Tests 全件）・`dotnet format --verify-no-changes`。
