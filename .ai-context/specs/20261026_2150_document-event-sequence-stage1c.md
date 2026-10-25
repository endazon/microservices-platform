---
title: 作業仕様書 — 並行例外を 409 へ写していなかった 8 経路を、発行の連番に負けても 500 にならないようにする（#2150 段 1c / #2258）
type: spec
status: done
related_ids: [FR-02, FR-06, FR-18, FR-19, FR-20, SC-05, SC-19, SC-20, IADR-0637, IADR-0597, IADR-0296, IADR-0549, IADR-0608]
author: claude
created: 2026-10-26
updated: 2026-10-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06・FR-18・FR-19・FR-20）
---

# 仕様書: 既存の 8 経路の 500 を 409 にする（#2150 段 1c）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-06（文書の CRUD・アーカイブ・公開・本文）、FR-18（AI タグ提案の承認の反映）、FR-19 / FR-20（個人資料・同期の衝突の解決）
- 実装 ADR: IADR-0637 決定 7（本書で追記）、IADR-0597（版の並行トークンと 409 の唯一の写し点）
- issue: #2258（本段の追跡）。前段: #2259（1a）・#2261（1b）

## 背景

- 段 1a で、`EventSequence` を `Version` と並ぶ並行トークンにした。
- 次の 8 経路は、もとから並行例外を 409 へ写していなかった。1a の前は Touch する相手にだけ負けて、500 を返した。
- 1a の後は、Touch しない状態の変化（共有・露出・論理削除・復元・タグの改名・再発行）にも負ける。窓が広がる（#2258）。

## 母集合（規則 9）

- 段 1a の作業仕様書の「引き直した母集合」の段 1c の行から取った。
  - 引き方: `Document` の mutator 10 個の呼び出し元と `Documents.Remove*` を全件 grep し、`DocumentVersionConflict` への写しの有無で分けた。
- 着手前に、各経路の確定の位置を読み直した（下の表の「確定」）。

| 経路 | 確定 | 負け得る保存 | 写し先 |
| --- | --- | --- | --- |
| Archive（`POST /documents/{id}/archive`） | `SaveChangesAndPublishUpdatedIfIndexableAsync` 1 回 | 文書の更新 | `DocumentVersionConflict.AfterLostSaveAsync` |
| Publish（`POST /documents/{id}/publish`） | 同上 | 同上 | 同上 |
| PutBody（`PUT /documents/{id}/body`） | 同上（本文のオブジェクトは確定の前に同じキーへ上書き済み） | 同上 | 同上 |
| SyncConflicts/Resolve（`POST /private-notes/sync/conflicts/{id}/resolve`） | `local` は元の資料の更新。`both` は別名資料の新規作成。`server` は競合の記録だけ | **`local` だけ**（`both`・`server` は元の文書行を書かない） | 同上 |
| AddTag（gRPC `DocumentTagWrite/AddTag`。REST は #1902 で撤去済み） | 同上 | 同上 | 利用の場の結果 `VersionConflict` → gRPC `ABORTED` |
| StripUntrustedBoundary（`POST /documents/strip-untrusted-boundary`） | ページ全件を 1 回で確定 | ページの文書の更新 | `DocumentVersionConflict.ProblemForBatch(message)` |
| ArchiveAstQuotes（`POST /documents/archive-ast-quotes`） | 同上 | 同上 | 同上 |
| ArchiveBodyless（`POST /documents/archive-bodyless`） | 同上 | 同上 | 同上 |

## 設計

- **単一文書の 5 経路**: 確定を `try` で囲む。`DocumentVersionConflict.Is` なら `AfterLostSaveAsync(db, id, expectedVersion: null)`（#2103 と同じ形の 409。現在版を読み直す）を返す。
- **AddTag（gRPC だけ）**:
  - 利用の場（`AddDocumentTagUseCase`）が `AddDocumentTagStatus.VersionConflict` を返す。
  - gRPC は `ToProto` の `VersionConflict` の枝で `RpcException(StatusCode.Aborted)` を投げる。
    - 既定の枝は `NotWritable` なので、専用の枝が無いと競合が「書けない」へ化ける。
    - 枝を `ToProto` に置くのは、gRPC の器を使わずに単体で確かめられるからである。
  - proto の結果の値は足さない（契約を変えない）。
  - 呼び出し元の GraphService は、どの `RpcException` も `Unavailable`（502。承認を確定しない）として扱う。1c の前も、未処理の例外が同じ `Unavailable` になっていたので、呼び出し元の挙動は変わらない。
  - `ABORTED` を選ぶのは、gRPC の状態コードで「並行する操作に負けた（やり直せる）」を表すのがこれだからである。
- **複数文書の 3 経路**: 確定を `try` で囲み、id なしの 409 `{ error: "version_conflict", message }` を返す。ページは 1 件も確定していない。
  - 読み直してやり直す処理は入れない（1b の 2 経路と違う）。どれも一回限りの管理操作で、ページとカーソルで再開できるので、同じ要求の再送で済む。
- **Purge の文言（#2259 の AI レビュー 🟡 2）**: 1a で入れた Purge の id なしの 409 に `message` を載せる。
  - 文言: 「他の変更と重なったため、完全削除しませんでした。本文の実体は消えている場合がありますが、資料は削除済みのまま残っています。もう一度完全削除してください。」
  - SC-19 の表示は、既存の `toMessages` が本文の `message` を出す。
- `ProblemForBatch` は `message` を引数で受ける（呼び出し側ごとに再開の方法が違うため）。

## 受け入れ基準

- [x] 単一文書の 4 経路（Archive・Publish・PutBody・Resolve の `local`）は、確定の直前に同じ文書へ別の確定が入ると、409 `version_conflict` を返す（500 にならない）。
  - Resolve で衝突の記録まで巻き戻ることは、実 PostgreSQL のトランザクションに依る。InMemory はトランザクションを持たず、同じ保存の中で文書より先に書いた行が残るので、単体では測らない（実 PostgreSQL の試験は本段では足していない）。
  - 負けた側の発行は出ず、勝った側の連番だけが進む。
- [x] AddTag は、負けると gRPC `ABORTED` を投げる（`NOT_WRITABLE` へ化けない）。負けた側の発行は出ない。
- [x] 複数文書の 3 経路は、id なしの 409 と `message` を返す。ページの発行は 1 件も出ない。
- [x] Purge の 409 は、本文の実体が消えている場合があることと、再送を促す `message` を運ぶ。
- [x] 写しを外す変異で、各経路の試験が赤になる（8 経路それぞれで、対応する 1 件だけが赤になった）。
- [x] OpenAPI に 409 を足した（サービスの口 5 つ）。BFF の口は、既にある 409 の説明だけを直した（公開・完全削除・衝突の解決）。
- 試験の器: `DocumentRace`（1b の `AtSave`。文書行を書く保存の直前に窓を開ける）。

## 残余

- PutBody と Resolve の `local` は、本文のオブジェクトを確定の前に同じキーへ上書きする。負けた後は、台帳の指紋と本文の実体が食い違い得る。
  - 1c の前（版の競合）からある性質で、本文を書き直せば揃う。
  - キーを版ごとに分けるのは範囲外である。
- StripUntrustedBoundary は、剥がした本文を新しいキーへ書いてから確定する。負けた分のオブジェクトはどこからも参照されず、次の実行が同じキーへ書き直す（既存の性質）。
- BFF のアーカイブの口（`/bff/documents/{id}/archive`）は OpenAPI に 409 を持たない。
  - 後段の 409 は本文ごと透過して届く。
  - 409 を足すには、orval の生成物（応答の状態ごとの型）の再生成が要る。手元で生成器を回せない（空き容量）ため、本段では足さない。
- 受け手の連番の比較は段 2 である。

## PoC の事前手順

- migration なし。
- 🔴 1a・1b・1c がそろってから配備する。本段がそろった時点で、配備の条件を満たす。
