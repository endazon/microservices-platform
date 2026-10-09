---
title: IADR-0518 Keycloak の realm の取り込み元を Secret に分け、管理用の 3 クライアントの secret は env を与えたときその値で渡す。起動器の判定は ESO の有無によらず 3 つを見る
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0124, ADR-0123, IADR-0517, IADR-0485, IADR-0369, IADR-0066, IADR-0082]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密は相手と対で書く・本番の秘密を realm の宣言から外す）
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md 決定 2（管理用の機密クライアント）
related_specs:
  - ../specs/20261009_1834_realm-import-secret.md
---

# IADR-0518: realm の取り込み元を Secret に分け、管理用の 3 クライアントへ env の値を渡す（#1834）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-09
- 決定者: オーケストレータ（差し替えの案を採る。2026-10-09）・claude（取り込み元の形・判定の範囲）

## 起点・関連

- 起点 issue: **#1834**（PR #1833〔#1830〕の独立監査 🟡1・🟢 から分離）。[IADR-0517](./IADR-0517_dev-client-secret-guard-kube-context-allowlist.md) の残余 1 を閉じる。
- 計画: **ADR-0124 決定 1**・**ADR-0123 決定 2**。要求 **NFR-18**。
- 前提: IADR-0517（kube context の許可集合・`--check-dev-secrets`）・[IADR-0485](./IADR-0485_paired-secrets-create-only-declarations.md)（宣言の `secret` は作成時にだけ運ぶ）・[IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md)（永続化と realm の後追い Job）・[IADR-0066](./IADR-0066_local-k8s-dev-environment.md)（経路B の realm の取り込み）。
- 基点コミット: MSP `origin/develop` `38f2e27f`。

## コンテキストと課題

経路B の Keycloak（`deploy/local/infra/keycloak.yaml`）は `start-dev --import-realm` で起動し、空の PVC の初回（と、realm を消した後の再起動）に `/opt/keycloak/data/import` の realm JSON を取り込む。取り込み元は ConfigMap `keycloak-realms` で、起動器（`scripts/k8s-local-up.sh` [3/7]）が realm ファイルから作っていた。

realm ファイルの 3 クライアント（`identity-admin`・`reset-gate`・`mcp-client-admin`。レルム管理のロールを持つ）の `secret` は公知の dev の値である。IADR-0517 は起動器の 3 つの「作る口」を dev 以外の context で止めたが、取り込みは起動器の外として残した（残余 1）。そのため dev 以外のクラスタで env を正しく与えて起動しても、(a) Keycloak 側だけ公知の値になり露出が閉じず、(b) env の値を持つ消費側の Secret と食い違って `invalid_client` になる。

## 検討した選択肢

1. **取り込み元を作るときに、宣言の secret を env の値へ差し替える（採用）** — 新しいクラスタは最初から env の値で立つ。露出と食い違いの両方が閉じる。
2. **起動の最後に `--check-dev-secrets` を自動で回し、dev の値が残っていれば非 0** — 検知だけで、食い違い（`invalid_client`）は閉じない。運用者に回す手間が毎回残る。
3. **宣言から dev の値を外す** — ADR-0124 決定 1 の射程（本番の秘密を宣言に置かない）。dev の手順と CI の既定が変わり、本 issue の範囲を超える。

取り込み元の形について:

- **A. ConfigMap `keycloak-realms` の中身を差し替える** — 実の secret が平の ConfigMap に入る。加えて、同じ ConfigMap を読む後追い Job の `--check-dev-secrets` は「宣言の値＝ dev の値」と稼働を比べるので、比較元が env の値に変わり、**正しく回したクライアントを dev の値と誤って名指す**。採らない。
- **B. 取り込み元だけを Secret に分ける（採用）** — 宣言の ConfigMap は宣言のまま残す（後追い Job・申請の門が読む）。Keycloak だけが Secret を読む。

## 決定

### 決定 1: 取り込み元は Secret `keycloak-realm-import`（`platform-infra`）

- `keycloak.yaml` の `realms` ボリュームを `secret.secretName: keycloak-realm-import`（非 optional）にする。マウント先・読み取り専用は従来どおり。
- 中身のキーは従来の ConfigMap と同じ（`microservices-platform-realm.json`、AST の realm があれば `ai-stock-trading-realm.json`）。
- **env が未設定（dev の既定）でも常に Secret で作る**（形を 1 つにする）。そのときの中身は宣言とバイト等価（末尾の改行を除く）。CI の `integration-stack` / `cutover-rehearsal` は env を与えないので、取り込む中身は変わらない。
- ConfigMap `keycloak-realms` は宣言のまま作り続ける。読み手は realm の後追い Job（期待値・`--check-dev-secrets` の比較元）と申請の門（宣言の `resetPasswordAllowed`）。
- `defaultMode` は絞らない（ConfigMap と同じ既定）。Keycloak のコンテナの利用者・グループを稼働で確かめずに絞ると読めなくなるおそれがあり、Pod の中の読み手は Keycloak 自身だけである。

### 決定 2: 差し替えは判定器の 1 関数、対象は判定器の集合

- `scripts/lib/dev-client-secret-guard.sh` の `dev_client_secret_realm_for_import <realm ファイル>`。対象は `DEV_CLIENT_SECRET_GUARDED`、env の名前は `dev_client_secret_env_name`、宣言の値は `dev_client_secret_dev_value`（どれも同じファイル＝単一情報源。宣言との一致は `keycloak-realm-reconcile.test.js` が固定済み）。
- **env が空でないクライアントだけ**、宣言の `"secret": "<dev の値>"` を `"secret": "<env の値>"` へ置き換える。**context を見ない**（どの context でも env が勝つ。dev 以外で env が無い場合は [1/7] の判定が先に止める）。
- 宣言の該当箇所が**ちょうど 1 か所**でなければ止める。値に `"`・`\`・制御文字があれば止める（JSON を壊さない）。どちらも値は出さない。
- 外部コマンドを使わない（bash の置換。`patsub_replacement` を切り、値の `&` をそのまま置く）。値はどのプロセスの引数にも載らない。差し替えたクライアント名だけを標準エラーへ出す。

### 決定 3: Secret は既存の `apply_secret` で作る

0700 の一時ディレクトリの 0600 のファイルへ組み込みの `printf` で書き、`kubectl create secret generic --from-file` にはパスだけを渡し、EXIT trap で消す（#1793 と同じ口）。差し替えに失敗したら起動器はその場で止まり、Secret も infra（Keycloak）も当てない。

### 決定 4: 起動器の判定は ESO の有無によらず 3 つを見る（IADR-0517 決定 4 の表の k8s-local-up.sh の行を改める）

- 取り込み元が ESO の有無によらず 3 つの env から作られる＝取り込みが 4 つ目の「作る口」になった。
- 従来は `ESO=1` で `identity-admin`・`mcp-client-admin` を Vault の種（`bootstrap.sh`）の判定に任せていたが、`bootstrap.sh` は ESO のブロック（[7/7] の後）で走り、Keycloak の初回の取り込み（[4/7]）より後である。`ESO=1` の dev 以外のクラスタの初回は、bootstrap が止める前に Keycloak が dev の値で 2 つを作っていた。
- よって [1/7] の判定は 3 つとも渡す。`ESO=1` の dev 以外のクラスタでは 3 つの env が要る（Vault の種と同じ値を与える。`bootstrap.sh` の判定は Vault の無い KV について従来どおり残る）。

### 決定 5: `realm-reconcile-job.yaml` は `check-deploy-manifests` の対象に入れない

- 同検査器の設計の要点 1 は「overlay と chart を走査で見つける・列挙を持たない」である。overlay に属さず `kubectl apply -f` で当てる単独のマニフェストは 42 本ある（`deploy/local/vault/eso/` の ExternalSecret 群・`aliases/`・`argocd/` 等。2026-10-09 の実測）。1 本だけ名前で足すと列挙を持ち込み、残り 41 本と扱いが割れる。
- 単独のマニフェストを類として検査へ入れるかは別の判断であり、本 IADR は扱わない。この Job の形（env・マウント・`DEV_CLIENT_SECRETS_ALLOWED` の既定 `deny`）は `scripts/k8s-local-up.test.js` が読んで固定している。

## 結果

- 良い影響: 新しい dev 以外のクラスタで 3 つの env を与えれば、Keycloak のクライアントは最初から env の値になる。公知の値の露出と `invalid_client` の食い違いが同時に閉じる。dev の既定と CI は取り込む中身が変わらない。
- 悪い影響: 取り込み元の Secret は実の secret を含み得る（Secret として扱う）。`ESO=1` の dev 以外のクラスタは毎回 3 つの env が要る。手で起動する手順は Secret も作る必要がある（`deploy/local/README.md`）。

## 残余

1. **env を与えずに取り込みが走る場合**（dev のクラスタ・dev 以外で `ALLOW_DEV_CLIENT_SECRETS=1`）、取り込みは宣言の dev の値で作る。`ESO=1` で Vault の値を回した後に env を与えず空の PVC から起動すると、Keycloak と Vault が食い違う。起動の後に `--check-dev-secrets` で検知する（手順書）。
2. 対象外のクライアント（IADR-0517 残余 2）は宣言の値で取り込む。広げるときは判定器の対象集合へ足せば、差し替えも同じ集合に従う。
3. docker compose の経路（`deploy/docker-compose.yml`）は realm ファイルを直接マウントする手元専用の経路であり、差し替えない（IADR-0517 残余 4 と同じ）。

## フォローアップ

- なし（本 IADR と同じ PR で配備・試験・文書まで入れた）。
