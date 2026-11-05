---
title: IADR-0676 基盤の資格情報の段階の fail-closed を続ける。秘匿管理の固定の管理者トークンは印の後は作り直さない形を段階で入れ、値を stdin で渡す。Grafana の管理者と DB の初期化の kp を対象に足す。dev のクラスタの判定にクラスタ側の印を段階で足す（#2407）
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0124, IADR-0672, IADR-0574, IADR-0517, IADR-0457]
author: claude
created: 2026-11-05
updated: 2026-11-05
related_specs:
  - ../specs/20261105_2407_infra-credentials-staged-followup.md
---

# IADR-0676: 基盤の資格情報の段階の fail-closed を続ける（#2407）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: コーディネーターの指示（2026-11-05）・claude（段の分け方・名前・印の形）

## 起点・関連

- 起点 issue: #2407。IADR-0672 の続き（残余 1・3 を含む）。
- 計画: **ADR-0124 決定 1**。要求 **NFR-18**。
- 前提:
  - [IADR-0672](./IADR-0672_infra-credentials-staged-fail-closed.md)（段階の対象・`INFRA_CREDENTIALS_ENFORCE`・検査と作り直し）
  - [IADR-0517](./IADR-0517_dev-client-secret-guard-kube-context-allowlist.md)（kube context の許可集合）
  - [IADR-0457](./IADR-0457_local-vault-file-storage-pvc-and-in-pod-unseal.md)（秘匿管理の永続化のラッパー）

## コンテキストと課題

- IADR-0672 決定 2 で、起動器は既存の Secret の値を引き継ぐようになった。作り直した固定の管理者トークンは、起動器の再実行で戻らない。
- ただし、永続化のラッパーは、Secret の値のトークンが無いときに起動のたびに作る。Secret が作り直しのスクリプトの外で変わったときも同じである。作る条件を絞る口が無い。
- ラッパーと作り直しのスクリプトは、トークンの値を Pod の中の `vault` の引数で渡していた（unseal 鍵は #1793 で stdin に移した）。
- IADR-0672 の母集合の走査語は env の既定値（`:-`）だった。資格情報を env で与えていない製品（Grafana の内蔵の管理者）は引けていなかった。DB の初期化の `kp` は残余 1 だった。
- dev のクラスタの判定は context の名前だけだった（IADR-0517 残余 3）。

## 検討した選択肢

1. **ラッパーで固定のトークンをもう作らない（初期の root トークンだけを使う）**
   - ESO の store・Vault の種・OIDC の bootstrap が固定のトークンで動く前提を崩す。既存のクラスタが止まる。
2. **ラッパーが作る条件を「印が無いとき」に絞り、段階で入れる（採用）**
   - 印は PVC 上のファイル（値を持たない）。作ったとき・在ることを確かめたときに置く。
   - 印が在るのに無いトークンは、段 1 は警告して作り、段 2 は作らずに止める。
3. **dev の判定に明示の env（`DEV_CLUSTER=1`）を足す**
   - 実行のたびに与える値で、クラスタの性質ではない。名前と同じく、取り違えた context でも与えられる。
4. **dev の判定にクラスタ側の印（ConfigMap）を足し、段階で入れる（採用）**
   - クラスタに付く手掛かりで、context の取り違えに強い。

## 決定

### 決定 1 — 固定の管理者トークンは、印の後は作り直さない形を段階で入れる

- ラッパーは、固定のトークンを作ったとき、または在ることを確かめたときに、PVC へ印 `$VAULT_DATA_DIR/.local-dev-root-token-created`（値を持たない）を置く。
- 印が無いとき（初回の起動・本 IADR より前のストア）は従前どおり作る。
- 印が在り、Secret の値のトークンが無いとき:

  | 段 | 切り替え | 動き |
  | --- | --- | --- |
  | 1（既定） | `root-token-init-only` が `1` でない | 作り、名指して警告する |
  | 2 | `root-token-init-only=1` | 作らずに起動を失敗させ、手順書を名指す |

- 切り替えは ConfigMap `platform-infra/vault-local-switches` の `root-token-init-only`。Pod は optional に読む（無ければ段 1）。
  - 起動器は `VAULT=1` の実行のたびに、`INFRA_CREDENTIALS_ENFORCE=1`（IADR-0672 の段 2 の切り替え）なら `1`、そうでなければ `0` を書く。切り替えを 1 つに保つ。
  - Pod は起動のときに読む。次の起動から効く。
- 作り直しのスクリプト（`vault-root-token`）は、作ったトークンが Secret に入ってから秘匿管理を作り直す。ラッパーは在ることを確かめるだけで作らない。段 2 でも通る。

### 決定 2 — トークンの値は stdin で渡す

- ラッパーの作成は `vault write auth/token/create-orphan id=-`（値は stdin）にする。
- 作り直しのスクリプトの作成・失効（元へ戻すときを含む）も同じ形にする（`auth/token/create-orphan id=-`・`auth/token/revoke token=-`）。
- 使うトークンは環境変数 `VAULT_TOKEN` で渡す（引数に載せない。従前どおり）。
- 値は Pod の中の sh へ渡すスクリプトに単一引用符で埋めるので、埋める前に字の範囲（英数字・`.` `_` `-`）を検める。外れたら何も変えずに止め、手順書の手作業へ倒す（値は表示しない）。

### 決定 3 — Grafana の内蔵の管理者を対象に足す

- 起動器は、`OBSERVABILITY=1` のとき Secret `platform-infra/grafana-admin`（`password`）を **明示（`GRAFANA_ADMIN_PASSWORD`）＞ 既存の値 ＞ 乱数**で作る。開発用の値を持たない（セッションストア・観測の前段と同じ形）。
- Grafana は `GF_SECURITY_ADMIN_PASSWORD` を optional に読む（Secret が無くても起動する）。
- 製品は管理者を DB の初回の作成のときしか env で作らない。永続化の overlay は、コンテナの command を薄いラッパー（`/bin/sh -c`）に差し替え、起動のたびに Secret の値を DB へ合わせてから製品の入口 `/run.sh` へ `exec` する（`grafana cli admin reset-admin-password --password-from-stdin`。DB が在り env が在るときだけ。値は stdin。失敗しても起動は止めない）。init container は使わない（イメージの参照を 2 か所に持たないため）。
- 検査（`check-infra-credentials.sh`）は、Grafana が在るとき Secret が無いか開発用の値と同じなら名指す。作り直し（`rotate-infra-credentials.sh grafana-admin`）は Secret を乱数で書き、Grafana を作り直す。

### 決定 4 — DB の初期化の `kp` を Secret の値で作る（IADR-0672 残余 1）

- 起動器は、`APP_DB_PASSWORD`（[1/7] の直後に決めた値）を Secret `platform-infra/postgres-init-users`（`kp-password`）に置く。DB の Pod は optional に読む。
- 初期化のスクリプトは、在ればその値で `kp` を作る。無ければ従前の値で作る（段 1）。値は psql の `\getenv` で読み、引数に載せない。
- 初期化が走るのは空の PVC の初回だけである。既存のクラスタの DB には効かない（作り直しのスクリプトが直す）。

### 決定 5 — dev のクラスタの判定に、クラスタ側の印を段階で足す

- 印は ConfigMap `kube-system/msp-dev-cluster`（`cluster-kind: dev`）。値は秘密ではない。
- 置く口は 2 つ:
  - 起動器が、この実行で k3d のクラスタを作ったとき（作ったばかりのクラスタは dev である）。
  - 利用者の明示 `MARK_DEV_CLUSTER=1`。context の名前が許可集合に在るときだけ置く（名前と印の両方を揃える）。
- 判定（`dev_client_secret_context_is_dev`）:

  | 段 | 切り替え | 名前が許可集合・印あり | 名前が許可集合・印なし | 名前が許可集合に無い |
  | --- | --- | --- | --- | --- |
  | 1（既定） | `DEV_CLUSTER_MARKER_REQUIRED` が `1` でない | dev | dev（入口が警告する） | dev ではない |
  | 2 | `DEV_CLUSTER_MARKER_REQUIRED=1` | dev | **dev ではない** | dev ではない |

- 判定は純関数のまま保つ。印は入口（起動器・Vault の種・realm の追随）が先頭で 1 回読み（`dev_cluster_marker_probe`）、環境変数 `DEV_CLUSTER_MARKER`（`present` / `absent`）で渡す。読まなかったときは `absent` と同じに扱う（段 2 で fail-closed）。
- 段 2 の既定化は、利用者が既存の dev のクラスタに印を置いた後に別の PR で入れる。

### 決定 6 — 秘匿管理へ届く送り手を NetworkPolicy で絞る（段 1 は opt-in）

- `deploy/local/vault-networkpolicy/networkpolicy.yaml`（Ingress だけ。8200）。送り手は走査で引いた次の 5 つに限る:
  - ESO のコントローラ（`external-secrets` 名前空間・`app.kubernetes.io/name: external-secrets`）
  - `bff-service`・`datasource-service`（`microservices-platform`）
  - バックアップの CronJob（`platform-infra`・`app: platform-backup` / `app.kubernetes.io/component: vault`）
  - 管理ツールのエッジ（`kube-system` の Traefik・`istio-system` の `istio-ingressgateway`）
- 起動器・Vault の種・OIDC の bootstrap は `kubectl exec`（Pod の中）で叩くので、送り手に入れない。
- Egress は絞らない（k8s auth の TokenReview・OIDC・audit の送り先を止めない）。
- 段 1: 起動器の `VAULT_NETWORK_POLICY=1` で当て、`=0` で外す。未指定は何も変えない。取りこぼした送り手は秘匿管理の停止になるため、当てた後に ESO の同期・SC-22 の一覧・バックアップの回を確かめてから既定にする（段 2 は別の PR）。
- 試験は、`deploy/`・`scripts/`・`src/`（試験・文書を除く）の中で秘匿管理の Service の名前と 8200（短縮形 `vault:8200` から FQDN まで）か `number: 8200` を書くファイルが、表に在ることを突き合わせる。網はこの字面だけで、ほかの書き方（環境変数で宛先を与える・別名の Service・取引ユニットのリポジトリ）は捕まえない。
- **段 2（既定化）の前提条件**: 当てる前に、送り手の母集合を Pod のラベルと ServiceAccount 名の軸で引き直す（稼働の Pod の一覧と、秘匿管理の audit の記録に現れる送り手を突き合わせる）。取引ユニットのリポジトリの送り手も含める。

## 結果

- 良い影響:
  - 作り直した固定の管理者トークンが、再起動と再実行を跨いで保たれることを試験が固定する。段 2 では、Secret が作り直しの外で変わっても管理者トークンが作られない。
  - トークンの値が Pod の中のプロセスの引数に載らない。
  - Grafana の管理者と DB の初期化の `kp` が、Secret の値で決まる。
  - dev の判定が、context の名前とクラスタの印の 2 つで行える（段 2）。
- 悪い影響:
  - 段 1 のうちは、印の後に無いトークンも作る（報告だけ）。
  - 段 2 の秘匿管理は、Secret を作り直しの外で変えると起動しない。戻すのは手順書（secret-rotation-runbook B-1）である。
  - 既存の Grafana は、永続化の overlay を当て直した次の起動で、管理者のパスワードが Secret の値に変わる。
  - 永続化の overlay は Grafana のイメージの ENTRYPOINT を command で上書きする。イメージの版を上げて入口（`/run.sh`）が変わると、ラッパーの `exec` が失敗して Grafana が起動しない（版を上げるときに入口を確かめる）。

## 残余

1. 段 2 の既定化（`INFRA_CREDENTIALS_ENFORCE`・`DEV_CLUSTER_MARKER_REQUIRED`）は別の PR（PoC で検査が 0 を返し、印を置いた後）。
2. 初期の root トークン（PVC 上の初期化の出力）は失効させない（IADR-0457 の判断のまま）。
3. `PERSIST=0`（`-dev`）の秘匿管理は毎回作る（使い捨てのスタック専用）。
4. DB 利用者 `ai` は取引ユニットと対で回す（IADR-0672 残余 2 のまま）。
5. 取引ユニット（AST）のリポジトリが秘匿管理へ直接繋ぐかは、本リポジトリからは確かめていない（ESO 経由だけなら影響しない）。段 2 の前に確かめる。
6. ラッパーが PVC へ印を書く処理は、書けなくても起動を止めない（`|| true`）。印が書けないと段 2 の判定が初回扱いへ倒れるので、段 2 の既定化の前に、失敗を見える形（警告の行・検査の項目）にする。
