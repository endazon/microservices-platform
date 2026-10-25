// NFR-30 (#2015): axe の**既知の違反の台帳**（指紋のスナップショット）。
//
// ■ 形: キーは `<面> / <テーマ>`（`expectAxeClean(surface)` の `surface` と `light` / `dark`）、
//   値は指紋（`<規則 ID> <対象セレクタ>`）と**受け入れる理由**の組の配列である。
// ■ 規律
//   - **1 件ずつ理由を書く**（何が・なぜ今は直さないか・どの issue で直すか）。理由の無い行は置かない。
//   - **画面側を直せる違反は直すのが先**である。ここへ載せるのは、直すと PR が肥大するか、
//     計画側の裁定を待つものに限る。
//   - 一致を求める（`support/axe.ts`）。直して違反が消えたら、**この台帳から行を消すまで落ちる**。
//   - `--update-snapshots` で黙って増やせる形（`toMatchSnapshot`）は採らない —— 受け入れに理由を
//     要求できないため（作業仕様書 `20261024_2015_axe-all-screens` §判断 2）。
//
// 現在の件数: 0（全面・両テーマで違反 0 件）。

interface KnownAxeViolation {
  /** `<規則 ID> <対象セレクタ>`。 */
  fingerprint: string;
  /** 受け入れる理由と、直す issue。 */
  reason: string;
}

export const KNOWN_AXE_VIOLATIONS: Readonly<Record<string, readonly KnownAxeViolation[]>> = {};
