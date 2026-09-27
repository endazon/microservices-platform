---
title: 作業仕様書 — 対になる秘密を SC-22 の対象外として扱い、本番の秘密を realm の宣言から外し、相手と Vault を対で書くローテーション手順を置く（#1682）
type: spec
status: done
related_ids: [SC-22, NFR-18, ADR-0124, ADR-0095, ADR-0104, IADR-0369, IADR-0433, IADR-0453, IADR-0456, IADR-0461, IADR-0485]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1・3・4・フォローアップ 1・2・4・5
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md 決定 1（部分改定）・決定 4（補完）
related_specs:
  - 20260925_458_secret-rotation-runbook
  - 20260911_issue-1411_sc22-console-fallback-and-bff-vault-write
  - 20260915_issue-1477_screen-only-poc-setup
  - 20260904_issue-1088_persist-by-default-and-realm-reconcile
issue: "#1682"
---

# 作業仕様書 — 対になる秘密の扱い（#1682）

> 本仕様書は実装着手前に作成する。受け入れ基準は issue #1682 の「やること」1〜5 である。
> 裁定は planning#700（利用者裁定 2026-09-28）、正本は計画 ADR-0124（ADR-0095 決定 1 の部分改定・決定 4 の補完）。

## 射程と、触らないもの

- **Vault の audit（ADR-0124 決定 2・フォローアップ 3）は扱わない。** 並行の #1683 が持つ。
  audit device・収集器・`docs/security/security.md` の監査の表には触れない（`security.md` そのものを本件では編集しない）。
- **画面（SC-22）の挙動は変えない。** BFF の allowlist（`items[]`）・policy・端点・フロントエンドは 1 行も動かさない。
- 稼働クラスタのスクリプトは実行しない（`LIVE` 未設定）。検査器は `--self-test` とユニットの試験で確かめる。
- 計画リポジトリは読み取り専用（`git archive origin/main` を scratch へ展開して実測する）。

## 計画の読み（ADR-0124）

- 決定 1: 相手（IdP の realm・データストア）と同時に変えないと成立しない秘密＝**対になる秘密**は SC-22 の対象外（ADR-0095 決定 1 の例外）。
  今の対象は OIDC クライアントシークレット・s2s 資格情報（実装の `deferred[]`・17 件）、AST の `*-auth-client-*` の 4 組（`notWritable`）、
  データストアの資格情報（`excluded[]`・7 件）。境界は性質であり項目名ではない。Git には置かない。ローテーションは相手と Vault を対で書く運用手順。
  **その前提として本番の秘密を realm の宣言から外し、初期投入を「無いときだけ作る」にする。開発用の値が Git に在ることは、本番へ持ち込まない限り認める。**
- 決定 4 の暫定手段: 手順ができるまでこの群は回さない／片側だけ書くことを禁じる。退避手段の記録は issue コメントで、記録先を 1 か所にまとめる。

## 設計

### 1. 本番の秘密を realm の宣言から外す（やること 1）

「realm の宣言が秘密を持つ」経路は 3 つあり、いずれも**宣言の値で稼働の値を当て直す**ことで、相手側で回した値を元へ戻していた（Runbook 手順 C の前提 ①③）。

| 経路 | 今 | 変えた後 |
| --- | --- | --- |
| Keycloak の `--import-realm` | 同名 realm が在ると飛ばす（`IGNORE_EXISTING`） | **変えない**（既に「無いときだけ」） |
| `reconcile-realm.js` の client 更新 | client の `secret` を宣言所有として比べ、違えば `PUT` で宣言の値へ戻す。比べるために `GET …/client-secret` で稼働の値を読む | **`secret` は作成時にだけ運ぶ**（`CLIENT_CREATE_ONLY_KEYS`）。更新の比較・本文から外し、稼働の値を読みに行かない。client が無いとき（`client.create`）と realm が無いとき（`realm.create`）は宣言の値で作る |
| `bootstrap.sh` の Vault の種 | 対になる秘密の 24 KV（`deferred[]` 17・`excluded[]` 7）を**毎回** `vault kv put`（全置換）で env か開発用既定値へ戻す | **無いときだけ作る**（`vkv_create_if_absent`。`-cas=0`）。在れば触らない（env も効かない。回すのは対の手順） |

- **開発用の値の扱い（既存 IADR との整合）**: realm JSON の client `secret` は開発用の値のまま Git に残す。
  `docs/security/security.md`「開発専用（dev-only）の平文認証情報 — 本番流用禁止」と IADR-0092・IADR-0133・IADR-0098 の
  「realm import の secret は dev 専用。本番は Secret（Vault）経由で注入し、realm import へコミットしない」をそのまま満たす
  （ADR-0124 決定 1「開発用の値が Git に在ることは、本番へ持ち込まない限り認める」と同じ線）。
  **宣言に本番の値を置けないことを機械で止める**: `check-realm-constraints.js` に検査 8 を足し、`clients[].secret` は開発用の形
  （`-dev-secret-change-me` で終わるか `dev-only-` で始まる）に限る。
- **既存 IADR との矛盾の有無**: IADR-0369 決定 2 の境界表は client の `secret` を宣言所有に置いている。本件はこれを「作成時にだけ宣言が運ぶ」へ動かす。
  これは ADR-0124 決定 1（本番の秘密を realm の宣言から外す）の直接の帰結であり、裁定の範囲を越えない。IADR-0369 に日付つき追記を入れ、新 IADR で理由を残す。
  IADR-0456 決定 6（画面が書く KV は無いときだけ作る）は同じ形を対になる秘密へ広げるだけであり矛盾しない。
- **残る戻り経路（本件で変えない）**: `scripts/k8s-local-up.sh` の `apply_secret` は env か開発用既定値で Secret を作る
  （`postgres` / `rabbitmq` / `keycloak-admin` / `reset-gate-oidc` は `ESO=1` でも作る。`creationPolicy: Merge` の同期が Vault の値で上書きする）。
  Vault の値は戻らないが、次の同期までの間 Secret が既定値になる。Runbook に「起動の後に同期を促す」と書く。`apply_secret` を変えるのは射程の外
  （裁定の前提 ①③ は realm の宣言と `bootstrap.sh` を名指しする）。

### 2. 対になる秘密のローテーション手順（やること 2）

- 新しい Runbook `docs/operations/paired-secret-rotation-runbook.md` を置く。
  - 群 1: 認証基盤のクライアントシークレット（`deferred[]` 17・AST の `*-auth-client-*` 4 組）。**新しい値を手元で作る → Vault の直前の版を控える（版番号だけ）→ Vault へ書く → 認証基盤の管理 API で client の `secret` を同じ値にする → 同期を促す → 消費側を作り直す → 確かめる。**
    重ねられない（認証基盤の client の secret は 1 つ）ので、書いてから作り直しまでの間は消費側が `invalid_client` になり得る。
  - 群 2: データストアの資格情報（`excluded[]` 7）。ストアごとのコマンドは既存の `secret-rotation-runbook.md` 手順 B-1 を正とし、本書は対で書く順序と途中で止まったときの戻し方を持つ。
  - **途中で止まったときの戻し方**を段ごとの表にする（どちら側が新しい値かで戻す向きを決める。Vault は `kv rollback`、認証基盤は Vault の直前の版の値を管理 API で当て直す）。
  - 記録先は #458（下の 5）。
- `secret-rotation-runbook.md`: 手順 C を「対の手順で回す」へ改め、戻す経路の表（bootstrap・reconcile が戻さなくなった）と分類の表を直す。

### 3. SC-22 の許可リストと文書を裁定に合わせる（やること 3）

- `deploy/bootstrap/sc22-secret-items.json`: 構造（`items` / `deferred` / `excluded` / `notWritable`）は変えない（BFF と試験がこの名前を読む。画面の挙動は不変）。
  `$comment` と `deferred[].reason` / `excluded[].reason` / `ast-app-secrets.note` を「対になる秘密（SC-22 の対象外。ADR-0124 決定 1）」へ書き換える。
  `deferred[].reason` の「SC-22 のモックアップ受領時にまとめて判断する」は裁定が出たので消す。
- `docs/screens/SC-22_secret-item-management.md`: 対象外の群の位置づけと、未決事項「`deferred[]` を画面で扱うか」を裁定済みへ直す。
- `docs/operations/operations.md` の該当節・`secret-item-console-injection-runbook.md`: 同じ位置づけへ。

### 4. 件数を実物の 17 件にそろえる（やること 4）

- 実物: `deferred[].vaultPaths` は 17（OIDC 8: `bff` / `identity-admin` / `grafana` / `vault` / `headlamp` / `wikijs` / `reset-gate` / `synthetic-monitor`、s2s 9）。`excluded[]` 7。
- IADR-0433（「計 18 件」「20 件」×3）と IADR-0453（「20 件」）へ日付つき追記で 17 件を記す。本文は凍結記録なので書き換えない。
- IADR-0453 フォローアップ 2・3 を ADR-0124 を引いて閉じる（2 は決定 1 で閉じる。3 は決定 2 が手段を定め、暫定の記録先を本件で 1 か所にまとめたので閉じる。audit の実装は #1683）。
- IADR-0433 フォローアップ 4（`deferred[]` の扱いを計画へ問う）も同じ裁定で閉じる。
- Runbook は 17 件で既に正しい箇所と、「`deferred[]` の 18」「計 25 KV」「28 項目」の誤りが混在する（下の母集合）。

### 5. 退避手段の使用の記録先を 1 か所にまとめる（やること 5）

- **#458 に一本化する。** 理由: #1411 は閉じている（閉じた issue へのコメントは棚卸しで見えにくい）。#458 は Vault 集中管理とローテーション Runbook の親で開いており、
  ローテーション（手順 B・対の手順）の記録先として既に使われている。
- `secret-item-console-injection-runbook.md` §5・§記録、`secret-rotation-runbook.md` §記録、新 Runbook §記録 がすべて #458 を指す。
  「Vault の audit を取り込めるまでの暫定」と書く（取り込みは #1683 の射程。本件は手段を書き換えない）。

### 6. 計画 ADR レンジ

- 実測（`git archive origin/main`＝`74eb255` を scratch へ展開し `node tools/doc-checks/gen-plan-ranges.js --check`）: MSP は FR [1, 22]・UC [1, 11]・SC [1, 22]・**ADR [1, 124]**（欠番なし）・NFR 実物 [1, 28]。
- 前回の出典 `17518cc` からの `07_adr/` の差分で `status:` 行の変化は追加ファイルの `+status: Accepted` 2 件（ADR-0123・ADR-0124）だけ（既存の 0024・0062・0095 は本文が変わったが状態は動いていない）。
- `traceability.repo.md` の ADR レンジを `ADR-0001..0122` → `ADR-0001..0124` にし、別紙 `docs/how-to/plan-id-range-history-annex.md` に 1 回分を追記する
  （コミット件名・trace ブロックに ADR-0124 を書くため。実物が 0124 まで進んでいるので 0123 も同時に開ける）。

## 母集合（規則 9・10）

### 秘密を持つ宣言の全件（走査語で引いた）

走査語: realm JSON は `"secret":`、`bootstrap.sh` は `kv put` / `vkv_`、`reconcile-realm.js` は `secret`、許可リストは `vaultPath` / `vaultPaths` / `notWritable`。

**realm JSON（`deploy/keycloak/microservices-platform-realm.json`）の `clients[].secret` — 23 件**

| client | 値の形 | Vault の置き場 | 分類 |
| --- | --- | --- | --- |
| `bff` / `identity-admin` / `grafana` / `vault` / `headlamp` / `wiki-js` / `reset-gate` / `synthetic-monitor` | `*-dev-secret-change-me` | `msp/<名>-oidc`（`wiki-js` は `msp/wikijs-oidc`） | `deferred[]`（OIDC 8） |
| `retrieval-service` / `ingestion-service` / `aianalysis-service` / `graph-service` / `conversion-service` / `wiki-service` / `datasource-service` / `mcp-server` / `document-service` | `*-dev-secret-change-me` | `msp/<名>-token` | `deferred[]`（s2s 9） |
| `ai-stock-trading-kb-writer` / `ai-stock-trading-llm-caller` | `*-dev-secret-change-me` | `ai-stock-trading/app-secrets` の `kb-` / `llm-auth-client-secret` | `notWritable` |
| `ai-stock-trading-svc` / `ai-stock-trading-owner` | `dev-only-*` | 同 `service-` / `discord-owner-auth-client-secret` | `notWritable` |
| `abac-seeder` | `*-dev-secret-change-me` | **Vault に無い**（dev の投入器だけが使う。IADR-0133） | SC-22 の母集合外（性質は対になる秘密） |
| `argocd` | `*-dev-secret-change-me` | **Vault に無い**（`argocd-secret` を `k8s-local-up.sh` が直接 patch。IADR-0092） | SC-22 の母集合外（性質は対になる秘密） |

- 除外理由: `abac-seeder`・`argocd` は Vault に置き場が無く、SC-22（Vault へ書く画面）の母集合に入らない。**本件の reconcile の変更（作成時にだけ運ぶ）と検査 8 は 23 件すべてに効く。**
  Runbook では「同じ性質だが Vault に無い」として扱い、回すときは同じ順序（相手→消費側）で行うと注記する。
- 利用者（`users[]`）の平文パスワード（`poc-user` 等）は人の資格情報で、相手と対で書く秘密ではない（相手が無い）。本件の対象外（security.md の dev-only 節のまま）。

**`deploy/local/vault/eso/bootstrap.sh` の KV — 28 件**

| 分類 | KV | 今 | 変えた後 |
| --- | --- | --- | --- |
| `items[]` のうち種を入れる 4 | `msp/llm-provider-credentials` / `msp/wikijs-sync` / `msp/keycloak-smtp` / `ai-stock-trading/app-secrets` | 無いときだけ（IADR-0456 決定 6） | 変えない |
| `deferred[]` 17 | 上表の OIDC 8・s2s 9 | 毎回全置換 | 無いときだけ |
| `excluded[]` 7 | `msp/postgres` / `postgres-app` / `rabbitmq` / `rabbitmq-app` / `keycloak-admin` / `object-storage-credentials` / `wikijs-db` | 毎回全置換 | 無いときだけ |

- `items[]` の `ast-moomoo` / `ast-moomoo-rsa` は seed しない（変えない）。

**`reconcile-realm.js` の `secret` の扱い — 4 か所**: 頭部の境界の注記（宣言が正に `secret`）／`plan` の client 更新の比較と `PUT` 本文／`collectLive` の `GET …/client-secret`／`realm.create`・`client.create` の本文（宣言の丸ごと）。
前 3 つを変え、最後は「無いときだけ作る」なので残す。

**許可リスト（`sc22-secret-items.json`）**: `items[]` 6 KV・書けるプロパティ 21／`notWritable` は `keycloak-smtp` の構成 3（`host` / `port` / `starttls`。秘密でなく Git 経由の構成）と `ast-app-secrets` の対になる秘密 8／`deferred[]` 17／`excluded[]` 7。

### 追随する文書（規則 9。誤りの側の文字列で走査した）

| 走査した文字列 | 当たり | 扱い |
| --- | --- | --- |
| `deferred` ＋ 件数（`20 件` / `18 件` / `計 18` / `の 18`） | IADR-0433 の 4 か所（150・156・212・251・258 行の 18 / 20）、IADR-0453 の 1 か所（242 行）、`secret-rotation-runbook.md` 82 行「`deferred[]` の 18、計 25 KV」 | IADR は日付つき追記。Runbook は 17・24 へ直す |
| `28 項目` | `secret-item-console-injection-runbook.md` 55 行・`operations.md` 1206 行 | bootstrap が全置換しなくなったので記述を改める（Runbook は日付つき追記の節、運用仕様書は本文） |
| `SC-22 のモックアップ受領時` | `sc22-secret-items.json` の `deferred[].reason` | 消す |
| `#1411 へのコメント` / `#1411 へコメント` | `secret-item-console-injection-runbook.md` 182・259 行 | #458 へ |
| `#458 へのコメント` | `secret-rotation-runbook.md` 241 行 | 正しいまま（記録先の一本化の宛先） |
| `realm JSON の値へ当て直す` / `クライアントの \`secret\` も含む` / `宣言が正` ＋ `secret` | `secret-rotation-runbook.md`（手順 C・戻す経路の表・失敗の分岐）、`deploy/local/keycloak-setup/README.md` 境界表、`reconcile-realm.js` 頭部、IADR-0369 決定 2 の境界表 | Runbook・README・JS は直す。IADR-0369 は日付つき追記 |
| `deferred[]` を画面で扱うか（未決） | `docs/screens/SC-22_secret-item-management.md` 216 行 | 裁定済みへ直す |
| `いまは回せない` | `operations.md` 1217 行・`secret-rotation-runbook.md` | 対の手順で回すへ直す |
| `realm の宣言と同値` / `realm import の置き場と同値`（bootstrap の注記） | `bootstrap.sh` の OIDC・s2s の注記 13 か所 | 「初期値は realm の開発用の値と同値」の意味で正しいまま。種の節の頭に「無いときだけ」の注記を足す |
| `env で上書き可`（bootstrap の注記） | `bootstrap.sh` の OIDC 群の注記 | 「作るときだけ効く」へ直す |

**規則 10（この変更で新たに誤りになる自分の記述）**:
- `SecretItemBootstrapSeedTests` の注記「items[] のパスへの put はすべて『無いときだけ』」は正しいまま（対になる秘密の試験を別に足す）。
- `secret-item-console-injection-runbook.md` の「一括再投入を既定の手順にしない」節は、bootstrap が秘密を全置換しなくなったことで根拠の一部（「稼働中の認証基盤・DB の資格情報が既定値に置き換わる」）が古くなる。日付つき追記で「今は無いときだけ作る。ただし `keycloak-smtp` の構成値と `apply_secret` の Secret は書き直す」と書く。
- `secret-rotation-runbook.md` の「`excluded[]` は env で新しい値を渡し続ける限り戻らない」は、Vault 側は env が無くても戻らなくなる。`apply_secret` の Secret は依然 env で決まるので、「Vault は戻らない。Secret は同期までの間だけ既定値になる」へ直す。
- `keycloak-realm-reconcile.test.js` の「client の secret が違えば client.update になる」試験は、逆（0 件）へ書き換える。

## 受け入れ基準

1. `reconcile-realm.js` は client の `secret` を更新で比べず・運ばず・読みに行かず、作成時にだけ宣言の値を運ぶ（単体試験）。
2. `bootstrap.sh` は対になる秘密の 24 KV を無いときだけ作る（`SecretItemBootstrapSeedTests` の字面の試験。陽性対照は許可リストから引いた 24 件）。
3. realm の宣言の client `secret` は開発用の形に限る（`check-realm-constraints.js` 検査 8。自己試験と実データのラチェット）。
4. 新しい Runbook が群 1・群 2 の対の手順と途中で止まったときの戻し方を持つ。既存 Runbook・運用仕様書・画面仕様書が同じ位置づけを書く。
5. IADR-0433・IADR-0453 に 17 件の追記。IADR-0453 フォローアップ 2・3、IADR-0433 フォローアップ 4 を ADR-0124 を引いて閉じる。IADR-0369 に境界の追記。新 IADR。
6. 退避手段の記録先は #458 の 1 か所。
7. 変異 3 件以上をコミット後に当て、赤を確かめて戻す。

## 実施結果（2026-09-28）

変異はコミットの後の状態に 1 件ずつ当て、赤を確かめてから `git show HEAD:<path> > <path>` で戻し、`git diff --quiet` を確かめた（スクリプトは作業場所の外に置いた）。

| # | 変異 | 当てた先 | 赤にした試験 |
| --- | --- | --- | --- |
| M1 | `CLIENT_CREATE_ONLY_KEYS` を空にする（既存の client の secret を宣言所有へ戻す） | `reconcile-realm.js` | `keycloak-realm-reconcile.test.js`（既存の client の secret が違っても 0 件） |
| M2 | `PUT` の本文から `secret` を落とす条件を外す | `reconcile-realm.js` | 同（本文に secret を載せない） |
| M3 | `msp/postgres` を無条件の `vault kv put` へ戻す | `bootstrap.sh` | `SecretItemBootstrapSeedTests.Paired_secret_kvs_are_created_only_when_absent`（T-78） |
| M4 | 補助関数の在否の確認を外す（`if false`） | `bootstrap.sh` | 同 |
| M5 | realm の宣言の `bff` の secret を開発用の形でない値にする | realm JSON | `check-realm-constraints.js`（検査 8。実データ） |
| M6 | 許可リストの `deferred[]` から `msp/document-service-token` を 1 件落とす（監査で生き残った変異。逆方向の突合〔補助関数で作るパスの集合＝deferred ∪ excluded〕を足して殺した） | 許可リスト | `SecretItemBootstrapSeedTests.Paired_secret_kvs_are_created_only_when_absent`（T-78） |

検証: `keycloak-realm-reconcile.test.js` 38 件・`check-realm-constraints.js --self-test` 174 件・`reset-gate.test.js` 12 件・`reset-gate.js --self-test` 17 件・
`check-stack-ready.js --self-test` 71 件・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 844 件が緑。Platform.Bff.Tests 790 件緑（1 件は既存の skip）。
`dotnet format src/platform/backend/backend.slnx --verify-no-changes` 緑。check-trace-blocks / check-test-spec-coverage / check-test-traceability / check-cross-repo-refs /
check-plan-id-qualification / check-doc-links / check-doc-type-vocabulary / gen-knowledge-graph --check / check-commit-messages が緑（check-reading-budget は既存の 90% 超の warn 1 件のみ）。
稼働クラスタのスクリプト（`bootstrap.sh`・`reconcile-realm.sh`）は実行していない。Runbook はリハーサル未実施である。

### 監査の後の追記（2026-09-28。PR #1684 の監査 GO の指摘 3 点）

- M6 を殺した（上表）。T-78 の「20 件を下回らない」を集合の一致へ改めた。
- IADR-0485 決定 2 と `reconcile-realm.js` の注記の言い過ぎ（「メモリを通らない」）を直した —— `GET /clients` の表現が `secret` を含む版では通る。値を取りに行かず、比べず、本文とログに出さないだけである。
- Runbook の同期後の確認に、`keycloak-admin` を回した直後は追随の Job が WARN で飛び得るので本実行でやり直すことを足した。
- 記録のみ: 検査 8 の射程は `clients[].secret` だけで、`users[].credentials` の開発用の平文パスワード 4 件は形の検査の外（対になる秘密ではないので本件の範囲外。IADR-0485 フォローアップ 3）。
