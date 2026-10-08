---
title: IADR-0517 dev 以外の kube context では、レルム管理のロールを持つ機密クライアントを公知の dev の secret で作らない。判定は context の許可集合（未知のクラスタは安全側）、稼働中の dev の値は後追いの check-dev-secrets で検知する
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0124, ADR-0123, IADR-0485, IADR-0516, IADR-0404, IADR-0329, IADR-0369, IADR-0286]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密・初期投入は無いときだけ作る・本番の秘密を realm の宣言から外す）
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md 決定 2（管理用の機密クライアント）
related_specs:
  - ../specs/20261009_1830_dev-secret-guard.md
---

# IADR-0517: dev の値の管理用クライアントを dev 以外のクラスタで作らない —— kube context の許可集合と、稼働中の検知（#1830）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-09
- 決定者: 利用者裁定（2026-10-09。#1830 のコメント）・claude（適用点と検知の形）

## 起点・関連

- 起点 issue: **#1830**（PR #1827〔#1817〕の独立監査 🟡4 から分離）。
- 計画: **ADR-0124 決定 1**（realm の宣言の client `secret` は開発用・初期投入は無いときだけ作る）・**ADR-0123 決定 2**（`mcp-client-admin`）。要求 **NFR-18**。
- 前提: [IADR-0485](./IADR-0485_paired-secrets-create-only-declarations.md)（宣言の `secret` は作成時にだけ運ぶ・Vault の種は無いときだけ作る・宣言は開発用の形に限る検査 8）・[IADR-0516](./IADR-0516_sc12-keycloak-service-account-provisioning.md)（`mcp-client-admin`）・[IADR-0404](./IADR-0404_nearby-mta-relay-and-realm-ownership.md)（`reset-gate`）・[IADR-0329](./IADR-0329_identity-admin-keycloak-provider-and-realm-wiring.md)（`identity-admin`）・[IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md)（realm の後追い Job）・[IADR-0286](./IADR-0286_default-credentials-fail-fast.md)（既定の資格情報を埋め込まない）。
- 基点コミット: MSP `origin/develop` `2f5d9c04`。

## コンテキストと課題

経路B の起動器は、env（`*_CLIENT_SECRET`）が無ければ realm の宣言と同じ dev の値（`<client>-dev-secret-change-me`）で機密クライアントの secret を作る。作る口は 3 つある —— `scripts/k8s-local-up.sh` の手動の Secret、`deploy/local/vault/eso/bootstrap.sh` の Vault の種、realm の後追い Job（`reconcile-realm.js`）の無い client の作成。リポジトリは公開なので、dev 以外のクラスタ（共有 PoC など）でこれらを使うと、Keycloak のトークン端点に届く誰でもそのクライアントのトークンを得られる。

`identity-admin`（manage-users）・`reset-gate`（manage-realm）・`mcp-client-admin`（manage-clients・manage-users）はレルム管理のロールを持ち、`mcp-client-admin` は全クライアントの secret を読めるのでレルムの全権に等しい。起動器には「dev かどうか」を判定する入力が無かった。PR #1827 は暫定として「dev 以外では起動の直後に回す」を文書に書いた。

## 検討した選択肢

1. **明示の環境宣言（例: `PLATFORM_ENV=dev` のときだけ dev の値を許す、または `PLATFORM_ENV=shared` のときだけ止める）** — 宣言を付け忘れたときの既定がどちらへ倒れるかで 2 通りある。「宣言があれば止める」は未知のクラスタで素通りする（付け忘れ＝事故）。「宣言が無ければ止める」は手元の k3d まで毎回宣言を要し、既存の手順・CI をすべて書き換える。
2. **kube context の許可集合（採用）** — 起動器が書き込む先（kubectl の current context）そのものを入力にする。既知の dev の形（`k3d-*` / `kind-*` / `rancher-desktop` / `docker-desktop`）だけを dev とみなし、それ以外（読めない・空を含む）は dev ではない。手元と CI の手順は変わらない。
3. **1 と 2 の併用（両方が dev を示すときだけ許す、等）** — 入力が 2 つになり、食い違ったときの扱いをもう 1 つ決める必要がある。2 だけで未知のクラスタが安全側に倒れるので、足しても守りは増えない。

## 決定

### 決定 1: 判定の入力は kube context の許可集合とする

- dev の許可集合は **`k3d-<名前>`・`kind-<名前>`・`rancher-desktop`・`docker-desktop`**（接頭辞の 2 つは名前が空でないこと。大文字小文字を区別する）。
- それ以外は dev ではない。**`kubectl config current-context` が失敗した・空を返したときも dev ではない**（未知のクラスタで既定が安全側に倒れる。選択肢 1 を採らない理由）。
- context は `kubectl config current-context` で読む。`KUBECONFIG` は kubectl が尊重する。3 本の呼び出し元はどれも `--context` を使わない（current context へ書く）ので、読む context と書く先は一致する。
- CI の `integration-stack` / `cutover-rehearsal` は k3d で `k3d-<CLUSTER>` を作るので dev である（`k8s-local-up.test.js` が 2 本のワークフローから読んで固定する）。

### 決定 2: 止める条件と対象

- 対象は **レルム管理のロールを持つ 3 クライアント**（`identity-admin`・`reset-gate`・`mcp-client-admin`）。他のクライアントは対象外（残余 2）。
- dev ではない context で、対象のクライアントを **env が空（未設定）か dev の値と同じ**値で作ろうとしたら、そのクライアント名と理由（どの env を与えるか・上書きの方法）を告げて非 0 で止まる。**値は出力しない。**
- 判定は純関数 1 本（`scripts/lib/dev-client-secret-guard.sh` の `dev_client_secret_decide <context> <上書き> <client>=<value>...`。外部コマンドを呼ばない）に閉じ、3 本の呼び出し元で共有する。真理値表は `scripts/k8s-local-up.test.js` が持つ。

### 決定 3: 明示の上書きは `ALLOW_DEV_CLIENT_SECRETS=1`

- 値は `1` だけを上書きとみなす（`true` 等は上書きにならない＝止まる）。通すときは `!!! WARNING` の数行を標準エラーへ出す。
- 用途は「dev のクラスタだと分かっているが context の名前が許可集合に無い」場合に限る。

### 決定 4: 適用点は 3 つ。どれも書き込みより前に判定する

| 適用点 | 位置 | 判定に渡すもの |
| --- | --- | --- |
| `scripts/k8s-local-up.sh` | `[1/7]` の直後（k3d が context を切り替えた後・Secret を 1 つも書く前） | `reset-gate` は常に。`identity-admin`・`mcp-client-admin` は `ESO=1` でないときだけ（`ESO=1` では Vault の種が作る） |
| `deploy/local/vault/eso/bootstrap.sh` | Vault へ何か書く前（policy・auth を含む） | 3 つの KV のうち **まだ無いもの**だけ（在る KV は `vkv_create_if_absent` が触らない。IADR-0485） |
| realm の後追い（`reconcile-realm.sh` → Job の `reconcile-realm.js`） | Job の apply の各周の書き込み前 | Job は context を持たない。**ホスト側の `reconcile-realm.sh` が同じ判定器（`dev_client_secret_create_allowed`）で決め、Job の env `DEV_CLIENT_SECRETS_ALLOWED`（`allow` / `deny`）として渡す**。マニフェストの既定は `deny`（直接 apply しても安全側）。`allow` 以外で、3 クライアントのどれかを宣言の secret で作る操作（`client.create`・`realm.create`）があれば、その realm には何も書かずに名指して非 0 |

Job へ渡すのを env にしたのは、判定の正を shell の判定器 1 本に保つためである（Job の中で context を推測する材料は無く、JS に許可集合を複写すると 2 か所になる）。

### 決定 5: 稼働中の dev の値は `--check-dev-secrets` で検知する

- `reconcile-realm.js --check-dev-secrets`（Job では `RECONCILE_MODE=check-dev-secrets`。ホストの入口は `bash deploy/local/keycloak-setup/reconcile-realm.sh --check-dev-secrets`、Job 名 `keycloak-realm-dev-secret-check`）。
- 3 クライアントについて `GET …/clients?clientId=<id>` と `GET …/clients/<id>/client-secret` だけを打ち、稼働の値が宣言の値（＝ dev の値）と一致するものを `dev-secret <client>` と名指して非 0 で終える。一致しなければ `ok`、稼働に無ければ `absent`（どちらも 0）。realm が無い・対象を宣言する realm が無いときは非 0（測れないを緑にしない）。
- **読むだけで書かない。値は出力しない**（判定の結果に値を持たせない）。稼働の secret を読むのはこのモードだけで、apply / check の `collectLive` は従来どおり読まない（IADR-0485）。
- 呼ぶ場面: dev 以外のクラスタで起動した直後と、回した後の確認（`docs/operations/paired-secret-rotation-runbook.md`・`docs/security/security.md`）。

## 結果

- 良い影響: 未知のクラスタで 3 クライアントが dev の値で作られる経路（3 つの口）が既定で閉じる。手元（k3d / Rancher Desktop / Docker Desktop / kind）と CI は変わらない。稼働中の dev の値を名指しで検知できる。
- 悪い影響: 許可集合に無い名前の dev のクラスタでは、env か上書きが要る。

## 残余

1. **Keycloak 本体の `--import-realm`**（空の PVC の初回起動）は宣言の dev の値で 3 クライアントを作る。起動器の外なので止めない。dev 以外のクラスタでは起動の直後に `--check-dev-secrets` を回し、対で回す（手順書）。宣言から dev の値を外すことは ADR-0124 決定 1 の射程（本番の秘密を宣言に置かない）であり、本 IADR は扱わない。
2. **対象外のクライアント**（`bff`・east-west の `*-service`・道具の OIDC・`synthetic-monitor`・AST の 3 つ）も同じ形で dev の値になる。レルム管理のロールを持たないので裁定の範囲外とした。同じ守りを広げるときは判定器の対象集合（`DEV_CLIENT_SECRET_GUARDED` と `DEV_SECRET_GUARDED_CLIENTS`。試験が一致を固定する）へ足す。
3. **判定は context の名前だけを見る。** 共有クラスタの context を `k3d-*` 等の名前にすれば dev とみなされる。名前を付ける人の責任とし、手順書に書く。
4. **docker compose の経路**（`deploy/docker-compose.yml`）は kube context を持たない手元専用の経路であり対象外とした。

## フォローアップ

- なし（本 IADR と同じ PR で配備・試験・文書まで入れた）。
