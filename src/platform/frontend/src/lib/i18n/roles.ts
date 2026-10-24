import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import { PlatformRole } from '../auth/roles';

// SC-17（#2214）: **realm ロールの識別子 → 計画の表示名**。
//
// 表示名の正は計画リポジトリの 05_screens §SC-17 主要素（ロール＝利用者／管理者／運用者／システム管理者）
// と計画 ADR-0026 §決定（ロールは 4 種・複数併任可）である。識別子との対応は次の 2 つだけが計画・実装の
// 双方で定まっている:
//   - `platform-admin`    → システム管理者（05_screens §SC-17 アクセス制御「実装のロールは `platform-admin`」。2026-10-23 明記）
//   - `platform-operator` → 運用者（IADR-0030「運用者ロールは platform-operator を新設」）
// **利用者・管理者には realm ロールが無い**（4 種への分離は ADR-0026 のフォローアップ。07_abac-attribute-model
// 「後続フェーズで導入する」）。識別子を先回りで作って表へ載せない —— 決まっていない対応を表が決めてしまう。
// ロールが足された日に、ここへ 1 行足す。
//
// ■ なぜ foundation に置くか
//   識別子の定数（`PlatformRole`）とヘッダのロールタグが foundation にあり、ロールの語彙は基盤のものである。
//   機密区分の `confidentialityLabel`（同じディレクトリ）と同じく `@foundation/i18n` の公開面から引かせる。
//   🔴 **ヘッダのロールタグ（`app/Layout.tsx` の「システム管理」「運用」）とは別の語である** —— あれは
//   hi-fi モックのタグの字面（略記）であり、ロールそのものの名前ではない。

const ROLE_LABELS: Record<string, MessageDescriptor> = {
  [PlatformRole.Admin]: msg({ message: 'システム管理者', context: 'ロール' }),
  [PlatformRole.Operator]: msg({ message: '運用者', context: 'ロール' }),
};

/**
 * ロールの表示名。**値集合の外の値は生値のまま出す** —— 割当可能ロールは認可基盤が realm から引いて返す
 * （`wiki-editor` 等も含む）。知らない識別子を既知の表示名へ丸めると、どのロールを割り当てたかを誤らせる。
 *
 * 表示だけを写す。**送る値（割当・フィルタ）は識別子のまま**である。
 * 訳は呼び出し側の `i18n._` で引く（描画時に解決し、ロケールの切替に追随させる）。
 */
export function roleLabel(
  value: string,
  translate: (descriptor: MessageDescriptor) => string,
): string {
  const descriptor = Object.hasOwn(ROLE_LABELS, value) ? ROLE_LABELS[value] : undefined;
  return descriptor ? translate(descriptor) : value;
}
