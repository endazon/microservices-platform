// NFR-30 (#2191 / IADR-0619): Keycloak のテーマが描く画面（SC-13 の Keycloak 側・SC-14〜16）の
// axe の**既知の違反の台帳**。形と規律は SPA の台帳（`../support/axeKnownViolations.ts`。IADR-0614 決定 2）と同じである。
//
// ■ 形: キーは `<面> / <テーマ>`（`expectKeycloakAxeClean(page, surface)` の `surface` と `light` / `dark`）、
//   値は指紋（`<規則 ID> <対象セレクタ>`）と**受け入れる理由**の組の配列。
// ■ 規律
//   - **1 件ずつ理由を書く**（何が・なぜテーマ側で直せないか・どこで直すか）。理由の無い行は置かない。
//   - **テーマ（CSS・メッセージ）で直せる違反は直すのが先**である。ここへ載せるのは、直すのに親テーマの
//     テンプレート（`.ftl`）や Keycloak 本体の React アプリの改変が要るもの（IADR-0532 決定 1 が禁じる複製）に限る。
//   - 一致を求める（`./support.ts`）。直して違反が消えたら、**この台帳から行を消すまで落ちる**。
//   - SPA の台帳とは分ける —— 面の名前空間が違い（Keycloak の面は SPA の spec に現れない）、
//     SPA 側の「どの走査にも当たらないキー」の検査が Keycloak の面のキーを死に行と誤判定するため。
//
// 現在の件数: 0（全面・両テーマで違反 0 件）。

interface KnownAxeViolation {
  /** `<規則 ID> <対象セレクタ>`。 */
  fingerprint: string;
  /** 受け入れる理由と、直す先。 */
  reason: string;
}

export const KNOWN_KEYCLOAK_AXE_VIOLATIONS: Readonly<Record<string, readonly KnownAxeViolation[]>> =
  {};
