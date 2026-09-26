---
title: 再実装版への切替（6 資産の破棄と realm の作り直し）移行仕様書
type: migration-spec
status: draft
author: Claude
created: 2026-09-25
updated: 2026-09-26
---
<!-- trace:
ids: [NFR-05, NFR-18]
adrs: [ADR-0002, ADR-0008, ADR-0032]
iadrs: [IADR-0459, IADR-0082, IADR-0197, IADR-0210, IADR-0369, IADR-0377, IADR-0456, IADR-0457]
specs: [20260925_457_cutover-discard-and-rebuild, 20260909_issue-457_cutover-decision-table-draft]
issues: [#457, #454, #439, #458]
-->

# 移行仕様書: 再実装版への切替 —— 6 資産の破棄と realm の作り直し

> 利用者の裁定（6 資産はすべて破棄し、Keycloak realm は realm.json から作り直す）の下で、切替を**どう行い、
> どう確かめるか**を定める。判断の論拠は trace ブロックの実装 ADR にある。
>
> 🔴 **本書の手順は稼働クラスタのデータを消す。** 本書を書いた時点では、どの手順も稼働クラスタで実行していない。
> **実行はオーナーが行う。** 先に使い捨てクラスタでリハーサルすること（§リハーサル）。

## 移行概要

| 項目 | 内容 |
| --- | --- |
| 移行対象 | データ（6 資産）と Keycloak の realm |
| 移行元 | 稼働中の経路B（ローカル k3s）の状態 |
| 移行先 | 同じクラスタ上に、同じ配備を**空の状態から**作り直したもの |
| 方式 | **メンテナンスウィンドウでの一括**（静止 → 破棄 → 再構築 → 検証 → 再開）。段階切替・並行稼働は採らない |
| データの引き継ぎ | **無し**（裁定。§判断表） |

**段階切替を採らない理由**: 段階切替が守るのは「移すデータ」であり、破棄の裁定で移すデータが無くなった。
可用性 99.9% の目標は**計画停止を除き**、しかも本切替は go-live の前に行う。

## 判断表（裁定済み）

| 資産 | 裁定 | 作り直した後の状態の出どころ |
| --- | --- | --- |
| platform アプリ DB（MSP のサービス DB。Wiki.js の DB を含む） | **破棄** | 各サービスの起動時マイグレーション。ABAC の属性辞書とポリシーは `deploy/local/abac-seed/` から、タグ辞書は `deploy/local/tag-seed/` から再投入 |
| Keycloak realm | **realm.json から作り直す** | `deploy/keycloak/microservices-platform-realm.json`（旧 realm 名が残っていれば同時に解消する） |
| Qdrant | **破棄** | 空（文書が入り直せば索引が作られる） |
| MinIO | **破棄** | 空（バケットはサービスの起動時に作られる） |
| Wiki.js | **破棄** | 空（初期化は `deploy/local/wikijs-setup/bootstrap.sh`。冪等） |
| 可観測性データ（Prometheus / Loki / Tempo） | **破棄** | 空 |

## 🔴 破棄の境界 —— 捨てるものと同じ入れ物に、捨ててはならないものが同居している

| 入れ物 | 捨てる | **同居していて触らない** | 消し方 |
| --- | --- | --- | --- |
| Postgres（`platform-infra`・PVC `postgres-data`） | MSP のサービス DB | **ai-stock-trading の DB**（売買 PoC が使っている） | **DB 単位で作り直す。PVC は消さない** |
| Keycloak（`platform-infra`・PVC `keycloak-data`） | realm `platform`（旧名 `microservices-platform` が残っていればそれも） | **master realm と ai-stock-trading の realm** | **realm 単位で消し、Keycloak の再起動で入れ直す。PVC は消さない** |
| Qdrant（`platform-infra`・PVC `qdrant-storage`） | すべて | — | PVC ごと作り直す |
| MinIO（`microservices-platform`・PVC `minio-data`） | すべて | — | PVC ごと作り直す |
| Wiki.js（`microservices-platform`・PVC `wiki-js-data`） | すべて | — | PVC ごと作り直す（DB は上の Postgres の行に含まれる） |
| Prometheus / Loki / Tempo（`platform-infra`・PVC `prometheus-data` / `loki-data` / `tempo-data`） | すべて | （ai-stock-trading のメトリクスも入っている。裁定どおり一緒に消える） | PVC ごと作り直す |
| RabbitMQ（`platform-infra`） | **MSP のキューに滞留した旧イベント** | **ai-stock-trading のキュー** | MSP のキュー（名前が `<MSP のサービス名>.` で始まるもの）だけを空にする |
| **Vault（`platform-infra`・PVC `vault-data`）** | — | **すべて**（秘密情報の画面で入れた値・ブローカーの資格情報） | **触らない**（6 資産に含まれない） |
| **ai-stock-trading namespace** | — | **すべて** | **触らない**（本切替の射程外） |

🔴 **「PVC を消して作り直す」を Postgres と Keycloak に当ててはならない。** ai-stock-trading の DB と、master / ai-stock-trading の realm まで消える。

🔴 **ai-stock-trading の稼働中の身元は、作り直す realm `platform` の中にある。** ローカルクラスタの連携配備では
ai-stock-trading は自分の realm ではなく platform realm で認証する。そこには ai-stock-trading のサービス用クライアント 4 つ
（`ai-stock-trading-kb-writer` / `ai-stock-trading-llm-caller` / `ai-stock-trading-svc` / `ai-stock-trading-owner`）、realm ロール
`trading-owner`（と `trading-service`）、利用者へのロールの付与（`developer` の `trading-owner` ほか）が入っている。
**作り直すと、これらは realm.json に宣言されたとおりにしか戻らない。**

- **ローテーションした client secret は宣言値（開発用の既定）に戻る。** ai-stock-trading 側が別の値を持っていれば、サービス間の認証が失敗する。
- **実行時に付けたロール（宣言に無い利用者への `trading-owner` など）は失われる。** 売買 PoC の操作者が画面に入れなくなる。
- 宣言にある分（`developer` の `trading-owner` など）は戻る。

**売買 PoC に直接効く。** 手順 0 の 6 で現状を書き出し、手順 6 の 4 で戻す。

🔴 **RabbitMQ の滞留を残してはならない。** 文書の更新・削除のイベントが作り直した DB と索引へ届くと、**存在しない文書を指すデータ**（グラフのノード・Wiki のページ・索引の点）が作られる。

## 検証スクリプト

`scripts/measure-cutover-inventory.js`（読み取り専用）。**「空か」ではなく「作り直されたか」を時刻で見る。**

| 側 | 見るもの | 合格の条件 |
| --- | --- | --- |
| 捨てた側 | MSP の DB の作成時刻・realm の人間の利用者の作成時刻・作り直した PVC の作成時刻（可観測性はこれで見る） | 破棄を始めた時刻（`--since`）以降 |
| **触らない側（作り直していないこと）** | ai-stock-trading の DB の作成時刻・`postgres-data` / `keycloak-data` / `vault-data` の作成時刻 | **`--since` より前のまま**（作り直しすぎを捕まえる） |
| **触らない側（消えていないこと）** | 切替前の実測（`--baseline`）に在った ai-stock-trading の DB と、作り直しの対象でない realm（master・ai-stock-trading ほか） | **切替後にも在る**。master は `--baseline` が無くても見る |
| 中身 | realm `platform` がある・旧名が無い・seed 利用者とクライアントがそろう／ABAC の属性辞書とポリシーが seed と一致／Wiki.js のページ 0／Qdrant の点 0・MinIO のオブジェクト 0／MSP のキューの滞留 0 | 各行のとおり |

- fail が 1 件でもあれば終了コード 1、収集自体の失敗は 2。**読めなかった資産は fail として出る**（0 件として扱わない）。
- 🔴 **`--baseline` を必ず渡す。** 消えたものには作成時刻が無いので、「消えた」は切替前の実測と突き合わせないと見えない。
  `--baseline` が無いと、ai-stock-trading の DB が無いことは「未配備」と区別できず skip になり、realm の消失（master を除く）も見ない。
  その場合は「基準」の行が skip として出る。
- Prometheus の head の最古サンプルは参考表示である。古いブロックが残っていると head の最古は TSDB 全体の最古ではないので、
  可観測性の作り直しは PVC の作成時刻で判定する（永続化を使っていない配備では PVC が無いので skip になる）。
- 件数 0 の判定（Qdrant・MinIO）は**書き込みを再開する前**に測る。再開後は合成監視や取り込みで増えるのが正常である。
- DB 名の一覧は本書に書かない。`deploy/local/infra/postgres.yaml` の初期化 SQL が単一情報源であり、スクリプトがそこから MSP 側と ai-stock-trading 側を分類する。

```bash
# 事前実測（生データを保存する。切替後の突合の基準値になる）
node scripts/measure-cutover-inventory.js --live --dump cutover-before.json
# MSP の DB を作り直す SQL を表示する（表示するだけで実行しない）
node scripts/measure-cutover-inventory.js --print-recreate-sql
# 再構築の後の検証（--since は破棄を始めた時刻）
node scripts/measure-cutover-inventory.js --live --since 2026-10-01T01:00:00Z --baseline cutover-before.json --dump cutover-after.json
# 保存した生データから判定だけやり直す
node scripts/measure-cutover-inventory.js --input cutover-after.json --since 2026-10-01T01:00:00Z --baseline cutover-before.json
```

環境変数（既定は経路B の値）: `CUTOVER_KC_ADMIN_USER` / `CUTOVER_KC_ADMIN_PASSWORD`（Secret `keycloak-admin` の値を渡す）、
`CUTOVER_QDRANT_URL` / `CUTOVER_PROM_URL`（API サーバのサービスプロキシが通らないときに `kubectl port-forward` した URL を渡す）。

## 手順・スケジュール

窓の長さはリハーサルで測る。窓を開く時刻はオーナーが選ぶ —— realm の作り直しで ai-stock-trading のサービスアカウントの
トークンと人のセッションが切れるため、**売買 PoC を止めてよい時間帯**を選ぶ。

### 0. 事前確認（窓の前日まで）

1. go-live の前提（BFF セッション方式の完了・セキュリティ暫定運用の解消）の状態を確かめる。本切替を go-live と同時に行うかどうかはオーナーが決める。
2. 秘密情報の画面で入れた値が Vault に残っていることを確かめる（Vault は触らない）。🔴 **起動器の再実行が既存の値を
   上書きしないのは、秘密情報の画面の項目（`deploy/bootstrap/sc22-secret-items.json` の対象）だけである。** それ以外の
   `secret/msp/*`（DB・MinIO・RabbitMQ のパスワード、各サービスの client secret ほか）は起動器が**毎回全置換する**
   （env が無ければ開発用の既定値）。手で変えた値があれば、4 の起動器に同じ env を渡すか、後で入れ直す。
3. realm のクライアントの secret を realm.json の宣言値と違う値へ変えていないか確かめる。変えているなら、作り直しの後に配り直す手順を用意する。
4. オーナーが実行時に作った利用者（seed 利用者以外）を書き出しておく。作り直しでは**入り直らない**。
5. 起動器（`scripts/k8s-local-up.sh`）を今のクラスタを作ったときと同じ環境変数で再実行できることを確かめる（`LOCALEDGE` / `OBSERVABILITY` / `VAULT` / `ARGOCD` ほか）。
6. **ai-stock-trading の身元を書き出す**（破棄の境界の節）: realm `platform` の `ai-stock-trading-*` 4 クライアントの現在の secret と、
   `trading-owner` / `trading-service` を持つ利用者・サービスアカウントの一覧。realm.json の宣言と違うものに印を付ける。
7. **`ARGOCD=1` で立てたクラスタなら**、Application `microservices-platform`（`argocd` namespace）は自動同期（`selfHeal` / `prune`）である。
   窓の間に 3 の削除と競合しないよう、2 で自動同期を止める（下記）。4 の起動器の再実行が Application を当て直すので、自動同期はそこで戻る。

### 1. 事前実測（窓の開始時）

```bash
node scripts/measure-cutover-inventory.js --live --dump cutover-before.json
```

出力の realm 一覧に旧名 `microservices-platform` があるか、ai-stock-trading の DB が並ぶかを確かめる。**この時点で収集が失敗するなら中止する**（検証スクリプトが稼働構成に合っていない）。

### 2. 静止

```bash
# 破棄を始めた時刻を記録する（検証の --since に渡す）
date -u +%Y-%m-%dT%H:%M:%SZ
# ai-stock-trading の書き込み（文書の取り込み・LLM 呼び出し）を止める（オーナーが PoC の止め方を選ぶ）
# ARGOCD=1 のクラスタだけ: 自動同期を止める（selfHeal が 3 で消した PVC を即座に作り直し、prune が差分を消しに来るのを防ぐ）
kubectl -n argocd patch application microservices-platform --type merge -p '{"spec":{"syncPolicy":{"automated":null}}}'
```

🔴 **MSP のサービスを `kubectl scale` で 0 にしてはならない。** chart の Deployment は Helm がサーバサイド apply で所有しており、
`kubectl` で `replicas` を書き換えると所有者が移り、**以後の `helm upgrade` が conflict で失敗し続ける**
（メッシュの mTLS モードで同じ事故を実測している）。chart の値で 0 にする手も無い（`replicas: 0` は既定の 1 に置き換わる）。
サービスは止めずに、**DB を消してからキューを空にし、最後に再起動する**順で静止の代わりにする（下の 3 → 4）。

### 3. 破棄

```bash
# (a) MSP の DB を作り直す。SQL は --print-recreate-sql が出したものを使う。
#     WITH (FORCE) が既存の接続を切る。以後サービスは表が無いので書けない（4 の再起動でマイグレーションが走る）
PG=$(kubectl -n platform-infra get pod -l app=postgres -o jsonpath='{.items[0].metadata.name}')
node scripts/measure-cutover-inventory.js --print-recreate-sql | kubectl -n platform-infra exec -i "$PG" -- psql -U postgres -d postgres -v ON_ERROR_STOP=1

# (b) MSP のキューの滞留を空にする（DB を消した後に行う —— 先に空にすると、その後に処理に失敗した旧イベントが
#     再試行で戻ってくる）。名前が MSP のサービス名で始まるキューだけ。ai-stock-trading のキューは触らない。
#     共有のデッドレターキューがあれば中身を見てから判断する
RMQ=$(kubectl -n platform-infra get pod -l app=rabbitmq -o jsonpath='{.items[0].metadata.name}')
kubectl -n platform-infra exec "$RMQ" -- rabbitmqctl list_queues name messages consumers
kubectl -n platform-infra exec "$RMQ" -- rabbitmqctl purge_queue <MSP のキュー名>   # 滞留のある MSP のキューごとに

# (c) realm を消す（旧名が残っていればそれも）。master と ai-stock-trading の realm は消さない
KC=$(kubectl -n platform-infra get pod -l app=keycloak -o jsonpath='{.items[0].metadata.name}')
kubectl -n platform-infra exec "$KC" -- /opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master --user "$KC_ADMIN_USER" --password "$KC_ADMIN_PASSWORD"
kubectl -n platform-infra exec "$KC" -- /opt/keycloak/bin/kcadm.sh delete realms/platform
kubectl -n platform-infra exec "$KC" -- /opt/keycloak/bin/kcadm.sh delete realms/microservices-platform   # 旧名が無ければ 404 になる。無視してよい

# (d) PVC ごと作り直す入れ物。PVC を消してから、それを使っている Pod を消す（Pod が居る間は PVC の削除が保留される）。
#     Deployment には触らない。作り直された Pod は PVC が無いので Pending になり、4 で PVC が作られると起きる
kubectl -n platform-infra delete pvc qdrant-storage prometheus-data loki-data tempo-data --wait=false
kubectl -n microservices-platform delete pvc minio-data wiki-js-data --wait=false
kubectl -n platform-infra delete pod -l 'app in (qdrant,prometheus,loki,tempo)'
kubectl -n microservices-platform delete pod -l 'app in (minio,wiki-js)'
kubectl -n platform-infra get pvc; kubectl -n microservices-platform get pvc   # 消えたことを確かめる
```

🔴 **`postgres-data` と `keycloak-data` と `vault-data` は消さない。** 🔴 **`scripts/k8s-local-down.sh` を使わない** —— クラスタ全体を落とす道具であり、ai-stock-trading の namespace と Vault まで巻き込む。

可観測性の永続化を使っていない配備では、Prometheus / Loki / Tempo の PVC は無い（削除の行から外す）。

### 4. 再構築

```bash
# Keycloak を再起動する。--import-realm は「無い realm」だけを ConfigMap から入れる（既存の realm は飛ばす）
kubectl -n platform-infra rollout restart deploy/keycloak
kubectl -n platform-infra rollout status deploy/keycloak
# MSP のサービスを再起動し、空の DB へマイグレーションを当てさせる（再起動の注記は Helm が所有しない欄なので conflict は起きない）。
# ここでは完了を待たない —— MinIO と Wiki.js は PVC が無いので、次の起動器が PVC を作るまで Pending のままである
kubectl -n microservices-platform rollout restart deployment
# 起動器を今のクラスタと同じ環境変数で再実行する。PVC を作り直し、realm の差分を当て、初期化と初期投入を行う。
# ABAC とタグ辞書の初期投入を有効にする（タグ辞書が空だと外部ユニットの文書が全件 400 になる）
ABACSEED=1 TAGSEED=1 <今のクラスタと同じ環境変数> bash scripts/k8s-local-up.sh --live
kubectl -n microservices-platform rollout status deployment --timeout=10m
```

起動器の初期化と初期投入（Wiki.js の初期セットアップ・ABAC・タグ辞書）は best-effort であり、失敗しても警告だけで先へ進む。
警告が出ていたら、全サービスが起きた後に `bash deploy/local/wikijs-setup/bootstrap.sh`・`node scripts/seed-abac-policies.js --live`・
`node scripts/seed-tag-dictionary.js --live` を再実行する（いずれも冪等）。

`SEARCHSEED=1`（検索検証用の文書を作る）は**付けない** —— 使い捨てスタック専用であり、検証の「点 0・オブジェクト 0」も崩す。

### 5. 検証（書き込みの再開前）

```bash
node scripts/measure-cutover-inventory.js --live --since <2 で記録した時刻> --baseline cutover-before.json --dump cutover-after.json
node scripts/check-stack-ready.js --live      # realm の乖離・永続化・イメージ参照の門を含む
```

どちらも緑であることを確かめる。🔴 **検証スクリプトが「触らない側」で fail を出したら、作り直しすぎか消しすぎである。** 再開せずに原因を調べる。
`--baseline` を付け忘れると消失は見えない（「基準」の行が skip で出る）。

### 6. 再開

1. realm の人間の利用者でログインし、TOTP を登録し直す（seed 利用者は初回ログインで登録を求められる）。
2. 0 の 4 で書き出した利用者を作り直す。
3. 0 の 3 で配り直しが要るとした secret を配り直す。
4. **0 の 6 で書き出した ai-stock-trading の身元を戻す**: 宣言値へ戻った `ai-stock-trading-*` の secret を ai-stock-trading 側の値と揃え
   （どちらへ揃えるかはオーナーが決める。realm へ手で入れ直した値は、次に realm の差分 Job を走らせたとき（起動器の再実行を含む）宣言値へ戻されるので、宣言側を変えないなら ai-stock-trading 側を揃える）、
   実行時に付けていた `trading-owner` などのロールを付け直す。売買 PoC の操作者でログインし、画面に入れることを確かめる。
5. `ARGOCD=1` なら、Application `microservices-platform` の自動同期が戻っていることを確かめる（4 の起動器が当て直す）。
6. ai-stock-trading の書き込みを再開し、文書の取り込みが 400 にならないこと（タグ辞書が入っていること）を確かめる。

## ロールバック・リスク

- **破棄は戻さない**（裁定）。手順の途中で失敗したら、**前へ進めて直す** —— 起動器は冪等であり、4 をやり直せばよい。
- **任意の保全**: 調査用の記録を残したいなら、3 の前に次を取る（取るかどうかはオーナーが決める。戻すための手順ではない）。

  ```bash
  kubectl -n platform-infra exec "$PG" -- pg_dumpall -U postgres --clean > cutover-pg-before.sql   # ai-stock-trading の DB も含む。扱いに注意
  kubectl -n platform-infra exec "$KC" -- /opt/keycloak/bin/kcadm.sh get realms/platform > cutover-realm-before.json
  ```

| リスク | 起きること | 抑え方 |
| --- | --- | --- |
| 作り直しすぎ（共有 PVC を消して作り直す） | ai-stock-trading の DB・master / ai-stock-trading の realm・Vault の秘密が消える | 破棄の境界の表を守る。検証スクリプトが共有 PVC と ai-stock-trading の DB の作成時刻で fail を出す |
| 消しすぎ（ai-stock-trading の DB や realm を個別に消す） | 同上 | 検証スクリプトが **`--baseline` を渡したときだけ** fail を出す（消えたものには作成時刻が無い）。master realm の消失は `--baseline` が無くても出す |
| 消し足りない（旧 realm・旧 DB が残る） | 旧データの上で動き続け、切り替えたつもりになる | 検証スクリプトの「捨てた側」が fail で出す |
| MSP のキューの滞留 | 存在しない文書を指すデータが作られる | 3(b) で空にし、検証スクリプトが滞留 0 を見る |
| タグ辞書の入れ忘れ | ai-stock-trading の文書の保存が全件 400 | 4 で `TAGSEED=1`。6 の 6 で確かめる |
| ai-stock-trading の身元の巻き戻り | client secret が宣言値へ戻りサービス間の認証が失敗する。実行時に付けた `trading-owner` が消え操作者が画面に入れない | 0 の 6 で書き出し、6 の 4 で戻す |
| `ARGOCD=1` の自動同期 | 削除の途中で PVC が作り直される・差分が消される | 2 で自動同期を止める。4 の起動器が戻す |
| 秘密情報の画面の外の `secret/msp/*` の巻き戻り | 手で変えた値が起動器の再実行で既定値へ戻る | 0 の 2 |
| realm の secret の巻き戻り | 宣言値と違う secret を使うサービスが認証に失敗する | 0 の 3 で確かめ、6 の 3 で配り直す |
| 人の資格情報の消失 | TOTP の再登録・実行時に作った利用者の作り直しが要る | 0 の 4 と 6 の 1・2 |

## リハーサル

使い捨てクラスタ（k3d など。**稼働クラスタではない**）で、次を通す。

1. 起動器で経路B を立てる（今の稼働クラスタと同じ環境変数）。ai-stock-trading も配備する（同居の境界を確かめるため）。
2. 適当なデータを入れる（検索検証用の文書・Wiki のページ・利用者 1 人）。
3. 本書の手順 1〜5 を通し、**窓の長さ（2 の開始から 5 の緑まで）を測る**。
4. 検証スクリプトが緑になること、**および** `postgres-data` を消した場合と ai-stock-trading の DB を 1 つ DROP した場合（`--baseline` つき）に「触らない側」が fail を出すことを確かめる（陰性対照）。
5. 測った窓の長さと、手順どおりに動かなかった箇所を記録し、本書を直す。

## オーナー作業（AI は行わない）

| 作業 | 理由 |
| --- | --- |
| リハーサルの実施 | 使い捨てクラスタが要る |
| 窓の時刻の選定と売買 PoC の停止・再開 | PoC の運用判断 |
| 手順 0〜6 の実行 | 稼働クラスタのデータを消す |
| 任意の保全の要否 | 裁定の運用判断 |
| TOTP の再登録・実行時に作った利用者の作り直し・secret の配り直し | 人の資格情報 |
| ai-stock-trading の身元（`ai-stock-trading-*` 4 クライアントの secret・実行時に付けた `trading-owner` などのロール）の書き出しと復元 | 売買 PoC の認証に直接効く |
| `ARGOCD=1` のクラスタでの自動同期の停止と復帰の確認 | 稼働クラスタの設定 |
| 旧 ArgoCD Application・イメージ・不要ブランチの整理 | 稼働クラスタ・レジストリ・リモートへの破壊的操作 |

## 関連仕様

- データ仕様書: 各サービスの DB は起動時マイグレーションで作られる（本切替はスキーマを変えない）
- 運用仕様書: [運用仕様書](../operations/operations.md)（永続化と realm の反映の経路）

## 未決事項

- 窓の長さ（リハーサルで測る）。
- 本切替を go-live と同時に行うか、先に行うか（オーナーが決める）。
- 検証スクリプトの収集部は稼働環境で未検証である（Pod ラベル・kcadm の出力形・Qdrant と Prometheus の応答形・MinIO の Pod に `ls` があること・`rabbitmqctl` の JSON 形）。**手順 1 の事前実測がその検証を兼ねる。**
- 切替の後、リポジトリの文書（README ほか）を再実装後の実態へ更新する作業と、残っている issue の最終トリアージ。
