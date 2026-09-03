using DataSourceService.Domain;
using DataSourceService.Domain.Ports;

namespace DataSourceService.Features.DataSources;

// FR-05, UC-04, SC-06, ADR-0036, ADR-0074 決定 4 (#1194): 写像表の受け入れ検査。
//
// **登録・全置換・部分更新の 3 操作が使う**ため、集約直下に置く（ADR-0068 決定 2 の
// 「操作をまたいで共有されるものだけを 2 段目に残す」）。**3 箇所に書かない** ——
// 書き分けると「登録では弾くのに PATCH では通る」という穴が空き、そこが最も普通の経路になる。
//
// 🔴 **サーバ側で拒否することが要件である**（#1194 やること 4）。画面だけの検証にすると
// API を直接叩いた経路で偽の所有者が入り、ADR-0036 の裁量制御が意図しない相手に開く。
internal static class OwnerMappingValidation
{
    // 検査に通れば null。通らなければ返すべき応答（400 または 502）。
    //
    // **写像表が要求に無い（null）・空のときは名簿を引かない。** 写像表を触らない PATCH が
    // 認可サービスの障害で落ちてはならない（無関係な操作を巻き込まない）。
    internal static async Task<IResult?> ValidateAsync(
        Dictionary<string, string>? ownerMappings,
        IPlatformUserDirectory directory,
        CancellationToken ct)
    {
        if (ownerMappings is null || ownerMappings.Count == 0) return null;

        var shapeErrors = OwnerMappingTable.ValidateShape(ownerMappings);
        if (shapeErrors.Count > 0) return ValidationProblem(shapeErrors);

        var normalized = OwnerMappingTable.Normalize(ownerMappings);
        if (normalized.Count == 0) return null;

        var snapshot = await directory.ListUsernamesAsync(ct);
        if (!snapshot.Available)
            // 🔴 **502 であって 400 ではない。** 「確かめられなかった」を「存在しない」と
            // 報告するのは嘘である。保存しない点は同じなので安全側は変わらない。
            return Results.Json(
                new
                {
                    // `message` キーで返す（SPA の問題本文パーサが読む 4 キーの 1 つ）。
                    message = "利用者名簿を取得できなかったため、写像先の実在を確認できませんでした。"
                            + "保存していません。時間をおいて再試行してください。",
                },
                statusCode: StatusCodes.Status502BadGateway);

        var missing = OwnerMappingTable.ValidateTargetsExist(normalized, snapshot.Usernames);
        return missing.Count > 0 ? ValidationProblem(missing) : null;
    }

    // 🔴 **RFC7807 の `ValidationProblem` で返す。`{ error = ... }` では画面に理由が出ない。**
    //
    // SPA 側の問題本文パーサ（`platform/frontend/src/lib/api/apiClient.ts` の
    // `parseProblemDetails`）が読むのは `errors` / `detail` / `title` / `message` の 4 キーだけで、
    // **`error` は読まない。** 本サービスの既存の 400（`ConnectionUriPolicy`）は `{ error }` 形だが、
    // それは**画面に理由が出ていない**ということであって、真似する理由にはならない。
    // #1194 の受け入れ基準は「保存されず、**理由が表示される**」なので、読まれる形で返す。
    //
    // 形は AuthorizationService の `UserAdminEndpoints.ValidationProblem` と同じにする
    // （画面が 2 種類の読み方を覚えなくて済む）。
    private static IResult ValidationProblem(List<string> errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["errors"] = [.. errors] });
}
