---
title: 統合テストの門を「Docker の有無」から「要るサービスを得られるか」へ変える（containerd で走らせられるようにする）
type: spec
status: done
related_ids: [FR-05, FR-06, NFR-09, UC-03, UC-05, ADR-0004, ADR-0027, ADR-0088, IADR-0130, IADR-0231, IADR-0232, IADR-0301, IADR-0413, IADR-0414]
author: Claude（実装）
created: 2026-09-08
updated: 2026-09-08
---

# 仕様書: 統合テストの門が訊く問いを変える（#1336）

## 起点

- 非機能: NFR（試験の実行可能性）／`ADR-0027`（メッセージング）／`ADR-0004`（ABAC）
- 実装 ADR: [[IADR-0231]] 決定 3（動的 skip は `Assert.Skip*`）／[[IADR-0130]]（0 件走査で緑にしない）／
  [[IADR-0232]]（`Category=Integration` の回収先）／[[IADR-0414]]（本 PR で新設）
- issue: #1336（本体）／#1337（切り出した限界）／#1335（本 PR が閉じる CI 失敗）

## 発端

利用者から「**containerd でもテストできるようにしておいてください**」。

## 🔴 実測 1 —— 門が訊いている問いが違う

`DockerRequired.SkipUnlessAvailable()` は `docker_engine` の名前付きパイプだけを見ていた。
**テストが要るのは PostgreSQL・ブローカ・Qdrant・オブジェクトストレージという「サービス」であって、
Docker という特定の入手経路ではない。**

代償は実測できる。`nerdctl` で PostgreSQL と RabbitMQ を起こし、
`PLATFORM_TEST_POSTGRES` / `PLATFORM_TEST_RABBITMQ` を与えても —— **46 passed / 44 skipped のまま**。
**外部供給の口は在るのに、門がそれを見ていなかった。**

🔴 **この経路は「無いから作る」のではなく「在るのに使われていなかった」。**
`RabbitMqFixture`（#455 W3）と `PostgresFixture`（#1073）のコメントは
**containerd を動機として名指ししている** —— 作った人の意図は門に届いていなかった。

## 実測 2 —— `BrokerRequired` だけが正しい問いを訊いていた

`BrokerRequired.IsObtainable()` は「外部エンドポイント **または** Docker」である。
当時のコメントは「`DockerRequired` を緩めると Postgres を要るテストが skip されずに落ちる」ので
判定を分けた、と書いている。**その前提は #1073 で Postgres が外部供給を持った時点で変わっていた。**

## 実測 3 —— 母集合（門の呼び出し 36 か所 / 15 ファイル）

| 種別 | ファイル数 | 呼び出し数 | 依存 |
| --- | --- | --- | --- |
| `PostgresFixture` のみ | 2 | 5 | Postgres |
| `PostgresFixture` ＋ `RabbitMqFixture` | 10 | 24 | Postgres ＋ ブローカ |
| 自前でコンテナを起こす | 3 | 7 | Postgres 1 / Qdrant 3 / MinIO 3 |

🔴 **Qdrant と MinIO は外部供給の口を持っていなかった**（6 件は外から与えようが無かった）。

## 実測 4 —— `DOCKER_HOST` を見ていなかった

Testcontainers は `DOCKER_HOST` を見るのに、門は既定のパイプ／ソケットしか見ていなかった。
**別アドレスに Docker API を持つ環境で「無い」と誤答する。**

## 決定

### 決定 1: 門は「**この試験が要るサービスを得られるか**」を訊く（`RequiredServices`）

依存ごとの記述（`ExternallySuppliable`）を持ち、`外部供給 または Docker` で判定する。
呼び出し側は**要る依存を明示する** ——
`RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker)`。

🔴 **「Docker がある」を 1 つの真偽値へまとめない。** まとめると
「ブローカだけ外から与えた」状態で Postgres を要る試験まで走って**失敗**する
（`BrokerRequired` が判定を分けた当時の理由）。**依存を明示させることで精度を保ったまま統合した。**

### 決定 2: skip の理由に「**どうすれば走るか**」を書く

従前は "start Docker Desktop" しか言わず、**Docker を使えない利用者に打つ手が無いように見せていた**。
新しい門は不足している依存の名前と、与えるべき環境変数と例を並べる。

### 決定 3: `DOCKER_HOST` を尊重する

値の妥当性は確かめない（それは Testcontainers の仕事であり、起動に失敗すれば
`ContainerStartupFailure` が原因を添えて落とす）。

### 決定 4: Qdrant / MinIO にも外部供給の口を置く（`PLATFORM_TEST_QDRANT` / `PLATFORM_TEST_MINIO`）

🔴 **MinIO の資格情報は変数にしない。** 試験が使うのは開発用の 1 組だけであり、
変数を増やすと「端点だけ変えて資格情報を変え忘れた」状態が作れる。

### 決定 5: 🔴 `DockerRequired` は門をやめ、**判定の部品**に降りる

`SkipUnlessAvailable()` を廃止した（呼び出しは 0 件）。残る `IsAvailable()` を直接使ってよいのは
`ContainerStartupFailure`（Docker があるのに起動できなかったのかを見分ける）だけである。

## 🔴 副産物 —— develop の Integration が赤いのを見つけて直した（#1335）

**containerd で走らせられるようにした結果、PR #1334（`ADR-0088` の是正）が
`AbacScopeTests` 2 件を壊していたことが分かった。**

- 原因: `/authz/scope` が `ServiceCaller` を要るようになったのに、統合テストの認証ハンドラは
  `platform-admin` しか名乗らない（→ 403）。加えて**属性が本文ではなく IdP から来る**ようになった。
- 🔴 **PR の CI では構造的に見えない。** `ci.yml` は `--filter "Category!=Integration"` であり、
  この 2 件は `integration.yml`（**push: develop ＋ 日次**）でしか走らない。
  **マージした後で初めて赤くなる。**
- 直し: 認証ハンドラに `X-Test-Roles` を足し（**既定へ `platform-service` を混ぜない** ——
  混ぜると「管理者でありサービスでもある」実在しない主体になり、面ごとの資格差が測れなくなる）、
  試験は**属性を偽 IdP 側へ置く**形へ変えた。
  🔴 片方の試験は**本文にわざと嘘の属性を入れてある** —— 使われていないことが見えるようにするため。

## 結果（実測）

| 環境 | 前 | 後 |
| --- | --- | --- |
| Docker 無し・外部供給 無し（既定） | 46 passed / 44 skipped | **46 passed / 44 skipped**（不変） |
| containerd ＋ 外部供給（`nerdctl`） | 46 passed / 44 skipped | 🔴 **82 passed / 2 failed / 6 skipped** |

**44 → 6 へ減った skip のうち 38 件が実際に走るようになった。**

🔴 **残る 2 件は #1337 へ切り出した。** fan-out の 2 件が外部ブローカで通らない ——
**#1336 が持ち込んだ欠陥ではなく、外部供給の口が #455 W3 で入って以来
一度も確かめられていなかった**（Docker が無いと門で skip されていたため）。
**試験は緩めない**（fan-out が競合コンシューマ化していないことを守る要である）。

## やらないこと

- **fan-out の 2 件を通すこと**（#1337。原因未特定のまま主張を弱めない）
- **外部 DB を試験クラスごとに分けること**（Testcontainers 経路はクラスごとに新しい DB を作る。
  分離の粒度が違うことは #1337 に記録した）
- **`ConversionService` の 6 skip**（pandoc / pdftotext であり、コンテナとは無関係）
