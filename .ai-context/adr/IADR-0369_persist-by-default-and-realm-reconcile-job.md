---
title: IADR-0369 経路B の永続化を既定にし、realm は「静的 import ＋ 起動器の後段で Job が差分を当てる」形へ移して、realm・永続化・イメージ参照の乖離を門で検知する
type: impl-adr
status: Proposed
related_ids:
  - FR-05
  - NFR-09
  - ADR-0004
  - ADR-0026
  - IADR-0066
  - IADR-0079
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
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md（認証＝Keycloak）
  - planning:projects/microservices-platform/07_adr/ADR-0026_auth-keycloak.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09
---

# IADR-0369: 経路B の永続化を既定にし、realm の差分は起動器の後段で Job が当て、乖離は門で検知する（#1088 / #324）

- 状態: Proposed
- 日付: 2026-09-04
- 決定者: claude（実装）／利用者裁定 2026-09-04（クラスタ作り直しを射程に入れる）

## 起点・関連

- 関連する計画書 ID: FR-05（ABAC の判定入力は realm の `abac-attributes` スコープと利用者属性）／NFR-09（認証・認可。
  TOTP 必須・ブルートフォース・パスワードポリシーは realm の宣言）／ADR-0004・ADR-0026（Keycloak を認証基盤とする）
- 関連 ADR: [IADR-0082](./IADR-0082_local-k8s-infra-persistence.md)（opt-in 永続化。**決定 1 を本 ADR が置換する**。決定 2〜4 は生きる）／
  [IADR-0210](./IADR-0210_local-k8s-observability-persistence.md)（qdrant・可観測性の永続化。ゲートの意味論だけ本 ADR に従う）／
  [IADR-0336](./IADR-0336_backchannel-logout-destination-and-mesh-boundary.md)（**決定 3 の 1 値だけの後追いを、本 ADR の決定 2 が宣言全体へ一般化して置換する**）／
  [IADR-0261](./IADR-0261_keycloak-theme-and-smtp-injection.md)・[IADR-0332](./IADR-0332_keycloak-smtp-externalsecret-wiring.md)（`smtpServer` は runbook が入れる実行時状態＝本 ADR が触らない層）／
  [IADR-0327](./IADR-0327_wikijs-setup-bootstrap.md)・[IADR-0342](./IADR-0342_wikijs-oidc-strategy-idempotent-seed.md)（manifest だけでは復元できない runtime 状態を冪等に当てる同型）／
  [IADR-0248](./IADR-0248_integration-stack-ci-readiness-gate.md)（`check-stack-ready.js` の門）／
  [IADR-0103](./IADR-0103_local-sso-persistence-and-claim-design.md)（再構築後の SSO 自動復旧。本 ADR が realm 側を引き受ける）
- 関連仕様書: [`.ai-context/specs/20260904_issue-1088_persist-by-default-and-realm-reconcile.md`](../specs/20260904_issue-1088_persist-by-default-and-realm-reconcile.md)
- Issue: #1088（本体）／#324（永続化。closed。「realm 更新の反映手順」を本 ADR が満たし直す）

## コンテキストと課題

稼働 dev クラスタは **`PERSIST=1` で立っていなかった**。2026-09-04 の実測: PVC は 6 本が `Bound`（過去の残骸）＋
`keycloak-data` が `Pending` 13 日で在るのに、**どの Deployment も PVC を参照していない**（`RollingUpdate` のまま）。
Keycloak の realm と runtime state（TOTP 資格情報・追加利用者・セッション）は Pod 再作成のたびに黙って消え、#1114 の
実測では当日登録した TOTP を復元できず `developer` の往復を `poc-user` で代替した。

永続化を入れると別の問題が始まる。`start-dev --import-realm` は**同名 realm が在ると黙って飛ばす**（`IGNORE_EXISTING`）ため、
**realm JSON の変更が二度と稼働 realm へ届かなくなる**。#324 自身がこの整合を受け入れ基準に書いており、#1088 と #324 は
同じ PR で解かなければならない。加えて、稼働 realm の ConfigMap は本作業の時点で既にリポジトリと不一致だった
（差分キー `clientScopes` `clients` `components` `smtpServer` `users`。同型の乖離は #1115 → #1088 → 本作業と 3 度目）。

稼働クラスタはさらに、PR 検証用イメージ（`bff:issue1187` / `conversion-service:pdf-1192` / `datasource-service:issue1194` /
`ingestion`・`retrieval:issue-1193` / `mcp-service:issue1185` / `wiki-service:issue1200` / `dashboard-service:issue1197` /
`graph-service:issue1187` / `document-service:issue1187` の 10 件）が残ったまま develop から乖離しており、検知する門が無かった。

決めるべき論点: (1) 永続化の既定、(2) 永続化と realm 更新の両立方式、(3) 反映を pod の中で行うか外で行うか、
(4) 宣言と実行時状態の境界、(5) 検査器を足すか記録に留めるか、(6) 管理者資格情報の置き場。

## 検討した選択肢

### 決定 1（永続化の既定）

| 案 | 利点 | 判断 |
| --- | --- | --- |
| A. opt-in のまま。「非永続で立っている」ことを門で赤くする | 既定不変 | **不採用**。門は up の後にしか走らず、up の時点で誰も付けていない既定は変わらない。#1088 はまさに「付けなかった」事故である |
| **B. 既定オン。opt-out は `PERSIST=0`。StorageClass が無ければ止める** | 忘れても永続。fail-safe の仮定した環境は実在しない | **採用** |
| C. 既定オン。StorageClass が無ければ黙って emptyDir へ落とす | 止まらない | **不採用**。「非永続で立っていることに気付けない」を作り直す |

### 決定 2（永続化と realm 更新の両立）

| 案 | 判断 |
| --- | --- |
| A. `KC_IMPORT_STRATEGY=OVERWRITE_EXISTING` / `kc.sh import --override` | **不採用**。realm を丸ごと作り直す＝永続化で守りたい runtime state を毎回消す |
| B. `POST /partialImport`（`OVERWRITE`） | **不採用**。users / clients / roles / groups / idp にしか届かず、realm 設定（`requiredActions` の既定＝#1088 の症状）を直せない。利用者は作り直され資格情報が消える |
| C. PVC を消して再 import（IADR-0082 決定 4 の「破壊的」経路） | **反映手順としては残す**（seed 利用者の宣言を変えたいとき）。既定の経路にはしない |
| **D. 静的 import（空 PVC のときだけ）＋ 起動器の後段で宣言と稼働の差分を Admin REST で当てる** | **採用**。IADR-0336 決定 3 の後追い（1 値）を宣言全体へ一般化する |

### 決定 3（反映をどこで行うか）

| 案 | 判断 |
| --- | --- |
| A. Keycloak pod で `kcadm.sh` を exec（IADR-0336 決定 3 の形） | **不採用**。別 JVM がコンテナのメモリ制限を食い潰し、**本体が OOMKilled で再起動する**（2026-09-02 実測。restartCount 2→3、数分 not ready） |
| B. ホストから Admin REST（エッジ `https://keycloak.localhost` か port-forward） | **不採用**。エッジは CA と `.localhost` 解決、port-forward は MSYS の不安定さに依存する。CI と Windows の両方で同じ経路を成立させにくい |
| **C. 同じ namespace（`platform-infra`）の Job（`node:22-alpine`）が `http://keycloak:8080` を叩く** | **採用**。エッジ・TLS・メッシュのどれにも依存しない（Wiki.js bootstrap が loopback へ出すのと同じ判断）。メッシュ内に置くとサイドカー付き Job が完了しないので、メッシュ外の platform-infra が正しい |

## 決定

### 1. 永続化は既定オン（`PERSIST=0` で opt-out）。StorageClass `local-path` が無ければ止める

`scripts/k8s-local-up.sh` の [4/7] は既定で `deploy/local/infra-persistence` を、`OBSERVABILITY=1` のときは
`deploy/local/observability-persistence` を選ぶ。`PERSIST=0` を明示したときだけ base（emptyDir）へ戻る（使い捨てスタック専用）。
既定経路では `kubectl get storageclass local-path` を確かめ、無ければ **ERROR で止める**（黙って非永続にはしない）。
`PERSIST=1`（旧 opt-in の綴り）は既定と同じ（古い手順書でも壊れない）。

**IADR-0082 決定 1 を置換する。** 同決定の根拠「provisioner 不在クラスタで Pod が Pending になる」は、本スクリプトが
受け付けるランタイム（Rancher Desktop 内蔵 k3s / k3d）がどちらも `local-path` を同梱するため成り立たず、その fail-safe が
守った環境は実在しなかった。代償は「常用クラスタが誰にも気付かれず非永続で立っていた」ことである。
CI（`integration-stack.yml`。k3d）も既定＝永続で走る（k3d は local-path を持つ。使い捨てなので害は無い）。

### 2. realm は「静的 import ＋ 起動器の後段で Job が差分を当てる」。宣言と実行時の境界を引く

```
[3/7] ConfigMap keycloak-realms（実 realm ファイル。AST realm も同梱）      ← 単一情報源（不変）
[4/7] Keycloak start-dev --import-realm                                   ← 空 PVC のときだけ全量 import（IGNORE_EXISTING）
[7/7] の後: bash deploy/local/keycloak-setup/reconcile-realm.sh           ← Job keycloak-realm-reconcile（platform-infra）
        └ node:22-alpine が /import（同じ ConfigMap）を読み、Admin REST で計画→適用→再計画（最大 3 周）
```

- 実体: `deploy/local/keycloak-setup/reconcile-realm.js`（計画器＝純粋関数 `plan(desired, live)` ＋ 適用器）／
  `realm-reconcile-job.yaml`（Job。`RECONCILE_MODE=apply|check`）／`reconcile-realm.sh`（ホスト側入口。ConfigMap 化 →
  delete → apply → 完了待ち → ログ）。up.sh からは best-effort（WARN）で呼ぶ。**fail-closed の門は決定 4 の G9。**
- **境界（宣言が所有する層／実行時が所有する層）**:

| 層 | 対象 | 扱い |
| --- | --- | --- |
| 宣言（realm JSON が正） | realm の非コレクション設定（テーマ・ロケール・token/セッション寿命・パスワードポリシー・OTP ポリシー・ブルートフォース・events）／`requiredActions`／realm ロール・client ロール／グループ（属性つき）／client scopes ＋ protocol mappers／clients（属性・redirect・secret・default/optional scope 割当 ＋ mappers）／**seed 利用者の存在**（作成時は資格情報・requiredActions・グループ・ロール割当を運ぶ）／サービスアカウント利用者のロールと属性 | 差分があれば当てる。**集合欄**（redirectUris / webOrigins / scope 割当 / enabledEventTypes …）は宣言が全集合（置換）。**実体**（client / role / group / mapper / user）は加算的（宣言に無い余剰は消さない） |
| 実行時（Keycloak / SC-17 / 本人が正） | 既存の人間の利用者の資格情報・属性・ロール・グループ・`requiredActions`・セッション／`smtpServer`（IADR-0261 決定 2） | **触らない** |

  既存利用者の `requiredActions` を宣言へ戻さないのは、TOTP を登録し終えた利用者へ `CONFIGURE_TOTP` を再要求するためである。
  TOTP 既定は realm の `requiredActions[CONFIGURE_TOTP].defaultAction`（宣言層）で新規利用者へ効く。seed 利用者の宣言を
  変えて既存クラスタへ届けたいときは IADR-0082 決定 4 の破壊経路（`keycloak-data` PVC を消して再 import）を使う。
- **収束**: apply は「計画 → 適用 → 再計画」を最大 3 周し、最後の計画が 0 件でなければ非 0（当てたつもりで当たっていない状態を
  緑にしない）。check は計画だけを行い 1 件でも残れば非 0。最終行 `realms=<n> drift=<m> applied=<k>` を門が読む。
  依存先の実体がまだ無い操作は `deferred` として数え、黙って消さない（次の周で計画し直す）。
- **比較の寛容さ**: スカラーは文字列で（Keycloak は `1025`/`"1025"`、`true`/`"true"` を往復で揺らす）、スカラー配列は集合で、
  オブジェクトは宣言のキーだけを見る。PUT は GET の全体へ宣言を合成して送る（kcadm の `update` と同じ作法。部分表現で
  他の欄を消さない）。

**IADR-0336 決定 3 を置換する**（`reconcile-backchannel-logout.sh` は撤去。`backchannel.logout.url` は client の属性として
本経路が当てる）。

### 3. 管理者名も Secret `keycloak-admin` に持ち、Keycloak と Job が同じキーを読む

`keycloak-admin` に `username` キーを足し（`KEYCLOAK_ADMIN_USER`、既定 `admin`）、`deploy/local/infra/keycloak.yaml` の
`KEYCLOAK_ADMIN` と Job の `KC_ADMIN_USER` が `secretKeyRef` で読む。ESO の `externalsecret-keycloak-admin.yaml` は
`creationPolicy: Merge` なので `password` だけ供給しても壊れない。標準出力・ログに値は出さない。

### 4. 門を 3 つ足す（`check-stack-ready.js` G9 / G10 / G11）。根拠は「同型の事故 2 回目」

| 門 | 何を見るか | 2 回目の根拠 |
| --- | --- | --- |
| **G9 realm の乖離** | `reconcile-realm.sh --check` を走らせ `drift=0` かつ `realms>=1` を要求（書き換えない） | #1115（宛先が届かず後追いを置いた）→ #1088（ConfigMap が 1 日古く TOTP 無効）→ 本作業の実測（差分キー 5 個）。3 度目 |
| **G10 永続化** | `infra-persistence/pvcs.yaml`・`observability-persistence/pvcs.yaml` が宣言する PVC を走査し、`app` ラベルと同名の Deployment が居るなら**参照**と **Bound** を要求。`PERSIST=0` の明示なら notice | #324 close 後の残骸 PVC（`postgres-data` 38 日）→ #1088（`keycloak-data` Pending 13 日）。2 度目 |
| **G11 イメージ参照** | `helm template … -f values-local.yaml`（MSP）と `kubectl kustomize deploy/local/infra-persistence`（infra）の描画結果と、稼働 Deployment のイメージ参照が文字列で完全一致 | 2026-07 に古いスクリプトで作られたクラスタと現行マニフェストの乖離（rabbitmq Secret のキー欠落で実際に踏んだ）→ #1088 の PR 検証用タグ 10 件。2 度目 |

G3 の必須ツールに `helm` / `bash` を足す（抜け道は置かない）。列挙は持たない（PVC はオーバーレイから、イメージは描画結果から走査）。

### 5. 記録に留めるもの（検査器を足さない）

- **`:latest` の中身が develop 最新か**（再ビルドしたが Pod が古いイメージのまま）。本作業でその手段が無いことを確認しただけで、
  事故としては未観測（1 回目に満たない）。置くなら `k8s-local-images.sh` の `--label org.opencontainers.image.revision` と
  Pod の `status.containerStatuses[].imageID` の突合が要る。
- Vault dev のメモリ状態（`auth/oidc` は Pod 再起動で消える。IADR-0363 が実測）。永続化の対象外（別 issue）。

## 理由

- 決定 1: 「既定を間違えると誰も気付かない」型の事故に対し、門は後段でしか効かない。既定そのものを直す。
- 決定 2: 永続化の目的（runtime state を守る）と宣言の再現性（realm JSON が正）は、丸ごと作り直す方式では両立しない。
  差分適用に境界を引けば両立する。境界は「人が変え得るもの」と「宣言が持つもの」で切る。
- 決定 3: 反映経路は**測る側と同じ前提に依存しない**ものにする。pod 内 exec は本体を壊し、ホスト側は環境ごとに前提が違う。
- 決定 4: `.claude/rules` の「同型の事故が 2 回起きたら検査器」に照らして 3 つとも 2 回目以上である。
  G9 が無ければ up の後追いが落ちても EXIT=0 で緑になる（#797 と同型の沈黙）。

## 結果

- 良い影響: 忘れても永続。realm JSON を変えて up を再実行すれば稼働 realm へ届き、届かなければ門が赤くなる。
  PR 検証用のタグが残ったクラスタは門が名指しする。`kcadm.sh` の pod 内 exec が無くなる。
- 悪い影響・トレードオフ: 既存の人間の利用者は宣言を変えても更新されない（境界。破壊経路は残る）。Job のイメージ
  `node:22-alpine` の pull が要る（busybox の G6 と同じ扱い）。`check-stack-ready.js` が `helm` / `bash` を要求する。
  CI の統合スタックも PVC を使う（k3d の local-path。使い捨てなので害は無い）。
- **実測（2026-09-04・作り直し前の稼働クラスタ）**:
  - `node scripts/check-stack-ready.js` → **17 件の失敗**（G10 が 7 件: postgres / keycloak / qdrant / prometheus / loki / tempo /
    grafana が PVC を参照していない。G11 が 10 件: 上記の PR 検証用タグ）。G9 は `realms=2 drift=0`（直前に apply で収束させた後）。
  - `reconcile-realm.sh --check`（ConfigMap をリポジトリから作り直した直後）→ `realms=2 drift=4`
    （`clientScope.create realm-management-roles` / `client.update bff（description, attributes）` / `client.create identity-admin` /
    `deferred service-account-identity-admin`）。`reconcile-realm.sh`（apply）→ pass 1 で 3 件、pass 2 で
    `user.clientRoles.add service-account-identity-admin:realm-management（view-users, manage-users, view-realm）`、
    `realms=2 drift=0 applied=4`。再度 `--check` → `drift=0`。所要 11 秒。
  - 作り直し後の実測は §実測（作り直し後）に追記する。

## 却下した代替案

- **既定 opt-in ＋ 門で赤くする**（決定 1 の A）: 門は up の後にしか走らない。
- **`OVERWRITE_EXISTING` / partial import**（決定 2 の A・B）: runtime state を消す／realm 設定に届かない。
- **pod 内 `kcadm.sh`**（決定 3 の A）: 本体が OOMKilled になる（実測）。
- **`:latest` の中身の門**（決定 5）: 1 回目に満たない。記録に留める。
- **既存利用者の宣言も当てる**: TOTP を登録し終えた利用者へ再要求し、SC-17 の変更を上書きする。境界の外。

## フォローアップ

- `.ai-context/adr/README.md` の索引と `scripts/README.md` の門一覧（8 → 11）を本 ADR で追随した。
- `check-stack-ready.js` の G4 は `curl -sk`（検証しない）のまま残っている（本 ADR の射程外。IADR-0363 の方針に照らせば直す価値がある）。

## 実測（作り直し後・2026-09-04）

`microservices-platform` / `platform-infra` namespace を消し（`ai-stock-trading` は残す）、`origin/develop` `bd6a18f5` のイメージで
`LOCALEDGE=1 ISTIO=1 ESO=1 VAULT=1 OBSERVABILITY=1 HEADLAMP=1 ARGOCD=1 WIKIJS_OIDC=1 ABACSEED=1 SEARCHSEED=1 LOCALEMBED=1 APISERVER_OIDC=1`
（永続化は既定）で立て直した。

- `node scripts/check-stack-ready.js` → **OK**（Deployment 32 件 available。G9 `realm 2 件 / 差分 0 件`、G10 PVC 3＋4 本すべて参照・Bound、G11 一致）。
- **AC-1**（runtime state が Pod 再作成をまたぐ）: 一時利用者を作成 → `rollout restart deploy/keycloak`（Pod `…-wmxnn` → `…-46gs2`）→
  起動ログ `Strategy: IGNORE_EXISTING` / `Realm 'platform' already exists. Import skipped` → 利用者 **count=1**（残存）→ 削除。
  `keycloak-data` Bound・Deployment が参照。
- **AC-2**（realm JSON の変更が届く）: `accessTokenLifespan` 300→301 と `headlamp.description` を変えて ConfigMap を作り直し →
  `--check` が `drift=2`（`realm.update platform — accessTokenLifespan` / `client.update headlamp — description`）→ apply →
  Admin REST で **301 / 'AC-2 probe (issue #1088)'** を確認 → JSON を戻して apply → 300 / 元の説明 → `--check` `drift=0`。
- `scripts/verify-oidc-edge-flow.sh`（`SEARCH_HITS=1 SEARCH_SEEDED=1`）→ **PASS 27 / FAIL 0**。
  `scripts/verify-tool-oidc-logins.sh` → **PASS 15 / FAIL 0**（Vault の `auth/oidc` は runbook STEP 2 を手で入れた後。
  README 経路 2 の CA 直渡しは `error checking oidc discovery URL` で落ち、CA を pod 内ファイルで渡すと通った）。
- 作り直しで踏んだこと（本 ADR の射程外だが記録する）:
  - 空の namespace から `ISTIO=1` ＋ `ESO=1` で立てると、サイドカー注入後の `rollout status` が ESO の Secret 供給前に
    10 分待って up 全体が落ちる → 待ちを best-effort にした（門は G1）。
  - `LOCALEDGE=1` ＋ `ISTIO=1` の再実行では、Traefik の Service が Istio エッジで落とされているため、HelmChartConfig の
    再 apply が chart の reinstall を起こし、180 秒の待ちに間に合わず一度落ちる（再実行で通る）。既知の #953 の門の
    再実行時の限界の一種。
  - `nerdctl images` の CREATED 列は再タグした名前の記録日を出すので、`:latest` が焼き直されたかの根拠にならない
    （イメージ内の DLL の mtime で確かめた）。決定 5 の「`:latest` の中身」を見る門が無い限界がここにも現れる。

## 関連

- 置換: [IADR-0082](./IADR-0082_local-k8s-infra-persistence.md) 決定 1（opt-in）／[IADR-0336](./IADR-0336_backchannel-logout-destination-and-mesh-boundary.md) 決定 3（kcadm の後追い）
- 同型の後追い: [IADR-0327](./IADR-0327_wikijs-setup-bootstrap.md)・[IADR-0342](./IADR-0342_wikijs-oidc-strategy-idempotent-seed.md)
- 触らない層の根拠: [IADR-0261](./IADR-0261_keycloak-theme-and-smtp-injection.md) 決定 2・[IADR-0332](./IADR-0332_keycloak-smtp-externalsecret-wiring.md)
- 門の置き場: [IADR-0248](./IADR-0248_integration-stack-ci-readiness-gate.md)
