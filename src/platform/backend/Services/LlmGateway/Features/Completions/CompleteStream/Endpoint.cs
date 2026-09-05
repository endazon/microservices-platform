using System.Text.Encodings.Web;
using System.Text.Json;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace LlmGateway.Features.Completions.CompleteStream;

// IADR-0037, FR-04, FR-11: SSE ストリーミング版のテキスト生成（POST /complete/stream）。
// FR-11: egress ゲートは /complete と同一の router.Route(...) を通し、Allowed=false は
// プロバイダを一切呼ばず理由イベントのみ返す（越境保証を弱めない）。
//
// IADR-0398 (#1255): 判定の本体は CompletionUseCase.StreamAsync にある。
// **gRPC 面（GrpcService.CompleteStream）が同じ本体を呼ぶ**（判定器を 2 つにしない）。
// 🔴 本ファイルに残るのは SSE の枠（ヘッダ・`data:` 行の直列化・flush）だけである ——
// **1 イベントごとに書いて flush する性質は残す**。まとめてから書くと初回トークンの境界が消える。
public static class CompleteStreamEndpoint
{
    // SSE の data: 行に載せる JSON は Web 既定（camelCase）で直列化し、呼び出し側の JSON と揃える。
    // 日本語を \uXXXX へ過剰エスケープしないよう緩和エンコーダを用いる（SSE 本文の可読性・帯域）。
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEndpointRouteBuilder MapCompleteStream(this IEndpointRouteBuilder app)
    {
        // AiAnalysisService が POST /complete/stream で呼び出す。
        app.MapPost("/complete/stream", async (
            CompletionApiRequest req,
            CompletionUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            // 前段プロキシでの応答バッファリング抑止。#1135 で SPA の前段が nginx から Caddy へ移ったが、
            // **Caddy の reverse_proxy も同じヘッダを解釈する**ため値は変えない（デファクト標準のヘッダ）。
            http.Response.Headers["X-Accel-Buffering"] = "no";

            // NFR-02, ADR-0044, ADR-0076 決定 4, [[IADR-0378]] (#1203): 合成監視の標識は外周が付けた
            // ヘッダを引き継ぐだけである（判定は SyntheticTraffic が単一情報源）。
            var isSynthetic = SyntheticTraffic.IsSyntheticInternalRequest(http.Request);

            await foreach (var ev in useCase.StreamAsync(req, isSynthetic, ct))
            {
                // 🔴 **1 イベントごとに書いて flush する。** ここでまとめると
                // 「最初の data: 行」の到着が生成完了まで遅れ、NFR-02 の初回トークンが測れなくなる。
                await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(ev, SseJson)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }).WithName("CompleteStream");

        return app;
    }
}
