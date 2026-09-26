using DataSourceService.Domain;
using DataSourceService.Domain.Ports;

namespace DataSourceService.Features.DataSources;

// FR-05, UC-04, SC-06, 計画 ADR-0115 決定 1・5, ADR-0074 決定 4, [[IADR-0472]] (#1557):
// **明示した `department` の値域検査**（書き込み時）。値域は realm の `/department/<code>` の `<code>` の集合である。
//
// 登録・全置換・部分更新の 3 操作が使うため、集約直下に置く（`OwnerMappingValidation` と同じ理由 ——
// 3 箇所に書き分けると「登録では弾くのに PATCH では通る」穴が空く）。
// 応答の形も `OwnerMappingValidation` と揃える（値域の外 = 400 の `ValidationProblem`、引けない = 502 の `message`）。
// 画面は同じパーサで両方を読める。
//
// 🔴 **サーバ側で拒否することが要件である。** 画面だけの検証にすると API を直接叩いた経路で値域の外の部門が入る。
// 値域の外の部門は誰にも開かない側へ倒れる（計画 ADR-0115 実測 5）が、**結果は誤り**であり、
// 管理者は保存できたと思い込む。
internal static class DepartmentDomainValidation
{
    // 検査に通れば null。通らなければ返すべき応答（400 または 502）。
    //
    // 🔴 **照会するのは「明示された」部門だけである。** 次のときは値域を引かない:
    //   - `defaultAttributes` が要求に無い（PATCH の現状維持）
    //   - `department` が未解決（欠落・空白・予約値 `unassigned`）—— 判定は `DataSource.IsDepartmentUnresolved`
    //     （取り込み経路・登録時の導出と**同じ述語**。「未解決」の定義を 2 つ持たない）
    //   予約値 `unassigned` は部門コードではなく「解決できなかった」の記録であり（[[IADR-0199]]）、値域の対象外である。
    //   引かないので、認可サービスが落ちていても部門を空にする・予約値へ戻す操作は通る（無関係な操作を巻き込まない）。
    //
    // ［2026-09-26 / #1557 監査］🔴 **更新（PUT / PATCH）は、値が保存済みの値から変わったときだけ照会する**
    // （`storedAttributes` を渡す。登録は渡さず、明示されていれば常に照会する）。
    // SC-06 の既定属性フォームは保存済みの部門を毎回送り返すので、変わっていない値まで検証すると、
    // 値域が定まる前に保存された値（例: 旧属性辞書の `finance`）を持つソースは**機密区分・ライフサイクル・
    // 写像表だけの編集まで 400** になり、認可サービスや Keycloak が落ちている間は**あらゆる編集が 502** になる。
    // 検証の目的（値域の外の値を**新しく**書かせない）は、変わった値だけを見れば果たせる。
    // 比較は保存される値そのもので序数一致（trim しない。照会と同じ規則）。
    internal static async Task<IResult?> ValidateAsync(
        IReadOnlyDictionary<string, string>? defaultAttributes,
        IDepartmentDomainDirectory directory,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? storedAttributes = null)
    {
        if (defaultAttributes is null || DataSource.IsDepartmentUnresolved(defaultAttributes)) return null;

        // 🔴 **保存される値そのもので照会する**（前後の空白も落とさない）。検証だけ trim すると、
        // `" sales "` が検証を通って空白つきのまま保存され、利用者の `department` 属性と一致しない値が残る。
        var code = defaultAttributes[DataSource.DepartmentKey];
        if (storedAttributes is not null
            && storedAttributes.TryGetValue(DataSource.DepartmentKey, out var stored)
            && string.Equals(stored, code, StringComparison.Ordinal))
            return null;
        var snapshot = await directory.LookupAsync(new HashSet<string>(StringComparer.Ordinal) { code }, ct);
        if (!snapshot.Available)
            // 🔴 **502 であって 400 ではない。** 「確かめられなかった」を「値域の外」と報告するのは嘘である。
            // 保存しない点は同じなので安全側は変わらない（ADR-0074 決定 4 の実装と同じ挙動）。
            return Results.Json(
                new
                {
                    message = "部門グループを取得できなかったため、部門コードが値域に在るかを確認できませんでした。"
                            + "保存していません。時間をおいて再試行するか、部門を空欄にしてください。",
                },
                statusCode: StatusCodes.Status502BadGateway);

        if (snapshot.Codes.Contains(code)) return null;

        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["errors"] =
            [
                $"部門コード「{code}」は部門グループ（/department/<コード>）にありません。"
                + "部門グループのコード（大小文字を区別します）を入力するか、空欄にしてください。",
            ],
        });
    }
}
