using SampleService.Features.Samples.Create;

// テンプレート: 追加可変機能ユニットのサンプルサービス（ASP.NET Core Minimal API・ADR-0030 / IADR-0282）。
// Program.cs は合成ルート（可変部分を構成で束ねる唯一の場所）。
//
// 要点:
//   - サービスは単一プロジェクト。層は Features / Domain / Infrastructure / Common のフォルダで分ける（IADR-0282）
//   - 設計様式は Pragmatic Clean Architecture ＋ Vertical Slice（不要な Repository / Service 抽象を作らない）
//   - ローカル/リモートいずれのディスパッチも Wolverine ハンドラに統一する（MediatR・独自 Dispatcher は使わない）
//   - エラー応答は標準 AddProblemDetails() + IExceptionHandler（Hellang は使わない）
//   - ロギングは標準 ILogger + OpenTelemetry Logs（Serilog は使わない）

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
// RFC7807: 標準の ProblemDetails を使う。例外→応答の変換は IExceptionHandler 実装を AddExceptionHandler で登録する。
builder.Services.AddProblemDetails();
// 時刻は TimeProvider を注入する（テストで差し替えるため DateTimeOffset.UtcNow を直接呼ばない）。
builder.Services.AddSingleton(TimeProvider.System);

// Wolverine の登録例（実サービスで有効化する。トランスポート設定はパイプライン共通ヘルパに従う）:
//   builder.Host.UseWolverine(opts => opts.Discovery.IncludeAssembly(typeof(Program).Assembly));

var app = builder.Build();

app.UseExceptionHandler();

// ヘルスチェック（compose / k8s の liveness/readiness 用）。
app.MapHealthChecks("/health");

// スライスの入口は Features/<集約>/<操作>/Endpoint.cs が持ち、ここでは束ねるだけにする。
app.MapCreateSample();

app.Run();

// 統合テスト（WebApplicationFactory）から参照するためのエントリポイント公開。
public partial class Program;
