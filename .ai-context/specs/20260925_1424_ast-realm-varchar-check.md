---
title: 写し突合器に AST realm export の varchar(255) 検査を足す（check-realm-copy-drift）
issue: "#1424"
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
author: Claude Opus 5.5 (worker)
created: 2026-09-25
updated: 2026-09-25
---

# 作業仕様書: 写し突合器に AST realm export の varchar(255) 検査を足す（#1424）

## 起点

- issue #1424。AST#787: AST の realm export（`src/ai-stock-trading/infra/keycloak/realm-export.json`）の
  client / role の `description` と realm `attributes` が varchar(255) を超え、**基盤の integration-stack で
  Keycloak が起動しなかった**（run 34609129031。SQLSTATE 22001）。
- 基盤の `check-realm-constraints.js`（#18）は `deploy/keycloak/*-realm.json` だけを見ており、submodule 経由で
  import する AST の export は対象外だった。AST 側には AST#787 で `scripts/check-realm-export.js` が足されたが、
  **import するのは基盤**（`scripts/k8s-local-up.sh` が `keycloak-realms` ConfigMap に同梱する）なので基盤側でも止める。
- 検査の置き場は issue の指定どおり `check-realm-copy-drift.js`（#1412・IADR-0434）。**submodule の AST export を
  既に読んでいる唯一の検査器**であり、CI の実走点（`static-checks-units`）も既に submodule を取得している。

## 走査した母集合

### 軸 1: AST の realm export を基盤へ取り込む経路（誤りの側＝「長すぎる値が届く先」から引く）

```
$ git grep -n "realm-export" -- . ':!src/ai-stock-trading' ':!CHANGELOG.md'
（.ai-context の凍結記録 5 件を除くと）
scripts/check-realm-copy-drift.js:61 / :516 / :517   … 検査器自身（置き場の注記と自己試験）
scripts/k8s-local-up.sh:158                          … ast_realm= … keycloak-realms ConfigMap へ同梱
```

- **取り込み経路は `k8s-local-up.sh` の 1 本**。integration-stack（`.github/workflows/integration-stack.yml`）は
  submodule を取得してから `k8s-local-up.sh` を呼ぶ —— AST#787 の事故はこの経路で起きた。
- `deploy/docker-compose.yml` は基盤レルムだけをマウントする（`:117`）。AST export は届かないので対象外。

### 軸 2: realm の長さ検査を持つ検査器（重複と射程）

```
$ git grep -nE "varchar\(255\)|MAX_LEN|collectFields" -- scripts .github deploy docs
（check-realm-constraints.js 自身を除くと）
.github/workflows/ci.yml:441     … check-realm-constraints の配線コメント
scripts/scripts.repo.test.js:1295-1326 … check-realm-constraints の単体試験
docs/data/conversion-job.md      … 別物（変換ジョブの DB 列定義）
```

- 長さ検査は `check-realm-constraints.js` の 1 か所だけで、**AST export は射程外**（母集合が
  `deploy/keycloak/*-realm.json` の glob）。除外: `docs/data/conversion-job.md` は Keycloak と無関係。

### 軸 3: AST export 内の長さの実測（submodule pin 84026a4）

`collectFields`（基盤）で 10 項目、realm `attributes` 3 項目（`_header` 235 / `_scope` 207 / `_sourceOfTruth` 231）。
最長は `collectFields` 側 220 文字。**違反 0 件**（AST#787 で短縮済み）。client / role の `attributes` は空。

### 軸 4: 本検査器を説明している文書（追随先）

```
$ git grep -n "realm-copy-drift" -- . ':!.ai-context/specs' ':!CHANGELOG.md'
.ai-context/adr/IADR-0434_… / .ai-context/adr/README.md:514 … 凍結記録（書き換えない）
.github/workflows/ci.yml:460 / :463 / :672                  … 配線（コメントを追随）
scripts/README.md:52 / :171 / :174                           … 説明（自己試験件数 24 を含む）
scripts/scripts.repo.test.js:7131                            … 検査器母集合ラチェットの注記（本数は不変）
```

- 除外: IADR-0434 と索引は凍結記録（Accepted）。**射程の追加は決定を覆さない**ので新 IADR も要らない。
- `scripts.repo.test.js` の検査器本数（58）は**新設ではない**ので変わらない。

## 対象範囲

- **対象**: `scripts/check-realm-copy-drift.js`（長さ検査の追加・自己試験）／`.github/workflows/ci.yml` の配線コメント／
  `scripts/README.md` の説明。
- **対象外**:
  - 基盤レルムの長さ検査（`check-realm-constraints.js` が既に持つ）。
  - `check-realm-constraints.js` の `collectFields` へ realm `attributes` を足すこと（後述「設計判断」）。
  - AST 側の export の修正（違反 0 件）・submodule pin の前進。

## 設計

- `check-realm-constraints.js` から `collectFields` / `findViolations` / `MAX_LEN` を再利用する（閾値と文字数の数え方を
  1 か所に保つ）。
- それに **realm `attributes` の値**（`realm.attributes[<key>]`）を足した集合を AST export に対して検査する。
  AST#787 の超過 6 箇所のうち 2 箇所は realm attributes であり、`collectFields` はこれを収集しない。
- 違反は写しのずれとは別の見出しで出し、**直す場所は AST リポジトリ**（export の短縮 → submodule pin の前進）と案内する。
- 縮退は従来どおり: AST export を見つけられないときは `::warning::` で「突合していない」と明示して exit 0。
  文言に「長さ検査も走っていない」ことを足す。
- `checkAstRealmLengths(root)`（純粋に近い関数）を切り出し、**一時ツリー**で陰性対照を自己試験できるようにする。

### 設計判断: attributes は写し突合器の側で足す

`check-realm-constraints.js` の冒頭は「attributes 値で同種の import 失敗が起きたら `collectFields` に足す」と書く。
しかし AST#787 のログに出たのは `DESCRIPTION` 列だけで、realm attribute の値の列が 255 で切られるかは実測していない。
基盤レルムの長さ検査の射程を未実測の根拠で広げるのは本 issue の外なので、**AST export の検査にだけ足す**
（AST 側 `check-realm-export.js` と射程を揃える）。

## 受け入れ基準

1. AST export の client `description` が 300 文字の一時ツリーで、長さ検査が違反 1 件を返す（issue の陰性対照）。
2. realm `attributes` の値が 256 文字以上でも違反になる。255 文字ちょうどは合格。
3. role の `description` も対象。
4. 実ファイル（pin 84026a4）では違反 0 件で、突合の結果も従来どおり。
5. submodule 未取得時は skip 警告で exit 0（長さ検査も走っていないことを明示）。
6. 既存の自己試験がすべて通る。`scripts/README.md` の自己試験件数を実数に合わせる。

## 検証

- `node scripts/check-realm-copy-drift.js --self-test`
- `node scripts/check-realm-copy-drift.js`（submodule あり／なし）
- `node scripts/scripts.repo.test.js` ほか scripts/README.md の既定検査
