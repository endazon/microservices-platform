import { createContext, useContext, useEffect } from 'react';

// 05_screens §共通シェル / #446: パンくずの**動的な葉**（SC-03 の文書タイトル）。
//
// 計画のモックアップ（SC-03）の crumb は `ホーム / 検索結果 / 経費精算規程 v3.2` であり、
// 最後の段は**実行時にしか決まらない**。共通シェルは文書 API を知らない（知ってはならない。
// IADR-0056 決定 3: platform → 可変ユニットの参照は禁止）ので、画面側から受け取る。
//
// 🔴 **取得前は葉を描かない。** 未確定の文字列（「読み込み中」等）をパンくずへ出すと、
// パンくずが「現在地の名前」ではなく「状態表示」になる。段ごと出さないほうが正確である。

/** 葉の設定関数。既定は no-op（共通シェルの外で描画された画面でも落ちない）。 */
export type SetBreadcrumbLeaf = (leaf: string | undefined) => void;

const noop: SetBreadcrumbLeaf = () => {};

export const BreadcrumbLeafContext = createContext<SetBreadcrumbLeaf>(noop);

/**
 * 画面が自分のパンくずの葉を共通シェルへ渡す（`FeatureBreadcrumb.label` を宣言しない画面用）。
 *
 * `leaf` が `undefined` の間は葉を描かない。画面を離れる（アンマウントする）ときは
 * `undefined` へ戻す —— 戻さないと、次の画面のパンくずに前の文書名が残る。
 */
export function useBreadcrumbLeaf(leaf: string | undefined): void {
  const setLeaf = useContext(BreadcrumbLeafContext);
  useEffect(() => {
    setLeaf(leaf);
    return () => setLeaf(undefined);
  }, [setLeaf, leaf]);
}
