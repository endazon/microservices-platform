---
title: 変換ジョブ 画面仕様書
type: screen-spec
status: completed
related_ids:
  - SC-07
  - UC-06
  - FR-12
  - IADR-0042
  - IADR-0121
  - IADR-0124
  - IADR-0125
  - IADR-0127
  - IADR-0128
author: claude
created: 2026-07-09
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - "./SC-03_document-detail.md"
  - "./SC-06_datasource-management.md"
  - "../adr/IADR-0042_conversion-job-read-model.md"
  - "../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md"
  - "../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md"
  - "../specs/20260805_issue-503_sc05-08-admin-screens.md"
  - "../specs/20260805_issue-501_retry-admin-only.md"
  - "../tests/SC-07_conversion-jobs.md"
---

# 画面仕様書: 変換ジョブ（SC-07）

> **［実装状態］`status: completed` は「本仕様書が記述する範囲の実装とテストが揃った」ことを表す**
> （`docs/README.md` 運用ルール 6）。**未実装のまま残っている要素がある**——hi-fi の
> 「人手補正」2 ペイン編集と「デッドレター」の内訳表示（いずれも契約に載る先が無い）、
> 共通シェルのパンくず・右レール。詳細と引き受け先は §hi-fi モックアップとの対応 と §未決事項 を見ること。

> **［2026-08-05 / #503］計画の 2026-08-04 確定（4 状態モデル・照会/再変換 API・**再変換は管理者ロール限定**・
> 同一ジョブの直列化）へ追随し、新スタックでの再実装に合わせて全面改訂した。**
>
> **［2026-08-05 / #501］API 側（`POST /bff/conversion/jobs/{id}/retry`）も `platform-admin` のみへ絞り、
> 計画確定事項「本画面のアクセス制御と API の権限を揃える」を満たした**（[[IADR-0128]] 決定 1）。
> 本書内の **`retry` について**「API は admin/operator のまま」とした記述は**この時点で解消済み**である。
> **照会（一覧・個別取得）の閲覧ロールは admin/operator のまま据え置き**であり
> （planning#198 提案 8 の裁定待ち。[[IADR-0128]] 決定 2）、§画面概要・目的 の「アクセス」と
> §データソース（BFF 境界）の［追記］が述べる据え置きは**現在も真である**——閉じたのは retry だけである。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-07 変換ジョブ画面**（[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-07・**§データソース〔2026-08-04 確定〕**・遷移図 `SC06 → SC07 → SC03`）
- 関連ユースケース（UC）: **UC-06**（文書を正規化変換する。**代替「変換ジョブの状況を照会する」「失敗した変換を再実行する」**〔2026-08-04 追記〕・**例外「恒久失敗はデッドレターへ送る」**）
- 関連機能要求（FR）: **FR-12**（正規化変換）
- モックアップ（**実装の正**）: [hi-fi/sc-07.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-07.html) ／ [wireframe/sc-07.html](../../planning/projects/microservices-platform/05_screens/mockups/wireframe/sc-07.html)
- 関連 IADR: [[IADR-0042]]（変換ジョブの読み取りモデル・API）・[[IADR-0127]]（**画面側の再変換の管理者限定**ほか #503 の設計判断）・
  [[IADR-0128]]（**API 側の再変換の管理者限定**・照会の据え置き・下流の代償統制）
- 連携 issue: **#501**（API 側の管理者ロール強制の突合。**解消済み**）

## 計画の確定事項（2026-08-04）とその実装

| 計画の確定 | 実装 |
| --- | --- |
| ジョブ状態モデルは **4 値**（`queued` / `processing` / `succeeded` / `failed`） | §ジョブ状態の表示 のとおり 4 値をそのまま写す |
| デッドレターの表示は `failed` の**内訳** | **内訳としては区別しない**（契約に標識が無い。§実装しない要素の理由 (b)） |
| 照会 API は `GET /jobs` 相当・**状態でのフィルタ**を備える | 「状態で絞り込み」`Select`（すべて / 待機中 / 変換中 / 完了 / 失敗）。「失敗のみ」はこの `Select` の 1 値 |
| 再変換 API は `retry` 相当 | `POST /bff/conversion/jobs/{id}/retry` |
| **再変換の実行権限は管理者ロールに限る** | **`platform-admin` を持つ利用者にだけ再変換ボタンを出す**（[[IADR-0127]] 決定 1）。**API（`POST …/retry`）も `platform-admin` のみ**（#501 / [[IADR-0128]] 決定 1）——確定の「画面と API の権限を揃える」を両側で満たす |
| 回数上限は設けない。**同一ジョブの再変換は直列化**し、実行中（`processing`）の要求は拒否する | 画面は `failed` の行にだけボタンを出す。**サーバの 409（`not_retryable`）も画面で扱う**——UI 制御だけに頼らない |

## 画面概要・目的

正規化変換（pandoc ＋ LLM）のジョブ状況を確認し、失敗ジョブを再変換する運用画面。
SC-06（データソース管理）からの遷移先であり、完了ジョブから SC-03（変換結果）へ遷移する。

- ルート: `/admin/conversions`（05_screens §共通シェル「ルートパス」）
- アクセス: **`platform-admin` または `platform-operator`**（[[IADR-0039]] / [[IADR-0042]]）。権限外は `NotFound`（存在秘匿）。
  **再変換の実行は `platform-admin` のみ**（計画 2026-08-04 確定）。
  **これは画面（#503 / [[IADR-0127]] 決定 1）と API（#501 / [[IADR-0128]] 決定 1）の両側で効いている**——
  計画確定事項（`01_screens.md:257`「本画面のアクセス制御と API の権限を揃える」）は満たされており、
  **API を直接叩いても運用者は retry できない**（403。無認証は 401）。
  **照会（一覧・個別取得）の閲覧ロールは admin/operator のまま据え置き**、計画（`01_screens.md:115` /
  `:234` / `:242` / `:250` の「管理者ロール限定」）との差異は **planning#198 提案 8 の裁定待ち**である
  （[[IADR-0039]] 決定 1 由来の既知の逸脱。[[IADR-0128]] 決定 2）。

## hi-fi モックアップとの対応（実装する要素／実装しない要素）

行番号は planning `d980a01` の [hi-fi/sc-07.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-07.html) に対するものである。
粒度の規則は [SC-05](./SC-05_document-management.md) と共通である。

| # | モックの要素（行） | 実装 | 備考 |
| --- | --- | --- | --- |
| 1 | 見出し「変換ジョブ（pandoc＋LLM）」（416 左） | **する** | `<h1>` |
| 2 | 絞り込み「失敗のみ ▾」（416 右） | **する** | `Select`（すべて / 待機中 / 変換中 / 完了 / **失敗**）。計画確定の「状態でのフィルタ」の実体。**既定は「すべて」**（理由は §絞り込みの既定値） |
| 3 | 一覧の**ジョブ**列（418・420-422。`#J-9812`） | **する** | `ConversionJobDto.id`（GUID）。モックの `#J-9812` のような短縮 ID は契約に無いため **GUID の先頭 8 桁 ＋ 完全な値を `title` 属性**で示す |
| 4 | 一覧の**原本**列（418・420-422） | **する** | `originalPath` |
| 5 | 一覧の**状態**列（418・420-422） | **する** | `StatusBadge`。**色 ＋ アイコン ＋ テキスト**（INDEX 決定 21）。§ジョブ状態の表示 |
| 6 | 状態の**「✕ 図コード化失敗」**（420。失敗の**理由**） | **する** | 状態バッジは「失敗」とし、**理由は備考列の `error`** に出す（契約上 `error` は自由文字列） |
| 7 | 状態の**「⚠ デッドレター」**（421） | **しない** | **契約の不在**。§実装しない要素の理由 (b) |
| 8 | 一覧の**備考**列（418・420-422） | **する** | `error`（あれば）。無ければ `—` |
| 9 | 行操作「**再実行**」（421） | **する（管理者のみ）** | `failed` の行にだけ。**`platform-admin` を持たない利用者には理由つきの注記**を出す（[[IADR-0127]] 決定 1） |
| 10 | 行操作「**人手補正**」（420） | **しない** | **契約の不在**。§実装しない要素の理由 (a) |
| 11 | 行操作「**結果 →**」（422） | **する** | `succeeded` かつ `documentId` があれば `/docs/$id`（SC-03）への内部リンク。計画の遷移図 `SC07 -- 変換結果 --> SC03` |
| 12 | **人手補正パネル**「変換結果（編集可）／原本プレビュー」＋「補正して再登録」（425-430） | **しない** | 同上 (a) |
| 13 | **共通シェル**: 右レール「AIチャットパネル」（433-438） | **しない** | 移行**第 4 段**（[[IADR-0121]] 決定 1・5） |
| 14 | **共通シェル**: パンくず（413。`ホーム / 管理 / データソース管理 / 変換ジョブ`）・ブランド／アバター（412）・左ナビ（414） | **本画面では作らない** | パンくずは #452 系。他は `foundation/ui/Layout` が既に持つ |

### モックに無いが実装する要素

| 要素 | 計画上の根拠 |
| --- | --- |
| 「データソース管理へ戻る」リンク | 計画のパンくず（413）が `データソース管理 / 変換ジョブ` の階層を示す。パンくず自体は #452 系の射程だが、遷移図 `SC06 → SC07` の**逆方向の導線**をリンク 1 本で残す |

### ジョブ状態の表示（計画確定の 4 値）

| `status` | 表示 | `StatusBadge` の `tone` | アイコン（`tone` から自動） |
| --- | --- | --- | --- |
| `queued` | 待機中 | `neutral` | `Info` |
| `processing` | 変換中 | `neutral` | `Info` |
| `succeeded` | 完了 | `success` | `CircleCheck` |
| `failed` | 失敗 | `danger` | `CircleX` |
| 上記以外（未知の値） | 生値をそのまま | `neutral` | `Info` |

**未知の値を握り潰さない**（`—` や「不明」へ丸めない）。契約が 4 値と定めていても、サーバが将来 5 つ目を返せば
画面は生値を見せて異常に気付ける状態にしておく。

### 絞り込みの既定値

**既定は「すべて」とする。** モック 416 は絞り込みの選択値として「失敗のみ ▾」を描くが、
**同じモックの一覧（420-422）は `failed` 以外（✓ 完了）を含んでいる**——モック内で矛盾しており、
「失敗のみ」を既定にすると描かれた一覧を再現できない。計画本文（§SC-07 主要素）も
「ジョブ一覧テーブル、**「失敗のみ」フィルタ**」と両方を挙げており、**一覧が既定で全件**であることと整合する。

### 実装しない要素の理由（**いずれも繰り延べであって放棄ではない**）

| # | 計画の記述 | 現在の契約（実測） | 必要な変更 |
| --- | --- | --- | --- |
| (a) 人手補正の 2 ペイン編集 | §SC-07 主要素「人手補正の2ペイン編集（左=変換結果の編集・右=原本プレビュー）」・アクション「人手補正の保存。補正結果は取り込みへ再投入する」 | 変換ジョブ API は `GET /jobs`・`GET /jobs/{id}`・`POST /jobs/{id}/retry` の 3 本のみ。**補正済み Markdown を受け取る口が無く、原本の本文／プレビューを返す口も無い**（`ConversionJobDto` が持つのは `originalPath` と `markdownUri` という**参照**だけで、本文は返らない）。`retry` は「**変換を最初からやり直す**」もので編集結果を受け取らない（計画 §データソース の表も「変換を最初からやり直す」と書く） | 補正投稿 API（補正済み Markdown の受け取り）＋ 原本・変換結果の本文取得 API |
| (b) 「デッドレター」の内訳表示 | §SC-07 主要素「デッドレター状態の表示」・§データソース「デッドレターの表示は `failed` の内訳として扱う」 | `ConversionJobDto(Id, SourceId, SourceType, OriginalPath, Status, Error, DocumentId, MarkdownUri, Attempts, CreatedAt, UpdatedAt)` に**デッドレターの標識が無い**。`Attempts` は試行回数であり「デッドレターへ送られたか」とは別（上限値も契約に無い） | `ConversionJobDto` へのデッドレター標識（あるいは失敗種別） |

実測の出所: `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/ConversionJobDto.cs` ／
`src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/ConversionBffEndpoints.cs` ／
`src/knowledge/backend/Services/ConversionService/src/ConversionService.Worker/Foundation/Endpoints/ConversionJobEndpoints.cs`（対象コミット `de55761`）。

**「動かない UI を置く」形は採らない**（#502 が確立した規則）。保存先の無い 2 ペイン編集を置くと、
管理者は補正したつもりで何も反映されない——UC-06 代替フロー「変換結果を管理者が補正して再登録する」を
**満たしたように見せて満たさない**のが最も悪い。2 件は環流の記録に載せた
（[feedback/20260805_sc05-07-admin-contract-gaps.md](../../feedback/20260805_sc05-07-admin-contract-gaps.md)。**planning#198 として起票済み・裁定待ち**）。

## データソース（BFF 境界）

| 用途 | エンドポイント | 呼び出し方 | 認可（サーバ側） | 応答 |
| --- | --- | --- | --- | --- |
| 一覧（状態フィルタ） | `GET /bff/conversion/jobs[?status=]` | **orval 生成フック `useBffConversionJobList`**（#519。キーは `['/bff/conversion/jobs', { status }]`） | admin / operator（非特権 403・無認証 401） | `ConversionJobDto[]` |
| 再変換 | `POST /bff/conversion/jobs/{id}/retry` | `useMutation` | **admin のみ**（operator は 403・無認証 401。#501 / [[IADR-0128]] 決定 1） | 202 / 404 / **409 `not_retryable`** |

> **［2026-08-05 追記・再変換は API 側も管理者ロール限定（#501 / [[IADR-0128]]）］**
> 計画 [05_screens §SC-07 §データソース](../../planning/projects/microservices-platform/05_screens/01_screens.md)（**2026-08-04 確定**）の
> 「**再変換の実行権限は管理者ロールに限る**。本画面のアクセス制御と API の権限を揃える」に追随し、
> `retry` の認可を `platform-admin` のみへ絞った（[[IADR-0128]] 決定 1・[[IADR-0042]] 決定 3 への［追記］）。
> 実装はグループの認可（admin または operator）へ `PlatformAuthPolicies.AdminOnly` を**重ね**、
> AND 合成で admin のみに絞る形である。**照会（一覧・個別取得）は admin/operator のまま据え置く**
> —— 2026-08-04 の確定が命じたのは再変換の実行権限の是正だからであり、
> 閲覧ロールの差異（[[IADR-0039]] 決定 1 由来の既知の逸脱）の是正の向きは
> **planning#198 提案 8 の裁定に従う**（[[IADR-0128]] 決定 2）。
> 画面側の再変換ボタンを管理者のみにする作業は **#503 / PR #508** が持ち、**マージ済み**である。
> 本画面が呼ばない `GET /bff/conversion/jobs/{id}`（個別取得）も同じく admin/operator であり、
> retry を絞った際に巻き添えで絞られていないことをテストで固定している
> （テスト仕様書 §BFF の 5b / 5c）。

- **orval 生成フックで呼ぶ**（#506 で契約が揃い、**#519** で載せ替えた。[[IADR-0135]] 決定 1）。
  再変換の成功後は**引数なしの生成キー**（`['/bff/conversion/jobs']`）で無効化する——絞り込み条件は
  キーの 2 要素目に載るため、これが条件つきキーの前方一致になる（[[IADR-0135]] 決定 3）。
- **BFF は後段障害を空一覧へ縮退させない**（502 で可視化する。「ジョブ無し」と「サービス障害」を誤認させない）。
  画面もこれに合わせて**取得失敗をエラーとして表示する**。
- 再変換の成功後は `invalidateQueries({ queryKey: getBffConversionJobListQueryKey() })`
  （**引数なし**＝`['/bff/conversion/jobs']`）のみを行う（[[IADR-0127]] 決定 5）。
  **［2026-08-06 追記］かつて `['bff','conversion','jobs']` と書いていたのは載せ替え前の階層キーで
  あり、上の行（前方一致が成立する理由）と食い違っていた。** 生成キーでの前方一致は
  「絞り込み条件がキーの 2 要素目に載る」ことで成立する（[[IADR-0135]] 決定 3）。

## レイアウト / 主要素

```text
┌──────────────────────────────────────────────────────────────────────────┐
│ 変換ジョブ（pandoc＋LLM）                         状態で絞り込み [すべて ▾]│
├──────────┬──────────────────┬────────────┬──────────────┬─────────────┤
│ ジョブ    │ 原本              │ 状態        │ 備考          │ 操作         │
├──────────┼──────────────────┼────────────┼──────────────┼─────────────┤
│ 9812ab34 │ 障害対応手順書.docx │ ✕ 失敗      │ 図コード化失敗 │ [再変換]     │
│ 9805cd12 │ 経費精算規程.docx   │ ✓ 完了      │ —            │ 結果 →       │
└──────────┴──────────────────┴────────────┴──────────────┴─────────────┘
  ← データソース管理へ戻る
```

## 表示・入力項目

| 項目 | 種別 | 必須 | 形式・制約 | 説明 |
| --- | --- | --- | --- | --- |
| 状態で絞り込み | `Select` | — | 空（すべて）/ `queued` / `processing` / `succeeded` / `failed` | 空以外は `?status=` として送る |
| ジョブ | 表示 | — | GUID の先頭 8 桁（完全な値は `title`） | 契約が短縮 ID を持たないため |
| 原本 | 表示 | — | `originalPath` | |
| 状態 | `StatusBadge` | — | 4 値（§ジョブ状態の表示） | 色 ＋ アイコン ＋ テキスト |
| 備考 | 表示 | — | `error` または `—` | 失敗の理由 |

## アクション・イベント

| 操作 | 挙動 | 遷移先 |
| --- | --- | --- |
| 状態で絞り込み | `useQuery` のキーが変わり再取得する（**UC-06 代替フロー「変換ジョブの状況を照会する」**） | — |
| 再変換（管理者のみ） | `POST …/retry` → 一覧を再取得（**UC-06 代替フロー「失敗した変換を再実行する」**） | — |
| 結果 → | 変換結果の文書へ | `/docs/$id` |
| ← データソース管理へ戻る | SC-06 へ | `/admin/sources` |

## 権限・表示条件

| 利用者 | 画面 | 再変換ボタン |
| --- | --- | --- |
| `platform-admin` | 見える | **`failed` の行に出る** |
| `platform-operator` | 見える | **出ない。**代わりに「再変換は管理者のみ実行できます」と**理由を書く** |
| ロールなし | `NotFound`（存在秘匿） | — |

**運用者に理由を書く**のは、無言でボタンを消すと「このジョブは再変換できない（状態の問題）」と読めてしまい、
権限の問題と区別できないためである。**これは存在秘匿の対象ではない**——画面へ到達できている時点で
画面の存在は既知であり、秘匿しているのは文書の存在であって操作の権限要件ではない（[[IADR-0127]] 決定 1）。

**この境界は API 側でも効く**——`POST /bff/conversion/jobs/{id}/retry` は operator に 403 を返す
（#501 / [[IADR-0128]] 決定 1）。すなわち**画面の制御は実効境界の写しであって、実効境界そのものではない**
（[[IADR-0039]] 決定 2 の「サーバ側を実効境界とする」に従う）。

## エラー・状態

| 状態 | 表示 |
| --- | --- |
| 取得中 | 「読み込み中…」（`role="status"`） |
| 一覧の取得失敗 | `Alert tone="danger"` `role="alert"`（**0 件表示へ縮退しない**） |
| 成功・0 件 | 「該当する変換ジョブはありません。」 |
| 再変換の受付（202） | `Alert tone="success"` `role="status"`（「再変換を受け付けました。」） |
| **再変換の拒否（409 `not_retryable`）** | `Alert tone="warning"` `role="alert"`（「このジョブは再変換できません（実行中、または失敗以外の状態です）。」）。**直列化の実体**——UI 制御をすり抜けた要求もここで扱う |
| 不在（404） | `Alert tone="danger"` `role="alert"` |

## i18n

- 文言はすべて Lingui のカタログ（ja / en）へ載せる。`eslint-plugin-lingui` の適用範囲に本 feature を含める。
- **ジョブ状態の 4 値は表示名を翻訳する**（計画が状態モデルを日本語の意味で定義しているため）。
  未知の値だけは生値のまま出す（翻訳しない）。

## UI 部品（`@platform/ui`）

`Table` 一式 / `Button` / `Select` / `Label` / `Alert` / **`StatusBadge`**。新規プリミティブは追加しない。

## 関連仕様

- 作業仕様書: [20260805_issue-503_sc05-08-admin-screens.md](../specs/20260805_issue-503_sc05-08-admin-screens.md)
- テスト仕様書: [SC-07_conversion-jobs.md](../tests/SC-07_conversion-jobs.md)
- 作業仕様書（API 側）: [20260805_issue-501_retry-admin-only.md](../specs/20260805_issue-501_retry-admin-only.md)
- 実装 ADR: [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) / [IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) / [IADR-0042](../adr/IADR-0042_conversion-job-read-model.md)
- 計画への環流（**planning#198 として起票済み・裁定待ち**）: [feedback/20260805_sc05-07-admin-contract-gaps.md](../../feedback/20260805_sc05-07-admin-contract-gaps.md)

## 未決事項

1. **人手補正の 2 ペイン編集**（§実装しない要素 (a)）。補正投稿 API と本文取得 API が要る。
   **環流の記録を作成済み・planning#198 として起票済み（裁定待ち）。**
2. **デッドレターの内訳表示**（同 (b)）。`ConversionJobDto` への標識が要る。同上。
3. ~~**API 側の管理者ロール強制**（#501）~~ **解消済み**（2026-08-05 / #501。[[IADR-0128]] 決定 1）。
   `POST /bff/conversion/jobs/{id}/retry` は `platform-admin` のみとなり、
   **運用者は API を直接叩いても再変換できない**（403）。計画確定事項（`01_screens.md:257`）は満たされた。
4. **閲覧ロール**（admin/operator か admin のみか）。計画 §共通シェル（`:115`）と §SC-05（`:234`）・
   §SC-06（`:242`）・§SC-07（`:250`）の 4 箇所 対 [[IADR-0039]] 決定 1 の差異。**planning#198 提案 8 で裁定待ち**。
   同上の環流記録に含めた。
