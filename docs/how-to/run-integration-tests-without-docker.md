---
title: Docker Engine API が無い環境（containerd 等）で統合テストを走らせる
type: how-to
status: fixed
created: 2026-09-08
updated: 2026-09-10
author: claude
---
<!-- trace:
ids: [FR-05, FR-06, NFR-09, UC-03, UC-05]
adrs: [ADR-0004, ADR-0027]
iadrs: [IADR-0130, IADR-0231, IADR-0232, IADR-0414]
specs: [20260908_issue-1336_integration-gate-asks-for-services, 20260909_issue-1337_fanout-tests-on-shared-broker]
issues: [#455, #1073, #1336, #1337]
-->

# 手順書: Docker Engine API が無い環境で統合テストを走らせる

統合テストは既定でコンテナを自分で起こす（Testcontainers）。それには **Docker Engine API** が要る。

Rancher Desktop を **containerd** バックエンドで使っている場合など、Docker Engine API が
無い環境では、**依存サービスを自分で起こして環境変数で渡す**。テスト側はコンテナを起こさず、
渡された端点へ繋ぐ。

> **切り替えたくない人のための手順である。** Rancher Desktop の
> Preferences → Container Engine を `dockerd (moby)` にすれば、この手順は要らない。

## 1. 依存サービスを起こす

`nerdctl` は Rancher Desktop に同梱されている（Windows なら
`C:\Program Files\Rancher Desktop\resources\resources\win32\bin\nerdctl.exe`）。

```bash
nerdctl run -d --name msp-test-pg -p 55432:5432 \
  -e POSTGRES_DB=integration_test -e POSTGRES_USER=kp -e POSTGRES_PASSWORD=kp \
  postgres:16-alpine

nerdctl run -d --name msp-test-mq -p 55672:5672 rabbitmq:3.13-alpine
```

**image はテストが使うものと同じにする**（`PostgresFixture` / `RabbitMqFixture` を参照）。
ポートは既定と衝突しないよう 5 万番台へずらしてある。

Qdrant と MinIO も要るなら:

```bash
nerdctl run -d --name msp-test-qdrant -p 56334:6334 qdrant/qdrant:latest
```

```bash
nerdctl run -d --name msp-test-minio -p 59000:9000 \
  -e MINIO_ROOT_USER=minioadmin -e MINIO_ROOT_PASSWORD=minioadmin \
  minio/minio:RELEASE.2025-04-08T15-41-24Z server /data
```

🔴 **MinIO の資格情報は `minioadmin` / `minioadmin` でなければならない。**
テストはこの 1 組を前提にしており、端点だけを変数で受け取る（資格情報を変数にすると
「端点だけ変えて資格情報を変え忘れた」状態が作れるため、意図的に変数にしていない）。

## 2. 端点を環境変数で渡す

```bash
export PLATFORM_TEST_POSTGRES="Host=127.0.0.1;Port=55432;Database=integration_test;Username=kp;Password=kp"
export PLATFORM_TEST_RABBITMQ="amqp://guest:guest@127.0.0.1:55672"
export PLATFORM_TEST_QDRANT="127.0.0.1:56334"
export PLATFORM_TEST_MINIO="http://127.0.0.1:59000"
```

**要る分だけ渡せばよい。** 渡さなかった依存を要するテストは、理由と渡すべき変数名を添えて
**真の Skipped** になる（黙って通ることはない）。

## 2.5 稼働中のクラスタを端点として使うときの前提（2026-09-10 追記）

稼働している k3s の Postgres / RabbitMQ を `kubectl port-forward` で借りて走らせる場合、
**次の 2 つを先に満たさないと、product の不具合と紛らわしい形で落ちる。**

### `port-forward` は試験と同じシェルの中で張る

別のコマンドで張ると、試験が走る頃には落ちている。そのときの症状は
`MassTransit.RabbitMqConnectionException : Broker unreachable` で、**あたかも fan-out の是正が
効いていないかのように 2 件とも落ちる**（実測で一度これに引っかかった）。

```bash
kubectl -n platform-infra port-forward svc/rabbitmq 15672:5672 >/dev/null 2>&1 &
kubectl -n platform-infra port-forward svc/postgres 15433:5432 >/dev/null 2>&1 &
sleep 8
export PLATFORM_TEST_RABBITMQ="amqp://guest:guest@127.0.0.1:15672"
export PLATFORM_TEST_POSTGRES="Host=127.0.0.1;Port=15433;Database=integration_test;Username=kp;Password=kp"
dotnet test src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj
```

### 接続する役割に `CREATEDB` が要る

試験は**実行ごとにデータベースを作る**。稼働クラスタの `kp` は `CREATEDB` を持たないため、
`42501: permission denied to create database` で **1〜2 秒で**落ちる。
🔴 **ブローカへ触る前に落ちるので、メッセージングの失敗と見分けが付きにくい。**

付与はスーパーユーザーで行う（`kp` 自身では `Only roles with the CREATEROLE attribute … may alter this role` になる）。

```bash
kubectl -n platform-infra exec deploy/postgres --   sh -c 'psql -U "$POSTGRES_USER" -d postgres -c "ALTER ROLE kp CREATEDB;"'
```

> **使い捨てのクラスタでのみ行うこと。** 恒久的な環境の役割へ `CREATEDB` を足す判断は別である。

## 3. 走らせる

```bash
dotnet test src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj --no-build
```

## 実測（2026-09-08・Windows / Rancher Desktop containerd）

| 状態 | 結果 |
| --- | --- |
| 何も渡さない（Docker も無い） | 46 passed / **44 skipped** |
| PostgreSQL ＋ RabbitMQ を渡す | **82 passed** / 2 failed / 6 skipped |

## 是正済み（実機での再実測待ち）

**外部から渡したブローカで fan-out の 2 件が落ちていた**（Testcontainers 経路では緑）。

- `Messaging.DocumentUpdatedFanOutTests.PublishOnce_BothSubscribersReceive`
- `Messaging.QueueOverrideFanOutTests.SharedQueueDeclaration_KeepsFanOut_ServicePrefixSeparatesQueues`

原因は**共有した資源に前の実行・他クラスの残りが載ること**であり、2 つ重なっていた。

| # | 共有していたもの | 起きていたこと |
| --- | --- | --- |
| 1 | ブローカ（全クラスで 1 台） | 購読キュー名が固定で、他クラスが発行した本物のイベントが同じキューへ溜まる（束縛はホストを破棄しても残る） |
| 2 | データベース（全クラスで 1 つ） | 文書 Title が固定で、同期先の slug 一意索引と衝突する。受信しているのに終端の副作用が現れず、「受信しなかった」と見分けが付かない |

**どちらもコンテナを毎回起こす経路では起こらない**（クラスごとに新品のため）。

是正は 2 つとも「**実行ごとに一意にする**」であり、**主張は緩めていない**（1 発行 → 2 購読が
競合コンシューマ化していないことを守る要である。待ち時間も 1 秒も伸ばしていない）。
併せて、待ち合わせが切れたときの失敗メッセージへ**購読ホスト側の警告・例外**を載せた ——
「受信していない」と「受信したが落ちた」がその場で読み分けられる。

🔴 **実機での再実測はまだである**（この作業を行った環境にブローカと DB が無い）。
確かめ方は下の「実機で確かめること」を参照。

なお残る 6 skip のうち、`ConversionService` の分は `pandoc` / `pdftotext` の有無であって
コンテナとは関係が無い。

## 実機で確かめること（利用者の手が要る）

1. 上の手順どおりブローカと DB を渡して**全件を 2 回続けて**走らせる。
   **2 回目も緑であること**が要点である（1 回目の残りが 2 回目を汚さない）。
2. 1 回目と 2 回目の間に購読キューを見る。

   ```bash
   nerdctl exec msp-test-mq rabbitmqctl list_queues name messages
   ```

   - fan-out の購読キューは**実行ごとに名前が変わる**（末尾の識別子が違う）
   - 実行を跨いで残る固定名のキューに**メッセージが積み上がっていない**こと
3. 落ちた場合は、失敗メッセージ末尾の「ホストの直近の警告/例外」を読む。
   一意制約違反が出ていれば**受信はしている**（配送ではなく書き込み側の問題である）。

後始末でコンテナごと破棄すれば、実行ごとに作られたキューも一緒に消える。

## 後片付け

```bash
nerdctl rm -f msp-test-pg msp-test-mq msp-test-qdrant msp-test-minio
```

## 補足: `DOCKER_HOST` を持っている場合

Docker API を既定以外の場所へ公開しているなら、`DOCKER_HOST` を設定するだけでよい
（判定はこの変数を尊重する）。その場合は上の環境変数は要らず、テストは従来どおり
コンテナを自分で起こす。
