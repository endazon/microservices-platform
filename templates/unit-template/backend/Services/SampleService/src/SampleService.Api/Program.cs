// テンプレート: 追加可変機能ユニットのサンプルサービス（最小 API）。
// Program.cs は合成ルート（可変部分を構成で束ねる唯一の場所。src/README.md のサービス標準レイアウト参照）。
// 実サービスでは Foundation/（固定）・Composable/（可変）へ分割し、イベント購読は MassTransit の
// IConsumer<T>、発行は IPublishEndpoint で行う。ユニット固有イベント契約は backend/Shared/<Unit>.Contracts/。

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

// ヘルスチェック（compose / k8s の liveness/readiness 用）。
app.MapHealthChecks("/health");

// サンプルの同期 API（契約は docs/api/openapi.yaml で管理する）。
app.MapGet("/sample", () => Results.Ok(new { message = "hello from sample unit" }));

app.Run();
