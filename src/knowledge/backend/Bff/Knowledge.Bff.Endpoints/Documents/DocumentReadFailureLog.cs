using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Knowledge.Bff.Endpoints.Documents;

// FR-06, NFR-09, NFR-16, ADR-0029 (#1897): BFF が文書の読み取りの失敗を**縮退へ畳む**ときに WARN を 1 行出す。
//
// ■ なぜ要るのか
//   一覧（`FetchListAsync`）は `RpcException` を全 status について空一覧へ、詳細（`FetchAuthorizedAsync`）は
//   404 へ畳んでいた。ログが無いので、`ListDocuments` の応答が受信上限を超えた（`ResourceExhausted`）ときも、
//   画面には「文書が 0 件」としか見えなかった（#1897）。認可スコープ解決の縮退の WARN（旧 `AuthzScopeRestLog`。REST の撤去とともに消えた）と同じ形にした。
//
// 🔴 **載せるのは rpc 名・輸送・状態コード・例外の型だけである。** 例外本体・メッセージ・status の detail は載せない
//   （s2s トークン取得失敗のメッセージは IdP の応答を含み得る。利用者 ID・属性・文書の本文や表題も載せない）。
// 🔴 **畳み方（空一覧・404）は変えない。** 本 PR は応急処置であり、`ResourceExhausted` を空一覧へ畳むかどうかの
//   見直しは恒久対応（ページング）の段で行う。
internal static class DocumentReadFailureLog
{
    // 運用者が grep する語を固定する（カテゴリ名もここで 1 つに決める）。
    internal const string Category = "Knowledge.Bff.Endpoints.DocumentBffEndpoints";

    // ［2026-10-10 / #1255・[[IADR-0533]]］REST の並走を撤去したので輸送は gRPC だけである。輸送の欄は運用者が grep する語として
    // 文言に残す（固定で `grpc`）。従前の引数 `grpc`（REST 経路が残っている前提の真偽値）は外した。
    internal static void Folded(HttpContext http, string rpc, Exception ex, string fallback)
    {
        var logger = http.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(Category)
            ?? NullLogger.Instance;
        logger.LogWarning(
            "文書の読み取り（{Rpc}・{Transport}）で後段から結果を得られませんでした（状態 {Status}・{ErrorType}）。{Fallback} へ縮退します。",
            rpc,
            "grpc",
            StatusOf(ex),
            ex.GetType().Name,
            fallback);
    }

    private static string StatusOf(Exception ex) => ex switch
    {
        RpcException rpc => rpc.StatusCode.ToString(),
        _ => "-",
    };
}
