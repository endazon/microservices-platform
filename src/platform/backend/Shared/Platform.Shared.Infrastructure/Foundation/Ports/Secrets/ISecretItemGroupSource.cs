using Microsoft.AspNetCore.Http;

namespace Platform.Shared.Infrastructure.Foundation.Ports.Secrets;

// SC-22, NFR-18, 計画 ADR-0126 決定 1・2・4, [[IADR-0501]] 決定 1 (#458 段 S2):
// SC-22 の**群**（実行時に成員が増える項目の集まり）の成員を、可変ユニットから基盤の BFF へ供給するポート。
//
// ■ なぜポートか
//   群の仕組み（認可・専用接頭辞・Vault への書き込み・監査・値を返さない）は基盤の BFF（`Platform.Bff/Foundation`）が持つ。
//   成員（例: 登録済みのデータソース）を知っているのは可変ユニットであり、**基盤は可変ユニットを参照できない**
//   （`src/README.md`「依存規則」・[[IADR-0117]]）。そこで成員の列挙と参照の配置だけをこのポートへ切り出し、
//   可変ユニットの BFF モジュールが実装して DI へ登録する（合成点 = BFF ホストの Program.cs）。
//
// ■ 🔴 このポートが**決めないもの**
//   Vault のパス（接頭辞）・書ける役割・値の扱いは、基盤が `sc22-secret-items.json` の `groups[]` と Vault の policy から決める。
//   実装は成員の ID・表示名・プロパティ名・供給の事実だけを返し、**値に触れない**（値はこのポートを通らない）。

/// <summary>群の成員を供給する。1 実装 = 1 群（`Group` が `sc22-secret-items.json` の `groups[].group` と一致する）。</summary>
public interface ISecretItemGroupSource
{
    /// <summary>群の名前（`groups[].group`）。</summary>
    string Group { get; }

    /// <summary>
    /// いま登録されている成員。**取れないときは null**（BFF は空の群へ縮退させず 502 にする —— 「成員が無い」と
    /// 「取れない」は別の意味である）。利用者の資格情報の伝播のため、要求の <see cref="HttpContext"/> を受ける。
    /// </summary>
    Task<IReadOnlyList<SecretItemGroupMember>?> ListMembersAsync(HttpContext http, CancellationToken ct);

    /// <summary>
    /// Vault へ書いた**後に**、成員が書いた値を使うよう参照を置く（値を持たないプロパティにだけ）。
    /// 戻り値は配置後の供給の事実。**失敗は null**（書き込みは成立しているので、BFF は失敗を書き込みの失敗にしない）。
    /// </summary>
    Task<SecretItemGroupSupply?> EnsureReferenceAsync(
        HttpContext http, string memberId, string property, CancellationToken ct);
}

/// <summary>群の成員 1 件。`MemberId` は Vault のパスの 1 セグメントになる（基盤が形を検査する）。</summary>
public sealed record SecretItemGroupMember(
    string MemberId,
    string DisplayName,
    string Kind,
    IReadOnlyList<SecretItemGroupProperty> Properties);

/// <summary>成員の書けるプロパティ 1 つと、その供給の事実。</summary>
public sealed record SecretItemGroupProperty(string Name, SecretItemGroupSupply Supply);

/// <summary>プロパティの供給の事実（計画 ADR-0126 決定 4）。</summary>
public enum SecretItemGroupSupply
{
    /// <summary>成員の設定が群のパスを指す参照を持つ。画面から書いた値が使われる。</summary>
    Referenced,

    /// <summary>成員の設定が値を持たない。画面から書くと参照が置かれ、使われる。</summary>
    Unset,

    /// <summary>成員の設定が画面以外の値（平文・別の場所の参照）を持つ。画面から書いた値は使われない。</summary>
    OtherSource,

    /// <summary>
    /// 供給の事実を判定できない（後段が未知の符号を返した等）。🔴 **「画面」にも「画面以外」にも寄せない**
    /// （計画 ADR-0126 決定 4「不明を 2 値へ寄せない」。独立監査の指摘で足した。表示「確認できない」）。
    /// </summary>
    Unknown,
}
