// テンプレート: feature 固有の型。
//
// 計画 13_frontend-stack §ディレクトリ構成 が定める Feature 単位の区分
// （`api/ components/ hooks/ routes/ stores/ types/`）のうち `types/`。
//
// **BFF の DTO をここへ手書きしない。** BFF 由来の型は orval の生成物
// （`@foundation/api/generated/bff.schemas`）を import する（ADR-0031 §基本方針「手書きクライアント禁止」）。
// ここへ置くのは、画面の都合で組み立てる**表示用の型**だけである。

/** 一覧の絞り込み条件（URL の検索パラメータと 1 対 1 に対応させる）。 */
export interface SampleFilter {
  /** 自由入力の検索語。空文字は「絞り込みなし」を意味する。 */
  keyword: string;
}

/** 一覧に表示する 1 件分（BFF の DTO から画面の都合へ写像したもの）。 */
export interface SampleListItem {
  id: string;
  title: string;
}
