---
title: IADR-0572 dev の値の守りの対象を、realm の宣言でサービスアカウントを持つ機密クライアント全部へ広げる。対象は手で並べず宣言から導出し、述語の正は後追い Job のスクリプトに置く
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0124, IADR-0517, IADR-0518, IADR-0485, IADR-0133]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密・初期投入は無いときだけ作る・本番の秘密を realm の宣言から外す）
related_specs:
  - ../specs/20261022_dev-secret-guard-all-sa-clients.md
---

# IADR-0572: dev の値の守りを、サービスアカウントを持つ機密クライアント全部へ広げ、対象を宣言から導出する（GHSA-rh44-px44-p48q）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: オーナー指示（2026-10-22）・claude（導出の置き場と env の名前）

## 起点・関連

- 起点: GHSA-rh44-px44-p48q（多層防御の強化。既定を fail-closed へ寄せる一連の変更の 1 本目）。
- 計画: **ADR-0124 決定 1**。要求 **NFR-18**。
- 前提: [IADR-0517](./IADR-0517_dev-client-secret-guard-kube-context-allowlist.md)（kube context の許可集合・`ALLOW_DEV_CLIENT_SECRETS=1`・`--check-dev-secrets`）・[IADR-0518](./IADR-0518_realm-import-secret-with-env-client-secrets.md)（取り込み元の差し替え）・[IADR-0485](./IADR-0485_paired-secrets-create-only-declarations.md)（宣言の `secret` は作成時にだけ運ぶ・種は無いときだけ作る）。
- 本 IADR は IADR-0517 の残余 2（対象外のクライアント）を閉じる。IADR-0517 の決定 1・3（context の許可集合・上書き）と決定 5（検知の形）は変えない。

## コンテキストと課題

IADR-0517 は守りの対象を、レルム管理のロールを持つ 3 クライアントに限り、集合を 2 か所（shell の `DEV_CLIENT_SECRET_GUARDED`・JS の `DEV_SECRET_GUARDED_CLIENTS`）に手で並べた。宣言のほかのサービスアカウントを持つ機密クライアント（`bff`・east-west の `*-service`・`synthetic-monitor`・`abac-seeder`・AST の 5 つ）も宣言の dev の値で作られ得る。最小権限・多層防御の観点では、ロールを持つ主体はどれも同じ守りに入れるべきである。手で並べた集合は、宣言にクライアントを足したときに追随を忘れる。

## 検討した選択肢

1. **手書きの集合へ全クライアントを足す** — 足し忘れの形は変わらない（新しいクライアントで再発する）。
2. **生成物（表のファイル）を CI で突き合わせる** — 実行時に node が要らないが、CI を通らない枝からの起動で食い違い得る。
3. **実行時に宣言から導出する（採用）** — 述語 1 本で、起動器・種・後追い・検知が同じ集合を使う。ホストで node を要する。

## 決定

### 決定 1: 対象は宣言から導出する

- 対象は realm の宣言のクライアントのうち **`serviceAccountsEnabled: true`・`publicClient` でない・宣言が `secret` を持つもの全部**。clientId では選ばない。
- **述語の正は `deploy/local/keycloak-setup/reconcile-realm.js` の `isDevSecretGuardedClient`**（後追い Job にはこのファイル 1 本だけがマウントされるため）。`DEV_SECRET_GUARDED_CLIENTS` は撤去する。
- ホスト側は `scripts/lib/dev-client-secret-targets.js` が同じ述語で宣言を読み、`clientId<TAB>env の名前<TAB>宣言の値` の表を標準出力へ出す（0 件・壊れた宣言は非 0）。`scripts/lib/dev-client-secret-guard.sh` の `dev_client_secret_load_targets` がこれを読む。
- **導出できなければ止まる**（dev の context でも。守れないまま進まない）。判定は、表に無いクライアント・表を読んでいない場合に止める側へ倒す。

### 決定 2: dev の値と env の名前

- dev の値は **宣言の `secret` そのもの**（`<client>-dev-secret-change-me` の形の決め打ちをやめる）。
- env の名前は既定の規則 **`<CLIENT_ID の大文字・- を _>_CLIENT_SECRET`**。既存の起動器・投入器が別の名前を使う 2 つだけを例外にする（`bff` → `BFF_OIDC_CLIENT_SECRET`、`abac-seeder` → `ABAC_SEED_CLIENT_SECRET`）。例外の表は `dev-client-secret-targets.js` に 1 か所。

### 決定 3: 適用点（IADR-0517 決定 4 の表を広げる）

| 適用点 | 変更 |
| --- | --- |
| `scripts/k8s-local-up.sh` の [1/7] 直後 | 対象全部の env を判定へ渡す（取り込み元の差し替えも同じ集合なので、ESO の有無によらない） |
| 取り込み元の差し替え（`dev_client_secret_realm_for_import`） | 渡された realm ファイルから対象を導出し、env を与えたもの全部を差し替える |
| `deploy/local/vault/eso/bootstrap.sh` | 対象ごとの置き場（`client_secret_kv_of`）で、無い KV（AST の app-secrets はキー）だけを判定へ渡す。**宣言に現れて置き場の決まっていないクライアントは止める**（入れ漏れの検知。種を置かない `abac-seeder` は空）。AST のキーは env を与えたときその値で入れる（値は stdin。在るキーは触らない） |
| 後追い（`reconcile-realm.js`） | `devSecretCreations`・`devSecretFindings`・`collectGuardedSecrets` が述語で対象を選ぶ |

## 結果

- 良い影響: 宣言にサービスアカウントを持つクライアントを足せば、守りの側を触らずに対象へ入る。起動器・種・後追い・検知の集合が 1 本の述語に揃う。
- 非対称: Vault の種（`bootstrap.sh`）の置き場の表（`client_secret_kv_of`）だけは、足したクライアントごとに追記が要る。追記するまで `bootstrap.sh` は止まる（集合は自動で入り、書く先だけを手で決める。意図した fail-closed）。
- 悪い影響: dev ではない context の起動では、対象全部（2026-10-22 時点で 20）の env が要る。ホストで node が要る（無ければ止まる。前提ツールとして `deploy/local/README.md`「必要ツール」と起動器の冒頭に書いた）。

## 残余

1. AST の realm（submodule を同梱したとき）は取り込み元の差し替えの対象外である（従来どおり）。後追いと検知は全 realm に掛かる。
2. 判定は context の名前だけを見る（IADR-0517 残余 3）。

## フォローアップ

- なし（本 IADR と同じ PR で配備・試験・文書まで入れた）。
