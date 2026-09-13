import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-03, FR-05/FR-06: 属性・タグパネルの表示（05_screens §SC-03 主要素「機密区分・部門・タグ」）。
//
// **キーだけを写像し、値は変換しない。**
//
// **［2026-08-10 追記 / #553］保留の理由は解消した。** 従前は「モックに現れる表示名が 4 値中 2 値だけで、
// 残る 2 値を実装が決めると事実上の用語定義になる」ため生値を出していた（planning#197 で裁定待ち）。
// **利用者裁定 2026-08-05 で 4 値すべての表示名が確定した**（正は計画リポジトリ `project-planning` の
// `docs/glossary.md`。
// public＝公開 / internal＝社内限 / confidential＝秘 / restricted＝**取扱制限**。
// **`restricted` は「極秘」ではない** —— 個人資料が既定でこの区分を持つため）。
//
// **それでも本ファイルでは値を変換しない。** 写像を入れる先は引用（`CitationDto`）側の **#541** であり
// （#553 がそう指示している）、ここで先に入れると**同じ写像が 2 か所に生まれる**。
// 値集合の単一情報源は `lib/abac/confidentiality.ts` である。

/**
 * 計画が画面ラベルを与えている属性キー。**ここに無いキーは描かない**
 * （計画 ADR-0102 決定 4 / #1455。下の `orderedAttributes` を参照）。
 */
const ATTRIBUTE_LABELS: Record<string, MessageDescriptor> = {
  confidentiality: msg`機密区分`,
  department: msg`部門`,
};

/** 属性キーに対応する表示ラベル（未定義なら `undefined`）。解決は描画時に行う。 */
export function attributeLabel(key: string): MessageDescriptor | undefined {
  return ATTRIBUTE_LABELS[key];
}

/**
 * 属性を表示順へ並べる。計画 §SC-03 §主要素 が挙げる 3 カテゴリ（機密区分・部門・タグ）に閉じる。
 *
 * 🔴 **［2026-09-13 / 計画 ADR-0102 決定 4 / #1455］既知のキーだけを描く（whitelist）。**
 * 従前は「既知のキー ＋ 残り全部」を返しており、**応答に載る属性がそのまま画面へ出ていた** ——
 * 個人資料では `owner=<利用者名>` / `doc_scope` / 露出 3 トグルの生の行が、読める者すべてに出ていた。
 * 計画は本パネルを「機密区分・部門・タグ」と定めており、**計画が正である**（実装が先行して乖離していた）。
 *
 * **落とした値は消えるのではなく、それぞれの持ち場が描く** —— 所有者は §個人資料の表示（`PrivateNoteSummary`）、
 * 個人資料であることは 👤 のラベル、露出 3 トグルは SC-19 である。
 *
 * 🔴 **`ATTRIBUTE_LABELS` が唯一の値域である。** 属性が増えても画面の統制は自動では緩まない
 * （出したい属性はラベルを与える＝明示の判断を要する）。
 */
export function orderedAttributes(attributes: Record<string, string>): [string, string][] {
  const entries = attributes ?? {};
  return Object.keys(ATTRIBUTE_LABELS).flatMap((key): [string, string][] =>
    key in entries ? [[key, entries[key]]] : [],
  );
}
