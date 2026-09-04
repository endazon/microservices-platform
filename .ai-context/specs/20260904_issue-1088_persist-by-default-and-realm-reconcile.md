---
title: 経路B の永続化を既定にし、realm は「静的 import ＋ 起動器の後段で差分を当てる」形へ移して、乖離を門で検知する
type: spec
status: in-progress
related_ids:
  - FR-05
  - NFR-09
  - ADR-0004
  - ADR-0026
  - IADR-0082
  - IADR-0103
  - IADR-0210
  - IADR-0248
  - IADR-0261
  - IADR-0273
  - IADR-0327
  - IADR-0332
  - IADR-0336
  - IADR-0342
  - IADR-0368
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md（認証＝Keycloak）
  - planning:projects/microservices-platform/07_adr/ADR-0026_auth-keycloak.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09
---

# 仕様書: 経路B の永続化を既定にし、realm の差分を起動器が当て、乖離を門で検知する（issue #1088 / #324）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（ABAC。realm の `abac-attributes` スコープと利用者属性が判定入力である）
- 非機能要件（NFR）: NFR-09（セキュリティ｜認証・認可。realm の TOTP 必須・ブルートフォース・パスワードポリシーが
  稼働 realm に届いていることが前提）
- 関連 ADR: ADR-0004（認証＝Keycloak）／ADR-0026（Keycloak 採用）
- 実装 ADR: [[IADR-0082]]（opt-in 永続化。**決定 1 を本作業が置換する**）／[[IADR-0210]]（qdrant・可観測性の永続化）／
  [[IADR-0336]]（backchannel の後追い。**決定 3 を本作業が一般化して置換する**）／[[IADR-0261]]・[[IADR-0332]]
  （smtpServer は runtime 注入＝本作業が触ってはならない層）／[[IADR-0327]]・[[IADR-0342]]（Wiki.js の冪等 bootstrap。同型）／
  [[IADR-0248]]（`check-stack-ready.js` の門）
- Issue: #1088（本体）／#324（永続化。closed。受け入れ基準「realm 更新の反映手順」を本作業が満たし直す）
- 新設 IADR: [[IADR-0368]]（仮番。マージ時に改番される）

## 目的・背景

稼働 dev クラスタは `PERSIST=1` で立っておらず、Keycloak の realm と runtime state（TOTP 資格情報・追加利用者・
セッション）が Pod 再作成のたびに黙って消えている。永続化を入れると `--import-realm` の `IGNORE_EXISTING` が効き
はじめ、**realm JSON の変更が二度と稼働 realm へ届かなくなる**ため、#1088 と #324 は同じ PR で解く。
加えて、稼働クラスタは PR 検証用イメージ（`bff:issue1187` 等）だらけで宣言と乖離しており、検知する門が無い。

## 母集合（自分で引いた。issue のコメントは転記していない）

**引いた日時**: 2026-09-04 18:50–19:10 JST。**基点**: `origin/develop` `78f9bda6`。
`git rev-parse --is-shallow-repository` = **`false`**。クラスタ: `rancher-desktop`（k3s・StorageClass `local-path` が既定・
`WaitForFirstConsumer`）。

### 軸 1: 永続化が要る状態と、その稼働実態

`kubectl get pvc -A` と `kubectl get deploy -n <ns> -o custom-columns=…volumes…persistentVolumeClaim.claimName` で引いた。

| 状態 | 置き場（マウント） | 宣言（どのオーバーレイ / chart） | 稼働の実態（2026-09-04） |
| --- | --- | --- | --- |
| Keycloak realm ＋ runtime state | `/opt/keycloak/data`（start-dev の file H2） | `infra-persistence` の PVC `keycloak-data` | **PVC `Pending` 13d（一度も消費されていない）。Deployment の volume は `realms,theme-platform` のみ・`RollingUpdate`** → 非永続 |
| Postgres 全アプリ DB（MSP ＋ AST ＋ `wikijs`） | `/var/lib/postgresql/data` | `infra-persistence` の PVC `postgres-data` | PVC `Bound` 38d（過去の残骸）。**Deployment は `data`=emptyDir・`RollingUpdate`** → 非永続 |
| Qdrant コレクション | `/qdrant/storage` | `infra-persistence` の PVC `qdrant-storage` | PVC `Bound` 18d（残骸）。**Deployment は emptyDir** → 非永続 |
| Prometheus / Loki / Tempo / Grafana | 各 config の storage パス | `observability-persistence` の PVC 4 本 | PVC 4 本とも `Bound` 18d（残骸）。**Deployment 4 件とも PVC 参照なし** → 非永続 |
| MinIO オブジェクト | `/data` | chart の PVC `minio-data` | `Bound` 38d・**参照あり**（永続） |
| Wiki.js のファイル | `/wiki/data` | chart の PVC `wiki-js-data` | `Bound` 38d・**参照あり**。ただし設定は Postgres の `wikijs` DB → Postgres と運命を共にする |
| RabbitMQ / Redis | queue / cache | emptyDir（IADR-0082 が却下） | emptyDir（宣言どおり・対象外） |
| Vault dev | メモリ（`server -dev`） | 宣言上メモリ。OIDC 消失は IADR-0363 が実測 | 対象外（別 issue） |
| Keycloak realm 宣言（ConfigMap `keycloak-realms`） | `/opt/keycloak/data/import` | `k8s-local-up.sh` [3/7] が実ファイルから生成 | **リポジトリと不一致**（live 25141 B / repo 29208 B。差分キー `clientScopes` `clients` `components` `smtpServer` `users`。ファイル最終更新 `66c316b7` 2026-09-03 07:15、pod 起動 2026-09-03 12:54 → 誰かが up を再実行していない） |

**結論**: 永続化オーバーレイは 2 つとも**まったく当たっていない**（IADR-0082 の opt-in を誰も付けていない）。残骸 PVC 6 本は
過去の `PERSIST=1` 起動の名残で、Deployment が参照していないため「Bound なのに非永続」という誤解を招く。

### 軸 2: 同型の事故の回数（「検査器は 2 回目から」の判定に使う）

| 事故の型 | 1 回目 | 2 回目以降 | 判定 |
| --- | --- | --- | --- |
| realm JSON の変更が稼働 realm へ届かない | #1115（`backchannel.logout.url`。IADR-0336 決定 3 で後追いスクリプトを置いた） | #1088 起票時（ConfigMap が 1 日古く TOTP が効いていない）、**本作業の実測（上表最終行。3 回目）** | **検査器を足す**（G9） |
| 永続化オーバーレイが当たっていない | #324 close 後の残骸 PVC（`postgres-data` 38d） | #1088（`keycloak-data` Pending 13d） | **検査器を足す**（G10） |
| 稼働イメージが宣言と違う | 2026-07-27 前後に作られたクラスタと現行マニフェストの乖離（記憶ファイル `msp-cluster-drift-and-tls`。rabbitmq Secret のキー欠落で実際に踏んだ） | #1088 の追記（`conversion-service:pdf-1192` / `bff:issue1187` / `datasource-service:issue1194` / `ingestion`・`retrieval:issue-1193` / `mcp-service:issue1185` / `wiki-service:issue1200` / graph・dashboard の 7 件） | **検査器を足す**（G11。ただし**参照の一致**まで。中身が develop 最新かは下記「記録に留める」） |
| `:latest` の中身が古い（再ビルドしたが Pod が古いイメージのまま） | 未観測（本作業で imageID を突合する手段が無いことを確認しただけ） | — | **記録に留める**（IADR-0368 §却下した代替案） |

### 軸 3: `reconcile-backchannel-logout.sh` を引く記述（撤去に伴う追随）

`grep -rn "reconcile-backchannel"` → `scripts/k8s-local-up.sh`（呼び出し 2 行）／`.ai-context/adr/IADR-0336`（決定 3。凍結記録
なので追記で扱う）／スクリプト自身。`docs/` には無い。

### 軸 4: `PERSIST` を引く記述（既定変更に伴う追随）

`deploy/local/README.md`（66・77–140・297・320・532 行）／`docs/operations/operations.md`（189–247 行）／
`docs/operations/local-sso-recovery-runbook.md`（33・44 行）／`scripts/README.md`（308 行）／`scripts/k8s-local-up.sh`
（124–131・316–322 行）／`scripts/k8s-local-up.test.js`（43・45・240・342–358・552–598 行）／
`deploy/local/infra-persistence/*.yaml`・`deploy/local/observability-persistence/*.yaml` のコメント。
除外: `.ai-context/specs` / `.ai-context/superpowers`（凍結記録）／`CHANGELOG.md`（生成物）／`.ai-context/adr/IADR-0082`・
`IADR-0210`（凍結。日付つき追記で扱う）。

## 対象範囲

- 対象:
  1. `scripts/k8s-local-up.sh`: `PERSIST` を**既定オン**（opt-out `PERSIST=0`）にし、StorageClass `local-path` が無ければ
     **黙って emptyDir へ落とさず止める**（fail-closed）。
  2. realm の反映方式: 静的 import（初回・空 PVC）＋ **起動器の後段で Job が Admin REST API で差分を当てる**
     （`deploy/local/keycloak-setup/reconcile-realm.{sh,js}` ＋ `realm-reconcile-job.yaml`）。`reconcile-backchannel-logout.sh`
     は撤去する（pod 内 `kcadm.sh` は本体を OOMKilled にする。2026-09-02 実測）。
  3. `keycloak-admin` Secret に `username` キーを足し、Keycloak Deployment と Job の両方がそこから読む（管理者名の単一情報源）。
  4. `scripts/check-stack-ready.js` に門を足す: **G9** realm 乖離（reconcile を check モードで走らせ drift 0 を要求）、
     **G10** 永続化（オーバーレイが宣言する PVC が Bound で、対応する Deployment が参照している）、
     **G11** イメージ参照（chart / kustomize の描画結果と稼働 Deployment のイメージ参照が一致）。
  5. `scripts/k8s-local-up.test.js`: 既定オン／opt-out の意味論、reconcile の配線（realm ConfigMap ＝ 単一情報源、
     keycloak rollout の後）、Job マニフェストと up.sh の ConfigMap 名の対応を固定。reconcile の計画器（純粋関数）の単体試験
     `scripts/keycloak-realm-reconcile.test.js`。
  6. 文書: `deploy/local/README.md`・`docs/operations/operations.md`・`docs/operations/local-sso-recovery-runbook.md`・
     `scripts/README.md`・`deploy/local/keycloak-setup/README.md`（新規）。IADR-0368 ＋ 索引。IADR-0082 / IADR-0336 へ日付つき追記。
  7. 稼働クラスタの作り直し（利用者承認済み）と、#1215 / #1118 / #600 AC-14 / #1176 / #1168 / #1185 の再測。
- 対象外:
  - RabbitMQ / Redis / Vault dev の永続化（IADR-0082 の却下は生きている。Vault は別 issue）。
  - 本番像（`deploy/helm` 本体・`deploy/argocd`・compose）。compose 側の永続化方式（IADR-0079）は不変。
  - `:latest` の**中身**が develop 最新かを見る門（記録に留める。上表）。
  - 利用者の実行時状態（資格情報・属性・ロール・グループ・requiredActions）を宣言へ戻すこと（下記「境界」）。

## 設計

### 1. `PERSIST` は既定オン（IADR-0082 決定 1 の置換）

IADR-0082 が opt-in を選んだ理由は「provisioner 不在クラスタで PVC が Pending になり Pod が立たない」だった。しかし
`k8s-local-up.sh` が受け付けるランタイムは **Rancher Desktop 内蔵 k3s と k3d の 2 つだけ**で、どちらも `local-path`
provisioner を同梱する。fail-safe の仮定した環境は実在せず、その代償として**常用クラスタが誰にも気付かれずに非永続で
立っていた**（実害: #1114 の TOTP 資格情報の消失）。したがって:

- 既定（env 未設定）＝ `deploy/local/infra-persistence`（＋ `OBSERVABILITY=1` なら `observability-persistence`）。
- `PERSIST=0` で従来の base（emptyDir）。**使い捨てスタック専用**の明示的な opt-out。
- 既定経路では `kubectl get storageclass local-path` を確かめ、無ければ **ERROR で止める**（fallback しない。黙って非永続に
  なることが #1088 の本体だから）。

### 2. realm は「静的 import ＋ 起動器の後段で差分を当てる」

```
[3/7] ConfigMap keycloak-realms（実 realm ファイル）        ← 単一情報源（不変）
[4/7] Keycloak start-dev --import-realm                    ← 空 PVC のときだけ全量 import（IGNORE_EXISTING）
[7/7] の後: Job keycloak-realm-reconcile                   ← 同じ ConfigMap を読み、Admin REST で差分を当てる
      （platform-infra ns・node:22-alpine・http://keycloak:8080・keycloak-admin Secret）
```

- **なぜ Job か**: Keycloak pod 内で `kcadm.sh` を exec すると別 JVM がコンテナのメモリ制限を食い潰し、本体が OOMKilled
  で再起動する（2026-09-02 実測、restartCount 2→3）。`reconcile-backchannel-logout.sh` はまさにその経路だった。
  ホスト側から直接叩く案は、エッジ（`https://keycloak.localhost`＝CA と `.localhost` 解決が要る）か port-forward
  （MSYS で不安定）に依存する。**同じ namespace の Job なら、エッジ・TLS・メッシュのどれにも依存しない**
  （Wiki.js bootstrap が loopback へ出すのと同じ判断）。
- **なぜ Node か**: 差分計算をリポジトリの他の検査器と同じ言語で書き、**計画器を純粋関数として `node` で単体試験できる**。
  Job 側は Node 22 の `fetch` だけで外部依存が無い。
- **なぜ `KC_IMPORT_STRATEGY=OVERWRITE_EXISTING` や `kc.sh import --override` ではないか**: どちらも realm を**丸ごと
  作り直す**ため、永続化で守りたい runtime state（TOTP 資格情報・追加利用者・セッション）を毎回消す。永続化の目的と両立しない。
- **なぜ partial import（`POST /partialImport`）ではないか**: 扱えるのが users / clients / roles / groups / idp に限られ、
  realm 設定（`requiredActions` の既定・`otpPolicy`・`passwordPolicy`・テーマ）に届かない。#1088 の症状（TOTP 既定）を
  直せない。加えて `OVERWRITE` は利用者を作り直す＝資格情報を消す。

#### 宣言が所有する層と、実行時が所有する層（境界）

| 層 | 対象 | reconcile の扱い |
| --- | --- | --- |
| 宣言（realm JSON が正） | realm の非コレクション設定（テーマ・ロケール・token/セッション寿命・パスワードポリシー・OTP ポリシー・ブルートフォース・events）／`requiredActions`／realm ロール・client ロール／グループ（属性つき）／client scopes ＋ protocol mappers／clients（属性・redirect・secret・default/optional scopes ＋ mappers）／**seed 利用者の存在** | 差分があれば当てる（無いものは作り、違うものは更新する。宣言に無い余剰は消さない＝加算的） |
| 実行時（Keycloak / SC-17 / 本人が正） | 既存利用者の資格情報・属性・ロール・グループ・`requiredActions`・セッション／`smtpServer`（IADR-0261 決定 2: runbook が `kcadm` で入れる秘匿値）／サービスアカウント**以外**の利用者の変更 | **触らない**。サービスアカウント利用者（`serviceAccountClientId` を持つ）は人ではないので宣言側（ロール・属性）を当てる |

利用者の requiredActions を宣言へ戻さないのは、TOTP を登録し終えた利用者に `CONFIGURE_TOTP` を再要求してしまう
ためである。TOTP 既定は realm の `requiredActions[CONFIGURE_TOTP].defaultAction`（宣言層）で、新規利用者へは効く。
seed 利用者の宣言を変えて既存クラスタへ届けたいときは、破壊経路（PVC を消して再 import）を使う（README に明記）。

#### 収束の確かめ方

`RECONCILE_MODE=apply` は「計画 → 適用 → 再計画」を最大 3 周し、最後の計画が 0 件でなければ非 0 で終える
（当てたつもりで当たっていない状態を緑にしない）。`RECONCILE_MODE=check` は計画だけを行い、1 件でも残れば非 0。
標準出力の最終行は `realms=<n> drift=<m>` で、G9 はこれを読む（`realms=0` は失敗＝走査が壊れている）。

### 3. 門（`check-stack-ready.js`）

- **G9 realm 乖離**: `bash deploy/local/keycloak-setup/reconcile-realm.sh --check` を走らせ、exit 0 かつ `drift=0` かつ
  `realms>=1` を要求。**realm を書き換えない**（check モード）。
- **G10 永続化**: `deploy/local/infra-persistence/pvcs.yaml`（＋ `observability-persistence/pvcs.yaml`）が宣言する PVC を走査し、
  `app` ラベルと同名の Deployment が `platform-infra` に**居るなら**、その Deployment が当該 PVC を参照し、PVC が `Bound`
  であることを要求する。`PERSIST=0` を明示したときだけ notice へ落とす（up.sh と同じ意味の env。既定は永続を期待する）。
  **列挙を持たない**（PVC 集合はオーバーレイから走査）。
- **G11 イメージ参照**: `helm template msp deploy/helm/microservices-platform -f deploy/local/values-local.yaml`（MSP ns）と
  `kubectl kustomize deploy/local/infra-persistence`（infra ns）の描画結果から `Deployment 名 → containers[].image` を取り、
  稼働 Deployment の同名のものと**文字列で完全一致**することを要求する。描画に無い Deployment（opt-in の overlay 由来）は
  見ない。`helm` が無ければ G3 と同じく失敗（抜け道を置かない）。

### 4. 起動器の配線

- [3/7]: `keycloak-admin` Secret を `username` ＋ `password` で作る。`keycloak.yaml` の `KEYCLOAK_ADMIN` を `secretKeyRef`
  へ移す（ESO の `externalsecret-keycloak-admin.yaml` は `Merge` なので `password` だけ供給しても壊れない）。
- [4/7]: `PERSIST` 既定オン（§1）。
- [7/7] の後: `reconcile-realm.sh`（best-effort・WARN。fail-closed の門は G9）。ConfigMap `keycloak-realm-reconcile`
  （スクリプト本体）を `--from-file` で作る（テーマ ConfigMap と同型）。

### 5. クラスタの作り直し（手順）

1. `helm uninstall msp -n microservices-platform`、`kubectl delete ns microservices-platform platform-infra`
   （`ai-stock-trading` namespace は残す＝AST の Secret `ast-secrets` / `moomoo-*` を失わない。AST は infra 復帰後に
   `rollout restart` する）。残骸 PVC は namespace ごと消える。
2. 本作業の up.sh で `LOCALEDGE=1 ISTIO=1 ESO=1 VAULT=1 OBSERVABILITY=1 HEADLAMP=1 ARGOCD=1 WIKIJS_OIDC=1 ABACSEED=1
   SEARCHSEED=1 LOCALEMBED=1` を立てる（現クラスタに実在するゲートの集合 ＋ #1215 の再測に要る seed / 決定的埋め込み。
   `ISTIO_MTLS_MODE` は付けない＝PERMISSIVE。#1159 が STRICT の手動ドリフトを問題にしているため宣言どおりにする）。
3. `node scripts/check-stack-ready.js` が緑になるまで直す。
4. 再測（§受け入れ基準の後半）。

## 受け入れ基準

- [ ] AC-1 Keycloak が Pod 再作成をまたいで realm と runtime state を保持する（一時利用者を作り、`rollout restart` 後も残ることを実測）。
- [ ] AC-2 realm JSON の変更が稼働 realm へ届く（realm JSON を実際に変え、up を再実行して Admin REST で値が変わることを実測。
      変更は戻す）。
- [ ] AC-3 #324 の受け入れ基準（Keycloak / Postgres の PVC 化・realm 更新の反映手順の docs 明記）を満たす。
- [ ] AC-4 AC-1・AC-2 が自動で確かめられる（G9 / G10 の門 ＋ `k8s-local-up.test.js` の不変条件。検査器を足した根拠は §母集合 軸 2）。
- [ ] AC-5 既定（env 未設定）で `deploy/local/infra-persistence` が apply され、`PERSIST=0` で base が apply される（stub 試験）。
- [ ] AC-6 reconcile の計画器は「一致なら 0 件」「差分の種類ごとに 1 件」「smtpServer と既存利用者の実行時状態には触れない」を単体試験で固定する。
- [ ] AC-7 `check-stack-ready.js --self-test` に G9 / G10 / G11 の陽性・陰性対照が入る（変異で赤くなる）。
- [ ] AC-8 稼働クラスタの全 MSP イメージが `k3d-local/microservices-platform/<x>:latest`（G11 緑）。
- [ ] AC-9 `scripts/verify-oidc-edge-flow.sh` と `scripts/verify-tool-oidc-logins.sh` が新クラスタで通る。
- [ ] AC-10 再測の結果を各 issue（#1215 / #1118 / #600 / #1176 / #1168 / #1185）へコメントする。閉じられるものは閉じる。

## テスト方針

- `scripts/k8s-local-up.test.js`（stub-on-PATH）: 既定オン／`PERSIST=0`／`OBSERVABILITY` との置換／reconcile の配線
  （keycloak rollout の**後**・ConfigMap 名の対応・Job マニフェストが `keycloak-realms` をマウント）。
- `scripts/keycloak-realm-reconcile.test.js`: 計画器の純粋関数（fixtures は realm JSON の実物から切り出す）。
- `scripts/check-stack-ready.js --self-test`: G9 / G10 / G11 の評価関数。
- 実機: 上の AC-1・AC-2・AC-8・AC-9。

## 計画書との差異

- 差異: なし（ADR-0004 / ADR-0026 は Keycloak を認証基盤とする決定で、realm の配備方式は実装側の判断）。
  実装 ADR の側で IADR-0082 決定 1 と IADR-0336 決定 3 を置換する（IADR-0368）。

## 未決事項

- 「`:latest` の中身が develop 最新か」を見る門は今回置かない（1 回目として記録）。置くなら `k8s-local-images.sh` の
  `--label org.opencontainers.image.revision` と Pod の `imageID` の突合が要る。
- Vault dev のメモリ状態（OIDC 設定）は別 issue。
