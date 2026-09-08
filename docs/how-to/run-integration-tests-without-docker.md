---
title: Docker Engine API が無い環境（containerd 等）で統合テストを走らせる
type: how-to
status: fixed
created: 2026-09-08
updated: 2026-09-08
author: claude
---
<!-- trace:
ids: [FR-05, FR-06, NFR-09, UC-03, UC-05]
adrs: [ADR-0004, ADR-0027]
iadrs: [IADR-0130, IADR-0231, IADR-0232, IADR-0414]
specs: [20260908_issue-1336_integration-gate-asks-for-services]
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

## 3. 走らせる

```bash
dotnet test src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj --no-build
```

## 実測（2026-09-08・Windows / Rancher Desktop containerd）

| 状態 | 結果 |
| --- | --- |
| 何も渡さない（Docker も無い） | 46 passed / **44 skipped** |
| PostgreSQL ＋ RabbitMQ を渡す | **82 passed** / 2 failed / 6 skipped |

## 🔴 既知の限界

**外部から渡したブローカでは、fan-out の 2 件が通らない**（Testcontainers 経路では緑）。

- `Messaging.DocumentUpdatedFanOutTests.PublishOnce_BothSubscribersReceive`
- `Messaging.QueueOverrideFanOutTests.SharedQueueDeclaration_KeepsFanOut_ServicePrefixSeparatesQueues`

**原因は未特定である**（ブローカの残留状態でも待ち時間不足でもないことは切り分け済み）。
切り出した issue は trace ブロックにある。**この 2 件を緑にするために主張を緩めてはならない** ——
1 発行 → 2 購読が競合コンシューマ化していないことを守る要である。

なお残る 6 skip のうち、`ConversionService` の分は `pandoc` / `pdftotext` の有無であって
コンテナとは関係が無い。

## 後片付け

```bash
nerdctl rm -f msp-test-pg msp-test-mq msp-test-qdrant msp-test-minio
```

## 補足: `DOCKER_HOST` を持っている場合

Docker API を既定以外の場所へ公開しているなら、`DOCKER_HOST` を設定するだけでよい
（判定はこの変数を尊重する）。その場合は上の環境変数は要らず、テストは従来どおり
コンテナを自分で起こす。
