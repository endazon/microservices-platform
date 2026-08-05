---
title: 文書管理 画面仕様書
type: screen-spec
status: completed
related_ids:
  - SC-05
  - UC-03
  - FR-06
  - FR-09
  - IADR-0041
  - IADR-0121
  - IADR-0124
  - IADR-0125
  - IADR-0127
author: claude
created: 2026-07-09
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
related_specs:
  - "./SC-03_document-detail.md"
  - "./SC-06_datasource-management.md"
  - "./SC-07_conversion-jobs.md"
  - "../adr/IADR-0041_document-write-bff-abac-scoped.md"
  - "../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md"
  - "../specs/20260805_issue-503_sc05-08-admin-screens.md"
  - "../tests/SC-05_document-management.md"
---

# 画面仕様書: 文書管理（SC-05）

> **［実装状態］`status: completed` は「本仕様書が記述する範囲の実装とテストが揃った」ことを表す**
> （`docs/README.md` 運用ルール 6。計画側 `05_screens` の `status` には追随しない）。
> **未実装のまま残っている要素がある**——hi-fi の「変換」列（BFF 契約に載る先が無い）と、
> 共通シェルのパンくず・右レール。詳細と引き受け先は §hi-fi モックアップとの対応 と §未決事項 を見ること。

> **［2026-08-05 / #503］新スタックでの再実装に合わせて全面改訂した。**
> ルート `/admin/documents` は #490（[[IADR-0124]] 決定 6）で計画へ是正済みであり、本改訂でも変えていない。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-05 文書管理画面**（[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-05・遷移図 `SC05 → SC03`）
- 関連ユースケース（UC）: **UC-03**（文書を管理する。基本 1・**例外「必須属性が未設定の場合は保存を拒否する」**）
- 関連機能要求（FR）: **FR-06**（文書の CRUD・バージョン管理・メタデータ管理）・**FR-09**（文書属性・タグの設定）
- モックアップ（**実装の正**）: [hi-fi/sc-05.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-05.html) ／ [wireframe/sc-05.html](../../planning/projects/microservices-platform/05_screens/mockups/wireframe/sc-05.html)
- 関連 IADR: [[IADR-0041]]（書き込みのロール ＋ ABAC スコープゲート）・[[IADR-0039]]（管理系のロール）・[[IADR-0127]]（本作業の設計判断）・[[IADR-0009]]（存在秘匿）

## 画面概要・目的

正規化文書の一覧・登録・編集（属性／タグ設定）を行う管理画面。詳細と版履歴は SC-03（`/docs/:id`）が持つ
（05_screens §SC-05「版ごとの履歴パネルは SC-03 文書詳細側に置く。本画面は一覧の版列で現行版を示す」）。

- ルート: `/admin/documents`（05_screens §共通シェル「ルートパス」）
- アクセス: **`platform-admin` または `platform-operator`**（[[IADR-0039]] / [[IADR-0041]]）。
  権限外は `RequireRole` が `NotFound` を描き、画面の存在を示さない（[[IADR-0009]]）。
  計画 §共通シェル は「SC-05/06/07 = 管理者（管理）」と書くが、operator を含める既存決定を据え置く
  （差異と根拠は [作業仕様書 §計画書との差異](../specs/20260805_issue-503_sc05-08-admin-screens.md)）。

## hi-fi モックアップとの対応（実装する要素／実装しない要素）

行番号は planning `d980a01` の [hi-fi/sc-05.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-05.html) に対するものである。
**粒度の規則は #502 の 3 画面と共通である**——(a) メイン領域の要素は個別に 1 行、
(b) 共通シェルはまとめて 1 行（引き受け先を書く）、(c) **モックに無い状態（0 件表示・読み込み中・エラー表示）は
本表に入れず §エラー・状態 で扱う**（本表はモックとの対応表であって実装要素の一覧ではない）。

| # | モックの要素（行） | 実装 | 備考 |
| --- | --- | --- | --- |
| 1 | 見出し「文書一覧」（417 左） | **する** | `<h1>` |
| 2 | 「＋ 新規登録」（417 右） | **する** | `Button variant="primary"`。押すと右ペインが新規登録フォームになる |
| 3 | 一覧の**タイトル**列（419・421-423） | **する** | `Table`。タイトルは `/docs/$id`（SC-03）への内部リンク |
| 4 | 一覧の**機密区分**列（419・421-423） | **する** | `Tag`（分類名）。値は `attributes.confidentiality` の**生値**（理由は #7） |
| 5 | 一覧の**版**列（419・421-423） | **する** | `v{version}`。05_screens §SC-05「版列＝現行版の表示」 |
| 6 | **一覧の「変換」列**（419・421-423） | **しない** | **契約の不在**。§実装しない要素の理由 (a) |
| 7 | 機密区分の**日本語表示名**（421「社内限」/ 422「秘」） | **しない**（生値を出す） | 値集合は 4 値だが計画に表示名があるのは 2 値だけ。SC-03（#502）と同じ扱い。**planning#197 の裁定待ち** |
| 8 | 編集フォームの見出し（428） | **する** | `Card` ＋ 見出し。新規登録時は「文書を登録」、編集時は「文書を編集（v{n}）」 |
| 9 | 「タイトル *」（430） | **する** | `Input`。必須（1 文字以上） |
| 10 | 「機密区分（ABAC属性）*」（431） | **する** | `Select`。**定義済み区分のみ**（`public` / `internal` / `confidential` / `restricted`） |
| 11 | 「タグ（既定辞書に整合）」（432。`経理 ✕　規程 ✕　＋`） | **する** | 追加欄 ＋ 削除可能な `Tag` チップ。**辞書からの補完は行わない**（§実装しない要素の理由 (b)） |
| 12 | 「保存」＋「→ 取り込み・Wiki同期をトリガ」（433） | **する** | `Button` ＋ 補助文。保存（作成 / 更新）は後段で `DocumentUpdated` を発行し取り込み・Wiki 同期が走る |
| 13 | 注記「必須属性未設定は保存拒否（UC-03 例外フロー）」（435） | **する** | `Alert tone="info"`（静的な注記のため `role` を付けない） |
| 14 | **共通シェル**: 右レール「AIチャットパネル」（439-444） | **しない** | 移行**第 4 段**（[[IADR-0121]] 決定 1・5） |
| 15 | **共通シェル**: パンくず（413）・ブランド／アバター（412）・左ナビ（414） | **本画面では作らない** | パンくずは #452 系。ブランド・アバター・左ナビは `foundation/ui/Layout` が既に持つ |

### モックに無いが実装する要素（**逆向きの漏れも名指しする**）

対応表はモック → 実装の一方向しか見ない。実装にあってモックに無い要素を書かないと、
「勝手に足した機能」と「計画が別の場所で要求している機能」の区別がつかなくなる。

| 要素 | 計画上の根拠 |
| --- | --- |
| 行操作: **公開 / アーカイブ / 削除** | 05_screens §SC-05 目的「正規化文書の **CRUD**・版管理・属性／タグ管理」／ FR-06「文書の **CRUD**・バージョン管理・メタデータ管理」／ [[IADR-0041]]（Accepted。公開・アーカイブ・削除を SC-05 の操作として決定済み） |
| 編集時の「変更メモ」 | FR-06 のバージョン管理（`UpdateDocumentRequest.changeNote` は版スナップショットの説明として SC-03 の版履歴に出る） |

**公開ボタンは未公開状態（`draft` / `normalized`）の行にだけ出す**（`published` / `archived` では出さない）。
アーカイブ済みの誤再公開を防ぐためで、サーバも 409 で拒否する（多層防御）。

### 実装しない要素の理由（**いずれも繰り延べであって放棄ではない**）

| # | 計画の記述 | 現在の契約（実測） | 必要な変更 |
| --- | --- | --- | --- |
| (a) 変換列 | §SC-05 主要素「…変換状況」・hi-fi の「変換」列（✓ 完了 / ✕ 失敗） | `DocumentDto(Id, Title, Status, MarkdownUri, Version, Attributes, Tags, CreatedAt, UpdatedAt)` に変換の情報が無い。`Status` は **公開ライフサイクル**（`draft` / `normalized` / `published` / `archived`）であって変換結果ではない。`ConversionJobDto` 側から結合しようにも、**失敗ジョブは `DocumentId` を持たない**（文書が生成されないため）——すなわち「✕ 失敗」は原理的に文書行へ結び付かない | 文書 → 直近の変換ジョブの対応を返す契約（あるいは `DocumentDto` への変換状態フィールド） |
| (b) タグ辞書からの補完 | §SC-05 入力表「タグ｜任意｜複数選択｜**既定タグ辞書に整合**」 | タグ辞書は `/bff/admin/authz` 配下（**システム管理者限定**）にあり、SC-05 の利用者（admin / operator）が引ける保証が無い | 管理系ロールが引けるタグ辞書の照会口 |

実測の出所: `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/DocumentDto.cs` ／ `ConversionJobDto.cs` ／
`src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs` ／
`src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/AuthzBffEndpoints.cs`（対象コミット `de55761`）。

**「常に空の列」を置かない**（#502 が確立した規則）。決して「✕ 失敗」を表示できない「変換」列は、
計画が本画面へ与えた役割（管理者が文書の状態を正確に把握する）をむしろ損なう。

**なお計画自身が SC-05 と FR-12 の関係を一度是正している**——02_requirements トレーサビリティ表（2026-07-24）は
FR-12 の関連画面を SC-07 / SC-03 とし、「**SC-05 はモックの FR バッジ準拠で対象外**」と明記した。
05_screens §SC-05 主要素の「変換状況」だけが旧い記述として残っている可能性がある。この点も環流の記録へ含めた
（[feedback/20260805_sc05-07-admin-contract-gaps.md](../../feedback/20260805_sc05-07-admin-contract-gaps.md)。**計画リポへの起票は未了**）。

## データソース（BFF 境界）

| 用途 | エンドポイント | 呼び出し方 | 認可（サーバ側） | 応答 |
| --- | --- | --- | --- | --- |
| 一覧 | `GET /bff/documents` | `useQuery(['bff','documents'])` ＋ `apiFetch` | 認証。**ABAC スコープ内のみ**返る | `DocumentDto[]` |
| 登録 | `POST /bff/documents` | `useMutation` | admin / operator ＋ スコープ解決 | `DocumentDto`（201） |
| 更新 | `PUT /bff/documents/{id}` | `useMutation` | 同上。**版不一致は 409** | 204 |
| 公開 | `POST /bff/documents/{id}/publish` | `useMutation` | 同上。不正遷移は 409 | 204 |
| アーカイブ | `POST /bff/documents/{id}/archive` | `useMutation` | 同上 | 204 |
| 削除 | `DELETE /bff/documents/{id}` | `useMutation` | 同上 | 204 |

- **orval 生成フックは使えない**——`/bff/documents/*` は `docs/api/openapi.yaml` に無い（**#506**）。
  `apiFetch` ＋ feature 内の手書き型で呼ぶ（[[IADR-0127]] 決定 3）。
- 更新系の成功後は `invalidateQueries({ queryKey: ['bff','documents'] })` のみを行う（[[IADR-0127]] 決定 5）。

## レイアウト / 主要素

```text
┌────────────────────────────────────────────┬──────────────────────────────┐
│ 文書一覧                     [＋ 新規登録]  │ 文書を編集（v3）              │
├───────────┬─────────┬────┬───────────────┤ タイトル *  [経費精算規程  ]   │
│ タイトル   │ 機密区分 │ 版 │ 操作           │ 機密区分 *  [internal ▾]      │
├───────────┼─────────┼────┼───────────────┤ タグ        ［経理 ✕］［規程 ✕］│
│ 経費精算規程│［internal］│ v3 │ 編集 公開 …   │             [       ] [追加]  │
└───────────┴─────────┴────┴───────────────┤ 変更メモ    [            ]     │
                                             │ [保存] → 取り込み・Wiki同期    │
                                             │ ⓘ 必須属性未設定は保存拒否     │
                                             └──────────────────────────────┘
```

## 表示・入力項目

| 項目 | 種別 | 必須 | 形式・制約 | 説明 |
| --- | --- | --- | --- | --- |
| タイトル | `Input` | **必須** | 1 文字以上（前後空白を除く）・最大 200 文字 | 空では保存不可（**UC-03 例外フロー**） |
| 機密区分 | `Select` | **必須** | `public` / `internal` / `confidential` / `restricted` の 4 値のみ | 05_screens「定義済み区分のみ」。既定は `internal` |
| タグ | チップ ＋ `Input` | 任意 | 1 件 1 文字以上・重複不可 | 追加は「追加」ボタン。各チップの ✕ で削除 |
| 変更メモ | `Input` | 任意 | 最大 200 文字 | 編集時のみ。版スナップショットに残る |
| 版 | 表示 | — | `v{version}` | 現行版（05_screens §SC-05） |

## アクション・イベント

| 操作 | 挙動 | 遷移先 |
| --- | --- | --- |
| ＋ 新規登録 | 右ペインを新規登録フォームへ切り替える | — |
| 一覧のタイトル | 文書詳細へ | `/docs/$id` |
| 編集 | 右ペインを当該文書の編集フォームへ切り替える（版を `expectedVersion` として保持） | — |
| 保存 | 作成（`POST`）／更新（`PUT`）。成功で一覧を再取得（取り込み・Wiki 同期は後段が起動） | — |
| 公開 / アーカイブ / 削除 | 該当 API を呼び、成功で一覧を再取得 | — |

## 権限・表示条件・存在秘匿

- ロール（admin / operator）を持たない利用者には**ルートもナビ項目も存在しない**（`NotFound`）。
- 書き込みは BFF が「対象文書が利用者の ABAC スコープ内か」を先に確かめ、**スコープ外・不在をいずれも 404** で返す
  （[[IADR-0041]] / [[IADR-0009]]。閲覧できない文書は変更もできない）。画面は 404 を中立に扱う。

## エラー・状態

| 状態 | 表示 |
| --- | --- |
| 取得中 | 「読み込み中…」（`role="status"`） |
| 一覧の取得失敗 | `Alert tone="danger"` `role="alert"` |
| 成功・0 件 | 「文書はありません。」 |
| 保存成功 | `Alert tone="success"` `role="status"`（「文書を登録しました。」／「文書を更新しました。」） |
| 検証エラー（400） | `Alert tone="danger"` `role="alert"` に Problem 本文の詳細を列挙（`toMessages`） |
| **版競合（409）** | `Alert tone="warning"` `role="alert"`。詳細があればそれを出し、無ければ「他の更新と競合しました（版が変わっています）。最新を再読み込みしてください。」 |
| 不在・スコープ外（404） | `Alert tone="danger"` `role="alert"`（中立文言。権限の有無を示さない） |

## i18n

- 文言はすべて Lingui のカタログ（ja / en）へ載せる。`eslint-plugin-lingui` の適用範囲に本 feature を含める。
- 機密区分の**値**（`internal` 等）は翻訳しない（生値。§hi-fi モックアップとの対応 #7）。

## UI 部品（`@platform/ui`）

`Table` 一式 / `Button` / `Input` / `Select` / `Label` / `Card` 一式 / `Alert` / `Tag`。**新規プリミティブは追加しない**
（タグ編集の 4 基準判定は [作業仕様書 §4](../specs/20260805_issue-503_sc05-08-admin-screens.md)）。

## 関連仕様

- 作業仕様書: [20260805_issue-503_sc05-08-admin-screens.md](../specs/20260805_issue-503_sc05-08-admin-screens.md)
- テスト仕様書: [SC-05_document-management.md](../tests/SC-05_document-management.md)
- 実装 ADR: [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) / [IADR-0041](../adr/IADR-0041_document-write-bff-abac-scoped.md)
- 計画への環流（**記録を作成済み・計画リポへの起票は未了**）: [feedback/20260805_sc05-07-admin-contract-gaps.md](../../feedback/20260805_sc05-07-admin-contract-gaps.md)

## 未決事項

1. **変換列**（§実装しない要素 (a)）。文書 → 変換ジョブの対応を返す契約が要る。**環流の記録を作成済み・計画リポへの起票は未了。**
2. **タグ辞書との整合**（同 (b)）。管理系ロールが引けるタグ辞書の照会口が要る。同上。
3. **機密区分の表示名**（#7）。**planning#197 の裁定待ち**（#502 から継続）。
4. **閲覧ロール**（admin/operator か admin のみか）。計画 §共通シェル と [[IADR-0039]] の差異。同上の環流記録に含めた。
5. **ページング**。計画が送り方を定めていない（SC-02 と同じ）。実装は BFF が返す一覧をそのまま表示する。
