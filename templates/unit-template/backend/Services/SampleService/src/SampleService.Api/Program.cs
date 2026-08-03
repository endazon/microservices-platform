// テンプレート: 追加可変機能ユニットのサンプルサービス（ASP.NET Core Minimal API・ADR-0030）。
// Program.cs は合成ルート（可変部分を構成で束ねる唯一の場所。src/README.md のサービス標準レイアウト参照）。
//
// ADR-0030 の要点:
//   - 設計様式は Pragmatic Clean Architecture ＋ Vertical Slice（不要な Repository / Service 抽象を作らない）
//   - ローカル/リモートいずれのディスパッチも Wolverine ハンドラに統一する（MediatR・独自 Dispatcher は使わない）
//   - エラー応答は標準 AddProblemDetails() + IExceptionHandler（Hellang は使わない）
//   - ロギングは標準 ILogger + OpenTelemetry Logs（Serilog は使わない）
// 詳細は docs/tech/tech-requirements.md「バックエンドアプリケーション層標準」を参照。

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
// RFC7807: 標準の ProblemDetails を使う。例外→応答の変換は IExceptionHandler 実装を AddExceptionHandler で登録する。
builder.Services.AddProblemDetails();

// Wolverine の登録例（実サービスで有効化する。トランスポート設定は #441 の方針に従う）:
//   builder.Host.UseWolverine(opts => opts.Discovery.IncludeAssembly(typeof(CreateSample).Assembly));

var app = builder.Build();

app.UseExceptionHandler();

// ヘルスチェック（compose / k8s の liveness/readiness 用）。
app.MapHealthChecks("/health");

// サンプルの同期 API（契約は docs/api/openapi.yaml で管理する）。
app.MapGet("/sample", () => Results.Ok(new { message = "hello from sample unit" }));

app.Run();

// 統合テスト（WebApplicationFactory）から参照するためのエントリポイント公開。
public partial class Program;
