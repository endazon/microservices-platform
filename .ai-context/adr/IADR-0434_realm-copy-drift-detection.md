---
title: IADR-0434 レルムの写しのずれは「接頭辞で導出した和集合」で突き合わせ、散文と secret は比べない
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0004, ADR-0026, IADR-0107, IADR-0288]
author: Claude（実装）
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0038_linked-deploy-auth-realm-is-the-platform-realm.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
---

# IADR-0434: レルムの写しのずれは「接頭辞で導出した和集合」で突き合わせ、散文と secret は比べない

- 状態: Accepted
- 日付: 2026-09-11
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: **AST/ADR-0038 決定 3**（`trading-owner` / `trading-service` と連結配備のクライアントの
  **正本は基盤レルムの宣言**であり、AST 専用レルムの同名ロールは**写し**）／同 **フォローアップ 2**
  （写しのずれを検知する手段を実装側へ求めた）／基盤 ADR-0026（**レルム名の正本**）／ADR-0004
- 起点 issue: **#1412**
- 関連する実装仕様書: `.ai-context/specs/20260911_issue-1412_realm-copy-drift.md`
- 先行する実装 ADR: [IADR-0107](./IADR-0107_ast-owned-service-single-deployment.md)（submodule の chart を読み、
  未取得なら縮退する検査器の先例）／[IADR-0288](./IADR-0288_notification-service-deployment-and-name-collision.md)
  （**黙って効く除外を作らない** —— 除外は理由つきで宣言し、効いたことを notice で必ず見せる）

## コンテキストと課題

AST/ADR-0038 は §統制と現在の実現手段 で、決定 3 について実現手段を「🔴 **無い**」、暫定手段も
「🔴 **無い**」と書いた。理由は同 ADR 自身が書いている ——

> **連結配備では基盤レルム側しか読まれないため、写しのずれは連結配備の挙動に出ない。**
> 出るのは単体 E2E であり、そこで食い違えば試験が落ちる —— これは統制ではなく副作用である。

突合を基盤リポジトリに置く理由も同 issue が書いている。**AST の CI は基盤リポジトリを読めないが、
基盤の CI は submodule `src/ai-stock-trading` を持つ。** 両辺が揃うのは基盤側だけである。

決めるべき点は 4 つあった。**(a) 何を突合の客体にするか（列挙か導出か）**、**(b) どのフィールドを比べるか**、
**(c) 片側のレルムにしか無い客体をどう扱うか**、**(d) 写しが読めない環境でどう振る舞うか**。

## 検討した選択肢

### (a) 客体の取り方

1. **名前を列挙する**（`trading-owner` / `trading-service` / クライアント 4 件を検査器へ書く）—— 却下。
   客体が増えるたびに検査器を直す必要があり、**直し忘れると「見ていないのに緑」**になる。
   issue #1412 自身も「接頭辞の規則より多くをハードコードするな」と書いている。
2. **接頭辞で導出し、両レルムの交差集合を取る** —— 却下。🔴 **検出したい事故がそのまま素通りする。**
   「写しからロールを 1 つ落とす」は交差集合を 1 つ縮めるだけで、残った客体はすべて一致するため緑になる。
3. **接頭辞で導出し、両レルムの和集合を取る** —— 採用。落とした側が「もう一方に在って自分に無い」として赤になる。

### (b) 比べるフィールド

1. **realm JSON を丸ごと深い一致で比べる** —— 却下。両レルムは**目的が違う**（連結配備 ⇔ 単体 E2E）ので
   `redirectUris` / `webOrigins` / 利用者は正当に違う。丸ごと比べれば常時赤になる。
2. **意味を持つフィールドだけを allowlist で比べる** —— 採用。ロールは存在・`composite`・`composites`・
   `attributes`・`description` の**有無**、クライアントは存在・4 つのフラグ・**service account の realm ロール付与**。
3. **散文（`description` / `name`）の本文も比べる** —— 却下。両者は独立に書かれている（基盤側は日本語の根拠＋
   issue 番号、AST 側は英語の dev 注記）。**バイト一致を課せば初日から赤であり、検査器ごと無視されるようになる。**
   同じ判断の先例が `check-prometheus-alerts-parity.js`（`summary` / `description` を突合しない）にある。
4. **`secret` も比べる** —— 却下。🔴 **差分メッセージへ平文が出る。**

### (c) 片側だけに在る客体

1. **交差集合だけ見て黙って無視する** —— 却下（(a) と同じ穴）。
2. **すべて赤にする** —— 却下。`ai-stock-trading-dev`（AST 単体 E2E 専用の public client）・
   `-kb-writer` / `-llm-caller`（基盤レルム専用の cross-unit s2s）は**正当に片側だけに在る**。
3. **理由つきで宣言し、宣言に無いものだけ赤にする。宣言が効いたことは notice で必ず見せる** —— 採用。
   `check-unit-service-ownership.js` の `NAME_COLLISION_EXEMPT`（IADR-0288 決定 2）と同型である。

### (d) 写しが読めないとき

1. **既知の一覧へフォールバックする**（`check-unit-service-ownership.js` の `AST_OWNED_FALLBACK` と同じ）
   —— 却下。あちらが持つのは**名前の一覧**だが、本件が要るのは**写しの中身**である。検査器の中に写しの写しを
   置けば、**それ自体が 3 つ目の正本**になる（決定 3 が断とうとした曖昧さを増やす）。
2. **失敗させる** —— 却下。submodule を取らないジョブ・クローンで検査が落ちる。
3. **「突合していない」と明示して exit 0** —— 採用。

## 決定

**`scripts/check-realm-copy-drift.js` を新設し、(a)-3 / (b)-2 / (c)-3 / (d)-3 を採る。**

### 決定 1: 客体は接頭辞で導出した**和集合**で取る

- realm ロールは `trading-`、クライアントは `ai-stock-trading-`。**両レルムの和集合**である。
- 🔴 **交差集合にしない。** 交差で取ると「写しから 1 つ落とす」が交差の縮小になって素通りする。

### 決定 2: 比べるのは意味を持つフィールドだけで、散文と secret は比べない

- ロール: 存在 / `composite` / `composites` / `attributes` / `description` の**有無**。
- クライアント: 存在 / `serviceAccountsEnabled` / `directAccessGrantsEnabled` / `publicClient` /
  `standardFlowEnabled` / **service account の realm ロール付与**（`users[].serviceAccountClientId` から引く）。
- **真偽値は Keycloak の既定へ正規化してから比べる**（明示と省略の書き分けだけでは赤にしない。意味が同じである）。
- 🔴 **`secret` は読まない・出さない。** 自己試験が「報告に secret が現れない」ことを固定する。
- 🔴 **`description` / `name` の本文と `redirectUris` / `webOrigins` は比べない。** 前者は独立に書かれた散文、
  後者は連結配備と単体起動で正当に違う。**「片方だけ説明が消えた」ことは有無で捕まえる。**

### 決定 3: 片側宣言は理由つきで持ち、効いたことを必ず見せる

- `ONE_SIDED_CLIENTS` / `ONE_SIDED_ROLES` に **clientId / name → 理由**で宣言する。
  宣言に無い片側在りは赤、宣言に在る片側在りは **notice**（exit には影響させない）。
- **理由を書けないなら足さない**（自己試験が理由の最短長を固定する）。現在の宣言は 3 件、
  `ONE_SIDED_ROLES` は **0 件**である。

### 決定 4: 写しが読めないときは「突合していない」と明示して exit 0

- 🔴 **「差分 0 件」とは書かない。** `check-planning-adr-range.js` の `scanned: 0` と同じ教訓であり、
  0 は「ずれが無い」ではなく「検査が動いていない」である。`::warning::` で skip と明示する。
- 写しの置き場は**決め打ちしない** —— `src/ai-stock-trading/infra/keycloak` と
  `src/ai-stock-trading/deploy/keycloak` を走査して `*realm*.json` を拾う。
  🔴 **issue #1412 は `deploy/` 配下と書いているが、submodule pin `db3cfe8` の実物は
  `infra/keycloak/realm-export.json` である。**
- **正本（`deploy/keycloak/microservices-platform-realm.json`）の欠落では縮退しない**（追跡下のファイルである）。

### 決定 5: CI の実走点は `static-checks-units` である

- `static-checks` には issue の指定どおり**自己試験 → 本走査**を `check-realm-constraints.js` と並べて置く。
  🔴 **同ジョブは checkout で submodule を取らないため、本走査は必ず skip 警告になる** ——
  **skip であることを毎回見せるために置く**（差分 0 件と取り違えさせないのが決定 4 の趣旨である）。
- **突合が実際に走るのは `static-checks-units`** である。同ジョブだけが `src/*` の submodule を取得している。
- 🔴 **`static-checks` の checkout へ submodule 取得を足す案は採らない。** 同ジョブには submodule 未取得を
  前提に射程を決めている検査器（`check-doc-links.js` 等）が並んでおり、**本 issue の射程外で挙動が黙って変わる。**

## 理由

- **決定 1 が本件の核である。** 「写しのずれ」で最も起きやすいのは**片方から落ちること**であり、
  交差集合で取る素朴な実装はその 1 形態を構造的に見逃す。**検出したい事故を見逃す検査器は、
  無いより悪い**（在ることで安心を作る）。
- **決定 2 の除外は「射程を狭める」のではなく「常時赤を避ける」ためである。** 常時赤の検査器は外されるか
  無視されるかのどちらかで、**どちらにしても統制は消える。** 本リポジトリは同じ判断を
  `check-prometheus-alerts-parity.js` で既に採っている。
- **決定 3 は IADR-0288 の再利用である。** 除外そのものは要るが、**黙って効く除外は次の読み手に見えない。**
- **決定 4 は `check-planning-adr-range.js` の教訓の再利用である。** 「走らなかった」を「合格」と書かない。
- **決定 5 は正直さのためである。** 配線だけして skip に落ちるのを黙っていると、**「CI に入れた」という
  記述が統制の実在と取り違えられる** —— AST/ADR-0038 が実測 3 で問題にしたのと同じ形である。

## 結果

- **良い影響**: AST/ADR-0038 決定 3 の統制に初めて機械的な裏付けが付いた（同 ADR の統制表は「無い」だった）。
  写しから客体が落ちる／フラグが反転する／service account のロール付与が変わる、のいずれも CI で赤になる。
  **正本がどちらかが報告文へ毎回出る**ため、直す向き（写しを正本に合わせる）を読み手が間違えない。
- **悪い影響 / トレードオフ**:
  - 🔴 **`static-checks` では毎回 skip の警告が出る**（決定 5）。**うるさいと判断されれば本走査ステップを
    外して自己試験だけ残せる** —— 統制は `static-checks-units` 側で成立する。
  - 🔴 **散文は比べないので、「説明はあるが内容が古い」写しは捕まらない。** 有無しか見ない。
  - **submodule pin が古ければ、古い写しと突合する。** pin の鮮度は本検査器の射程外である。
- **フォローアップ**:
  1. AST/ADR-0038 **フォローアップ 1**（決定 2 の「配備された全経路が同じレルムを指す」網羅検査）は**別軸**
     （helm 描画 ⇔ 経路）であり、本 IADR では扱っていない。別 issue で受ける。
  2. AST 側が realm export を `infra/` から移したときは `AST_REALM_DIRS` に候補を足す
     （**走査で拾うので、候補さえ足せば決め打ちは増えない**）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0107](./IADR-0107_ast-owned-service-single-deployment.md)（submodule を読む検査器の縮退の先例）／
  [IADR-0288](./IADR-0288_notification-service-deployment-and-name-collision.md)（黙って効く除外を作らない）
