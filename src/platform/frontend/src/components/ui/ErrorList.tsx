// FR-09, SC-05/SC-06/SC-09（#183）: 検証（400）・競合（409）エラーの詳細メッセージをアラート一覧表示する共通部品。
// 各画面での重複（Errors）を foundation/ui へ単一情報源化する（IADR-0040: ApiError.details 統一）。
// メッセージ写像は同ディレクトリの apiErrors.ts（toMessages）を用いる。

// 検証・競合の詳細メッセージ群をアラートとして一覧表示する。空なら何も描画しない。
export function ErrorList({ errors }: { errors: string[] }) {
  if (errors.length === 0) return null;
  return (
    <ul role="alert" style={{ color: '#b00' }}>
      {errors.map((e, i) => (
        <li key={i}>{e}</li>
      ))}
    </ul>
  );
}
