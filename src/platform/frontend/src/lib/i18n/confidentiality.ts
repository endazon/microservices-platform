import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// FR-04, SC-01, SC-02（#2017 / #2018）: **機密区分（ABAC 文書属性 `confidentiality`）の表示名**。
//
// 表示名の正は計画リポジトリの用語集「機密区分」である（public＝公開 / internal＝社内限 /
// confidential＝秘 / restricted＝**取扱制限**。「極秘」ではない）。値集合（キー）の正は
// knowledge の `lib/abac/confidentiality.ts` が持つ。
// 「公開」は SC-05 の動詞（文書を公開する）と同じ字面なので、`context` で訳を分ける。
//
// ■ なぜ foundation に置くか
//   出典一覧の部品（`components/ai-chat/CitationList`。SC-01 と右レールが使う）と、knowledge ユニットの
//   検索結果一覧（SC-02）の両方が同じ写像を使う。knowledge → foundation の向きだけが許されるので、
//   共有するなら foundation に置くしかない。2 か所に表を持つと片方が古くなる（#2018 の作業仕様書）。
//   置き場を `@foundation/i18n` にしたのは、新しい別名を足さずに両方から引ける唯一の「語彙」の置き場だからである。

const CONFIDENTIALITY_LABELS: Record<string, MessageDescriptor> = {
  public: msg({ message: '公開', context: '機密区分' }),
  internal: msg({ message: '社内限', context: '機密区分' }),
  confidential: msg({ message: '秘', context: '機密区分' }),
  restricted: msg({ message: '取扱制限', context: '機密区分' }),
};

/**
 * 機密区分の表示名。**値集合の外の値は生値のまま出す** —— 契約は `enum` にしておらず（後段の値追加を
 * 破壊的変更にしない）、知らない値を既知の表示名へ丸めると、引用の可否の判断を誤らせる。
 *
 * 訳は呼び出し側の `i18n._` で引く（描画時に解決し、ロケールの切替に追随させる）。
 */
export function confidentialityLabel(
  value: string,
  translate: (descriptor: MessageDescriptor) => string,
): string {
  const descriptor = Object.hasOwn(CONFIDENTIALITY_LABELS, value)
    ? CONFIDENTIALITY_LABELS[value]
    : undefined;
  return descriptor ? translate(descriptor) : value;
}
