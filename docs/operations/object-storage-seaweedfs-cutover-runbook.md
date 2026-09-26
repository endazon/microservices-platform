---
title: 運用 Runbook — 稼働クラスタのオブジェクトストレージを MinIO から SeaweedFS へ切り替える
type: runbook
status: draft
author: claude
created: 2026-09-25
updated: 2026-09-26
---
<!-- trace:
ids: [FR-06, FR-12, FR-21, NFR-18]
adrs: [ADR-0014, ADR-0015, ADR-0106, ADR-0107]
iadrs: [IADR-0024, IADR-0093, IADR-0459, IADR-0461]
specs: [20260925_1499_object-storage-seaweedfs, 20260926_issue-1562_bucket-existence-head]
issues: [#457, #1483, #1435, #1499, #1506, #1562, planning#648]
-->

# 運用 Runbook: 稼働クラスタのオブジェクトストレージを MinIO から SeaweedFS へ切り替える

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> リポジトリ（docker-compose・helm・CI）はすでに SeaweedFS へ切り替わっている。**本書は、切替前に作った
> 稼働クラスタ（経路B の k3d）を追随させる手順**であり、オーナーが実行する。

## この手順を実行する条件（いつ走らせるか）

- 稼働クラスタで MinIO の Pod がまだ動いている（ノードにキャッシュ済みのイメージで起動できている）状態で、
  リポジトリの最新 chart を当てる前。
- 🔴 **MinIO のイメージは匿名で取得できなくなっている。** ノードのキャッシュが消えた時点（ノードの作り直し・
  イメージの掃除）で MinIO の Pod は起動できなくなる。先延ばしにできる手順ではない。
- クラスタを作り直す場合（`scripts/k8s-local-up.sh` を新しいクラスタに対して流す）は本書は要らない ——
  最初から SeaweedFS で立つ。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | 対象クラスタの `kubectl`（`microservices-platform` 名前空間の管理）／`ESO=1` の構成なら Vault の root トークン |
| 必要なツール | `kubectl`・`helm`。中身を写す場合だけ S3 互換のクライアント（`aws` CLI・`rclone` 等） |
| 所要時間の目安 | 15〜30 分（写す場合はデータ量に比例） |
| 取得元 | `docker.io/chrislusf/seaweedfs`（digest で固定。匿名で取得できる） |

## 手順

1. **中身を捨ててよいかを決める。** オブジェクトストレージの資産は、再実装版への切替の裁定で「破棄」と決まっている
   （バケット 1・オブジェクト 0 を実測したうえでの判断。切替の移行仕様は `docs/migration/` に置かれる）。
   その後に書かれたオブジェクトを残したいときだけ、次の 2 で数えて 3 で写す。捨てるなら 4 へ進む。
2. **数える（読み取りのみ）。** MinIO の API をポートフォワードし、S3 互換のクライアントで一覧する。

   ```bash
   kubectl -n microservices-platform port-forward svc/minio 9000:9000 &
   AWS_ACCESS_KEY_ID=<minio のアクセスキー> AWS_SECRET_ACCESS_KEY=<シークレット> \
     aws --endpoint-url http://127.0.0.1:9000 s3 ls s3://knowledge-normalized --recursive | wc -l
   ```

3. **写す（残す場合だけ）。** 🔴 **次の 4 の `helm upgrade` は chart から消えた `minio-data` PVC を削除する**
   （＝その時点で MinIO の中身は消える）。**写すなら必ず upgrade の前**に、手元のディスクへ最新版だけを退避する
   （版の履歴は写らない）。`aws s3 sync s3://knowledge-normalized ./knowledge-normalized` 等。
4. **新しい資格情報だけを作る。** Secret 名は `minio-credentials` から **`object-storage-credentials`** へ替わった。
   🔴 **`bootstrap.sh` や `scripts/k8s-local-up.sh` を丸ごと再実行してはならない。** どちらも Vault / Secret の他の値
   （データベース・ブローカ・認証基盤の管理者、OIDC クライアントやサービス間の資格情報）を**開発用の既定値で書き直す**ため、
   稼働中のワークロード（トレーディング PoC を含む）の認証が壊れる。**この 1 つだけを作る。**
   値は MinIO で使っていたものを引き継いでも、新しく決めてもよい（下の例は環境変数から渡す。値を手順書やシェル履歴に残さない）。
   - `ESO=1` の構成（Vault → ExternalSecret で供給している）:

     ```bash
     kubectl -n platform-infra exec -i deploy/vault -- sh -c \
       'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID";
        vault kv put secret/msp/object-storage-credentials accessKey="$1" secretKey="$2"' \
       _ "$OBJECT_STORAGE_ACCESS_KEY" "$OBJECT_STORAGE_SECRET_KEY"
     kubectl apply -f deploy/local/vault/eso/externalsecret-object-storage.yaml
     kubectl -n microservices-platform wait --for=condition=Ready externalsecret/object-storage-credentials --timeout=90s
     ```

   - `ESO` を使っていない構成:

     ```bash
     kubectl -n microservices-platform create secret generic object-storage-credentials \
       --from-literal=accessKey="$OBJECT_STORAGE_ACCESS_KEY" \
       --from-literal=secretKey="$OBJECT_STORAGE_SECRET_KEY" \
       --dry-run=client -o yaml | kubectl apply -f -
     ```

5. **chart だけを当てる。** `scripts/k8s-local-up.sh` は流さず、その段 6 と同じ `helm upgrade` だけを実行する。
   🔴 **先に、いまの release に `--set` で渡されている値を取り出し、同じ値を渡す。** 起動器は opt-in（メッシュの mTLS の方式・
   セルフホスト埋め込み等）を `--set` で重ねており、`values-local.yaml` だけで upgrade するとそれらが既定へ戻る
   （例: STRICT の mTLS が外れる）。`helm get values` は利用者が与えた値（`values-local.yaml` 由来と `--set` 由来の和）を返す:

   ```bash
   helm get values msp -n microservices-platform -o yaml > /tmp/msp-user-values.yaml
   # 中身を目で確かめる（mesh.* / services.*.extraEnv 等）。秘密の値は入らない（chart は Secret 名だけを持つ）。
   helm upgrade --install msp deploy/helm/microservices-platform \
     -n microservices-platform -f deploy/local/values-local.yaml -f /tmp/msp-user-values.yaml
   ```

   後ろの `-f` が優先されるので、稼働中の release の値が `values-local.yaml` を上書きする。ただし **`values-local.yaml` 側で
   意図して変わった値（本変更の `seaweedfs.*` と、撤去した `minio.*`）** まで旧い値で戻さないよう、取り出したファイルから
   `minio:` の節を消してから渡す（`seaweedfs:` の節は旧 release に無いので問題にならない）。

   SeaweedFS の Deployment・Service・PVC（`seaweedfs` / `seaweedfs-data`）が作られ、MinIO のものは消える。
   オブジェクトストレージを使う 7 サービスは参照する Secret 名が変わるので自動で作り直される。
6. **写した中身を戻す（3 を行った場合だけ）。** `kubectl -n microservices-platform port-forward svc/seaweedfs 8333:8333`
   のうえで `aws --endpoint-url http://127.0.0.1:8333 s3 sync ./knowledge-normalized s3://knowledge-normalized`。
   バケットは ConversionService の起動時に作られている（無ければ書き込み時に作られる）。
7. **旧い部品を掃除する。** chart から外れたものと、宣言から外れても稼働環境に残るものを消す。

   ```bash
   # 旧 Secret（ESO=1 の構成では、所有者の ExternalSecret を先に消す —— 残すと Secret が作り直される）
   kubectl -n microservices-platform delete externalsecret minio-credentials minio-oidc --ignore-not-found
   kubectl -n microservices-platform delete secret minio-credentials minio-oidc --ignore-not-found
   # エッジの旧 route（LOCALEDGE=1 / ISTIO=1 の構成）
   kubectl -n microservices-platform delete ingress minio-admin-edge --ignore-not-found
   kubectl -n istio-system delete virtualservice msp-admin-minio --ignore-not-found
   # Vault の旧パス（ESO=1 の構成。版の履歴ごと消す）
   kubectl -n platform-infra exec -i deploy/vault -- sh -c \
     'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID";
      vault kv metadata delete secret/msp/minio-credentials; vault kv metadata delete secret/msp/minio-oidc'
   ```

   - 🔴 **realm の旧 client `minio`（と client ロール `consoleAdmin`・`admin` 利用者への付与）は、realm の宣言から外しても
     稼働中の Keycloak からは消えない**（realm の取り込みも起動器の後追いも、宣言に無いものを消さない）。管理コンソールで
     client `minio` を削除するか、Keycloak の Pod で `kcadm.sh get clients -r platform -q clientId=minio` で id を引いて
     `kcadm.sh delete clients/<id> -r platform` を実行する（client を消すと、その client ロールと付与も一緒に消える）。

## 確認（この手順が成功したと言える条件）

- `kubectl -n microservices-platform get deploy seaweedfs` が `1/1`、`get deploy minio` が NotFound。
- **テレメトリが無効になっている**:
  `kubectl -n microservices-platform get deploy seaweedfs -o jsonpath='{.spec.template.spec.containers[0].args}'`
  に `-master.telemetry=false` が含まれる。
- イメージが digest で固定されている: 同じ Deployment の `image` が `@sha256:` を含む。
- S3 ゲートウェイの管理用 gRPC が認証を要する: 同じ Deployment の `command` が `WEED_JWT_FILER_SIGNING_KEY` を作ってから
  entrypoint を呼んでいる（鍵は起動のたびに作られ、どこにも保存されない）。
- 文書の本文を 1 件登録し、詳細画面で本文が表示される（書き込み・読み取りの往復が通る）。
- ConversionService を起動（再起動）した直後のログに、次の 2 つの警告が**どちらも 1 行も出ていない**。
  起動のたびに 1 回ずつ出ることも「失敗」に数える（回数ではなく有無で判定する）。
  - `Object storage bucket bootstrap failed` —— 起動時のバケットの準備そのものが例外で止まった。
  - `Object storage bucket knowledge-normalized existence is unknown` —— バケットの存在確認（HEAD）が
    在る・無いのどちらとも答えなかった（503・403・接続不能など）。このときバケットは作られず、
    無かった場合は最初の書き込みが作って再試行する。書き込みが通っていても、存在確認が答えない原因
    （ゲートウェイの不調・資格情報の権限）は別に調べる。
  - 在るバケットに対して起動のたびにどちらかが出る場合は、下の分岐表の「起動のたびに警告」の行へ進む。

## 失敗したときの分岐

| 事象 | 見る場所 | 対処 |
| --- | --- | --- |
| `seaweedfs` が `CreateContainerConfigError` | `kubectl describe pod` | Secret `object-storage-credentials` が無い。手順 4 をやり直す（**丸ごとの再実行はしない**） |
| `seaweedfs` が ImagePullBackOff | `kubectl describe pod` | 取得元への到達を確かめる（匿名で取得できるはずである）。社内ミラーを使う構成ならミラーに同じ digest を置く |
| 各サービスの書き込みが 403 | サービスのログ | サーバとクライアントが別の資格情報を読んでいる。両方とも `object-storage-credentials` を読んでいることを確かめ、サービスを作り直す |
| ConversionService の起動のたびに警告（上の確認項目の 2 つ） | ConversionService のログ（警告の行に付く例外と状態コード） | ConversionService のイメージが、存在確認を HEAD で行う版より古くないかを確かめる（古い版は在るバケットにも起動のたびに 503 の警告を出していた。イメージを作り直して再起動する）。新しい版でも出るなら、`existence is unknown` の行の状態コードを見る —— 403 は資格情報の権限、503 はゲートウェイ（`seaweedfs` の Pod のログ）を調べる。書き込み・読み出しが通っていれば、急ぎの対処は要らない |
| MinIO の中身が要ったのに消えた | — | 手順 3 の退避が無ければ戻せない。資産は破棄の裁定の対象であり、再取り込み（データソース同期・変換の再実行）で作り直す |
