---
title: 基盤レルムの宣言と AST 専用レルムの写しのずれを機械で検知する（check-realm-copy-drift）
issue: "#1412"
type: spec
status: draft
related_ids:
  - NFR
  - ADR-0004
  - IADR-0434
plan_refs:
  - "planning:projects/ai-stock-trading/07_adr/ADR-0038_linked-deploy-auth-realm-is-the-platform-realm.md"
adr_refs:
  - IADR-0434
  - IADR-0107
author: Claude Fable 5.1 (worker)
created: 2026-09-11
updated: 2026-09-11
---

# 作業仕様書: 基盤レルムの宣言と AST 専用レルムの写しのずれを検知する（#1412）

## 起点

- issue #1412。計画 AST/ADR-0038（Accepted・2026-09-11）決定 3 が「`trading-owner` / `trading-service` と
  連結配備のクライアントの**正本は基盤レルムの宣言**（`deploy/keycloak/microservices-platform-realm.json`）であり、
  AST 専用レルムの同名ロールは**写し**」と定めた。
- 同 ADR の §統制と現在の実現手段 は決定 3 について実現手段を「🔴 **無い**」と書き、暫定手段も「🔴 **無い**」と書いている。
  §残るもの も「**決定 3 の写しのずれを検知する手段が無い**」を残件として挙げ、**フォローアップ 2** が
  「AST 専用レルムの宣言と基盤レルムの宣言で、同名ロールの定義が食い違わないことを確かめる」手段を実装側へ求めた。
- 検知をこちら（基盤リポ）に置く理由も同 issue が書いている —— **AST の CI は基盤リポを読めないが、
  基盤の CI は submodule `src/ai-stock-trading` を持つ**。突合の両辺が揃うのは基盤側だけである。

## 走査した母集合

**規則 1〜10（`.claude/rules/traceability.md` §是正・追随の母集合の取り方 ＋ `traceability.repo.md` の 9・10）に従い、
着手前に自分で引いた。** 引いた軸・生の出力・除外したものと理由を以下に残す。

### 軸 1: 突合の両辺になる realm 宣言（誤りの側＝「写しが在る場所」から引く）

```
$ git ls-files 'deploy/**' | grep -i 'realm.*\.json$'
deploy/keycloak/microservices-platform-realm.json

$ find src/ai-stock-trading -iname '*realm*' -name '*.json' -not -path '*/node_modules/*'
src/ai-stock-trading/infra/keycloak/realm-export.json
```

- 基盤側 **1 件**、AST 側 **1 件**。**どちらも 1 件しか無いので「どの 2 つを比べるか」は一意に決まる。**
- 🔴 **issue #1412 は AST の realm export を `src/ai-stock-trading/deploy/` 配下と書いているが、実物は
  `src/ai-stock-trading/infra/keycloak/realm-export.json` である**（submodule pin `db3cfe8` で実測）。
  拡張子で絞らず（規則 3）ディレクトリも決め打ちせずに `find` で引いたため捕まった。
  **検査器は置き場所を 1 本に決め打ちせず、候補ディレクトリを走査して見つける**（後述）。
- **除外**: `deploy/local/keycloak-setup/` 配下の reconcile スクリプトは realm の**適用器**であって宣言ではない。
  突合の辺にならないため対象外。

### 軸 2: 既に realm を読んでいる検査器（重複と射程の切り分け）

```
$ grep -ln "deploy/keycloak" scripts/*.js scripts/*.sh
scripts/check-password-reset-mail.js
scripts/check-realm-constraints.js
scripts/check-stack-ready.js
scripts/k8s-local-up.test.js
scripts/seed-abac-policies.js
scripts/k8s-local-up.sh
scripts/verify-oidc-edge-flow.sh
```

- 🔴 **7 件のいずれも AST 専用レルムを読んでいない。** `check-realm-constraints.js` は
  `deploy/keycloak/*-realm.json` という glob で母集合を作るため、**`realm-export.json` は名前の形からして
  対象外**である（submodule の外という以前の問題）。**したがって本件は既存検査器の射程拡大ではなく新設である。**
- **除外**: `check-stack-ready.js` G9（稼働 realm と宣言の差分）は**宣言 ⇔ 稼働**の軸であり、
  本件の**宣言 ⇔ 宣言**とは軸が違う。`seed-abac-policies.js` / `k8s-local-up.sh` / `verify-oidc-edge-flow.sh` は
  投入器・起動器・実機検証であって静的検査ではない。

### 軸 3: submodule を読む検査器の作法（縮退の先例）

```
$ grep -ln "src/ai-stock-trading" scripts/*.js
（19 件。うち検査器は check-backend-libraries / check-bff-authz-docs / check-contract-schema /
 check-coverage-floor / check-cpm-versions / check-cross-repo-refs / check-default-credentials /
 check-doc-links / check-image-mapping / check-knip / check-reading-budget / check-route-manifest /
 check-secret-injected-options / check-test-traceability / check-unit-service-ownership /
 check-xunit1051-ratchet の 16 件）
```

- 家の作法は `check-unit-service-ownership.js` が持つ —— **submodule 未取得なら「検査を落とさず縮退する」**。
  本件もこれに倣う（ただし縮退の形は後述のとおり**「既知の一覧へのフォールバック」ではなく「明示的な skip」**）。

### 軸 4: 突合の客体（同名ロール・AST 由来クライアント）

実測（両 realm JSON を読んだ生の値）:

| 客体 | 基盤レルム（`platform`） | AST 専用レルム（`ai-stock-trading`） |
| --- | --- | --- |
| realm ロール `trading-owner` | 在る | 在る |
| realm ロール `trading-service` | 在る | 在る |
| client `ai-stock-trading-svc` | 在る | 在る |
| client `ai-stock-trading-owner` | 在る | 在る |
| client `ai-stock-trading-kb-writer` | 在る | **無い** |
| client `ai-stock-trading-llm-caller` | 在る | **無い** |
| client `ai-stock-trading-dev` | **無い** | 在る |

- 🔴 **issue #1412 の射程は「両方に在る AST 所有クライアント」として 4 件を挙げているが、実測では
  両方に在るのは 2 件（`-svc` / `-owner`）である。** `-kb-writer` / `-llm-caller` は**基盤レルム専用**の
  cross-unit s2s（AST/IADR-0093・MSP#1364）であり、AST 専用レルムには初めから無い。逆に `-dev` は
  AST 単体起動・単体 E2E 専用の public client（AST/IADR-0050）で、連結配備では使わない。
- **除外はしない。片側にしか無いことを「宣言つきの除外」として検査器に持たせる**（理由つき。後述）。
  黙って交差集合だけを見ると、**写しからクライアントを 1 つ落としたときに交差が縮むだけで赤にならない。**

## 目的・背景

AST/ADR-0038 決定 3 の統制（正本が 1 つであること）に、**現在ひとつも機械的な裏付けが無い**。
同 ADR 自身が書くとおり、連結配備では基盤レルム側しか読まれないため、**写しが古くなっても連結配備の挙動には出ない** ——
出るのは単体 E2E であり、それは統制ではなく副作用である。本作業はその欠落を埋める。

## 対象範囲

- **対象**: `scripts/check-realm-copy-drift.js`（新設・`--self-test` つき）／`.github/workflows/ci.yml` への配線／
  `scripts/README.md` への登録／`scripts/scripts.repo.test.js` の検査器母集合ラチェット（57 → 58）／
  `.ai-context/adr/IADR-0434`（決定の記録）と `.ai-context/adr/README.md` への索引行。
- **対象外**:
  - AST/ADR-0038 **フォローアップ 1**（決定 2 の「配備された全経路が同じレルムを指す」網羅検査）。**軸が違う**
    （あちらは helm 描画 ⇔ 経路、本件は宣言 ⇔ 宣言）。別 issue で受ける。
  - **secret の突合**。🔴 **比較も出力もしない**（両 realm とも dev 既定値を持つ。突合すると差分メッセージへ
    平文が出る）。読むフィールドを allowlist で固定し、自己試験で「出力に secret が現れないこと」を固定する。
  - **submodule の gitlink（pin）の更新**。読み取り専用で取得するだけで、`src/ai-stock-trading` はステージしない。
  - **稼働クラスタ**への一切の接触。

## 設計

### 突合の客体の導出（列挙を書かない）

- **realm ロール**: `ROLE_PREFIX = 'trading-'` に一致する名前を**両 realm から集めた和集合**。
  和集合にするのが要点で、**片側から 1 つ落ちたら「もう一方に在って自分に無い」として赤になる。**
- **クライアント**: `CLIENT_PREFIX = 'ai-stock-trading-'` に一致する `clientId` の**両 realm の和集合**。
  🔴 **接頭辞より多くを決め打ちしない**（issue の要求）。
- **片側だけに在ってよいもの**は `ONE_SIDED_CLIENTS`（clientId → 理由）に**理由つきで宣言**する。
  宣言に無い名前が片側だけに在れば**赤**、宣言に在れば**notice で必ず見せる**（exit には影響させない）。
  これは `check-unit-service-ownership.js` の `NAME_COLLISION_EXEMPT`（黙って効く除外を作らない）と同型である。
  `ONE_SIDED_ROLES` も同じ機構で用意するが、**現在の実測では 0 件**である。

### 比較するフィールド

| 客体 | 比較する | 比較しない（理由） |
| --- | --- | --- |
| realm ロール | 両レルムでの**存在**／`composite`／`composites`（深い一致）／`attributes`（深い一致）／`description` の**有無** | `description` の**本文**。🔴 両者は独立に書かれた散文である（基盤側は日本語の根拠＋ issue 番号、AST 側は英語の dev 注記）。**バイト一致を課すと常時赤になり、検査器ごと無視される** —— `check-prometheus-alerts-parity.js` が `summary` / `description` を突合しないのと同じ判断。**「片方だけ説明が消える」ことは有無で捕まえる。** |
| クライアント | 両レルムでの**存在**／`serviceAccountsEnabled`／`directAccessGrantsEnabled`／`publicClient`／`standardFlowEnabled`／**service account の realm ロール付与**（`users[].serviceAccountClientId` から引く。昇順で集合比較） | `secret`（上記）／`name`・`description` の本文（同上）／`redirectUris`・`webOrigins`（連結配備と単体起動で正当に違い得る。**ここを課すと単体起動の宣言を壊す**） |

- **真偽値は Keycloak の既定へ正規化してから比べる**（`publicClient`=false / `standardFlowEnabled`=true /
  `directAccessGrantsEnabled`=true / `serviceAccountsEnabled`=false）。**片方が明示、もう片方が省略という
  書き分けだけで赤にしない** —— 意味が同じだからである。差分メッセージには**正規化前の生値と正規化後**を併記する。
- **差分は 1 件ずつ、両辺の値を添えて報告する。** 1 件でもあれば **exit 1**。

### 縮退（submodule 未取得）

- AST 側 realm を**見つけられない**ときだけ、`::warning::`（`scripts/lib/ci-annotate.js`）で
  **「突合していない」と明示して exit 0** で抜ける。🔴 **「差分 0 件」とは絶対に書かない**
  （`check-planning-adr-range.js` の `scanned: 0` の教訓 —— 0 は「ずれが無い」ではなく「検査が動いていない」）。
- 基盤側 realm が無い場合は**縮退しない**（本リポジトリの追跡下ファイルであり、無いのは異常）。

### CI への配線

- `static-checks` に `check-realm-constraints.js` と並べて **自己試験 → 本走査**（issue の指定）。
  🔴 **同ジョブは submodule を取得しない**（`.github/workflows/ci.yml` の checkout に submodule 指定が無い）ため、
  **ここでの本走査は必ず上記の skip になる。**
- したがって**実際に突合が走る場所として `static-checks-units` にも本走査を置く** ——
  同ジョブは `src/*` の submodule を非再帰で取得しており、`check-unit-service-ownership.js` が
  実 chart で突合できているのと同じ理由で、ここでは AST realm が読める。
- 🔴 **`static-checks` の checkout に submodule 取得を足す案は採らない。** 同ジョブには submodule 未取得を
  前提に射程を決めている検査器（`check-doc-links.js` 等）が並んでおり、本 issue の射程外の挙動変化になる。

## 受け入れ基準

- [ ] `node scripts/check-realm-copy-drift.js --self-test` が緑（陽性対照・陰性対照の両方を含む）。
- [ ] submodule 取得済みの作業ツリーで `node scripts/check-realm-copy-drift.js` が **exit 0・差分 0 件**、
      片側宣言 3 件を notice で見せる。
- [ ] **変異での検出力**: 取得済み submodule の作業ツリーで AST 側の写しから `trading-owner` を落とすと **exit 1**、
      戻すと exit 0（**submodule はコミットしない**）。
- [ ] submodule 未取得を模した状態で `::warning::` つき exit 0（「突合していない」と明示）。
- [ ] **secret が出力に現れない**（自己試験で固定）。
- [ ] `.github/workflows/ci.yml` の `static-checks` に自己試験＋本走査、`static-checks-units` に本走査。
- [ ] `scripts/README.md` に登録、`scripts/scripts.repo.test.js` のラチェットを 57 → 58。
- [ ] `IADR-0434` を起票し `.ai-context/adr/README.md` に索引行（タイトル 200 字以内・本文タイトルと 12 字以上共有）。

## テスト方針

- **自己試験（`--self-test`）**: 合成フィクスチャで純粋関数を直接叩く。
  - **陽性対照**: 同一の 2 レルム → 差分 0 件。
  - **陰性対照**: ①ロールを 1 つ落とす ②`serviceAccountsEnabled` を反転 ③`directAccessGrantsEnabled` を反転
    ④service account の realm ロール付与を変える ⑤宣言に無いクライアントが片側だけに在る → いずれも差分あり。
  - **既定の正規化**: 明示 `true` と省略（既定 `true`）が差分にならないこと。
  - **secret 非漏洩**: secret を持つフィクスチャの報告文字列に secret 値が含まれないこと。
- **リポジトリ試験（`scripts.repo.test.js`）**: ラチェット 57 → 58 の宣言。

## 計画書との差異

- 差異: **あり（issue 本文と実態の 2 点。いずれも計画 ADR の決定とは矛盾しない）**
  1. AST realm export の置き場が `deploy/` ではなく `infra/keycloak/`（軸 1）。**検査器を走査で書いて吸収する。**
  2. 「両方に在る AST 所有クライアント 4 件」は実測 2 件（軸 4）。**片側宣言を理由つきで持つ形にして吸収する。**
- **計画側（AST/ADR-0038）の決定 3・フォローアップ 2 そのものへの異議は無い。** 環流は不要と判断した。

## 未決事項

- `static-checks` での本走査が常に skip 警告を出すことの是非（設計 §CI への配線）。**突合は
  `static-checks-units` で成立するため統制は満たすが、`static-checks` 側の警告は毎回出る。**
  レビューで「うるさい」と判断されれば、`static-checks` の本走査ステップを外して自己試験だけ残す形へ寄せられる。
