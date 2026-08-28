// SC-01〜SC-11, IADR-0135 決定 1: orval 生成フックの応答封筒（`{ data, status, headers }`）から
// 本文だけを取り出す `select`。
//
// 生成コード（`httpClient: 'fetch'`）の応答型は「成功枝 ＋ 宣言したエラー枝」の union であり、
// 画面が読みたいのは成功枝の `data` だけである。**非 2xx は `apiRequest` が `ApiError` を投げる**
// （`orvalMutator.ts` → `apiClient.apiRequest`）ため、**エラー枝は実行時に解決しない**——
// 型の上にだけ残る枝であり、SC-08 が `mutation.data?.status === 200` で落としているのと同じ事実である。
//
// `select` に置く理由: 画面が読む `query.data` の形が載せ替え前（本文そのもの）と同じになり、
// 画面側の読み出しコードを書き換えずに済む（＝挙動の差分が入り込む余地が小さい）。
//
// **呼び出し側は生成フックの `TData` に本文型を明示する**（`useBffX<XDto>({ query: { select: okData } })`）。
// TanStack Query の `UseQueryOptions` は `Omit<…>`（マップ型）を経由して定義されているため、
// **`select` の戻り値から `TData` は推論されない**（実測: 推論されず既定＝封筒のままになる）。
// 明示した本文型と `okData` の戻り値は型検査で突き合わされるので、OpenAPI 側で応答スキーマが
// 変われば呼び出し側が型エラーになる（IADR-0131 決定 1 の保証を画面まで届かせるための結線）。

/**
 * 生成された応答 union のうち、成功（200）の本文型。
 *
 * union に対して分配されるため、エラー枝（`{ data: void; status: 404 }` 等）は `never` へ落ちる。
 */
export type OkPayload<TResponse> = TResponse extends { status: 200; data: infer TData }
  ? TData
  : never;

/**
 * 応答封筒から成功（200）の本文を取り出す。
 *
 * 型としてはエラー枝を落とすだけの絞り込みであり、実行時は `res.data` をそのまま返す
 * （エラー枝は `apiRequest` が投げるためここへ到達しない）。
 */
export function okData<TResponse extends { status: number; data: unknown }>(
  res: TResponse,
): OkPayload<TResponse> {
  return res.data as OkPayload<TResponse>;
}

/**
 * 配列を返す面のための `select`。**本文が配列でなければ空配列へ縮退させる。**
 *
 * IADR-0135 決定 7［2026-08-06 追記］: 載せ替え前は `apiFetch` が本文なし（204・空ボディ）で
 * `undefined` を返したため `?? []` が実効ガードだった。**`bffFetch` は同じ場合に `{}` を返すので
 * `okData(res) ?? []` の `??` は発火しない**（`{} ?? []` は `{}`）——「契約上は必須でも実行時に
 * 必ず来るとは限らない」という決定 7 の前提は正しいまま、**その前提を守る式だけが空振りしていた**。
 * したがって `??` ではなく `Array.isArray` で判定する（実測: 空ボディで `entries.map is not a
 * function` により SC-11 / SC-09 がルートごとクラッシュしていた）。
 *
 * **型の保証は `okData` と同じ**である——戻り値は `OkPayload<TResponse>` なので、契約側で応答型が
 * 変われば呼び出し側（`useBffX<XDto[], unknown>`）が型検査で落ちる。
 *
 * **配列を返す面にだけ使う。** 非配列の面へ誤って使っても型検査は落ちない（空ボディのとき静かに
 * `[]` になる）。それでも `okData` のまま落ちるより退行は小さい。
 */
export function okArray<TResponse extends { status: number; data: unknown }>(
  res: TResponse,
): OkPayload<TResponse> {
  const value = okData(res);
  return (Array.isArray(value) ? value : []) as OkPayload<TResponse>;
}
