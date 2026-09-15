using System.Security.Cryptography;
using System.Text;

namespace Platform.Bff.Foundation.Secrets;

// SC-22, NFR-18, IADR-0456 決定 2・3 (#1477): プロパティの種別から「保管する値」を作る。
//
// 🔴 **ここで作った値（ハッシュ・鍵）も、入力の値と同じ扱いである。** 呼び出し側は Vault への要求本文にだけ渡し、
// 応答・監査・ログへ出さない（長さも出さない。IADR-0433 決定 6）。
internal static class SecretPropertyValues
{
    /// <summary>生成する RSA 鍵の長さ（OpenD の要件。契約 #1477）。</summary>
    public const int GeneratedRsaKeySizeBits = 1024;

    public static string Derive(SecretPropertyKind kind, string? submitted) => kind switch
    {
        SecretPropertyKind.Md5FromPassword => Md5LowerHex(submitted ?? string.Empty),
        SecretPropertyKind.GenerateRsaPkcs1 => GenerateRsaPkcs1Pem(),
        _ => submitted ?? string.Empty,
    };

    /// <summary>
    /// パスワードの MD5（UTF-8・小文字 hex 32 桁）。**消費側（moomoo OpenD）の設定形式が MD5 を要求する**ための変換であり、
    /// MSP の中で認証や完全性の判定に使うものではない。平文を保管しないことが目的である（IADR-0456 決定 2）。
    /// </summary>
#pragma warning disable CA5351 // 消費側の形式の要求（上記）。暗号学的な保護の目的では使わない。
    public static string Md5LowerHex(string password) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password)));
#pragma warning restore CA5351

    /// <summary>
    /// RSA 鍵を生成し、PKCS#1 の PEM で返す。鍵の長さは消費側（OpenD）の要件に合わせる（IADR-0456 決定 3）。
    /// 🔴 生成器は使い終えたら破棄する。返した文字列は呼び出し側が Vault へ書く 1 回だけに使う。
    /// </summary>
#pragma warning disable CA5385 // 鍵長は消費側（OpenD）の要件。MSP の中の保護には使わない。
    public static string GenerateRsaPkcs1Pem()
    {
        using var rsa = RSA.Create(GeneratedRsaKeySizeBits);
        return rsa.ExportRSAPrivateKeyPem();
    }
#pragma warning restore CA5385
}
