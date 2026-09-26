---
title: "realm の検査に、人の利用者名が service-account- で始まらないこと・標準フローのクライアントが profile を既定スコープに持つことを足す（#1589）"
type: spec
status: done
related_ids: [NFR-09, SC-17, ADR-0032, IADR-0420, IADR-0429]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md §決定（SPA はトークンを扱わない・BFF セッション方式）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09
---

# 仕様書: realm の検査に「人を無人の主体と読ませる宣言」の 2 種を足す（#1589）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: **NFR-09**（全 API で OIDC/JWT 認証。暫定はエッジ（BFF）で担保）
- 画面: **SC-17**（ユーザーアカウント管理。稼働中の realm での利用者作成の注意書きの置き場）
- 計画 ADR: **ADR-0032**（BFF セッション方式。人の経路は HttpOnly Cookie ＋ CSRF ヘッダ）
- 実装 IADR: **IADR-0420**（`MachinePrincipal.IsMachine` —— トークンが名乗る形だけで人か機械かを決め、許可集合を持たない）／
  **IADR-0429 決定 3**（BFF の Bearer 受理を無人の主体に絞る。#1587 で腕 B を落とし、無人の主体だけになった）
- 起票: #1589（#1587 / #1535 の監査で出た既存の残余リスク。回帰ではない）

## 目的・背景

BFF の Bearer 受理（`BearerCallerPolicy.IsAcceptedCaller`）は `MachinePrincipal.IsMachine` だけで呼び出し元を振り分ける。
判定は「利用者名が `service-account-` で始まる」（腕 A）か「利用者名が無く、クライアント識別（`azp` など）がある」（腕 B）で、
**許可集合を持たない**（IADR-0420 の意図的な設計。集合を構成に持つと統制の抜け道になる）。したがって次のどちらかが realm に入ると、
**人のトークンが無人の主体として Bearer 受理を通り**、BFF セッション（Cookie ＋ CSRF）を迂回できる。

1. `service-account-*` という名前の**人の利用者**（腕 A が人を機械と読む）
2. `profile` を既定スコープに持たない**標準フローのクライアント**（人のトークンに `preferred_username` が乗らず、`azp` は必ず乗るので腕 B が人を機械と読む）

どちらも realm import は成功し、ログインも画面も動くため E2E では気付けない。宣言の時点で止める。

## 🔴 着手前に確認した制約

- **IADR-0420 の判定規則は変えない。** 許可集合を足す・腕 B を落とす方向は IADR-0420 / IADR-0429 の決定を覆す（射程外）。本作業は**判定の前提を宣言で守る**だけである。
- 稼働クラスタには触らない（LIVE 未設定）。稼働中の realm で管理コンソールから作る利用者は**宣言の外**であり、issue の指示どおり**運用の注意書き**に留める（SC-17 画面仕様書 §運用上の注意）。
- `check-realm-constraints.js` の他検査と同じく、純関数＋自己試験（変異と陰性対照）＋実データ・ラチェット（0 件走査を緑にしない）の形に揃える。

## 母集合（[[IADR-0141]] 決定 1。着手時に自分で引いた）

### 検査を適用する realm JSON

`git ls-files | grep -i -E "realm[^/]*\.json$"` → **1 件**: `deploy/keycloak/microservices-platform-realm.json`（本スクリプトの既定走査 `deploy/keycloak/*-realm.json` と一致）。

| 候補 | 判定 | 理由 |
| --- | --- | --- |
| `deploy/keycloak/microservices-platform-realm.json`（realm `platform`） | **適用** | BFF と各サービスが受理する発行者の realm |
| AST 専用 realm の写し `src/ai-stock-trading/infra/keycloak/realm-export.json`（realm `ai-stock-trading`。submodule 内・本スクリプトの走査対象外） | **適用しない** | ① 本スクリプトは `deploy/keycloak/*-realm.json` しか走査しない（写しの突合は `check-realm-copy-drift.js` の射程）。② BFF / 基盤サービスは基盤 realm の発行者しか受理しないので、写しの宣言は `IsMachine` の穴にならない。③ 参考に読むと、写しの `ai-stock-trading-dev`（public・標準フロー）は `defaultClientScopes` を宣言しておらず、写しは `clientScopes` も宣言しないため Keycloak の組み込み既定（`profile` を含む）が当たる。検査 7 を当てると「明示せよ」で赤になるが、それは AST 側の判断であり本 PR で AST の submodule を書き換えない |

検査関数は他の token 意味論の検査（検査 5・SA 権限）と同じく **realm 名 `platform` だけ**を対象にする（別の realm の JSON を明示で渡しても 0 件）。

### 実データの realm の現況（node で読んだ。検査 7 は 0 件）

- 人の利用者 4（`admin` / `poc-user` / `poc-operator` / `developer`）。いずれも接頭辞なし。`serviceAccountClientId` を持つ利用者 17 はすべて `service-account-<clientId>`。
- 標準フローのクライアント 6（`wiki-js` / `bff` / `headlamp` / `grafana` / `argocd` / `vault`）。すべて `defaultClientScopes` に `profile` を持つ。他 18 は `standardFlowEnabled=false`。
- realm は `clientScopes` を明示しており、`profile` スコープは `preferred_username` を access token へ載せるマッパー（`username`）を持つ。

### 追随する文書（誤りの側 `service-account-` で `git ls-files | xargs grep -l` を引いた。submodule を除く）

| ファイル | 判定 |
| --- | --- |
| `docs/screens/SC-17_user-account-management.md` | **追記**（§運用上の注意。trace ブロックへ NFR-09 / IADR-0420 / IADR-0429 / 本仕様書 / #1589 / #1587） |
| `docs/tests/SC-17_user-account-management.md` | **追記**（T-53） |
| `deploy/local/keycloak-setup/reconcile-realm.js` | 変更不要（利用者は同じ realm JSON から作るので、検査 7 の射程に入る） |
| `.ai-context/adr/*`・`.ai-context/specs/*`（16 件） | 変更しない（凍結記録。サービスアカウントの規約の引用で、本検査の不在を前提にした記述は無い） |
| `docs/authz/FR-19_share-target-authorization.md`・`docs/observability/knowledge-health-indicators.md`・`docs/operations/password-reset-relay-state-measurement-runbook.md`・`deploy/helm/.../values.yaml`・`scripts/check-password-reset-mail.js` | 変更不要（サービスアカウントの利用者名の規約を引くだけ） |
| `docs/security/security.md` | 変更しない（注意書きの置き場を SC-17 の 1 箇所に絞る。2 箇所に置くと片方が古くなる） |

## 実装

`scripts/check-realm-constraints.js` に**検査 7** `collectMachineJudgementGaps` を足し、`checkFiles` → `main` に配線する。

| # | 不変条件 | 判定の細目 |
| --- | --- | --- |
| (1) | 人の利用者（`serviceAccountClientId` を持たない。空文字も人）の `username` が `service-account-` で始まらない | 大小を無視（Keycloak は小文字へ正規化・`IsMachine` は `OrdinalIgnoreCase`）。`enabled=false` でも検出（後から有効化され得る） |
| (2) | 人がログインするクライアントは `defaultClientScopes` に `profile` を**明示**する | 対象 = `bearerOnly !== true` かつ（`standardFlowEnabled !== false`（未設定は Keycloak の既定 true）または `implicitFlowEnabled === true`）。`optionalClientScopes` は数えない（要求しない限り載らない） |
| (2') | realm が `clientScopes` を明示するなら、`profile` スコープが `claim.name=preferred_username` / `access.token.claim=true` のマッパーを持つ | `clientScopes` を明示しない realm は組み込みが生成されるので判定しない |

`directAccessGrantsEnabled` は検査 5 が既に全クライアントで禁じているので (2) の対象に含めない。

## 受け入れ基準

- [x] 自己試験に検査 7 の変異・境界・陰性対照 18 件と実データ・ラチェット 1 件を足し、すべて通る（`node scripts/check-realm-constraints.js --self-test` → 136 件 OK）
- [x] 実データの realm が通る（`node scripts/check-realm-constraints.js` → OK）
- [x] CLI の入口から 2 種の変異を検出する試験（実データの写しを一時ファイルへ作り、変異なしは exit 0・変異ありは exit 1 で 2 件を名指し）が `node scripts/scripts.test.js` で通る
- [x] SC-17 画面仕様書に稼働中 realm での利用者作成の注意書き、テスト仕様書に T-53（trace ブロックの規約どおり、表示テキストへ ID を書かない）

## 検証

- `node scripts/check-realm-constraints.js --self-test` → `自己試験 136 件 OK。`
- `node scripts/check-realm-constraints.js` → `OK: 1 ファイルに …人を無人の主体と読ませる宣言（…）はありません。`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → `★ #1589: …` ok を含む全件 pass
- `node scripts/check-trace-blocks.js`（変更した docs 2 件）
