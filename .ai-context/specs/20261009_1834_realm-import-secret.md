---
title: 作業仕様書 — Keycloak の realm の初回取り込みへ、管理用の 3 クライアントの secret を env の値で渡す（取り込み元を Secret にする）（#1834）
type: spec
status: done
related_ids: [NFR-18, ADR-0124, ADR-0123, IADR-0518, IADR-0517, IADR-0485, IADR-0369, IADR-0066]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密は相手と対で書く・本番の秘密を realm の宣言から外す）
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md 決定 2（管理用の機密クライアント `mcp-client-admin`）
issue: "#1834"
---

# 作業仕様書 — realm の初回取り込みへ env の値を渡す（#1834）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。判断の記録は **IADR-0518**（新規）に置き、IADR-0517 の残余 1 と決定 4 の表へ日付つきの追記で後継を指す。
> 基点は MSP `origin/develop` `38f2e27f`（#1830 / PR #1833 のマージ直後）。
> 🔴 **稼働中のクラスタ・Keycloak には何も実行しない。** 試験はすべて PATH のスタブの下で行う。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR-18**（シークレット管理）。
- 計画 ADR: **ADR-0124 決定 1**・**ADR-0123 決定 2**。
- 起点 issue: **#1834**（PR #1833〔#1830〕の独立監査 🟡1 と 🟢 から分離。IADR-0517 の残余 1）。
- 実装 ADR: **IADR-0518**（本作業で新設）。前提 **IADR-0517**（kube context の許可集合・`--check-dev-secrets`）・**IADR-0485**（宣言の `secret` は作成時にだけ運ぶ）・**IADR-0369**（realm の後追い Job・永続化）・**IADR-0066**（経路B の realm の取り込み）。

## 裁定（2026-10-09。オーケストレータ）

- issue の 2 案（「取り込みの直前に env の値へ差し替える」／「起動の最後に `--check-dev-secrets` を自動で回す」）のうち、**差し替え**を採る。新しい dev 以外のクラスタで env を与えれば、Keycloak のクライアントは最初からその値になる（公知の値の露出と `invalid_client` の食い違いの両方が閉じる）。
- 差し替えは**取り込み元を作る 1 か所**で行い、対象の集合・env の名前・宣言の値は `scripts/lib/dev-client-secret-guard.sh` から引く（単一情報源）。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 | dev 以外の context で 3 つの `*_CLIENT_SECRET` を与えて起動 → Keycloak が取り込む realm の 3 クライアントの `secret` は env の値（宣言の dev の値ではない） | `k8s-local-up.test.js` #1834 節（スタブが受け取った取り込み元の Secret の realm JSON を JSON として読み、3 つの `secret` が env の値・他のクライアントは宣言のまま） |
| AC1' | env を与えたなら dev の context でも同じ（どの context でも env が勝つ） | 同上（k3d の context で 1 つだけ与える → その 1 つだけ差し替わる） |
| AC2 | 否定形: dev の context（k3d・integration-stack・cutover-rehearsal）で env 未設定 → 宣言のまま | 同上（既定の実走の取り込み元が宣言のファイルとバイト等価〔末尾改行を除く〕）。CI の 2 本は env を与えない（#1830 の陽性対照の試験が固定済み） |
| AC3 | 値をどこにも出力しない・引数に載せない・一時ファイルは 0600／0700 で必ず消す | 同上（stdout・stderr・kubectl の引数の記録に値が無い／スタブが受け取ったファイルの権限 600・ディレクトリ 700／終了後に一時ディレクトリが残らない） |
| AC4 | 取り込み元は ConfigMap ではなく Secret | 同上（`kubectl create secret generic keycloak-realm-import` の 1 行・`keycloak.yaml` の `realms` ボリュームが `secret.secretName: keycloak-realm-import`・ConfigMap 由来ではない） |
| AC5 | 止めたときのメッセージに `--check-dev-secrets` の確認を書く（issue AC3） | `k8s-local-up.test.js` #1830 節の止まる試験に表明を足す |
| AC6 | 手順書・セキュリティ仕様書の更新 | `scripts.repo.test.js` の #1817 節（文言の表明）を追随 |
| AC7 | `realm-reconcile-job.yaml` が `check-deploy-manifests` の対象外である理由を記録する（issue AC4・監査 🟢） | IADR-0518 決定 5・本仕様書「監査 🟢」 |
| AC8 | 宣言の値が realm に 1 か所でない（壊れた宣言）・値に JSON の引用符等が入る → 名指して止まる（黙って差し替えずに進まない） | `k8s-local-up.test.js` #1834 節（判定器の関数を直接） |

## 設計（IADR-0518 に記録）

### 取り込みの経路（実測）

`deploy/local/infra/keycloak.yaml` の Keycloak は `start-dev --import-realm` で起動し、ボリューム `realms`（ConfigMap `keycloak-realms`）を `/opt/keycloak/data/import` へ読み取り専用でマウントする。ConfigMap は kustomize の外で `scripts/k8s-local-up.sh` の [3/7] が `kubectl create configmap keycloak-realms --from-file=…` で作る（kustomize の `configMapGenerator` は root 外ファイルを参照できない。`deploy/local/infra/kustomization.yaml` の NOTE）。helm の chart は Keycloak を持たない。

同じ ConfigMap を読む者はほかに 2 つある —— realm の後追い Job（`realm-reconcile-job.yaml`。宣言の期待値・`--check-dev-secrets` の「dev の値」の比較元）と申請の門（`deploy/mail-relay/reset-gate.yaml`。宣言の `resetPasswordAllowed`）。

### 決定

1. **取り込み元を Secret `keycloak-realm-import`（`platform-infra`）に分ける。** `keycloak.yaml` の `realms` ボリュームは `secret.secretName: keycloak-realm-import` にする。ConfigMap `keycloak-realms` は**宣言のまま**残し、後追い Job と門はそちらを読み続ける。
   - ConfigMap を差し替えると、(a) 実の secret が平の ConfigMap に入る、(b) `--check-dev-secrets` の比較元（宣言の dev の値）が env の値に変わり、正しく回したクライアントを `dev-secret` と誤って名指す。だから分ける。
   - Secret は env が未設定（dev）でも常に作る（形を 1 つにする。dev では中身が宣言と同じ）。
2. **差し替えは `scripts/lib/dev-client-secret-guard.sh` の `dev_client_secret_realm_for_import <realm ファイル>` 1 本。** 対象は `DEV_CLIENT_SECRET_GUARDED` の 3 つ、env の名前は `dev_client_secret_env_name`、宣言の値は `dev_client_secret_dev_value`（どれも同じファイル）。env が空でないクライアントだけ、宣言の `"secret": "<dev の値>"` を `"secret": "<env の値>"` へ置き換える。外部コマンドを使わない（bash の置換。値はどのプロセスの引数にも載らない）。
   - 宣言の該当箇所が **ちょうど 1 か所**でなければ止める（壊れた宣言で黙って差し替えないまま進むと、`invalid_client` の食い違いが戻る）。
   - 値に `"`・`\`・制御文字が入っていれば止める（JSON を壊さない。Keycloak の client secret に要らない文字。値は出さない）。
   - 差し替えたクライアント名だけを標準エラーへ出す（値は出さない）。
3. **Secret は既存の `apply_secret`（#1793）で作る。** 0700 の一時ディレクトリの 0600 のファイルへ組み込みの `printf` で書き、`--from-file` にパスだけを渡し、EXIT trap で消す。
4. **起動器の判定（IADR-0517 決定 4 の k8s-local-up.sh の行）を、ESO の有無によらず 3 つとも見るように変える。** 取り込み元の Secret が ESO の有無によらず 3 つの env から作られる＝取り込みが 4 つ目の「作る口」になったため。従来 `ESO=1` で `identity-admin`・`mcp-client-admin` を Vault の種（`bootstrap.sh`）に任せていたが、`bootstrap.sh` は [4/7]（Keycloak の起動と初回取り込み）より後に走るので、`ESO=1` の dev 以外のクラスタの初回は、bootstrap が止める前に Keycloak が dev の値で 2 つを作っていた。
5. **`realm-reconcile-job.yaml` は `check-deploy-manifests` の対象に入れない**（監査 🟢）。検査器の設計の要点 1 は「overlay と chart を走査で見つける・列挙を持たない」であり、overlay に属さない単独のマニフェストは 42 本ある（`kubectl apply -f` で起動器・スクリプトが当てる。`deploy/local/vault/eso/` の ExternalSecret 群・aliases・argocd 等）。1 本だけ足すと列挙を持ち込み、残り 41 本との扱いが割れる。単独のマニフェストを類として検査へ入れるかは別の判断（本 issue の射程外）。この Job の形は `k8s-local-up.test.js`（IADR-0369 節・#1830 節）が env・マウント・`DEV_CLIENT_SECRETS_ALLOWED` の既定を読んで固定している。

## 母集合（規則 9・10）

### 規則 9 — realm JSON を Keycloak へ渡す経路を全数走査した

`git grep -n -e 'import-realm' -e 'data/import' -e 'microservices-platform-realm.json' -e 'realm-export.json' -e 'keycloak-realms' -- deploy scripts .github src/platform ':!*.test.js'`（＋ helm の templates・workflows の `keycloak` を目で確認）:

| 経路 | 渡し方 | 扱い | 理由 |
| --- | --- | --- | --- |
| `scripts/k8s-local-up.sh` [3/7] → `deploy/local/infra/keycloak.yaml`（`--import-realm`） | 旧: ConfigMap `keycloak-realms` をマウント | **直す**（Secret `keycloak-realm-import` へ・3 つを env で差し替え） | 本 issue の本体 |
| `deploy/local/infra-persistence`（overlay） | base の keycloak.yaml を取り込み、PVC を足すだけ | **追随**（コメントだけ） | マウント定義は base が持つ |
| `deploy/local/README.md`「手動でステップ実行する場合」 | 手で ConfigMap を作る手順 | **直す**（Secret も作る手順を足す） | 手で起動した人の Keycloak が起動できなくなる |
| `.github/workflows/integration-stack.yml`・`cutover-rehearsal.yml` | 起動器を k3d で走らせる（cutover は realm を消して Keycloak を再起動＝取り込み直し） | **変えない**（陽性対照） | env を与えないので取り込み元の中身は宣言と同じ。取り込み直しも同じ Secret を読む |
| realm の後追い Job（`realm-reconcile-job.yaml` → `reconcile-realm.js`） | ConfigMap `keycloak-realms` をマウント（期待値・`--check-dev-secrets` の比較元） | **変えない**（宣言のまま読む） | 差し替えると比較元が壊れる（決定 1）。無い client の作成は IADR-0517 の `deny` が守る |
| 申請の門（`deploy/mail-relay/reset-gate.yaml`） | ConfigMap `keycloak-realms` をマウント（宣言の `resetPasswordAllowed`） | **変えない**（コメントの「realm import と同じ ConfigMap」だけ直す） | client の secret を使わない |
| `deploy/docker-compose.yml`（`--import-realm`・realm ファイルを直接マウント） | bind mount | **対象外** | kube context を持たない手元専用の経路（IADR-0517 残余 4 と同じ） |
| `deploy/helm/microservices-platform`（chart） | Keycloak を持たない（`keycloak` は認証の設定値の参照だけ） | **対象外** | 取り込みの経路ではない |
| AST の realm（`src/ai-stock-trading/infra/keycloak/realm-export.json`。在れば同梱） | 同じ取り込み元へ同梱 | **同梱を続ける**（中身は差し替えない） | 対象の 3 クライアントを持たない |
| `ci.yml` の `check-realm-constraints` / `check-realm-copy-drift` / `keycloak-realm-reconcile.test.js`・`check-stack-ready.js` G9・`measure-cutover-inventory.js` | realm ファイルを読む・稼働を読む | **対象外** | Keycloak へ渡さない |

### 規則 10 — この変更で新たに誤りになる記述

`git grep -n -e 'realm import 用 ConfigMap' -e 'realm import ConfigMap' -e 'realm import と同じ ConfigMap' -e '起動器の外であり' -e '初回 import は' -e 'そちらの判定に任せる' -e 'realm ConfigMap' -e 'keycloak-realms' -- ':!.ai-context/specs' ':!CHANGELOG.md'`:

| 箇所 | 扱い |
| --- | --- |
| `docs/operations/paired-secret-rotation-runbook.md`・`docs/security/security.md`「初回 import は宣言の dev の値で 3 つを作る（起動器の外であり止められない）」 | **直す**（起動器が取り込み元へ env の値を渡す。残るのは ESO の Vault と env の食い違い等の後追いの検知） |
| `scripts/k8s-local-up.sh` の [1/7] の判定のコメント（ESO=1 は bootstrap に任せる）・[3/7] の見出しとコメント | **直す** |
| `deploy/local/infra/keycloak.yaml` 冒頭のコメント（configMapGenerator が作る・realm ConfigMap 直後）・`realms` ボリューム | **直す** |
| `deploy/local/infra/kustomization.yaml`・`deploy/local/infra-persistence/kustomization.yaml` の NOTE | **直す** |
| `deploy/mail-relay/reset-gate.yaml`「realm import と同じ ConfigMap」 | **直す**（宣言の ConfigMap。取り込み元は別の Secret） |
| `deploy/local/README.md` の手動手順 | **直す** |
| `scripts/k8s-local-up.test.js` の #1830「ESO=1 では … reset-gate だけを見る」 | **直す**（3 つとも見る） |
| `.ai-context/adr/IADR-0517` の残余 1・決定 4 の k8s-local-up.sh の行 | **日付つきの追記**（本文は書き換えない。IADR-0518 を指す） |
| `deploy/local/keycloak-setup/*`・`reconcile-realm.js`・`check-realm-copy-drift.js` の「ConfigMap keycloak-realms＝単一情報源」 | **そのまま**（後追い Job の期待値は今も ConfigMap） |
| 既存の試験の `keycloak-realms` の create 行（IADR-0369 節・#438 節） | **そのまま**（ConfigMap は今も作る） |

## 変更するファイル

- 新規: `.ai-context/adr/IADR-0518_realm-import-secret-with-env-client-secrets.md`・本仕様書。
- 変更: `scripts/lib/dev-client-secret-guard.sh`・`scripts/k8s-local-up.sh`・`deploy/local/infra/keycloak.yaml`・`deploy/local/infra/kustomization.yaml`・`deploy/local/infra-persistence/kustomization.yaml`・`deploy/mail-relay/reset-gate.yaml`（コメント）・`deploy/local/README.md`・`scripts/k8s-local-up.test.js`・`scripts/scripts.repo.test.js`（文言の表明の追随があれば）・`docs/operations/paired-secret-rotation-runbook.md`・`docs/security/security.md`・`.ai-context/adr/IADR-0517_…`（追記）・`.ai-context/adr/README.md`。

## 検証の計画

- `node scripts/k8s-local-up.test.js`・`node scripts/keycloak-realm-reconcile.test.js`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`node scripts/reset-gate.test.js`・`node scripts/reset-floor.test.js`。
- `shellcheck -x`（変更したシェル）・`node scripts/check-deploy-manifests.js`（helm / kubectl / kubeconform があれば）・gitleaks（あれば）。
- 文書の検査器一式（trace-blocks・adr-numbering・doc-updated・commit-messages・cross-repo-refs・plan-id-qualification・reading-budget・doc-links・test-spec-coverage・test-traceability・knowledge-graph・realm-constraints）。
- 変異: 差し替えを外す／Secret ではなく ConfigMap へ戻す／一時ファイルの umask を外す／[1/7] の判定を ESO=1 で 1 つに戻す／止めたときの `--check-dev-secrets` の行を消す → 赤くなることを確かめてから戻す。

## 残余

- **`ESO=1` で Vault に回した値が在り、env を与えずに空の PVC から起動する**と、取り込みは宣言の値で作られる（env の値しか知らない）。本作業の判定（決定 4）で dev 以外のクラスタでは env が要るので止まるが、dev 以外で上書き（`ALLOW_DEV_CLIENT_SECRETS=1`）したとき・dev のクラスタでは起き得る。起動の後に `--check-dev-secrets` で検知する（手順書）。
- 取り込み元の Secret は Keycloak の Pod の中では 0644 のファイルとして見える（ConfigMap と同じ既定）。Keycloak のコンテナの利用者・グループを稼働で確かめずに `defaultMode` を絞ると読めなくなる恐れがあるので、絞らない。
- 対象外のクライアント（`bff` 等。IADR-0517 残余 2）は従来どおり宣言の値で取り込む。広げるときは判定器の対象集合へ足せば、差し替えも同じ集合に従う。

## 検証の結果（2026-10-09）

| 実行 | 結果 |
| --- | --- |
| `node scripts/k8s-local-up.test.js` | 285 件 合格（既存 277 ＋ #1834 の 8。#1830 の ESO=1 の 1 件は 3 つとも見る形へ書き換え） |
| `node scripts/keycloak-realm-reconcile.test.js` | 45 件 合格 |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 1016 件 合格（#1817 の文言の表明に取り込み元の 2 行を足した） |
| `node scripts/reset-gate.test.js`・`node scripts/reset-floor.test.js` | 12 件・13 件 合格 |
| `shellcheck -x`（`k8s-local-up.sh`・判定器・`reconcile-realm.sh`・`bootstrap.sh`） | 新規の指摘 0（既存の info 5 件は変更前と同数） |
| `node scripts/check-deploy-manifests.js`（helm・kubectl・kubeconform あり） | OK（chart 1 件 / overlay 17 件。`infra-persistence` の描画でも `realms` は `secret.secretName: keycloak-realm-import`） |
| 文書の検査器 | trace-blocks・adr-numbering・doc-updated・commit-messages・cross-repo-refs・plan-id-qualification・reading-budget・doc-links・test-spec-coverage・test-traceability・knowledge-graph・realm-constraints・image-digests が OK |
| gitleaks | この環境に無い（試験の値は `probe-1834-<client>` の形で、鍵の形を避けた） |

### 変異（壊す → 赤 → 戻す。`k8s-local-up.test.js`）

| 変異 | 結果（赤になった試験） |
| --- | --- |
| M1: 差し替えの置換を外す | 「dev 以外の context で 3 つの env を与える → 取り込み元の secret は env の値」 |
| M2: `keycloak.yaml` の `realms` を ConfigMap `keycloak-realms` へ戻す | 「keycloak.yaml は取り込み元を Secret からマウントし…」 |
| M3: `apply_secret` の `umask 077` を外す | 「一時ファイルは 0600・ディレクトリは 0700…」 |
| M4: [1/7] の判定を ESO=1 で `reset-gate` だけに戻す | 「ESO=1 でも 3 クライアントを見る」 |
| M5: 止めたときの `--check-dev-secrets` の行を消す | 「dev ではない context・env 未設定 → 止まる」（確認の手順を告げていない） |
| M6: `patsub_replacement` を切る行を外す | 「差し替えは値をそのまま置く（& 等）」（`&` が一致部分に化け、JSON が壊れる） |
| M7: 宣言が 1 か所かの検査を外す | 「宣言の secret が 1 か所でない realm では止まる」 |
| M8: JSON を壊す値の検査を外す | 「JSON を壊す値は値を出さずに止まる」 |
| M9: 起動器が差し替えの失敗を無視して続ける（`exit 1` を `true` に） | 「起動器は差し替えに失敗したら止まり、取り込み元も Keycloak も作らない」 |

いずれも戻した後に全件緑であることを上の表で確かめた。
