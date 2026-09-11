// FR-09, SC-05/SC-06/SC-09（#183）: 検証（400）・競合（409）エラーの詳細メッセージをアラート一覧表示する共通部品。
// 各画面での重複（Errors）を foundation/ui へ単一情報源化する（IADR-0040: ApiError.details 統一）。
// メッセージ写像は同ディレクトリの apiErrors.ts（toMessages）を用いる。

// 検証・競合の詳細メッセージ群をアラートとして一覧表示する。空なら何も描画しない。
export function ErrorList({ errors }: { errors: string[] }) {
  if (errors.length === 0) return null;
  // ［2026-09-12 / UI/UX 改善］**素の 16 進色（`#b00`）を意味トークンへ置き換えた。**
  // 直値はテーマ（ライト / ダーク）を持たず、ライト面では地色との対比が確保できていなかった。
  // 危険色だけに意味を載せないのは呼び出し側の責務ではなく構造の側で担保する——
  // `role="alert"` が「これは失敗の知らせである」ことを、本文が中身を伝える（INDEX 決定 21）。
  return (
    <ul role="alert" className="flex flex-col gap-n1 text-sm text-danger">
      {errors.map((e, i) => (
        <li key={i}>{e}</li>
      ))}
    </ul>
  );
}
