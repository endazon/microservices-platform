---
title: IADR-0127 管理画面（SC-05〜08）の実装方針 — 再変換は画面側で管理者限定、状態表示は契約から導出できる値だけで作る
type: impl-adr
status: Accepted
related_ids: [SC-05, SC-06, SC-07, SC-08, UC-02, UC-03, UC-04, UC-06, FR-01, FR-06, FR-07, FR-11, FR-12, ADR-0031, IADR-0039, IADR-0041, IADR-0042, IADR-0121, IADR-0124, IADR-0125, IADR-0126]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - ../specs/20260805_issue-503_sc05-08-admin-screens.md
  - ../screens/SC-05_document-management.md
  - ../screens/SC-06_datasource-management.md
  - ../screens/SC-07_conversion-jobs.md
  - ../screens/SC-08_ai-analysis-dashboard.md
  - ../adr/IADR-0039_datasource-management-bff-and-role-gating.md
  - ../adr/IADR-0041_document-write-bff-abac-scoped.md
  - ../adr/IADR-0042_conversion-job-read-model.md
  - ../adr/IADR-0126_sse-answer-state-and-search-url-state.md
---

# IADR-0127: 管理画面（SC-05〜08）の実装方針 — 再変換は画面側で管理者限定、状態表示は契約から導出できる値だけで作る

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-05
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: **SC-05 / SC-06 / SC-07 / SC-08**・**UC-02 / UC-03 / UC-04 / UC-06**・
  FR-01 / FR-02 / FR-06 / FR-07 / FR-09 / FR-11 / FR-12 ／
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted）
- **計画の確定事項（2026-08-04）**: 05_screens §SC-07 §データソース ——
  ジョブ状態は **4 値**（`queued` / `processing` / `succeeded` / `failed`。デッドレターは `failed` の内訳）／
  照会 API は `GET /jobs` 相当／再変換は `retry` 相当／**再変換の実行権限は管理者ロールに限る**／
  回数上限は設けず**同一ジョブの再変換は直列化**する。
- 関連 IADR: [[IADR-0039]]（管理系画面のロールゲート）・[[IADR-0041]]（文書書き込みのスコープゲート）・
  [[IADR-0042]]（変換ジョブの読み取りモデル）・[[IADR-0126]]（画面のサーバー状態の持ち方）・
  [[IADR-0121]] 決定 3（BFF 境界）・[[IADR-0125]] 決定 1（プリミティブは文言・ドメインを持たない）
- 起点 issue: **#503**（連携 **#501**）

## コンテキストと課題

#503 は SC-05〜08 を新スタックへ載せ替える。実装にあたり、計画が値を与えていない／
計画と既存の実装決定が食い違う次の 6 論点を確定する必要がある。

1. **再変換の権限**。計画は 2026-08-04 に「管理者ロールに限る」と確定したが、既存の
   [[IADR-0039]]（Accepted）は SC-05/06/07 を **admin または operator** としており、
   API（`/bff/conversion/jobs`）も admin/operator である。どこをどこまで狭めるか。
2. **契約に無い状態表示**。hi-fi は SC-06 に「⚠ 再試行中（3/5）」「次回同期 毎日 03:00」、
   SC-05 に「変換 ✓完了／✕失敗」を描くが、いずれも BFF の DTO に該当フィールドが無い。
3. **BFF の呼び出し経路**。`/bff/analysis/analyze` は `docs/api/openapi.yaml` にあり orval 生成フックが存在するが、
   `/bff/documents` / `/bff/datasources` / `/bff/conversion/jobs` は openapi.yaml に無い。
4. **SC-08 の結果の持ち方**。[[IADR-0126]] は自ら適用範囲を「SC-01 の本文」に限定している。SC-08 へ広げるのか。
5. **一覧の再取得**。旧 4 画面は `useCallback` の `load()` を手で呼び直していた。
6. **操作結果の伝え方**。共通シェルには `notify`（sonner トースト）があるが、本番の呼び出し元がまだ無い。

## 検討した選択肢と決定

### 決定 1: SC-07 の**再変換ボタン**を `platform-admin` のみに出す。**閲覧ロールは admin/operator のまま据え置く**

| 案 | 採否 | 理由 |
| --- | --- | --- |
| **A. 再変換だけを admin 限定にする**（採用） | ○ | 計画 2026-08-04 の確定は文言どおり「**再変換の実行権限**は管理者ロールに限る」であり、閲覧（画面到達）には触れていない。確定した範囲だけを狭める |
| B. SC-05/06/07 の画面ごと admin 限定にする | × | 05_screens §共通シェル の「SC-05/06/07 = 管理者（管理）」は 2026-07-24 のバッジ由来の記述で、[[IADR-0039]]（Accepted・2026-07-08）が「データソース・変換ジョブ・文書 CRUD はいずれも運用／コンテンツ管理者の職務」として operator を含めた既存決定を持つ。**3 画面すべての到達可否を本 issue の射程で覆すのは、計画の 2026-08-04 確定が求めた範囲を超える**。差異は作業仕様書 §計画書との差異 に残し、計画側の裁定を仰ぐ |
| C. 何もしない（#501 の結果を待つ） | × | issue #503 が「本 issue は**画面側のアクセス制御**を実装して #501 の結果と揃える」と明示的に分担している |

**画面が API より厳しい状態を一時的に作ることを許容する。** 計画が否定したのは
「**API 側だけ緩い**（＝画面の制御が意味を持たない）」形である。画面が先に厳しくなっても
画面の制御は意味を持ち続ける——ただし**運用者が API を直接叩けば再変換できる穴は残る**ため、
#501 が API を admin 限定へ揃えるまでの暫定状態であることを明記する。

**運用者に「ボタンが無い」ことをどう伝えるか**: `failed` 行の操作欄へ
「再変換は管理者のみ実行できます」と**理由を書く**。無言でボタンを消すと、運用者は
「このジョブは再変換できない」と読む（状態の問題と権限の問題が区別できない）。
**これは存在秘匿の対象ではない**——SC-07 へ到達できている時点で画面の存在は既知であり、
秘匿しているのは文書の存在であって操作の権限要件ではない（[[IADR-0009]] の射程外）。

### 決定 2: 契約から**導出できる値だけ**で状態を表示する。導出できない状態は実装せず環流する

| 対象 | 採用した表示 | 実装しないもの |
| --- | --- | --- |
| SC-06 同期状態 | `disabled` → **無効（琥珀・警告）** ／ `active` ＋ `lastSyncedAt` あり → **同期済み（日時）**（成功） ／ `active` ＋ なし → **未同期**（中立） | 「⚠ 再試行中（3/5）」（連続失敗回数の契約が無い） |
| SC-06 次回同期 | — | 列ごと作らない（同期は全ソース共通の間隔で回る hosted service であり、ソース別スケジュールが契約に無い） |
| SC-05 変換 | — | 列ごと作らない（文書 → 変換ジョブの対応が契約に無く、**失敗ジョブは `documentId` を持たない**ため原理的に結合できない） |
| SC-07 ジョブ状態 | 計画確定の **4 値をそのまま**写す（`queued` 待機中 / `processing` 変換中 / `succeeded` 完了 / `failed` 失敗） | 「デッドレター」の内訳表示（`ConversionJobDto` にデッドレターの標識が無い） |

**琥珀（警告色）は `disabled` へ充てる。** 05_screens のモック間相違の確定 ② は
「SC-06 の**同期異常表示**の警告色＝琥珀」と定める。同期異常そのものは表示できないが、
琥珀が指すべき意味——「**このソースから取り込みが行われていない**」——は `disabled` が満たす。
色は `StatusBadge` の `tone` が持ち、アイコンとテキストが必ず伴う（INDEX 決定 21）。

**「常に空の列」「押しても結果が変わらないボタン」は置かない**（#502 が確立した規則）。
計画が SC-05 へ与えた役割は「管理者が文書の状態を正確に把握する」ことであり、
決して失敗を表示できない「変換」列はその役割をむしろ損なう。

### 決定 3: **openapi.yaml に載っている API だけ orval 生成フックで呼ぶ**。載っていない 3 群は `apiFetch` ＋ 手書き型

ADR-0031 / [[IADR-0121]] 決定 3 は「orval 生成フック**または** `foundation/api` の `apiFetch` / `apiStream`」を許す。
本 issue では画面ごとに経路が分かれる。

| 画面 | 経路 | 理由 |
| --- | --- | --- |
| SC-08 | **orval 生成フック `useAnalysisAnalyze`** | `/bff/analysis/analyze` が openapi.yaml にある。生成物があるのに手書きするのは契約の二重管理 |
| SC-05 / SC-06 / SC-07 | `apiFetch` ＋ feature 内の手書き型 | `/bff/documents` / `/bff/datasources` / `/bff/conversion/jobs` が **openapi.yaml に無く生成物が存在しない**。SC-03 が同じ状態にあり **#506** として起票済み |

**生成型と後段の実体の食い違いに触れない。** openapi.yaml の `AiAnswerDto.citations` は
`SearchResultDto[]` だが、後段（`Knowledge.Contracts.Dtos.AiAnswerDto`）が返すのは `CitationDto[]` である
（`number` / `snippet` / `sourceUri` を持ち、`text` / `tags` を持たない）。
**SC-08 は両者に共通して存在するフィールド（`documentId` / `documentTitle` / `chunkId`）だけを使う**——
これは hi-fi モックの出典表示（タイトルのリンクのみ）と一致するため、表示を削る妥協ではない。
**食い違いそのものは #506 へ申し送る**（openapi.yaml を実体へ揃える作業の一部）。

### 決定 4: **[[IADR-0126]] を SC-08 へ広げない。** 追加の決定も要らない

SC-08 の分析は **SSE ではない**（`POST /bff/analysis/analyze` が 1 度で完結する JSON 応答）。
かつ orval 生成フックは `useMutation` であり、**そもそも Query のキャッシュに載らない**。
[[IADR-0126]] 決定 1 が避けようとした事象（戻る操作で古い回答が古い出典つきで復活する）は、
mutation では構造的に起こらない——結果はコンポーネントの寿命と一致し、再マウントで消える。

したがって **[[IADR-0126]] の適用範囲（SC-01 本文）を書き換える必要も、新しい決定を足す必要も無い。**
本項は「広げないことを確認した」記録である。

**SC-07 の絞り込み条件も URL へ載せない**（[[IADR-0126]] 決定 3 は SC-02 の検索語の決定である）。
計画 §ルートパス は SC-02 を `/search?q=` と**クエリつきで**定める一方、SC-07 は `/admin/conversions` と
クエリを持たない形で定めている。計画が共有・ブックマークを要求していない条件を URL へ持ち上げると、
ルートの型（`validateSearch`）が計画に無い契約を持つ。**単一の `useState` を `useQuery` のキーに入れる**形とし、
情報源は 1 つに保つ。

### 決定 5: 更新系の成功後は `invalidateQueries` だけを行う（手書きの再取得を持たない）

旧 4 画面は `load()` を `useCallback` で作り、`useEffect` で初回に呼び、各操作の後で呼び直していた。
新実装では取得は `useQuery` が持ち、`useMutation` の `onSuccess` で
`queryClient.invalidateQueries({ queryKey: [...] })` するだけにする。
**「取得のタイミングを手で管理する」コードが消え、測るべき分岐が減る**（#502 の branches の伸びと同じ効果）。

### 決定 6: 操作結果は**画面内の `Alert`** で伝える（`notify` トーストを使わない）

| 案 | 採否 | 理由 |
| --- | --- | --- |
| **A. 画面内 `Alert`**（採用） | ○ | 操作結果は**一覧の文脈に紐づく**。特に 409（版競合・再変換不可）と 400（検証）は**詳細を読んで次の操作を決める**ための情報であり、数秒で消えると読めない |
| B. `notify`（sonner トースト） | × | 消えることが前提の部品であり上記に合わない。加えて描画先（`<Notifications />`）は共通シェル（`Layout`）にしかなく、**画面の責務がシェルの有無に依存する**形になる |

`notify` は**共通シェル横断の一過性の通知**（例: セッション期限切れ）のための部品であり、本 issue でも
本番の呼び出し元は増えない（#496 / #502 からの申し送りは解消しない。§フォローアップ）。

## 結果

### 良い点

- 計画の 2026-08-04 確定（再変換 = 管理者限定）が**画面側で実際に効く**。認可を外すと落ちるテストで固定される。
- 「契約から導出できない状態」を作らないため、**表示が常に本物のデータに裏付けられる**。
- 取得・再取得の分岐が TanStack Query 側へ寄り、画面が持つ状態は「未確定の入力値」だけになる。

### 悪い点・トレードオフ

- **画面と API の権限が一時的にずれる**（画面 = admin のみ、API = admin/operator）。#501 が揃えるまでの間、
  運用者は API を直接叩けば再変換できる。**この穴は #501 へ申し送る。**
- モックに描かれた要素のうち **6 種**を実装しない（作業仕様書 §3 の B）。利用者から見ると
  「モックにあるのに無い」状態が残る。**画面仕様書の対応表と環流記録で所在を明示する**ことで補う。
- SC-05 / 06 / 07 が orval 生成フックに載らないため、**契約の変更が型では検出されない**（#506 の射程）。

## フォローアップ

1. **#501 が API 側を admin 限定へ揃えたら、本 IADR 決定 1 の「一時的なずれ」を解消済みとして追記する。**
2. **#506 の射程を広げる**——`/bff/documents` に加えて `/bff/datasources` / `/bff/conversion/jobs` も
   openapi.yaml に無い。加えて `AiAnswerDto.citations` の型が実体（`CitationDto`）と食い違う。
3. `notify` の本番の呼び出し元は依然 0 件（#496 / #502 からの申し送りを引き継ぐ）。
4. 実装しない 6 種の要素は、計画側の裁定（planning#197 ＋ 新規環流記録）ののち後続 issue で実装する。
