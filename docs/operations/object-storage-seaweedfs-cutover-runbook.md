---
title: 運用 Runbook — 稼働クラスタのオブジェクトストレージを MinIO から SeaweedFS へ切り替える
type: runbook
status: draft
author: claude
created: 2026-09-25
updated: 2026-09-25
---
<!-- trace:
ids: [FR-06, FR-12, FR-21, NFR-18]
adrs: [ADR-0014, ADR-0015, ADR-0106, ADR-0107]
iadrs: [IADR-0024, IADR-0093, IADR-0459, IADR-0461]
specs: [20260925_1499_object-storage-seaweedfs]
issues: [#457, #1483, #1435, #1499, #1506, planning#648]
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
4. **新しい資格情報を用意する。** Secret 名は `minio-credentials` から **`object-storage-credentials`** へ替わった。
   - `ESO` を使っていない構成: `scripts/k8s-local-up.sh` を再実行する（Secret を作る。既定値は開発用）。
   - `ESO=1` の構成: `bash deploy/local/vault/eso/bootstrap.sh`（Vault の `secret/msp/object-storage-credentials` を
     seed する）→ `kubectl apply -f deploy/local/vault/eso/externalsecret-object-storage.yaml`。
5. **chart を当てる。** 通常どおり `helm upgrade`（`scripts/k8s-local-up.sh` の段 6）を流す。
   SeaweedFS の Deployment・Service・PVC（`seaweedfs` / `seaweedfs-data`）が作られ、MinIO のものは消える。
   オブジェクトストレージを使う 7 サービスは参照する Secret 名が変わるので自動で作り直される。
6. **写した中身を戻す（3 を行った場合だけ）。** `kubectl -n microservices-platform port-forward svc/seaweedfs 8333:8333`
   のうえで `aws --endpoint-url http://127.0.0.1:8333 s3 sync ./knowledge-normalized s3://knowledge-normalized`。
   バケットは ConversionService の起動時に作られている（無ければ書き込み時に作られる）。
7. **旧い部品を掃除する。**

   ```bash
   # 旧 Secret（ESO=1 の構成では先に ExternalSecret を消す）
   kubectl -n microservices-platform delete externalsecret minio-credentials minio-oidc --ignore-not-found
   kubectl -n microservices-platform delete secret minio-credentials minio-oidc --ignore-not-found
   # エッジの旧 route（LOCALEDGE=1 / ISTIO=1 の構成）
   kubectl -n microservices-platform delete ingress minio-admin-edge --ignore-not-found
   kubectl -n istio-system delete virtualservice msp-admin-minio --ignore-not-found
   ```

   - Vault の旧パス `secret/msp/minio-credentials`・`secret/msp/minio-oidc` は `vault kv metadata delete` で消す。
   - realm の旧 client `minio`（と client ロール `consoleAdmin`）は、realm の宣言から外れても**稼働中の Keycloak からは
     消えない**。管理コンソールか `kcadm.sh delete clients/<id> -r platform` で消す。

## 確認（この手順が成功したと言える条件）

- `kubectl -n microservices-platform get deploy seaweedfs` が `1/1`、`get deploy minio` が NotFound。
- **テレメトリが無効になっている**:
  `kubectl -n microservices-platform get deploy seaweedfs -o jsonpath='{.spec.template.spec.containers[0].args}'`
  に `-master.telemetry=false` が含まれる。
- イメージが digest で固定されている: 同じ Deployment の `image` が `@sha256:` を含む。
- 文書の本文を 1 件登録し、詳細画面で本文が表示される（書き込み・読み取りの往復が通る）。
- ConversionService のログにバケットの作成失敗（`Object storage bucket bootstrap failed`）が繰り返し出ていない。

## 失敗したときの分岐

| 事象 | 見る場所 | 対処 |
| --- | --- | --- |
| `seaweedfs` が `CreateContainerConfigError` | `kubectl describe pod` | Secret `object-storage-credentials` が無い。手順 4 をやり直す |
| `seaweedfs` が ImagePullBackOff | `kubectl describe pod` | 取得元への到達を確かめる（匿名で取得できるはずである）。社内ミラーを使う構成ならミラーに同じ digest を置く |
| 各サービスの書き込みが 403 | サービスのログ | サーバとクライアントが別の資格情報を読んでいる。両方とも `object-storage-credentials` を読んでいることを確かめ、サービスを作り直す |
| MinIO の中身が要ったのに消えた | — | 手順 3 の退避が無ければ戻せない。資産は破棄の裁定の対象であり、再取り込み（データソース同期・変換の再実行）で作り直す |
