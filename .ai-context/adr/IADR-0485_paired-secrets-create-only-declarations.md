---
title: IADR-0485 対になる秘密（認証基盤の client シークレット・データストアの資格情報）は SC-22 の対象外とし、realm の宣言と Vault の種は「無いときだけ作る」に改め、realm の宣言の secret は開発用の形に限る。回すのは相手と Vault を対で書く運用手順で、退避手段とローテーションの記録先は #458 の 1 か所にする
type: impl-adr
status: Accepted
related_ids: [SC-22, NFR-18, ADR-0124, ADR-0095, ADR-0104, IADR-0369, IADR-0433, IADR-0453, IADR-0456, IADR-0461, IADR-0092, IADR-0133]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1・3・4・フォローアップ 1・2・4・5
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md 決定 1（部分改定）・決定 4（補完）
related_specs:
  - ../specs/20260928_issue-1682_paired-secrets-outside-sc22.md
---

# IADR-0485: 対になる秘密の扱い —— 宣言は作成時だけ、回すのは対の手順（#1682）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-28
- 決定者: claude（#1682。計画 ADR-0124 決定 1・4 の実装側の形）

## 起点・関連

- 関連する計画書 ID: SC-22、NFR-18
- 関連する計画 ADR: **ADR-0124** 決定 1（相手と同時に変えなければ成立しない秘密＝対になる秘密は SC-22 の対象外。ADR-0095 決定 1 の例外。
  Git に置かず、相手と Vault を対で書く運用手順で回す。**その前提として本番の秘密を realm の宣言から外し、初期投入は無いときだけ作る。
  開発用の値が Git に在ることは本番へ持ち込まない限り認める**）・決定 4（手順ができるまでの暫定手段と、退避手段の記録先を 1 か所にまとめること）、
  ADR-0095 決定 1・4、ADR-0104
- 関連する実装 ADR: [[IADR-0369]]（realm の追随の境界。**決定 2 の境界表の client の `secret` を本 ADR が動かす**）・[[IADR-0433]]（許可リストの分類）・
  [[IADR-0453]]（SC-22 の残作業。フォローアップ 2・3）・[[IADR-0456]]（画面が書く KV は無いときだけ作る。決定 6）・[[IADR-0461]]・
  [[IADR-0092]]・[[IADR-0133]]（realm import の開発用 secret）
- 裁定: planning#700（利用者裁定 2026-09-28）
- 並行: Vault の audit を可観測性基盤へ取り込むこと（ADR-0124 決定 2）は #1683 が持つ。本 ADR は記録の**手段**を変えない。

## コンテキストと課題

計画の裁定の前、許可リスト（`deploy/bootstrap/sc22-secret-items.json`）は対になる秘密を 3 か所に分けて持っていた
（`deferred[]` 17 件・`excluded[]` 7 件・`items[].notWritable` の `*-auth-client-*` 4 組）。Runbook（`secret-rotation-runbook.md` 手順 C）は
`deferred[]` を「経路B では恒久的に回せない」とし、回せるようにする前提として 3 点を挙げていた:
①realm JSON から client の `secret` を外し、宣言の追随が当て直さないようにする ②認証基盤と Vault を対で書く経路 ③`bootstrap.sh` が無いときだけ作る。

現物を読むと、**「realm の宣言（Git）の値で稼働の値を当て直す」経路は 3 つ**あった。

| 経路 | 裁定の前 |
| --- | --- |
| Keycloak の `--import-realm` | 同名 realm が在ると飛ばす（`IGNORE_EXISTING`）。**既に「無いときだけ」** |
| `reconcile-realm.js` の client 更新 | client の `secret` を宣言所有として比べ、違えば `PUT` で宣言の値へ戻す。比べるために `GET …/client-secret` で稼働の値を読む |
| `bootstrap.sh` の Vault の種 | 対になる秘密の 24 KV を毎回 `vault kv put`（全置換）で env か開発用既定値へ戻す |

したがって、認証基盤と Vault を対で回しても、次の `scripts/k8s-local-up.sh` が**両側を開発用の値へ戻し**、しかも追随の Job と種の投入は別の時点に走るので、
間は片側だけ書いた状態になる。①③ を塞がない限り、② の手順を書いても回した値は保たれない。

## 決定

### 決定 1: 対になる秘密の分類は許可リストの構造のまま、位置づけを「SC-22 の対象外」として書き直す

- `deferred[]` / `excluded[]` / `notWritable` の**構造（キー名）は変えない。** BFF（`SecretItemCatalog`）は `items[]` だけを読み、試験（`SecretItemVaultPolicyTests` ほか）は
  `deferred` / `excluded` を名前で読む。画面の挙動は変えない（裁定が求める）。
- 変えるのは位置づけの記述である: `$comment`・`deferred[].reason`・`excluded[].reason`・`ast-app-secrets.note` を「対になる秘密（ADR-0124 決定 1。SC-22 の対象外）」へ書き換え、
  `deferred[].reason` の「SC-22 のモックアップ受領時にまとめて判断する」を消す（裁定が出た）。
- **`deferred`（保留）という名前は残る。** 意味は「画面で扱うか保留」ではなく「対になる秘密のうち認証基盤と対のもの」になった。名前を変えないのは、
  読み手が 5 つ（BFF の policy・allowlist・Runbook・RBAC・bootstrap の試験）あり、改名の利得が境界の記述で足りるからである。

### 決定 2: realm の宣言の client `secret` は作成時にだけ運ぶ（IADR-0369 決定 2 の境界の変更）

- `reconcile-realm.js` に `CLIENT_CREATE_ONLY_KEYS = { 'secret' }` を置く。既存の client では `secret` を**比べず・`PUT` の本文に載せず・`GET …/client-secret` で読まない。**
  client が無いとき（`client.create`）と realm が無いとき（`realm.create`）にだけ、宣言の値で作る。
- Keycloak の client 更新は、本文に `secret` が無ければ現在の値を保つ。GET の表現が `secret` を含む版でも本文から落とす（稼働の値を往復させない）。
- 読みに行かないのは、比べないので要らないからである。値を取りに行かず、比べず、`PUT` の本文とログに出さない。
  ただし `GET /clients?max=1000` の表現が `secret` を含む版の Keycloak では、値はこの Job のメモリを通る（使わないだけで、通ることは止めていない）。

### 決定 3: Vault の種は対になる秘密も「無いときだけ作る」

- `bootstrap.sh` に `vkv_create_if_absent <path> <key>='<value>' …` を置き、対になる秘密の 24 KV（`deferred[]` 17・`excluded[]` 7）をこれで作る。
  在否を `vkv_exists` で確かめ、在れば `keep:` を出して戻り、無ければ `vault kv put -cas=0`（Vault 側でも「無いときだけ」）。
- **env は作るときだけ効く。** 在る KV を env で書き換えない。IADR-0456 決定 6 は画面が書く KV に「在れば env が空でないプロパティだけ差し替える」を採ったが、
  対になる秘密は保管先だけを書き換えると相手と食い違うので、**差し替えもしない**（回すのは対の手順）。
- 試験（`SecretItemBootstrapSeedTests.Paired_secret_kvs_are_created_only_when_absent`。SC-22 T-78）は、許可リストから母集合を引き、各パスが
  補助関数の 1 文だけで書かれ無条件の put / patch が無いことと、補助関数が「在れば戻る」を `put -cas=0` より前に持つことを字面で固定する。

### 決定 4: realm の宣言の client `secret` は開発用の形に限る（本番の値を置かせない）

- `check-realm-constraints.js` に検査 8 を足す。`clients[].secret` は `^dev-only-[a-z0-9-]+$` か `^[a-z0-9-]+-dev-secret-change-me$` の形に限り、それ以外（空文字・数値を含む）を違反にする。報告に値を出さない。
- 開発用の値が Git に在ることは、`docs/security/security.md`「開発専用（dev-only）の平文認証情報 — 本番流用禁止」と IADR-0092・IADR-0133 が既に定めており、ADR-0124 決定 1 も
  「本番へ持ち込まない限り認める」とした。**本 ADR はこの扱いを変えない** —— 変えるのは「宣言の値が稼働の値を上書きする」ことだけである。
- 実データのラチェット: 実物の realm の `secret` を持つ client は 23 件（下限 20 を置く）で、すべて開発用の形である。

### 決定 5: 回すのは相手と Vault を対で書く運用手順（新しい Runbook）

- `docs/operations/paired-secret-rotation-runbook.md` を置く。群 1（認証基盤の client シークレット 17 ＋ 4 組）は
  **新しい値を手元で作る → Vault の直前の版を控える → Vault へ書く → 認証基盤の管理 API で client の `secret` を同じ値にする → 同期を促す → 消費側を作り直す**。
  群 2（データストア 7）はストアごとのコマンドを既存の `secret-rotation-runbook.md` 手順 B-1 に置いたまま、順序と戻し方を新しい Runbook が持つ。
- **Vault を先に書く**のは、同期を促すまで消費側は旧の値を持ち続けて何も壊れず、途中で止まったときに `vault kv rollback` だけで戻せるからである。相手を書いた後は戻さず前へ進める。
- 認証基盤の書き込みは管理 API で行う（Keycloak の Pod で `kcadm.sh` を exec しない。IADR-0369 の実測）。BFF に `manage-clients` を与えない（ADR-0124 実測 9。画面で扱わないので要らない）。

### 決定 6: 退避手段とローテーションの記録先は #458 の 1 か所

- 退避手段（画面を使わないコンソール投入）の記録は #1411、ローテーション（手順 B）の記録は #458 に分かれていた。**#458 にまとめる。**
  #1411 は閉じており、閉じた issue へのコメントは棚卸しで見えにくい。#458 は Vault 集中管理とローテーション Runbook の親で開いている。
- #1411 に在る過去の記録は動かさない。記録の手段（issue コメント）は ADR-0124 決定 4 の暫定手段のままで、Vault の audit の取り込み（#1683）ができたら Runbook を差し替える。

## 塞いでいない経路

- 🔴 **起動器（`scripts/k8s-local-up.sh`）の `apply_secret`** は、`postgres` / `rabbitmq` / `keycloak-admin` / `reset-gate-oidc` の Secret を `ESO=1` でも env か開発用既定値で作る。
  Vault の値は戻らないが、次の同期までの間 Secret が既定値になる。**本 ADR では変えない** —— 裁定の前提 ①③ は realm の宣言と `bootstrap.sh` を名指ししており、
  `apply_secret` の Secret は `creationPolicy: Merge` の同期が Vault の値で上書きする（戻る先は Vault）。Runbook に「起動の後に同期を促す」を書いた。
- realm を作り直す（`keycloak-data` の PVC を消す・切替の作り直し）と、client は宣言の開発用の値で作られ、Vault の回した値と食い違う。
  移行仕様書と Runbook に「作り直しの後に Vault の値を認証基盤へ書き直す」を書いた。

## 検討した選択肢

| 案 | 採否 | 理由 |
| --- | --- | --- |
| realm JSON から `secret` を消す（Keycloak に生成させ、Vault へ書き戻す） | 不採用 | 起動のたびに生成値を Vault へ書き戻す経路が要り、「無いときだけ」の判定が 2 か所（認証基盤と Vault）に割れる。開発用の値を Git に置くことは裁定が認めている |
| 追随の Job が稼働の `secret` を Vault から読んで当てる | 不採用 | 追随の Job に Vault の読み取り権限が要る。ESO だけが読む現在の境界（IADR-0433 決定 5）を広げる |
| `bootstrap.sh` で在る KV にも env が空でないときだけ差し替える（IADR-0456 と同じ） | 不採用 | 保管先だけを書き換えると相手と食い違う。対になる秘密で env の差し替えを許すと、片側だけ書く経路が残る |
| 許可リストのキーを改名する（`deferred` → `paired` 等） | 不採用 | 読み手が 5 つあり、画面の挙動を変えない裁定の下で得るものが記述の明確さだけである |

## 結果

- **良い影響**
  - 対で回した値が、次の起動で認証基盤・Vault のどちらからも戻らない。Runbook 手順 C が「回せない」から「対の手順で回す」になった。
  - 本番の値を realm の宣言へ書くと検査が止める。
- **悪い影響 / トレードオフ**
  - 🔴 **realm JSON の client の `secret` を直しても、既存の client には届かない。** 開発用の値を変えたいときは client を作り直すか、対の手順で回す。
  - 🔴 **env で対になる秘密を差し替える運用ができなくなった**（在る KV は env を渡しても変わらない）。回すのは対の手順である。
  - Runbook はリハーサル未実施である（稼働クラスタに触れない作業条件）。
- **フォローアップ**
  1. 対の手順のリハーサル（新しい環境で群 1・群 2 を通しで実行し、Runbook を直す）。
  2. `apply_secret` の既定値の Secret を `ESO=1` で作らないようにするか（塞いでいない経路）。**同型の事故が起きたら**起票する（1 回目は記録に留める）。
  3. （記録のみ）検査 8 の射程は `clients[].secret` だけである。realm の `users[].credentials` にある開発用の平文パスワード（4 件）は形の検査の外にある。
     これらは相手と対で書く秘密ではない（人の資格情報）ので #1682 の範囲外とし、`docs/security/security.md`「開発専用の平文認証情報」の扱いのままとする。

## 関連

- Supersedes: なし（[[IADR-0369]] 決定 2 の境界表の client の `secret` の行だけを動かす。同 IADR に日付つき追記）
- Superseded by: なし
