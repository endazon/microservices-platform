import { useEffect, useState } from 'react';

// SC-19 主要素 3, FR-19, ADR-0098 決定 1 / IADR-0445 / IADR-0447: 共有先を検索する 2 つの読み口
// （利用者・グループ）が**同じ作法で検索語を扱う**ための 1 か所。
//
// 🔴 **下限とデバウンスを読み口ごとに持たない。** `/bff/users/lookup` と `/bff/groups/lookup` は
// どちらも `q` の `minLength: 2` を持ち、下限未満は 400 になる。同じ値を 2 か所に書くと、
// 一方の契約が変わったときに**片方だけ直る**（そして下限未満の問い合わせが静かに 400 で返る）。
//
// **`useDebounced` を export しない** —— 汎用の口を作ると未使用 export の床（check-knip）を
// 押し上げる。他画面が要るようになったら foundation へ出す。

/** 検索を始める最小文字数（契約 `q` の `minLength: 2`）。1 文字は 400 になる。 */
const MIN_QUERY_LENGTH = 2;

/** 入力の落ち着きを待つ時間。**1 文字ごとに問い合わせない**ための間隔である。 */
const DEBOUNCE_MS = 300;

/**
 * 値の変化を `delay` ミリ秒だけ遅らせて返す。
 */
function useDebounced<T>(value: T, delay: number): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), delay);
    return () => clearTimeout(timer);
  }, [value, delay]);
  return settled;
}

export interface LookupTerm {
  /** 落ち着いた検索語（前後の空白を落としたもの）。 */
  term: string;
  /** 入力が落ち着いて、かつ 2 文字以上になったか（画面の案内文の出し分けに使う）。 */
  enabled: boolean;
}

/**
 * 生の入力を「問い合わせてよい検索語」へ整える。
 *
 * **2 文字未満では問い合わせない。** 契約が 400 を返す条件を画面側でも止める
 * （多層防御。`syncFolders.ts` と同じ考え方で、**値域の防壁はサーバ側にある**）。
 */
export function useLookupTerm(rawQuery: string): LookupTerm {
  const term = useDebounced(rawQuery.trim(), DEBOUNCE_MS);
  return { term, enabled: term.length >= MIN_QUERY_LENGTH };
}
