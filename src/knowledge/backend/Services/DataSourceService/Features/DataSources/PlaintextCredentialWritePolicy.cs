using DataSourceService.Domain;
using DataSourceService.Infrastructure.Secrets;
using Microsoft.Extensions.Options;

namespace DataSourceService.Features.DataSources;

// NFR-18, FR-01, SC-06, 計画 ADR-0126 フォローアップ 2（「移送の後は、設定への平文の書き込みを拒む」）,
// [[IADR-0664]] 決定 2 (#458 段 S4): SC-06 の登録・更新・部分更新の API で、**移送期間の後は秘密のキーへの平文を 400 で拒む**。
//
// - 旗（`ConnectorCredentials:PlaintextAllowed`）が真の間は何もしない（移送期間。従前どおり受け入れる）。
// - 拒むのは平文だけ（`PlaintextCredential.IsPlaintext`）。参照（`vault:`）・空白（値なし）・マスク（`***`。既存値を保つ書き戻し）は通す。
// - 🔴 応答はキー名だけを返す。値は返さない・ログに出さない。資格情報は SC-22 の「データソースの資格情報」の群から入れる（計画 ADR-0126 決定 1）。
internal static class PlaintextCredentialWritePolicy
{
    internal const string ErrorCode = "plaintext-credential-rejected";

    internal static IResult? Reject(IReadOnlyDictionary<string, string>? config, IOptions<ConnectorCredentialOptions> options)
    {
        if (options.Value.PlaintextAllowed)
            return null;

        var keys = PlaintextCredential.KeysOf(config);
        return keys.Count == 0
            ? null
            : Results.BadRequest(new
            {
                error = ErrorCode,
                keys,
                message = "資格情報は設定に平文で保存できない。SC-22（秘密情報・接続設定の管理）の「データソースの資格情報」から入れる。",
            });
    }
}
