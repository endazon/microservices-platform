// FR-19, SC-03, ADR-0054, 計画 ADR-0102 (#1455): 文書スコープ（ABAC 文書属性 `doc_scope`）の語彙。
//
// **画面ではなく語彙の単位で置く**（`confidentiality.ts` / `owner.ts` と同じ理由）。
// SC-03（個人資料としての表示）と SC-05（編集不可の属性）が同じキーを指す。
//
// 🔴 **値集合は 2 値だけを持たない。** 判定に要るのは「個人資料か」であり、
// 組織文書は **`doc_scope` を持たない文書も含む**（ADR-0054 決定 5。契約 `GraphNodeItem` の注記が同じ扱いを述べている）。
// したがって `organization` の定数は置かず、**`private-note` かどうかだけ**を判定する。

/** 文書スコープの属性キー。 */
export const DOC_SCOPE_KEY = 'doc_scope';

/** 個人資料を表す値（バックエンド `DocumentAttributes.DocScopePrivateNote` と同じ）。 */
export const DOC_SCOPE_PRIVATE_NOTE = 'private-note';

/** その文書が個人資料か（属性を持たない文書は組織文書として偽を返す）。 */
export function isPrivateNote(attributes: Record<string, string> | undefined): boolean {
  return attributes?.[DOC_SCOPE_KEY] === DOC_SCOPE_PRIVATE_NOTE;
}
