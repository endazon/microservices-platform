---
title: 作業仕様書 — オブジェクトストレージを MinIO から SeaweedFS へ差し替える（#1499・計画 ADR-0106 の受け入れ試験・配備・IADR-0461）
type: spec
status: done
related_ids:
  - FR-06
  - FR-12
  - FR-21
  - ADR-0014
  - ADR-0015
  - ADR-0106
  - ADR-0107
  - IADR-0024
  - IADR-0093
  - IADR-0461
author: claude
created: 2026-09-25
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0106_object-storage-seaweedfs.md (Accepted 2026-09-25)
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md (Accepted 2026-09-25)
  - planning:projects/microservices-platform/10_feedback/20260925_object-storage-minio-distribution-stopped.md
related_specs:
  - 20260925_1496_plan-adr-range-0107
  - 20260915_issue-1434_minio-image-registry
issue: "#1499"
---

# 作業仕様書 — オブジェクトストレージを MinIO から SeaweedFS へ差し替える

## 目的と射程

MinIO の公開イメージが匿名で取得できなくなり、Integration（#1483。`ObjectStorageRoundTripTests` 3 件の pull が
`unauthorized`）と integration-stack（#1435。MinIO の Pod が ImagePullBackOff）が落ち続けている。計画は製品を
SeaweedFS へ差し替えると裁定した（planning#648 → ADR-0106。次点 RustFS。選定基準は ADR-0107）。

**射程**（裁定コメントの実装側 1〜4）:

1. 受け入れ試験（ADR-0106 決定 4）: digest 固定の実イメージで `ObjectStorageRoundTripTests` 3 件と `EnsureBucketAsync`。
   `MinioBuilder`（上流で削除）を汎用 `ContainerBuilder` へ組み替える。**本ホストに Docker は無い**ため実走は CI
   （Integration を `workflow_dispatch` でブランチに対して走らせる）。
2. 配備の差し替え（docker-compose・helm・ヘルスチェック・digest 固定・テレメトリ無効・資格情報の配線）。
3. 新 IADR（**IADR-0461**。予約番号 0466 は欠番を作るため使わない。起案時は 0464 を取ったが、develop に 0458（#1505）・0459（#1506）が
   入り、0460 を #1511 が使うため、次の連番 0461 へ改番した〔2026-09-26〕。改番に伴い、件名に旧番号を持つコミットを残さないよう PR を出し直した）。IADR-0024 の配備の部分と IADR-0093 の改定、データ移行の要否。
4. MinIO を指すスクリプト・文書の追随。

**射程外**: 稼働クラスタへの適用（オーナー手順として Runbook に書く）・AST リポジトリの文書・
アプリの S3 API の使い方（ADR-0106 決定 3 で不変）。

## 計画の確認（隣接クローン `origin/main` = `6d88ca2`）

- ADR-0106 決定 1〜7（製品・次点・方式不変・受け入れ試験・外部通信の無効化と digest・Console 不要・名前）と着手可否の注記
  （覆るのは決定 1 だけ。実装は直ちに着手してよい）。ADR-0107 決定 3（配布の持続性・digest 固定・Harbor mirror）・決定 4（外部通信の無効化）。
- 完了記録 `20260925_object-storage-minio-distribution-stopped.md` の「実装側の残作業」と planning#648 の裁定コメントを読んだ。

## 実測（2026-09-25）

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | `quay.io/minio/minio:RELEASE.2025-04-08T15-41-24Z` と `docker.io/minio/minio` の manifest は匿名で 401 | registry API へ `curl -I`（匿名トークン付き） |
| 2 | `chrislusf/seaweedfs:4.47`（2026-09-14 リリース）の image index digest は `sha256:ce9e796f…882`。tag 指定・digest 指定の manifest、amd64 の config blob の取得がいずれも 200 | Docker Hub の tags API と registry API（匿名トークン） |
| 3 | config: `licenses=Apache-2.0`・`revision=c5073360007d28385a33426a42ac3e4ec504c5a3`・entrypoint `/entrypoint.sh`・既定 CMD `mini -dir=/data`・`curl` 同梱・`seaweed` 利用者へ su-exec | config blob と `docker/entrypoint.sh`（同 revision） |
| 4 | `weed server` の `-master.telemetry` 既定値は **true**（送信先 telemetry.seaweedfs.com） | `weed/command/server.go` 110〜111 行・`master.go` 110〜111 行（同 revision） |
| 5 | S3 の `/healthz`・`/status` は待ち受けていれば 200 | `weed/s3api/s3api_server.go` 769〜770 行・`s3api_status_handlers.go` |
| 6 | `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` で静的な管理者 ID が作られ、ID が 1 つでもあれば認証が有効 | `weed/s3api/auth_credentials.go` 393〜410・519〜 行 |
| 7 | GetBucketAcl・Put/GetBucketVersioning・ListObjectVersions の経路がある | `s3api_server.go` 898・946〜947・985 行 |
| 8 | Testcontainers 4.12.0 の `DockerImage` は digest を解釈する（`Digest` プロパティ）。`ContainerBuilder(string image)` がある | ローカル NuGet キャッシュの XML ドキュメント＋本 PR の `SeaweedFsContainerDefinitionTests`（ローカルで緑） |
| 9 | 稼働クラスタの資産は #457 の裁定で破棄（バケット 1・オブジェクト 0）。切替の移行仕様（IADR-0459・#1506）は MinIO の PVC ごと作り直す | #457 のコメント・#1506 |

## 母集合の引き方（規則 1〜6・9・10）

**軸 1（誤りの側の語＝製品名）**: `git grep -il minio -- . ':!src/ai-stock-trading' ':!.ai-context/specs' ':!.ai-context/superpowers'`
→ 着手時 **136 ファイル**（issue の「約 89」は転記しない）。

**軸 2（計画 ADR の引用）**: `git grep -n "ADR-0015"`（`IADR-0015` を除く）→ live な文書・コード 44 行。#580 の書式
（旧 ID を残し `（Superseded by ADR-0106）` を併記。trace ブロックの ID リストは `ADR-0106` を項目として併記）で追随した。

**軸 3（資源名・ポート・資格情報）**: `minio-data|minio-credentials|minio-oidc|app: minio|minio:9000|svc/minio`・
`:900[01]|900[01]`・`minioadmin|MINIO_|PLATFORM_TEST_MINIO|MinioEndpoint`・`quay.io`・`mc admin|mc alias`・`consoleAdmin`。
軸 1 に無い新規のヒットは realm JSON の `http://localhost:9001`（軸 1 と同じファイル内）と `RequiredServices.cs` の `http://localhost:9000`（同）。

**軸 4（並行 PR の変更ファイル）**: #1490・#1491・#1493・#1494・#1495・#1501・#1503・#1504（→ #1506）の `gh pr diff --name-only` と突き合わせた（後述「並行 PR との交差」）。

### 追随したもの（分類）

| 分類 | ファイル |
| --- | --- |
| 受け入れ試験 | `Knowledge.IntegrationTests`（`ObjectStorageRoundTripTests`・新設 `Fixtures/SeaweedFsContainer.cs`・新設 `Storage/SeaweedFsContainerDefinitionTests.cs`・`ExternalEndpoints.cs`・`RequiredServices.cs`・`BrokerRequired.cs`・csproj）／`src/Directory.Packages.props`（`Testcontainers.Minio` 撤去） |
| 配備 | `deploy/docker-compose.yml`／helm `templates/minio.yaml` → `templates/seaweedfs.yaml`・`values.yaml`・`deployment.yaml`・`embedding.yaml`・`frontend.yaml`（コメント）／`deploy/local/values-local.yaml` |
| 資格情報 | `deploy/local/vault/eso/`（`externalsecret-minio.yaml` → `externalsecret-object-storage.yaml`・`externalsecret-minio-oidc.yaml` 削除・`bootstrap.sh`・`README.md`・`externalsecret-postgres-app.yaml` のコメント）／`deploy/bootstrap/sc22-secret-items.json`（2 行）／`scripts/k8s-local-up.sh`・`k8s-local-up.test.js`／`deploy/local/README.md`（1 行） |
| Console の SSO 撤去 | realm JSON（`minio` client・client ロール・admin への付与）／`deploy/local/edge/`（`admin-ingress-minio.yaml` 削除・kustomization・証明書・README・wiki の Ingress のコメント）／`deploy/local/edge-istio/`（VirtualService・証明書・README・CoreDNS のコメント）／`deploy/istio/`（2 ファイルのコメント）／`deploy/local/minio-oidc/` 削除／`deploy/local/aliases`・`infra/keycloak.yaml`・`vault/oidc/README.md`・`wiki-oidc/README.md` のコメント／`scripts/check-realm-constraints.js`・`lib/tool-oidc-login.js`・`verify-tool-oidc-logins.sh`・`scripts.repo.test.js`・`check-stack-ready.js`（コメント）・`seed-abac-policies.js`（コメント）・`check-bff-downstreams.js`（自己試験のフィクスチャ） |
| コード内コメント・単体試験の値 | knowledge 14 ファイル・platform 14 ファイル（`ADR-0015（Superseded by ADR-0106）` の併記・製品名・`http://seaweedfs:8333`） |
| 文書 | `README.md`（構成図）・`docs/how-to/`（2）・`docs/operations/`（`operations.md`・`local-sso-recovery-runbook.md`・新設 `object-storage-seaweedfs-cutover-runbook.md`）・`docs/security/security.md`・`docs/screens/SC-13_login.md`・`docs/tech/`（3）・`docs/tests/`（4。うち 2 は trace ブロックへ `ADR-0106` を併記するだけ）・`docs/data/wiki-page-sync.md`・`scripts/README.md`（3 行）・`.github/workflows/ci.yml`（コメント 1 行） |
| IADR | 新設 IADR-0461／IADR-0093（Superseded）／IADR-0024（配備の部分の追記）／索引 |
| 凍結仕様書の frontmatter（例外） | `.ai-context/specs/20260721_issue-353_minio-keycloak-oidc.md` の `related_specs` のうち、撤去したファイル（helm の `templates/minio.yaml`・`deploy/local/minio-oidc/README.md`）を指す 2 本を、`［2026-09-25 追記 / #1499］` の YAML コメントへ置き換えた。**本文は触っていない**。残すと `check-doc-links` がリンク切れとして落ちる（ファイルを消す以上、避けられない）。経過追記の書式（traceability.repo.md「凍結の射程」）に揃えた |

### 除外したもの（理由）

| 除外 | 理由 |
| --- | --- |
| `.ai-context/specs/`・`.ai-context/superpowers/` | 確定済みの凍結記録（traceability.repo.md） |
| `.ai-context/adr/` の IADR-0024・0093 以外（44 ファイル） | 決定時点の記録。MinIO の名は当時の事実であり、決定を覆していない（IADR-0097・0098・0103 等は Secret 名・client 名を当時の値で引く。現行の正は IADR-0461 が持つ） |
| `CHANGELOG.md` | 生成物 |
| `src/coverage-floor.json` の 2 行 | 床を置いた当時の実測の経緯（「MinIO 不在のため失敗」「MinIO も含めて成功」）であり、現行の構成を述べていない |
| `scripts/check-stack-ready.js` 12 行 | 当時の実測（run 32554340800）の記録 |
| `scripts/check-secret-injected-options.js` 199 行 | 自己試験の合成フィクスチャの文字列で意味を持たない |
| 各所の「IADR-0461 で撤去」「旧名 minio-credentials」の注記 | 追随の注記そのもの（意図的に残した） |
| `src/ai-stock-trading`（AST） | 別リポジトリ（AST の `docs/tech/system-architecture.md` に MinIO の記述がある。AST の管轄） |

**規則 10（この変更で新たに誤りになる自分の記述）**: 「7 クライアント」「管理ツール 7 件」「mesh 内の 4 Service」「MSP ns 常時 18 本」
「PASS 15（段 15/15）」「dnsNames 8 件」を数え直した（6・6・3・17・13・7）。数えた根拠はそれぞれの列挙（`msp_es` の語数・
`TOOLS.length`・`ADMIN_ING_FILES` の Ingress 数）。

## 決定（要約。正は IADR-0461）

1. イメージ `docker.io/chrislusf/seaweedfs:4.47@sha256:ce9e…882`（3 か所。`SeaweedFsContainerDefinitionTests` が突き合わせる）。
2. 起動形 `weed server -ip=127.0.0.1 -ip.bind=127.0.0.1 -s3 -s3.ip.bind=0.0.0.0 -s3.port=8333 -s3.port.iceberg=0 -s3.port.lance=0 -master.telemetry=false`。
3. Service `seaweedfs:8333`・PVC `seaweedfs-data`・Secret `object-storage-credentials`・Vault `msp/object-storage-credentials`・env `OBJECT_STORAGE_*`・試験の口 `PLATFORM_TEST_OBJECT_STORAGE`。
4. ヘルスは S3 の `/healthz`。helm は `strategy: Recreate`。
5. MinIO Console の SSO（IADR-0093）を撤去。IADR-0093 は Superseded。
6. データ移行なし（稼働クラスタは #457 / IADR-0459 で破棄。オーナー手順は Runbook）。

## 受け入れ基準

- [x] **Integration（`workflow_dispatch`・本ブランチ）で `ObjectStorageRoundTripTests` 3 件が Passed**（Skipped ではない）—— 2026-09-25 の初回は全版削除の 1 件が Failed、削除手順の修正後（2026-09-26・run 36157636410）に 3 件とも Passed
- [x] `SeaweedFsContainerDefinitionTests` 4 件がローカルで緑（digest の解釈・テレメトリ無効・compose / helm と同じ参照）
- [x] `helm lint`（既定・values-local）・`helm template` が通り、描画結果に MinIO のイメージ・`/minio/health/*`・`minio-credentials` が無い
- [x] 両ユニットの `dotnet build`・`dotnet test`（`Category!=Integration`）・`dotnet format --verify-no-changes` が緑（2026-09-26 に develop を取り込んだ後、全アセンブリ成功。取り込み前に出ていた Bff の 1 件は本 PR 以前の JSON でも再現しており、develop 側の変更で解消した）
- [x] `node scripts/scripts.test.js`（800）・`k8s-local-up.test.js`（181）・`keycloak-realm-reconcile.test.js`（34）・文書検査（trace ブロック・リンク・知識グラフ・IADR 採番）が緑
- [x] IADR-0461 に製品・起動形・名前・テレメトリ・Console・IADR-0024 の改定・データ移行・オーナー手順への参照がある

## 受け入れ試験の結果（Integration・`workflow_dispatch`・run 36147130563・2026-09-25）

| 試験 | 結果 |
| --- | --- |
| `SeaweedFsContainerDefinitionTests` 4 件 | Passed |
| `ObjectStorageRoundTripTests.Persists_and_reads_markdown_and_asset`（`EnsureBucketAsync` を含む） | **Passed**（12 s。イメージの取得・起動・バケット作成・版管理の有効化・保存・読み出しが通った） |
| `ObjectStorageRoundTripTests.Reconversion_overwrites_same_key_idempotently` | **Passed** |
| `ObjectStorageRoundTripTests.Delete_removes_every_version` | 🔴 **Failed** —— 削除後の `ListVersions` に **delete marker が 1 つ残った**（`IsDeleteMarker=True`・`IsLatest=True`・作成時刻は削除の直後） |

**原因の切り分け（ソースで確認）**: `S3ObjectStorageClient.DeleteAsync` は全版を versionId 付きで消した**後に、
versionId 無しの削除を 1 回撃つ**（IADR-0296 決定 1 の最後の項「冪等なので害が無い」）。**バージョニングが有効なバケットでの
versionId 無しの削除は、対象が無くても delete marker を作る** —— SeaweedFS の `deleteVersionedObject`
（`weed/s3api/s3api_object_handlers_delete.go` 153〜161 行。`versionId == ""` かつ `VersioningEnabled` なら無条件に
`createDeleteMarker`）がそう実装しており、これは AWS S3 の意味論と同じである。MinIO では marker が残らなかったため、
IADR-0296 の前提「害が無い」は MinIO の振る舞いに依存していた。**SeaweedFS に機能が欠けているのではなく、
実装側の削除手順が MinIO 固有の挙動を前提にしていた。**

**判断は利用者へ返す**（計画 ADR-0106 の着手可否の注記: 覆す判断は利用者が行う）。選択肢は IADR-0461 には書かず PR で示す。

［2026-09-26 追記 / #1499］**利用者裁定: 案 A（実装の削除手順を直し、SeaweedFS を維持する）。**

- 修正: `S3ObjectStorageClient.DeleteAsync` の versionId 無しの削除を、全版削除の「後」から版の列挙の「前」へ移した
  （理由と他の直し方の比較は IADR-0461 決定 9。IADR-0296 決定 1 へ日付付きの追記）。
- 試験: 新設 `S3ObjectStorageClientDeleteMarkerSemanticsTests`（Docker 不要・状態つきの偽 S3）。
  **修正前のコードで 5 件中 4 件が落ち**（版管理が有効・対象なし・停止・前方一致の隣のキー。無効のケースだけ通る）、修正後は 5 件とも通る。
  既存の `S3ObjectStorageClientDeleteTests` 7 件も通る。受け入れ試験 `ObjectStorageRoundTripTests` の判定は変えていない。
- 計画へ環流する（ADR-0106 決定 3 の「配備・試験・名前に閉じる」を越えてアプリのコードを直したため）。起票はコーディネータ。
- **修正後の受け入れ試験（Integration・`workflow_dispatch`・run 36157636410・head `3a8b4234`）: 3 件とも Passed**
  （`Persists_and_reads_markdown_and_asset` 13 s／`Delete_removes_every_version` 9 s／`Reconversion_overwrites_same_key_idempotently` 9 s）。
  同 run の赤は AST の `MarketMonitorService.Tests.RiskManagementGrpcTests.T_10_1057…` だけで、本 PR と無関係
  （MSP の submodule の pin が AST 側の修正より前。platform / knowledge の両ユニットは成功）。

## ［2026-09-26 追記 / #1499］監査の指摘（NO-GO 1 件）への対応

**指摘**: S3 ゲートウェイは HTTP（8333）に加えて gRPC（既定 10000 + 8333 ＝ 18333）を同じ `-s3.ip.bind`（0.0.0.0）で開く。
その管理用 RPC（`PutIdentity` 等）は署名鍵が空だと認証を素通りし、Pod IP へ届く相手が S3 の管理者 ID を足せた（ABAC 迂回）。
起案時の「外へ開くのは S3 の 1 口だけ」は誤りだった（本仕様書・IADR-0461 決定 2 を訂正）。

**対応**（正は IADR-0461 決定 10）:

1. 口を閉じる手段は 4.47 に無い（`weed/command/s3.go` 438 行: gRPC と HTTP は同じ bindIp。無効化・別 bind の指定なし・
   0 は 10000 + port へ置換・負の値は起動失敗）ことをソースで確かめた。
2. **起動のたびに乱数の署名鍵**（`WEED_JWT_FILER_SIGNING_KEY`。viper の環境変数。`weed/util/config.go` 120〜122 行）を
   コンテナ内で作ってから entrypoint を呼ぶ（compose・helm・試験のコンテナで同じ文字列）。鍵を使うのは同じプロセスの filer と
   S3 だけなので Secret にしない（理由は IADR-0461 決定 10）。gRPC の口は `-s3.port.grpc=18333` と明示した。
3. **NetworkPolicy** `allow-seaweedfs-s3-only`（ingress は同 Namespace からの TCP 8333 のみ）。`allow-intra-namespace` は
   SeaweedFS を `app NotIn [seaweedfs]` で外した（許可の和で全ポートが開くのを防ぐ）。
4. `SeaweedFsContainerDefinitionTests` を「引数の全体」「起動スクリプト」「NetworkPolicy」まで広げた（7 件）。
   **変異試験**: compose / helm から gRPC の行を消す・allow-intra-namespace の除外を外す、の 3 通りでそれぞれ 1 件ずつ落ちることを確かめた。
5. 切替 Runbook の手順 4・5 を「丸ごとの再実行をしない」形へ直した（`bootstrap.sh` / `k8s-local-up.sh` は他の資格情報を開発用の
   既定値で書き直し、稼働中の PoC を壊す）。`object-storage-credentials` だけを作る Vault / Secret の手順、chart だけの
   `helm upgrade`、旧 Secret・ExternalSecret・Ingress・VirtualService・Vault パス・realm の `minio` client の掃除を書いた。
6. 偽 S3 の試験に「有効 → 停止へ切り替えたバケット」「版の一覧が複数の応答に分かれる（1 応答 2 件）」を足した（7 件）。
7. **署名鍵ありでの受け入れ試験**（Integration・run 36164059772・head `a90ce4c8`）: `ObjectStorageRoundTripTests` 3 件とも Passed、
   定義試験 7 件も Passed。赤は AST の `T_10_1057…` だけ（無関係）。鍵を与えても S3 の読み書き・版管理・全版削除は通る。
   🔴 **gRPC の管理用 RPC が鍵なしで拒まれることそのものは実機で確かめていない**（gRPC クライアントを試験に持ち込んでいない）。
   根拠はソース（`checkAdminAuth` は鍵が空でなければ Bearer JWT を要求する）と、起動スクリプトが鍵を export してから
   entrypoint を exec すること（試験のコンテナも同じスクリプトで起動し、起動に成功している）。

## ［2026-09-26 追記 / #1499］再監査（GO-with-nits）への対応

1. **起動スクリプトを fail-closed にした**: `export X="$(…)"` は中の失敗に関係なく 0 を返すため、鍵を変数へ受けて長さ（下限 40。
   32 バイトの base64 は 44 文字）を確かめ、足りなければ entrypoint を呼ばずに exit 1。compose（`$$` でエスケープ）・helm・
   `SeaweedFsContainer.StartupScript`・how-to・IADR-0461 決定 10 を同じ文字列へ揃えた。
   - 定義試験に長さの門の検査を足し、さらに**スクリプトを /bin/sh で実際に走らせる試験**を足した（Docker 不要。`head` を失敗する
     スタブに差し替えると exit 1・メッセージあり・entrypoint のスタブが呼ばれない／本物の `head` なら鍵 44 文字で entrypoint へ
     引数がそのまま渡る）。Windows では /bin/sh が無いので Skipped、CI（Linux）で走る。
2. **NetworkPolicy の試験を固めた**: 変異 M6（`NotIn` のキーを `app.kubernetes.io/name` へ）・M7（ingress の送り元を
   `namespaceSelector: {}` へ）が生き残っていた。除外のブロックと ingress のブロックを文字列の完全一致で固定し、除外キー `app` が
   Pod テンプレートのラベルと一致することも見る。**M6・M7 と、長さの門を helm / compose から消す M8 の計 4 通りで、それぞれ 1 件落ちる**ことを確かめた。
3. **切替 Runbook 手順 5**: upgrade の前に `helm get values msp -n microservices-platform` で稼働中の release の値（起動器が `--set` で
   重ねたメッシュの mTLS の方式等）を取り出し、`-f` で重ねて渡す形にした。

## 並行 PR との交差

| PR | 交差 | 扱い |
| --- | --- | --- |
| #1495（秘密情報のローテーション Runbook。**2026-09-25 にマージ済み**） | `docs/operations/operations.md`（本 PR は MinIO の 5 行だけを置換） | 起案時は Runbook 本体を触らない方針だったが、#1495 のマージ後に develop を取り込み、**本 PR で追随させた**: `secret-rotation-runbook.md` の excluded 7 項目の `minio-credentials`（→ `object-storage-credentials`）・env `MINIO_*`（→ `OBJECT_STORAGE_*`）・資格情報の変更の未実測の注記・`msp/minio-credentials` の行・`deferred[]` の 18 → 17 項目（OIDC クライアント 9 → 8）・MinIO が起動しない場合の行 |
| #1493（k8s-local-down） | `deploy/local/README.md`（本 PR は資格情報の表の 1 行）・`operations.md`・`scripts/README.md` | 行単位で交差しない見込み |
| #1490（realm 写しの突合） | `scripts/README.md` | 同上 |
| #1494（監査の抽出） | `docs/security/security.md`（本 PR は保存時暗号化の 2 行と trace ブロック） | 同上 |
| #1506（切替の移行仕様。#1504 を出し直したもの） | 交差なし。ただし同 PR の MinIO の実測（`ls -R /data` の解釈）と PVC 削除手順は、切替が本 PR の後になるなら SeaweedFS へ追随が要る | 本 PR では触らない（フォローアップ） |
| #1501（→ #1505）・#1503・#1511 | 交差なし（IADR 番号は 0458 が #1505、0460 が #1511） | — |

## 必須チェックへの影響

ワークフローのジョブ名・起動条件は変えない（`ci.yml` はコメント 1 行のみ）。IADR の採番検査（`check-adr-numbering`）は 0460（#1511）の develop 入りで欠番が解消した（2026-09-26）。
