# 移行 Runbook: デプロイ資産の改名（`knowledge-platform` → `<new-name>`）

- 起点: Issue #228（FR-14 / IADR-0056 フォローアップ 2）／方式: [IADR-0061](../adr/IADR-0061_deploy-rename-migration.md)
- 状態: **起草（未実行）**。実行は stg 検証を伴う（本 Runbook は手順の定義であり、実ファイルの改名は未実施）。
- パラメータ: `<new-name>`（推奨 `microservices-platform`。実行前に確定）。旧名称 `knowledge-platform` を `OLD`、
  新名称を `NEW` と表記する。

> **注意**: Namespace / Keycloak realm は in-place 改名ができない（再作成/新規構築が要る）。ステートフル
> （postgres / qdrant / minio / wikijs）はデータ移行を伴う。無停止・ロールバック可能な **Blue/Green** を基本とする。

## 1. 改名対象インベントリ（全数）

`grep -rn "knowledge-platform"` で網羅（本書時点）。分類ごとに置換する。

| 分類 | 対象 | 備考 |
| --- | --- | --- |
| Helm チャート名 | `deploy/helm/knowledge-platform/Chart.yaml`（`name`）・**ディレクトリ名** `deploy/helm/knowledge-platform/` | チャート改名＋パス移動 |
| k8s Namespace | `values.yaml` `namespace.name`、`templates/namespace.yaml` | **in-place 改名不可**（再作成） |
| コンテナイメージ | `values.yaml` の各 `image: knowledge-platform/*`（12 サービス） | Harbor プロジェクト接頭辞。再タグ・再 push |
| Ingress ホスト | `values.yaml` `*.knowledge-platform.local`（例 `wiki.knowledge-platform.local`） | DNS/hosts と整合 |
| Deployment（コンテナ内パス） | `templates/deployment.yaml`(3): L2 コメント・L56 env `/etc/knowledge-platform/pipeline/...`・L117 `mountPath: /etc/knowledge-platform/pipeline` | **コンテナ内パス `/etc/knowledge-platform/`**（#209 明記の改名対象）。env と mountPath を対で改名 |
| Istio / NetworkPolicy | `templates/istio-mtls.yaml`(4)・`templates/networkpolicy.yaml`(2) | Namespace セレクタ等 |
| pipeline / drift ジョブ | `templates/pipeline-config.yaml`・`templates/drift-postsync-job.yaml`・`files/pipeline.schema.json`・`files/README.md` | ConfigMap 名・ラベル |
| Keycloak realm | `deploy/keycloak/knowledge-platform-realm.json`（`realm` 値・**ファイル名**） | **新 realm を export/import で新名称構築**。issuer 変更 |
| OIDC authority（アプリ） | `values.yaml` `oidc.authority`（`/realms/knowledge-platform`）・各サービス `src/**/appsettings*.json`・SPA `platform/frontend/public/config.js` | realm 変更に追随 |
| ArgoCD | `deploy/argocd/application.yaml`（`name`/`releaseName`/`path`/`destination.namespace`）・`appproject.yaml`(4)・`README.md`(5) | **新 Application を作成**し切替後に旧を削除 |
| docker-compose | `deploy/docker-compose.yml`(6) | ローカル/compose 起動名 |
| 観測 | `deploy/grafana/provisioning/dashboards/knowledge-platform-overview.json`（**ファイル名**含む）・`deploy/prometheus/alerts.yml`(3) | ダッシュボード uid/タイトル・アラートラベル |
| bootstrap | `deploy/bootstrap/secret-templates.example.yaml`(3)・`deploy/bootstrap/README.md`(6) | Secret 名・namespace |
| CI | `.github/workflows/ci.yml`（`pipeline-config` の `deploy/helm/knowledge-platform/files/pipeline.json` パス） | チャートパス改名に追随 |
| istio README | `deploy/istio/README.md`(8) | 手順記述 |

> 実行時は上記を機械置換（`git grep -l knowledge-platform | xargs sed -i 's/knowledge-platform/<new-name>/g'`）
> したうえで、**ファイル名/ディレクトリ名の改名**（チャートディレクトリ・realm json・grafana dashboard json）と
> `.github/workflows/ci.yml` のパス整合、`pipeline-config` の参照先を個別に確認する。機械置換後は
> `helm lint` / `helm template` / `node scripts/validate-pipeline-config.js` / `node scripts/check-doc-links.js` で回帰確認する。

## 2. 事前準備

1. `<new-name>` を確定（プロダクト判断。推奨 `microservices-platform`）。
2. stg クラスタと Harbor への権限、Keycloak 管理権限、ArgoCD 権限を確認。
3. データ資産のバックアップ: postgres（各サービス DB）・qdrant コレクション・minio バケット・wikijs DB。
4. メンテナンス告知（Blue/Green のため原則無停止だが、cutover 時に短時間の整合待ちが生じ得る）。

## 3. Blue/Green 移行手順（stg → prod）

### 3.1 新 realm（Green）を構築
1. 旧 realm を export（`kcadm.sh get realms/knowledge-platform -r ... > realm.json` または管理 UI）。
2. `realm` 値を `NEW` に置換し import（`kcadm.sh create realms -f realm-new.json`）。clients（`spa-web` 等）・
   roles・mappers を検証。**旧 realm は保持**（ロールバック用）。

### 3.2 新イメージ（Green）を publish
1. CI/ビルドで各サービスを `NEW/<service>` タグへ再 push（Harbor 新プロジェクト作成）。
2. `values.yaml` の `image` を `NEW/*` に更新（新チャートで使用）。

### 3.3 新チャート/新 Namespace（Green）をデプロイ
1. チャートを `deploy/helm/<new-name>/` へ改名し、`Chart.yaml` `name`・`values.yaml`（namespace/oidc/image/host）を `NEW` へ。
2. 新 ArgoCD Application（`name: <new-name>`・`releaseName: <new-name>`・`path: deploy/helm/<new-name>`・
   `destination.namespace: <new-name>`）を作成し同期。新 Namespace に全ワークロードが立ち上がることを確認。
3. ステートフルのデータ移行:
   - postgres: `pg_dump`/`pg_restore` で各 DB を新 Namespace の postgres へ。
   - qdrant: スナップショット→リストア。
   - minio: `mc mirror` で旧→新バケット。
   - wikijs: DB 移行（postgres と同様）。

### 3.4 疎通・整合検証（Green、ingress 切替前）
- 新 realm での OIDC ログイン（SPA `config.js` を新 issuer に向けた検証用 URL で）。
- BFF 経由の主要フロー（検索・文書・管理系）。
- `drift-postsync-job` によるパイプライン構成のドリフト無し。
- 観測（Grafana ダッシュボード・Prometheus アラート）が新ラベルで疎通。

### 3.5 Cutover（切替）
1. Ingress/DNS を Green（`*.<new-name>.local` / 本番ホスト）へ切替。
2. 短時間の整合監視（エラーレート・レイテンシ）。
3. 旧（Blue）Namespace/realm/Application を**一定期間保持**後に撤去（下記ロールバック猶予）。

### 3.6 dev 環境（in-place を許容）

dev（`docker-compose` 中心・データ消失許容）では Blue/Green は過剰なため、in-place を許容する（IADR-0061 決定②）。
1. `git grep -l knowledge-platform | xargs sed -i 's/knowledge-platform/<new-name>/g'` で機械置換。
2. ファイル名/ディレクトリ名（チャートディレクトリ・realm json・grafana dashboard json）と CI パス
   （`.github/workflows/ci.yml` の `pipeline-config` 参照先）を個別に改名・整合。
3. `docker compose down -v && docker compose up`（ボリューム破棄で作り直し。dev はデータ移行不要）。
4. 回帰確認（下記 §5 のうち実環境非依存の項目：`helm lint`/`helm template`/`validate-pipeline-config`/`check-doc-links`/
   dev での OIDC ログイン）。

## 4. ロールバック

- Cutover 前: Green を破棄するだけ（Blue は無傷）。
- Cutover 後（猶予期間内）: Ingress/DNS を Blue に戻す。realm/イメージ/Namespace は Blue が残存。
- データ移行後に書き込みが Green に入った場合は、差分の再移行 or 猶予期間の書き込み凍結方針を事前に定める。

## 5. stg 検証チェックリスト（受け入れ基準）

- [ ] `helm lint deploy/helm/<new-name>` / `helm template` が通る（旧名称の残存なし）。
- [ ] 新 Namespace に全サービスが Ready。
- [ ] 新 realm で OIDC ログイン成功・各サービスの authority が新 issuer。
- [ ] データ移行後、主要フロー（検索/文書/管理）が新環境で正常。
- [ ] 観測（ダッシュボード/アラート）が新ラベルで機能。
      - 注: Prometheus/Grafana は現状 dev（`docker-compose`）のみ配線で、stg/prod（k3s）展開は別 follow-up
        （`docs/operations/operations.md` の監視適用範囲）。実行時点で未展開なら本項目は該当環境で検証不能なため、
        監視の stg/prod 展開の完了を前提条件とする。
- [ ] `git grep knowledge-platform` の残存が意図的なもの（履歴/移行ドキュメント）のみ。
- [ ] ロールバック手順を stg で予行。

## 6. 実行後のドキュメント反映

- [IADR-0061](../adr/IADR-0061_deploy-rename-migration.md) を Accepted 化し、実施日・旧資産撤去を追記。
- `docs/operations/` に新名称の運用手順を反映。
- 本 Runbook に実施ログ（日時・環境・結果）を追記。

---

> 本 Runbook は**起草**である。実行（stg 適用・データ移行・cutover）は実環境と新名称確定を要するため #228 に残す。
