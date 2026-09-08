---
title: IADR-0414 統合テストの門は「要るサービスを得られるか」を依存ごとに訊く（「Docker が入っているか」ではない）
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-06, NFR-09, UC-03, UC-05, ADR-0004, ADR-0027, ADR-0088, IADR-0130, IADR-0141, IADR-0231, IADR-0232, IADR-0301, IADR-0413]
author: claude
created: 2026-09-08
updated: 2026-09-08
---

# IADR-0414: 統合テストの門が訊く問い

## 状況

統合テストの門（`DockerRequired.SkipUnlessAvailable()`）は
**`docker_engine` の名前付きパイプが在るか**だけを見ていた。

🔴 **それは問いが違う。** テストが要るのは PostgreSQL・ブローカ・Qdrant・
オブジェクトストレージという**サービス**であって、Docker という特定の入手経路ではない。

### 🔴 実測 —— 外部供給の口は在るのに、門がそれを見ていなかった

`nerdctl`（Rancher Desktop / containerd）で PostgreSQL と RabbitMQ を起こし、
`PLATFORM_TEST_POSTGRES` / `PLATFORM_TEST_RABBITMQ` を与えても **46 passed / 44 skipped のまま**。

**この 2 つの口は既に在った** —— `RabbitMqFixture`（#455 W3）と `PostgresFixture`（#1073）である。
🔴 **どちらのコメントも containerd を動機として名指ししている。**
**作った人の意図が門に届いていなかった。**

### 実測 —— `BrokerRequired` だけが正しい問いを訊いていた

`BrokerRequired.IsObtainable()` は「外部エンドポイント **または** Docker」である。
当時のコメントは「`DockerRequired` を緩めると Postgres を要るテストが skip されずに落ちる」ので
判定を分けた、と述べている。**その前提は #1073 で Postgres が外部供給を持った時点で変わっていた**
—— 前提が変わったことを、誰も門へ反映していなかった。

### 実測 —— `DOCKER_HOST` を見ていなかった

Testcontainers はこの変数を見るのに、門は既定のパイプ／ソケットしか見ていなかった。
**使えるのに「無い」と答える**環境がある。

## 決定

### 決定 1: 門は依存ごとに「得られるか」を訊く（`RequiredServices`）

呼び出し側は**要る依存を明示する**:

```csharp
RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
```

判定は依存ごとに `外部供給 または Docker`。

🔴 **「Docker がある」を 1 つの真偽値へまとめない。** まとめると
「ブローカだけ外から与えた」状態で Postgres を要る試験まで走り、skip ではなく**失敗**になる
（`BrokerRequired` が判定を分けた当時の理由そのもの）。
**依存を明示させることで、精度を保ったまま 1 つの門へ統合した。**

### 決定 2: skip の理由に「**どうすれば走るか**」を書く

従前は "start Docker Desktop" しか言わず、**Docker を使えない利用者に打つ手が無いように見せていた。**
新しい門は、不足している依存の名前・与えるべき環境変数・値の例を並べる。

🔴 **これは体裁の話ではない。** #1336 の出発点は「containerd では走らない」であり、
**走らせる方法は既に在ったのに、門がそれを伝えていなかった**ことが問題の半分だった。

### 決定 3: `DOCKER_HOST` を尊重する

値の妥当性は確かめない —— それは Testcontainers の仕事であり、
起動に失敗すれば `ContainerStartupFailure`（#1292）が原因を添えて落とす。

### 決定 4: Qdrant / MinIO にも外部供給の口を置く

`PLATFORM_TEST_QDRANT` / `PLATFORM_TEST_MINIO`。読み方の規則（空文字は未設定）は
`ExternalEndpoints` 1 か所が持つ —— 4 つ目を足すときに判定が写ると、
片方だけ空文字を通す状態が作れる。

🔴 **MinIO の資格情報は変数にしない。** 試験が使うのは開発用の 1 組だけであり、
変数を増やすと「端点だけ変えて資格情報を変え忘れた」状態が作れる。

### 決定 5: `DockerRequired` は門をやめ、判定の**部品**に降りる

`SkipUnlessAvailable()` を廃止した（呼び出しは 0 件）。`IsAvailable()` を直接使ってよいのは
`ContainerStartupFailure` だけである —— あれは「Docker があるのに起動できなかったのか」を
見分けるので、**Docker の有無こそが訊きたい問い**である。

## 結果

| 環境 | 前 | 後 |
| --- | --- | --- |
| Docker 無し・外部供給 無し（既定） | 46 passed / 44 skipped | **46 passed / 44 skipped**（不変） |
| containerd ＋ 外部供給 | 46 passed / 44 skipped | 🔴 **82 passed / 2 failed / 6 skipped** |

**38 件が実際に走るようになった。**（既定の挙動は 1 件も変えていない。）

### 🔴 副産物 —— develop の Integration が赤いのを見つけた

containerd で走らせられるようにした結果、**PR #1334（`ADR-0088` の是正）が
`AbacScopeTests` 2 件を壊していた**ことが分かった（#1335）。

🔴 **PR の CI では構造的に見えない欠陥である。** `ci.yml` は `--filter "Category!=Integration"` で
走らせるので、この 2 件は `integration.yml`（push: develop ＋ 日次）でしか走らない ——
**マージした後で初めて赤くなる**（[[IADR-0232]] が意図した分担であり、欠陥ではない。
ただし「PR が緑でもここが赤ければ、その退行は入っている」）。

**直しの形も記録しておく。** 統合テストの認証ハンドラへ `X-Test-Roles` を足した。
🔴 **既定へ `platform-service` を混ぜない** —— 混ぜると統合テストの主体が
「管理者でありサービスでもある」**実配備に存在しない principal** になり、
面ごとに資格が違うこと（管理系は `AdminOnly`・スコープ解決は `ServiceCaller`）を**測れなくなる**。

### 🔴 残るもの

**外部から与えたブローカでは fan-out の 2 件が通らない**（#1337）。
**本 IADR が持ち込んだ欠陥ではない** —— 外部供給の口は #455 W3 から在り、
**その経路でこの 2 件が通ることは一度も確かめられていなかった**（Docker が無ければ門で skip されていた）。

🔴 **試験は緩めない。** この 2 件は 1 発行 → 2 購読が競合コンシューマ化していないことを守る要である。
外部ブローカで落ちるからといって主張を弱めれば、**守っている性質が消える。**
原因（受信そのものが起きていないのか、受信後の処理が落ちているのか）は未切り分けである。

## 関連

- 前提として扱い覆さないもの: [[IADR-0231]] 決定 3（動的 skip は `Assert.Skip*`）／
  [[IADR-0130]]（0 件走査で緑にしない）／[[IADR-0232]]（`Category=Integration` の回収先）／
  [[IADR-0301]]（身元プロバイダの抽象）／[[IADR-0413]]（`/authz/scope` の `ServiceCaller`）
- 手順書: `docs/how-to/run-integration-tests-without-docker.md`
