using Microsoft.AspNetCore.Http;
using System.Net.Http;

namespace Knowledge.Bff.Endpoints;

// FR-01, FR-04, FR-07, FR-08, FR-17, NFR-06, ADR-0074 決定 4, ADR-0115 決定 5 (#2109 / #2280): 後段の**失敗**の応答を、状態コード・
// content-type・本文とも**作り替えずに**返す（`DocumentBffEndpoints.RelayAsync` と同じ形）。
//
// 後段は保存しない・答えない理由を 400 の RFC7807（`errors`）や `{ "error": "<文>" }`・502 の `message` で返す。
// 状態コードだけを返して本文を捨てると、画面は「入力内容に誤りがあります。」しか出せない（SPA の `parseProblemDetails` は
// `errors` / `detail` / `title` / `message`、最後の手段として文の `error` を読む）。本文の無い失敗は空のまま同じ状態コードで返る。
//
// 🔴 **使うのは利用者の入力を受ける口だけ**（データソースの書き込み・質問・分析・評価の送信・検索・グラフの近傍）。
// 読み取りの口（一覧・個別・集計・辞書）は状態コードだけを返す —— 後段は理由の本文を持たず、画面は状態コードで表示を分ける
// （作業仕様書 20261030_2280 の母集合の表）。
internal static class BffFailureRelay
{
    public static async Task<IResult> RelayAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (content.Length == 0)
            return Results.StatusCode((int)resp.StatusCode);
        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(content, contentType, statusCode: (int)resp.StatusCode);
    }
}
