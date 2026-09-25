---
title: IADR-0461 オブジェクトストレージを MinIO から SeaweedFS へ差し替える —— digest 固定・1 プロセスの weed server・テレメトリ無効・Secret 名から製品名を外し・MinIO Console の SSO を撤去する
type: impl-adr
status: Accepted
related_ids:
  - FR-06
  - FR-12
  - FR-21
  - ADR-0014
  - ADR-0015
  - ADR-0106
  - ADR-0107
  - ADR-0057
  - IADR-0024
  - IADR-0093
  - IADR-0296
  - IADR-0303
  - IADR-0414
  - IADR-0459
author: claude
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0106_object-storage-seaweedfs.md (Accepted 2026-09-25)
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md (Accepted 2026-09-25)
  - planning:projects/microservices-platform/07_adr/ADR-0014_object-storage.md
  - planning:projects/microservices-platform/10_feedback/20260925_object-storage-minio-distribution-stopped.md
---

# IADR-0461: オブジェクトストレージを MinIO から SeaweedFS へ差し替える

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: 利用者裁定（planning#648 → 計画 ADR-0106 / ADR-0107）を受けた実装判断（Claude Code 起案）

## 起点・関連

- issue: #1499（差し替え）。原因の CI 失敗は #1483（Integration）・#1435（integration-stack）。前提のレンジ追随は #1496
- 作業仕様書: `.ai-context/specs/20260925_1499_object-storage-seaweedfs.md`
- 計画: ADR-0106（製品を SeaweedFS に。次点 RustFS。決定 4 が受け入れ試験、決定 5 がテレメトリ無効化と digest 固定、
  決定 6 が Console を要求しないこと）／ADR-0107（インフラ製品の選定基準）／ADR-0014（方式。**改めない**）／
  ADR-0015（Superseded by ADR-0106）
- 改める実装 IADR: [IADR-0024](IADR-0024_object-storage-minio-buckets-and-access.md) の**配備の部分**（決定 6）／
  [IADR-0093](IADR-0093_minio-keycloak-oidc.md)（**全体を Superseded**。決定 5）
- 前提として扱う IADR（覆さない）: [IADR-0296](IADR-0296_deletion-propagation-to-object-storage.md)（全版削除）／
  [IADR-0303](IADR-0303_object-storage-bucket-self-heal.md)（バケットの自己修復）／IADR-0290（版の応答）／
  [IADR-0414](IADR-0414_integration-gate-asks-for-services-not-docker.md)（外部供給の口）

## コンテキストと課題

MinIO の公開イメージが匿名で取得できなくなり（quay.io・docker.io とも HTTP 401。本作業でも 2026-09-25 に再現した）、
Integration の `ObjectStorageRoundTripTests` 3 件が pull の `unauthorized` で落ち、integration-stack では MinIO の Pod が
ImagePullBackOff になっていた。計画は製品を SeaweedFS へ差し替えると裁定した（ADR-0106）。アプリは `AWSSDK.S3` の
S3 互換 API に閉じており（ADR-0106 決定 3）、**差し替えは配備・試験・名前に閉じる**。決めるのは次の 8 点である。

## 決定

### 決定 1: イメージは `docker.io/chrislusf/seaweedfs:4.47@sha256:ce9e796f…` に固定する

- 2026-09-25 時点の最新リリース 4.47（GitHub release・2026-09-14）。**digest は multi-arch の image index のもの**
  （amd64 / arm64 / arm/v7 / 386）。Docker Hub の registry API へ匿名トークンで照会し、**tag・digest 指定の manifest と
  amd64 の config blob の取得がいずれも HTTP 200** であることを確かめた（＝匿名で pull できる。ADR-0107 決定 3）。
- config の label は `org.opencontainers.image.licenses=Apache-2.0`・`revision=c5073360007d28385a33426a42ac3e4ec504c5a3`。
  以下のソースの確認はこの revision で行った。
- 参照は 3 か所（`deploy/docker-compose.yml`・helm `values.yaml` の `seaweedfs.{registry,image,tag,digest}`・
  `Knowledge.IntegrationTests` の `SeaweedFsContainer.Image`）。**一致は `SeaweedFsContainerDefinitionTests` が PR で突き合わせる**。
- Harbor への mirror（ADR-0107 決定 3）は Harbor の配備後に行う（未配備。フォローアップ）。

### 決定 2: 起動形は 1 プロセスの `weed server`、外へ開くのは S3 の 1 口だけ

```
weed server -dir=/data                         # -dir はイメージの entrypoint が足す
  -ip=127.0.0.1 -ip.bind=127.0.0.1             # master / volume / filer は loopback だけで待ち受ける
  -s3 -s3.ip.bind=0.0.0.0 -s3.port=8333        # S3 ゲートウェイだけを外へ開く
  -s3.port.iceberg=0 -s3.port.lance=0          # 使わない付属の口（Iceberg REST・Lance）は閉じる
  -master.telemetry=false                      # 決定 4
```

- **根拠（ソース）**: `weed/command/server.go` —— `-s3` を立てると filer も立つ（249〜251 行）、`-s3.ip.bind` が
  空なら `-ip.bind` を継ぐ（301〜303 行）、`-s3.port.iceberg` / `-s3.port.lance` は「0 で無効」（167〜168 行）。
  `-ip.bind` は外向きの接続元アドレスにも使われる（287 行 `SetOutboundLocalIP`）。
- **ヘルスは S3 ポートの `/healthz`**（`weed/s3api/s3api_server.go` 769〜770 行。待ち受けていれば 200 を返す）。
  MinIO の `/minio/health/live` / `/ready` の置き換え。compose はイメージに入っている `curl` で叩く。
- **資格情報は環境変数 `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY`**。SeaweedFS はこれを静的な管理者 ID として読み、
  ID が 1 つでもあれば認証を有効にする（`weed/s3api/auth_credentials.go` 393〜410 行・519 行〜）。
  🔴 **未設定だと匿名で全許可になる** —— helm の `secretKeyRef` を optional にしてはならない。
- helm の Deployment は `strategy: Recreate`。1 つの PVC（RWO）上の filer の leveldb を新旧 2 つの Pod が同時に開けないため。
- **パス形式・us-east-1**: 実装は `ForcePathStyle=true` と `AuthenticationRegion=us-east-1` で繋ぐ（変更なし）。
  使う S3 機能（PutObject / GetObject / CreateBucket / GetBucketAcl〔`DoesS3BucketExistV2Async`〕/ Put・GetBucketVersioning /
  ListObjectVersions / versionId 付き DeleteObject）の経路は `s3api_server.go` にある（ACL は 898 行、versioning は 946〜947 行、
  ListObjectVersions は 985 行）。**実機での成立は決定 7 の受け入れ試験で確かめる**（計画 ADR-0106 の着手可否の注記）。

### 決定 3: 名前 —— サーバは製品名、資格情報は製品名を持たない

| 対象 | 旧 | 新 | 理由 |
| --- | --- | --- | --- |
| Deployment / Service / PVC | `minio` / `minio-data` | `seaweedfs` / `seaweedfs-data` | 他のインフラ（postgres・qdrant・wikijs・vault）と同じく現物の名前で呼ぶ（計画 ADR-0106 決定 7「現物を示すものは製品名」） |
| 接続先 | `http://minio:9000` | `http://seaweedfs:8333` | S3 ゲートウェイの既定ポート |
| Secret（キー `accessKey`/`secretKey` は不変） | `minio-credentials` | `object-storage-credentials` | **サーバとクライアント（書き込み・読み取りの 7 サービス）が同じ Secret を読む**。次に製品が替わっても名前を変えずに済むよう製品名を外す（ADR-0106 決定 7 の要求文と同じ理由） |
| Vault パス / ExternalSecret | `secret/msp/minio-credentials` / `externalsecret-minio.yaml` | `secret/msp/object-storage-credentials` / `externalsecret-object-storage.yaml` | 同上。SC-22 の対象外リスト（`sc22-secret-items.json` の `excluded[]`）も追随 |
| env（compose / k8s-local-up / bootstrap） | `MINIO_ROOT_USER` / `MINIO_ACCESS_KEY` 等 | `OBJECT_STORAGE_ACCESS_KEY` / `OBJECT_STORAGE_SECRET_KEY` | 同上 |
| dev 既定の資格情報 | `minioadmin` / `minioadmin` | `objectstorage-dev` / `objectstorage-dev-secret` | 製品固有の既定名を持ち越さない |
| 試験の外部供給の口 | `PLATFORM_TEST_MINIO` / `MinioEndpoint` | `PLATFORM_TEST_OBJECT_STORAGE` / `ObjectStorageEndpoint` | 同上（IADR-0414 の口そのものは不変） |

### 決定 4: テレメトリは 3 つの定義すべてで無効化し、PR の段で機械的に止める

- **SeaweedFS はテレメトリが既定で有効**である（`server.go` 110〜111 行 `master.telemetry` の既定値 `true`、送信先
  `https://telemetry.seaweedfs.com/api/collect`。`master.go` 110〜111 行の `-telemetry` も同じ）。計画 ADR-0106 実測 8 と一致した。
- compose・helm・試験のコンテナ定義の 3 つすべてに `-master.telemetry=false` を書く。**配備と同時に効く**（暫定手段は要らない。ADR-0106 決定 5）。
- `SeaweedFsContainerDefinitionTests`（コンテナ不要・PR の ci.yml で走る）が 3 つの定義のフラグとイメージ参照を突き合わせる。
- 他の既定の外部通信は見つからなかった。GitHub API を叩くのは手動の `weed update` サブコマンド（`weed/command/update.go`）だけで、
  サーバの起動では呼ばれない（コード検索 `api.github.com repo:seaweedfs/seaweedfs` の結果は `install.sh`・`update.go`・
  CI 用スクリプトの 3 件）。加えて決定 2 の `-ip.bind=127.0.0.1` により外向き接続の接続元が loopback に縛られる。

### 決定 5: MinIO Console の SSO（IADR-0093）を撤去する —— SeaweedFS の管理 UI も外へ出さない

- 計画は Console を要求しない（ADR-0106 決定 6）。SeaweedFS に Console の OIDC は無い。使わない confidential client と
  その dev secret を realm に残すのは攻撃面を残すだけである。
- 撤去したもの: realm の `minio` client・client ロール `consoleAdmin`・`admin` 利用者への付与／`minio-oidc` の Secret・
  ExternalSecret・Vault の seed／values-local の `minio.oidc`／エッジの `admin-ingress-minio.yaml`・Istio の
  `msp-admin-minio`・証明書の `minio.localhost`／`deploy/local/minio-oidc/`（MinIO ポリシーと `mc` 手順）／
  ツール側 OIDC 検証器の `minio`（7 → 6 クライアント）／realm 検査器の必須 URL 表の `minio`（7 → 6）。
- SeaweedFS の master（9333）・filer（8888）の UI は決定 2 で loopback にしか居ない。エッジにも Service にも出さない。
- **IADR-0093 は Superseded とする**（本 IADR が後継）。

### 決定 6: IADR-0024 の配備の部分を改める（S3 API の使い方は改めない）

- 改めるのは IADR-0024「決定」の**配備**（製品・Secret 名）と**バックアップ・保持方針**の方式例（`mc mirror` →
  S3 互換の同期ツール〔`aws s3 sync` 等〕でのバケット複製／ボリュームスナップショット）の 2 項目だけである。
- **参照 URI・バケット／キー設計・バージョニング・アクセス制御・共有クライアントは改めない**（ADR-0106 決定 3）。

### 決定 7: 受け入れ試験（ADR-0106 決定 4）は汎用コンテナで組み、Integration で走らせる

- Testcontainers の MinIO モジュールは上流で削除された（testcontainers/testcontainers-dotnet#1769）ので、`Testcontainers.Minio` を撤去し、
  汎用 `ContainerBuilder` で決定 1・2 と同じイメージ・引数のコンテナを起こす（`SeaweedFsContainer`）。
- 待ち合わせは `/healthz` に加え、**別バケットへの書き込みが通るまで**（上限 90 秒）。`/healthz` は S3 が待ち受けた時点で 200 を
  返すが、volume サーバの master への登録は非同期であり、起動順の揺れで最初の書き込みが落ちると S3 機能の欠如と区別できないため。
  受け入れの対象（`EnsureBucketAsync` と試験バケットの操作）には触れない。
- 3 件は `Category=Integration` であり PR の ci.yml では走らない。**本 PR では `workflow_dispatch` で Integration をブランチに対して走らせて
  結果を得る**（結果は作業仕様書と PR 本文に記録し、計画へ環流する —— 通れば ADR-0106 への記録〔例外 4〕、落ちれば次点 RustFS）。

### 決定 8: データ移行はしない（稼働クラスタの切替はオーナーの手順）

- リポジトリ・CI にはオブジェクトストレージの永続データが無い（CI は毎回作り直す）。
- 稼働クラスタの資産は #457 の裁定（2026-08-16。バケット 1・オブジェクト 0 を実測したうえで「破棄」）と、切替の移行仕様
  （IADR-0459・#1506。MinIO の PVC ごと作り直す）で**破棄と決まっている**。本 PR は稼働クラスタに触れない。
- 🔴 **helm upgrade は chart から消えた `minio-data` PVC を削除する**（＝その時点で MinIO の中身は消える）。残したいものがあるなら
  upgrade の前に写す。手順は `docs/operations/object-storage-seaweedfs-cutover-runbook.md`。

## 検討した選択肢

| 論点 | 選択肢 | 採否 |
| --- | --- | --- |
| 起動形 | `weed mini`（イメージの既定 CMD）／`weed server -s3`（**採用**）／master・volume・filer・s3 を別 Pod | `mini` は開発向けの一括起動であり、計画 ADR-0106 決定 5 が無効化フラグを名指しした形（`weed master` / `weed server`）ではない（`mini` もテレメトリの送信経路を持つ ——`weed/command/mini.go` が送信先を参照している）。`server` は計画が名指ししたフラグ（`-master.telemetry=false`）がそのまま使える。別 Pod は単一ホストの k3s に重い |
| Secret 名 | `minio-credentials` のまま／`seaweedfs-credentials`／`object-storage-credentials`（**採用**） | 据え置きは現物と名前が食い違い続ける。製品名は次の差し替えで再び改名が要る |
| Console の SSO | 残す（死んだ client を保持）／**撤去（採用）** | 計画は要求せず、SeaweedFS に対応物が無い |
| digest | 各アーキテクチャの manifest digest／**image index の digest（採用）** | index なら amd64（CI）と arm64 の両方で同じ参照が効く |

## 結果

- 良い影響: Integration・integration-stack がイメージの取得で落ちなくなる見込みが立つ。上流の保守が続く製品へ移る。
  テレメトリの無効化とイメージの固定が PR の段で機械的に守られる。
- 悪い影響・トレードオフ: 稼働クラスタの切替にオーナーの手順が要る（Secret の改名・Vault の旧パスと realm の旧 client の掃除）。
  digest 固定は上流の修正版の取り込みに手間を足す（3 か所を対で上げる。検査が突き合わせる）。
- フォローアップ:
  1. 受け入れ試験の結果の環流（計画 ADR-0106 への記録）。
  2. Harbor 配備後の mirror（ADR-0107 決定 3）。
  3. 並行 PR の追随: 秘密情報のローテーション Runbook（#1495）の MinIO の行、切替の移行仕様（IADR-0459・#1506）の MinIO の実測・削除手順。
  4. `src/ai-stock-trading`（AST）の文書に残る MinIO の記述は AST リポジトリの管轄（本 PR の射程外）。

## 関連

- Supersedes: [IADR-0093](IADR-0093_minio-keycloak-oidc.md)（MinIO Console の SSO）
- 部分改定: [IADR-0024](IADR-0024_object-storage-minio-buckets-and-access.md)（配備の部分のみ）
- Superseded by: なし
