---
title: SC-06 データソース管理 テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-06
  - UC-04
  - FR-01
  - FR-02
  - IADR-0039
  - IADR-0127
author: claude
created: 2026-07-09
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - "../screens/SC-06_datasource-management.md"
  - "../specs/20260805_issue-503_sc05-08-admin-screens.md"
  - "../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md"
---

# テスト仕様書: データソース管理（SC-06）

> **［2026-08-05 / #503］新スタックでの再実装に合わせて全面改訂した。**

対象: `src/knowledge/frontend/src/features/sc06-datasources/`
テスト: `syncState.test.ts`（純関数）／ `DataSourceManagementPage.test.tsx`（Vitest + Testing Library）／
導線は `src/knowledge/frontend/src/features/adminFlow.test.tsx`／
E2E は `src/platform/frontend/e2e/sc06-datasources.smoke.spec.ts`

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-06 ／ ユースケース（UC）: **UC-04**（データソースを登録・同期する）／ 機能要求（FR）: FR-01・FR-02

## UC-04 のフロー → テストの写像

| UC-04 のフロー | 画面での現れ方 | テスト |
| --- | --- | --- |
| **基本 1. 管理者がソース（ファイルサーバー／Wiki／SaaS／業務DB）を登録する** | 登録フォーム → `POST /bff/datasources`（既定の機密区分つき） | `registers a data source with a default confidentiality attribute` |
| **代替. 手動同期を実行する** | 行操作「手動同期」→ `POST /bff/datasources/{id}/sync` | `triggers a manual sync` |
| **例外. 接続失敗時は再試行し、継続失敗はアラートする** | **画面が担うのは注記のみ**（再試行状態そのものは契約に無い。§実装しない要素） | `states that credentials live in Vault and that repeated failures raise an alert` |
| 基本 2. システムが定期的に原本を取得し、変換へイベント送出する | **写像しない**（サーバ側の hosted service） | — |

## テストケース

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | 一覧 | SC-06 / FR-01 | `GET /bff/datasources` を呼び、ソース名 ＋ 接続先・**種別（日本語表示名）**・**同期状態**を表示する |
| 2 | **同期状態の導出** | INDEX 決定 21 / [[IADR-0127]] 決定 2 | `disabled` → 無効（**琥珀の警告**）／ `active`＋最終同期あり → 同期済み／ `active`＋なし → 未同期。**tone とテキストが対で決まる** |
| 3 | 種別の写像 | SC-06 | 4 種（`filesystem` / `wiki` / `saas` / `db`）に表示名がある。**未知の種別は生値**を出す |
| 4 | 登録 | UC-04 基本 1 | 名前・種別・接続先・既定の機密区分を送る |
| 5 | 必須項目 | UC-04 | 名前と接続先が埋まるまで登録できない |
| 6 | 手動同期 | **UC-04 代替** | `POST …/sync` を呼び、完了を伝える |
| 6-b | **再取得** | [[IADR-0127]] 決定 5 | 手動同期の成功後に一覧を取り直す（`invalidateQueries` のみ） |
| 7 | 無効化 | FR-01 | `active` の行だけに操作が出る。`DELETE /bff/datasources/{id}` を呼ぶ |
| 8 | 注記 | **UC-04 例外** | Vault 管理と継続失敗アラートを明示する |
| 9 | **異常系（縮退しない）** | [[IADR-0039]] | 取得失敗を `role="alert"` で出し、**「登録されていません」へ寄せない**（重複登録の誘発を避ける） |
| 10 | 操作の失敗 | — | 一覧を保ったままエラーを出す |
| 11 | 0 件 | — | 「データソースは登録されていません。」 |
| 12 | **権限別の出し分け** | [[IADR-0035]] / [[IADR-0009]] | ロールを持たない利用者には画面が無い（`NotFound`）。**要求も出さない** |
| 13 | **契約の不在**（実装しない要素） | 画面仕様書 §hi-fi 対応 #6・#7・#9 | 「次回同期」列・「再試行中」表示・「設定」操作が無い。**先に手動同期の操作が在ることを確かめてから**無いことを見る |
| 14 | ロケール `en` | ADR-0031 | 見出しと種別が英語で描画される |

## 純関数（`syncState.test.ts`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| P1 | 琥珀の充て先 | `disabled` が `warning` になり、「同期済み（日時）」を出さない |
| P2 | 同期済み / 未同期 | `lastSyncedAt` の有無で `success` / `neutral` が決まる |
| P3 | 種別の値集合 | 計画が挙げる 4 種と表示名 |
| P4 | 未知の種別 | 生値をそのまま返す |
| P5 | 日時整形 | 空は `—`、解釈できない値はそのまま出す |

## 導線（`adminFlow.test.tsx`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A | SC-06 → SC-07 | 「変換ジョブの状況を見る →」から変換ジョブ画面へ遷移する（計画の遷移図 `SC06 → SC07`） |

## 実行

- `pnpm run test -- knowledge/frontend/src/features/sc06-datasources`（純関数 **6** ＋ 画面 **14** ケース）
- `pnpm run test -- knowledge/frontend/src/features/adminFlow.test.tsx`（導線）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）
