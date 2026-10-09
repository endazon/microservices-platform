---
title: 運用 Runbook — 稼働中の経路 B の秘匿管理を Vault から OpenBao へ移す
type: runbook
status: draft
author: claude
created: 2026-10-09
updated: 2026-10-09
---
<!-- trace:
ids: [NFR-18, NFR-21, SC-22]
adrs: [ADR-0132, ADR-0124, ADR-0107, ADR-0112]
iadrs: [IADR-0457, IADR-0471, IADR-0486, IADR-0525]
specs: [20261009_1840_secret-store-openbao]
issues: [#1840]
-->

# 運用 Runbook: 稼働中の経路 B の秘匿管理を Vault から OpenBao へ移す

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 秘匿管理の製品は OpenBao（Vault API 互換・MPL-2.0）へ差し替えた。名前（`deploy/vault`・Service `vault`・Secret `vault-dev-token`・
> `VAULT=1`）は変えていない。本書は**既に永続化した旧 Vault を動かしている経路 B のクラスタ**の移し方である。
>
> 🔴 **値を表示しない。** どの手順も秘密の値・unseal 鍵・root トークンを端末へ出さない。root トークンは Pod の環境変数を
> Pod の中のシェルで読む（ホストのシェルで展開しない。コマンドを単一引用符で囲む）。init のファイル（`/vault/data/.local-dev-init`）は
> 開かない・写さない。
>
> 🔴 **バックアップの秘密鍵（age の identity）はクラスタにもリポジトリにも置かない**（[バックアップの手順書](platform-infra-backup-runbook.md)と同じ）。

## この手順を実行する条件（いつ走らせるか）

次の**すべて**に当てはまるとき。どれかに当てはまらなければ、本書は要らない（起動器 `scripts/k8s-local-up.sh` をそのまま走らせる）。

| 条件 | 確かめ方 |
| --- | --- |
| 稼働中の `deploy/vault` が旧 Vault のイメージである | `kubectl -n platform-infra get deploy vault -o jsonpath='{.spec.template.spec.containers[0].image}'` が `hashicorp/vault` を含む |
| 永続化している（PVC `vault-data` が在る） | `kubectl -n platform-infra get pvc vault-data` |

- 起動器は、この 2 つがそろうと**秘匿管理の入れ替えの前に止まり、本書を名指しする**（データを守るため）。
- `PERSIST=0`（`-dev`・インメモリ）で動かしていたなら移すデータは無い。起動器を走らせれば OpenBao が空から立ち、
  `ESO=1` なら起動器の種まき（`deploy/local/vault/eso/bootstrap.sh`）が入れ直す。
- 移さずに入れ替えた場合も、Pod 内のラッパーは**旧 Vault のデータの上に新しい鍵で初期化しない**（起動を拒んで止まる）。
  その状態からも本書の手順で移せる（`deploy/vault` のイメージは既に OpenBao なので、まず手順 7 の戻し方 (b) で旧 Vault の配備を当て直し、旧 Vault が立ってから手順 1 から行う。OpenBao は起動を拒んでいたので、失う書き込みは無い）。

### なぜデータを移す必要があるか

- OpenBao 2.x は旧 Vault が使っていた **file ストレージを持たない**。旧 Vault の `/vault/data`（`core/`・`logical/`・`sys/`）は
  そのままでは開けない。
- 旧 Vault（1.16.3）の `vault operator migrate` で **file → raft** へ写すと、OpenBao 2.7.1 はその raft を開ける
  （**同じ unseal 鍵で unseal でき、KV の値と版の履歴・policy・認証の設定・固定の root トークンが残る**。手元の実測）。
- 旧 Vault の起動器が API で作った 2 つの audit device（`stdout/`・`otel-collector/`）は、**写す前に外す**。残したまま写すと、
  OpenBao は設定で宣言した同じ名前の device と衝突し、unseal の後の準備に失敗する（`was already created by API`。実測）。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | 名前空間 `platform-infra` の Deployment・Pod・Job の作成と削除、`pods/exec` |
| 必要なツール | `kubectl`・`jq`・リポジトリの作業コピー（OpenBao へ差し替えた版） |
| 所要時間の目安 | 15 分（秘匿管理が止まるのは手順 3〜5 の約 5 分。その間、秘密情報の投入画面 の書き込みと ESO の同期は失敗する。既に同期済みの Secret は残るので、稼働中のサービスは止まらない） |

## 手順

以下、`IN_POD` は Pod の中で root として `vault` を動かすための前置きである（値はホストへ出ない）。

```sh
IN_POD='export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID";'
```

1. **バックアップを 1 回取り、成功を確かめる。** 手順 7 の戻し方の最後の拠り所になる。

   ```sh
   BACKUP_JOB="vault-before-openbao-$(date +%s)"
   kubectl -n platform-infra create job --from=cronjob/platform-backup-vault "$BACKUP_JOB"
   kubectl -n platform-infra wait --for=condition=complete "job/$BACKUP_JOB" --timeout=600s
   ```

   あわせて、移した後に突き合わせる**値を含まない**数を控える（KV の数と、いくつかの KV の現在版）:

   ```sh
   kubectl -n platform-infra exec deploy/vault -- sh -c "$IN_POD"' vault kv list -format=json secret/msp | jq length'
   kubectl -n platform-infra exec deploy/vault -- sh -c "$IN_POD"' vault kv metadata get -format=json secret/msp/llm-provider-credentials | jq .data.current_version'
   ```

2. **旧 Vault の audit device を外す**（API で作られたもの。外した後は手順 3 まで監査が残らないので、続けてすぐ止める）:

   ```sh
   kubectl -n platform-infra exec deploy/vault -- sh -c "$IN_POD"' vault audit disable otel-collector; vault audit disable stdout; vault audit list'
   ```

   `No audit devices are enabled.` と出ればよい。

3. **旧 Vault を止める**（PVC を手放させる）:

   ```sh
   kubectl -n platform-infra scale deploy/vault --replicas=0
   kubectl -n platform-infra wait --for=delete pod -l app=vault --timeout=120s
   ```

4. **旧 Vault のイメージで file → raft を写す。** 写す元（file）は読むだけで、書き換えない（戻し方の元として残る）。
   写す先は `/vault/data/raft`（OpenBao の設定 `deploy/local/vault-persistence/local.hcl` の raft の path と同じ）。
   既に raft が在れば**上書きせずに止める**。イメージは稼働していた Deployment から取る（版を書き写さない）。

   コードブロックは字下げせずに置いてある（写したときにヒアドキュメントの終端 `EOF` が行頭に来るように）。

```sh
OLD_IMAGE="$(kubectl -n platform-infra get deploy vault -o jsonpath='{.spec.template.spec.containers[0].image}')"
case "$OLD_IMAGE" in *hashicorp/vault*) ;; *) echo "旧 Vault のイメージではない: $OLD_IMAGE"; exit 1 ;; esac
cat <<'EOF' | sed "s#__OLD_IMAGE__#$OLD_IMAGE#" | kubectl apply -f -
apiVersion: v1
kind: Pod
metadata:
  name: vault-to-openbao-migrate
  namespace: platform-infra
spec:
  restartPolicy: Never
  securityContext: { runAsUser: 100, runAsGroup: 1000, fsGroup: 1000, fsGroupChangePolicy: OnRootMismatch }
  containers:
    - name: migrate
      image: __OLD_IMAGE__
      command: ["/bin/sh", "-c"]
      args:
        - |
          set -eu
          if [ -f /vault/data/raft/vault.db ]; then echo "raft already exists under /vault/data/raft; refusing to overwrite" >&2; exit 1; fi
          mkdir -p /vault/data/raft
          cat > /tmp/migrate.hcl <<'HCL'
          storage_source "file" {
            path = "/vault/data"
          }
          storage_destination "raft" {
            path    = "/vault/data/raft"
            node_id = "vault-0"
          }
          cluster_addr = "http://127.0.0.1:8201"
          HCL
          vault operator migrate -config=/tmp/migrate.hcl > /tmp/migrate.log 2>&1 || { tail -3 /tmp/migrate.log >&2; exit 1; }
          tail -1 /tmp/migrate.log
      volumeMounts:
        - { name: data, mountPath: /vault/data }
  volumes:
    - name: data
      persistentVolumeClaim: { claimName: vault-data }
EOF
kubectl -n platform-infra wait --for=jsonpath='{.status.phase}'=Succeeded pod/vault-to-openbao-migrate --timeout=300s
kubectl -n platform-infra logs pod/vault-to-openbao-migrate   # 「Success! All of the keys have been migrated.」の 1 行
kubectl -n platform-infra delete pod vault-to-openbao-migrate
```

   - 写しの記録（`/tmp/migrate.log`）はキーの**パス**（ハッシュ化されたものを含む）を並べるだけで値は含まないが、Pod の外へは出さない。

5. **OpenBao の配備を当てる**（作業コピーは OpenBao へ差し替えた版）:

   ```sh
   kubectl apply -k deploy/local/vault-persistence
   kubectl -n platform-infra rollout status deploy/vault --timeout=180s
   ```

   - Pod 内のラッパーは、写した raft を同じ unseal 鍵で unseal し、固定の root トークンがそのまま在ることを確かめる（作り直さない）。
   - 以後は起動器（`scripts/k8s-local-up.sh`）をいつもどおり走らせてよい（`deploy/vault` が OpenBao になったので門は通る）。

6. **確かめる**（下の「確認」）。

7. **戻し方**（確認で問題が出たとき）:
   - (a) **手順 5 の前**（OpenBao を当てる前）なら、旧 Vault をそのまま立て直す:
     `kubectl -n platform-infra scale deploy/vault --replicas=1`。旧 Vault の起動器が audit device を 2 つ作り直す。
     raft の写しは残っても旧 Vault は読まない。やり直すときは、手順 4 が上書きを拒むので、先に raft の写しを消す（下の (c)）。
   - (b) **手順 5 の後**（または移さずに OpenBao を当ててしまった後。`deploy/vault` のイメージが既に OpenBao）なら、
     旧 Vault の配備（差し替えの前のコミットの `deploy/local/vault-persistence`）を当て直す。(a) の scale ではイメージが OpenBao のままなので戻らない。
     旧 Vault は `/vault/data` 直下の file ストレージ（手順 4 で書き換えていない）を読み、raft の写しは読まない。
     差し替えを入れたコミットはリポジトリの履歴から引き、その親を使い捨ての作業ツリーに取り出して当てる:

```sh
SWAP="$(git log --reverse --format=%H -S 'image: openbao/openbao' -- deploy/local/vault/vault-dev.yaml | head -1)"
git log -1 --format='%h %s' "$SWAP"            # 差し替えのコミットであることを目で確かめる
PRE="$(mktemp -d)"
git worktree add --detach "$PRE" "$SWAP^"
grep -n 'image: hashicorp/vault' "$PRE/deploy/local/vault/vault-dev.yaml"   # 旧 Vault のイメージであること
kubectl apply -k "$PRE/deploy/local/vault-persistence"
kubectl -n platform-infra rollout status deploy/vault --timeout=180s
kubectl -n platform-infra logs deploy/vault | grep vault-entrypoint  # initialized (reusing …)・unsealed・ready
git worktree remove "$PRE"
```

     旧 Vault の起動器が audit device を 2 つ作り直す。もう一度移すときは、(c) で raft の写しを消してから手順 1 から行う
     （手順 4 は `deploy/vault` が旧 Vault のイメージに戻っているので通る）。
     🔴 **OpenBao に移した後に入れた値（秘密情報の投入画面 の書き込み等）は失われる。** 入れ直す。
   - (c) raft の写しを消す（やり直す前だけ。**OpenBao で使い始めた後に消すと、その後の書き込みが失われる**）:
     旧 Vault のイメージの使い捨ての Pod（手順 4 と同じ形）で `rm -rf /vault/data/raft` を 1 度だけ走らせる。
   - (d) PVC そのものを失ったときは、[バックアップの手順書](platform-infra-backup-runbook.md) §5 で戻す。
     🔴 **差し替えの前に取った回は file ストレージの写しである**（OpenBao は開けない）。戻した後に本書の手順 3〜5 で移す。

8. **旧 file ストレージの片付け（任意・後日）。** 手順 6 が通り、OpenBao の下でバックアップの回が 1 つ以上成功してから行う。
   OpenBao のイメージの使い捨ての Pod（PVC `vault-data` をマウントし、OpenBao を止めた状態）で
   `rm -rf /vault/data/core /vault/data/logical /vault/data/sys /vault/data/audit` を走らせる。
   🔴 **片付けた後は 7 の (b) で戻せない**（戻すにはバックアップの回が要る）。片付けるまでは旧データもバックアップの写しに入る
   （守りの水準は同じ。同じ unseal 鍵で開く）。

## 確認（この手順が成功したと言える条件）

| 見るもの | 成功の条件 | コマンド |
| --- | --- | --- |
| 製品・ストレージ・封印 | `version` が `2.7.1`、`storage_type` が `raft`、`sealed` が `false` | `kubectl -n platform-infra exec deploy/vault -- sh -c "$IN_POD"' vault status -format=json' \| jq '{version, storage_type, sealed}'` |
| ラッパーの記録 | `initialized (reusing …)`・`unsealed`・`fixed root token present`・`audit device present at stdout/`・`ready` が出て、`not initialized` が**出ない** | `kubectl -n platform-infra logs deploy/vault \| grep vault-entrypoint` |
| データ | 手順 1 で控えた KV の数と現在版が同じ | 手順 1 の 2 つのコマンド |
| 認証の設定 | `kubernetes/`・`oidc/`（OIDC を入れていれば）が在る | `… vault auth list` |
| ESO | `vault-backend` が `Ready=True`、ExternalSecret がすべて `SecretSynced` | `kubectl get clustersecretstore vault-backend`・`kubectl get externalsecret -A` |
| audit | `stdout/` と（collector が動いていれば）`otel-collector/` が在る。`OBSERVABILITY=1` なら Loki の `{job="vault-audit"}` に行が届く | `… vault audit list` |
| 秘密情報の投入画面 | 1 項目を書いて同期される | [live sync の確認の手順書](secret-item-live-sync-check-runbook.md) |

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| 起動器が「稼働中の deploy/vault は旧 Vault …」で止まる | 本書の対象（設計どおりの門） | 本書の手順 1 から |
| OpenBao の Pod のログに `holds Vault file storage that has not been migrated to raft` | 移さずに OpenBao を当てた（ラッパーの門。データは無事） | 7 の (b) で旧 Vault の配備を当て直し（イメージが既に OpenBao なので (a) の scale では戻らない）、手順 1 から |
| OpenBao の Pod のログに `refusing to init over existing keys` | raft が空（または消えた）のに鍵ファイルが在る。新しい鍵で初期化すると旧データの鍵を失うので、ラッパーが止めている | 手順 4 の写しが raft に在るかを確かめる。写していなければ 7 の (b) で旧 Vault に戻して手順 1 から |
| 手順 4 が `raft already exists` で止まる | 前の試行の写しが残っている | 7 の (c) で消してから手順 4 |
| OpenBao が unseal の後に `was already created by API` を出して使えない | 手順 2 を飛ばした | 7 の (b) で旧 Vault に戻し、(c) で写しを消し、手順 2 から |
| `audit device present at stdout/` が出ず `refusing to run Vault without audit` | `local.hcl` の宣言が当たっていない | `kubectl -n platform-infra get configmap vault-local-config -o yaml` に `audit "file" "stdout"` が在るか見る |
| `otel-collector/` が無い | collector が動いていない（設計どおり。標準出力の device で動き続ける） | collector を直してから `kubectl -n platform-infra rollout restart deploy/vault` |

## 記録

- 実施日・実施者・手順 1 で控えた数と確認の結果を、作業の issue に残す（値は書かない）。

## 限界（この手順で担保できないこと）

- **移行の検証は手元の実測（旧 Vault 1.16.3 → OpenBao 2.7.1。コンテナで再現）であり、上流が保証する経路ではない。**
  OpenBao の上流の記載は「Vault 1.14 以前からの移行」を前提にしている。手順 6 の確認で数と版を必ず突き合わせる。
- 手順 2〜5 のあいだ監査は残らない（旧 Vault の audit device を外してから OpenBao が立つまで）。この間に秘匿管理を操作しない。
- 移行は `deploy/local`（ローカル開発・PoC 環境）専用である。本番像の chart には秘匿管理の配備が無い（本番へ足すときは OpenBao で
  新しく立てる）。
