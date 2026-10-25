---
title: 作業仕様書 — dev の値の守りの対象を、realm の宣言でサービスアカウントを持つ機密クライアント全部へ広げ、対象を宣言から導出する（GHSA-rh44-px44-p48q）
type: spec
status: done
related_ids: [NFR-18, ADR-0124, IADR-0572, IADR-0517, IADR-0518, IADR-0485]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密・初期投入は無いときだけ作る・本番の秘密を realm の宣言から外す）
issue: "GHSA-rh44-px44-p48q"
---

# 作業仕様書 — dev の値の守りを、サービスアカウントを持つ機密クライアント全部へ広げる（GHSA-rh44-px44-p48q）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0572**（新規）に置く。
> 多層防御の強化として、既定を fail-closed へ寄せる一連の変更（4 本の PR）の 1 本目である。参照は GHSA の ID だけとする。
> 🔴 **稼働中のクラスタ・Keycloak には何も実行しない。** 試験はすべて PATH のスタブ・偽の Keycloak の下で行う。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR-18**（シークレット管理）。
- 計画 ADR: **ADR-0124 決定 1**（対になる秘密は相手と対で書く。初期投入は無いときだけ作る）。矛盾しない（守りの対象を広げるだけで、対で書く運用・無いときだけ作る形は変えない）。
- 実装 ADR: **IADR-0572**（本作業で新設）。前提 **IADR-0517**（kube context の許可集合・`--check-dev-secrets`。その残余 2「対象外のクライアント」を閉じる）・**IADR-0518**（取り込み元への env の差し替え）・**IADR-0485**（宣言の `secret` は作成時にだけ運ぶ）。

## 要件（オーナー指示 2026-10-22）

1. `dev-client-secret-guard`（と `--check-dev-secrets`）の対象を、realm 内でサービスアカウントを持つ全クライアントへ広げる。
2. 対象は手で並べず、realm の定義から導出する（新しいクライアントの入れ漏れを防ぐ）。
3. 変異で赤になることを確かめる。

## 受け入れ基準と試験の写像

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | 対象は宣言の「`serviceAccountsEnabled: true`・`publicClient` でない・宣言が `secret` を持つ」クライアント全部で、当初の 3 つを含み、SA を持たない道具の OIDC・公開クライアントを含まない | `k8s-local-up.test.js`「IADR-0572: 判定器の対象は…」・`keycloak-realm-reconcile.test.js`「IADR-0572: 対象は宣言から導出する」 |
| AC2 | 宣言へ SA を持つクライアントを足すと、守りの側を触らずに対象へ入る（判定・取り込み元の差し替え・後追い・検知）。SA を外す・公開にする・secret を消すと外れる | `k8s-local-up.test.js`「IADR-0572 変異: 宣言へ…」・`keycloak-realm-reconcile.test.js` 同 |
| AC3 | 起動器（`k8s-local-up.sh`）は dev ではない context で、対象全部について env が未設定か宣言の値と同じなら、何も書かずに名指して止まる。当初の 3 つだけを与えても止まる | `k8s-local-up.test.js`「#1830/IADR-0572: …対象のクライアント全部を名指して止まる」「当初の 3 つだけに値を与えても…」 |
| AC4 | 対象を導出できない（node・宣言）ときは、dev の context でも何も書かずに止まる。表に無いクライアント・表を読んでいない判定は止める側に倒れる | `k8s-local-up.test.js`「対象を導出できない…」「表に無いクライアント…」 |
| AC5 | Vault の種（`bootstrap.sh`）は無い KV（AST の app-secrets はキー）だけを判定へ渡し、当初の 3 つ以外も名指す。置き場の決まっていないクライアントが宣言に現れたら止める。AST のクライアントは env を与えればその値で入れる（在るキーは触らない） | `scripts.repo.test.js` の #1830 / IADR-0572 節 |
| AC6 | 後追い（Job）は対象を宣言の dev の値で作る計画を `allow` でなければ書かずに止め、`--check-dev-secrets` は当初の 3 つ以外（例 `bff`）も名指す | `keycloak-realm-reconcile.test.js` #1830 節 |
| AC7 | 値は出力・引数に出さない（従来どおり） | 上の各試験の表明 |

## 方式

- **述語の正は `deploy/local/keycloak-setup/reconcile-realm.js` の `isDevSecretGuardedClient`**（Job へはこのファイル 1 本だけがマウントされるため）。`DEV_SECRET_GUARDED_CLIENTS`（手書きの 3 つ）は撤去する。
- **ホスト側は `scripts/lib/dev-client-secret-targets.js`** が同じ述語で宣言を読み、`clientId<TAB>env の名前<TAB>宣言の値` の表を出す。判定器（`scripts/lib/dev-client-secret-guard.sh`）の `dev_client_secret_load_targets` がこれを読む。判定（`dev_client_secret_decide`）は従来どおり外部コマンドを呼ばない。
- **env の名前**は既定の規則 `<CLIENT_ID の大文字・- を _>_CLIENT_SECRET`。既存の名前と違う 2 つだけを例外にする（`bff` → `BFF_OIDC_CLIENT_SECRET`、`abac-seeder` → `ABAC_SEED_CLIENT_SECRET`）。
- **dev の値は宣言の `secret` そのもの**（従来の `<client>-dev-secret-change-me` の形の決め打ちをやめる。AST の 2 つは別の形のため）。
- **`bootstrap.sh`** は対象ごとの Vault の置き場（`client_secret_kv_of`）を持つ。置き場の表は「対象の集合」ではなく「どこに書くか」の対応であり、宣言に現れて表に無いクライアントは止める（入れ漏れの検知）。種を置かないクライアント（`abac-seeder`）は空を返す。AST の app-secrets のキーは env を与えたときだけその値で入れる（seed の字面は `SecretItemBootstrapSeedTests` が固定しているので変えず、作った直後・無いキーへ env の値を stdin で入れる）。

## 母集合（規則 9・10。誤りの側の文字列で走査した結果）

走査語: `3 クライアント` / `3 つの env` / `identity-admin.*reset-gate.*mcp-client-admin` / `DEV_CLIENT_SECRET_GUARDED` / `DEV_SECRET_GUARDED_CLIENTS`（`.ai-context/specs`・`.ai-context/adr`・`CHANGELOG.md` を除く全追跡ファイル）。

| 場所 | 扱い |
| --- | --- |
| `scripts/lib/dev-client-secret-guard.sh`・`scripts/k8s-local-up.sh`・`deploy/local/vault/eso/bootstrap.sh`・`deploy/local/keycloak-setup/reconcile-realm.{js,sh}` | 直す（本体） |
| `deploy/local/keycloak-setup/README.md`・`deploy/local/README.md`・`deploy/local/infra/keycloak.yaml`・`deploy/local/keycloak-setup/realm-reconcile-job.yaml`（コメント） | 直す |
| `docs/security/security.md`・`docs/operations/paired-secret-rotation-runbook.md` | 追記で直す（trace ブロックに本仕様書と IADR-0572） |
| `scripts/k8s-local-up.sh` の #1834 の経緯のコメント（「3 つの env から作られ」） | 残す（当時の経緯の記述。直前に本作業の追記を置いた） |
| `reconcile-realm.js` 冒頭の #1830 の段落 | 残し、直後に読み替えの追記を置く |
| `.ai-context/adr/IADR-0517*`・`IADR-0518*`・確定済みの仕様書 | 書き換えない（凍結記録。IADR-0572 が後継を名指す） |
| 試験（`k8s-local-up.test.js`・`keycloak-realm-reconcile.test.js`・`scripts.repo.test.js`） | 直す（対象を宣言から数える） |

## 変異（赤になることを確かめる）

| 変異 | 赤になる試験 |
| --- | --- |
| M1: 述語を当初の 3 つの clientId の列挙へ戻す | 「判定器の対象は宣言から導出した集合と一致」「変異: 宣言へ足すと…」「当初の 3 つだけに値を与えても…」 |
| M2: 述語から `serviceAccountsEnabled` の条件を外す | 「SA を持たない道具の OIDC が対象に入った」 |
| M3: 判定で表に無いクライアントを通す（`dv` の失敗を無視） | 「表に無いクライアント…止める側に倒れる」 |
| M4: 導出に失敗しても起動器が進む（`|| exit 1` を外す） | 「対象を導出できない…何も書かずに止まる」 |
| M5: `bootstrap.sh` で置き場の無いクライアントを黙って飛ばす | 「変異: 宣言に Vault の置き場の決まっていない SA クライアントが現れたら…」 |

## 配備前の手順（PoC。値は書かない）

PR 本文に順序つきで書く。要点: dev ではない kube context の PoC では、起動器を再実行する前に、対象全部の env（一覧は `node scripts/lib/dev-client-secret-targets.js deploy/keycloak/microservices-platform-realm.json | cut -f1,2`）へ稼働中の値（`ESO=1` なら Vault に在る値）を与える。

## 残余

- AST の realm（submodule 同梱時）は取り込み元の差し替えの対象外（従来どおり。後追いと検知は全 realm に掛かる）。
- 判定は context の名前だけを見る（IADR-0517 残余 3。変えない）。
