---
title: IADR-0289 統合テストの器は対象サービスの起動時依存に追随させ、欠落は型で止める
type: impl-adr
status: Accepted
related_ids:
  - FR-17
  - SC-09
  - ADR-0027
  - ADR-0033
  - IADR-0231
  - IADR-0232
  - IADR-0242
  - IADR-0260
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "ADR-0027（メッセージング基盤は Wolverine）"
  - "ADR-0033 決定 5・6・9（辺の型辞書の DB 層の防壁）"
---

# IADR-0289 統合テストの器は対象サービスの起動時依存に追随させ、欠落は型で止める

## 状況

`IADR-0260` に沿って、辺の型辞書の DB 層の防壁（`ON DELETE RESTRICT` / `ux_edge_types_name` /
`ux_edges`）を実 PostgreSQL で発火させる結合テスト 6 件を 2026-08-23 に置いた（#941 第 1 巡）。
6 件は当時 Docker の無い環境で書かれたため **1 件も実走しないまま**着地し、回収先である
`integration.yml` はまだ回っていない。

その 5 日後、**GraphService に Wolverine ホストが入った** —— `#1016`（graph-delete 段）と
`#911`（graph-sync 段）である。`Program.cs` は `builder.Host.UseWolverine(...)` の中で
`opts.UseRabbitMq(new Uri(builder.Configuration["RabbitMq:ConnectionString"] ?? "amqp://guest:guest@rabbitmq:5672")).AutoProvision()`
を呼ぶ。**接続先はホスト構築時に読まれ、繋がらなければ起動が失敗する**（ADR-0027 / #441 E1 の実測。
`IntegrationTestFactory.cs` に `BrokerInitializationException: Unable to initialize the Broker
rabbitmq in time` として記録がある）。

一方 `GraphServiceFactory` は次のままだった。

```csharp
// 🔴 RabbitMQ を渡さない。GraphService は Program.cs でメッセージングを一切構成しない（実測: …）。
public GraphServiceFactory(PostgresFixture pg) : base(pg, null) { }
```

**「実測」と書かれた断言が偽になっていた。** 器がブローカを渡さないので、6 件は
`integration.yml` で初めて走るその瞬間に、**防壁へ到達する前にホスト起動で落ちる。**

### なぜ気付けなかったか（この失敗の形）

`CreateClient()` は `InitializeAsync` にあり、**`DockerRequired.SkipUnlessAvailable()` より先に
走る**。したがって:

| 環境 | 起きること | 見え方 |
| --- | --- | --- |
| Docker 無し（開発機・PR の `ci.yml`） | `postgres.IsAvailable` が false → `CreateClient()` を呼ばない | **6 件 skip。緑。** |
| Docker 有り（`integration.yml`） | ホスト起動でブローカへ繋ぎに行き失敗 | 6 件 fail |

**器の欠落は、器が使われない環境では skip としてしか現れない。** そして skip は緑である。
`IADR-0260` が「緑は測った証拠にならない」と警告した対象は**防壁**だったが、
**同じ罠が防壁を測る器の側にも在った** —— 警告が 1 段くり上がって自分自身に当たった形である。

## 決定

**1. 統合テストの器は、対象サービスの「起動時依存」を母集合として引き直した上で構築する。**

起動時依存とは「不足するとホストが起動しない構成・外部資源」である。GraphService の実測:

| 依存 | 由来 | 不足時 |
| --- | --- | --- |
| `ConnectionStrings:DefaultConnection` | #1012 の fail-fast | throw |
| `RabbitMq:ConnectionString` | `UseRabbitMq(...).AutoProvision()` | **ブローカ接続失敗で起動失敗** |
| `Pipeline:ConfigPath` ＋ 段宣言の一致 | `AddPlatformWolverineStep` 規則 2・3 | 起動失敗 |

**遅延して読まれるもの（`HttpClient` の基底 URI、オブジェクトストレージ）は起動時依存ではない**
（後者は未設定なら縮退クライアントが入る。実測）。両者を混ぜて数えない。

**2. 欠落は型で止める。器の引数から「省ける」形を無くす。**

`GraphServiceFactory` の引数を `(PostgresFixture pg, RabbitMqFixture rabbit)` の必須 2 引数にする。
既定値も null 許容も置かない。**ブローカ無しでは器がコンパイルできない。**

これが本決定の要である。注記（「メッセージングを構成しない」）は**腐るが、型は腐らない**。
今回まさに注記のほうが腐り、しかもその注記が「実測」を名乗っていたために信用されていた。

**3. 本番の配線を剥がして回避しない。**

「テストでは Wolverine を外す」案は採らない。器の既定方針（`#455` U0：サービス自身の配線を
そのまま使う）に反し、**出荷される版の起動経路を試験しなくなる** —— `IADR-0260` が
「変異を入れた版だけを試験することになる」として退けたのと同型である。

**4. 検査器は足さない。本件を「1 回目」として記録する。**

「`Program.cs` が `UseWolverine` を呼ぶサービスの器がブローカを渡しているか」は機械で突合できる
（実際、本巡の発見はその突合を手で回して得た）。しかし本リポジトリの規約は
**同型の事故が 2 回起きてから検査器を足す**（1 回目は記録に留める）。
**本 IADR がその記録である。2 回目が起きたら、この突合を検査器にすること。**

## `IADR-0260`「実走の確認手順」への追加

同 IADR の確認手順は「6 件が `Passed` として現れること」から始まるが、**その前に 1 段要る。**

> 0. **ホストが起動したか。** ログに `BrokerInitializationException` /
>    `Unable to initialize the Broker` が **0 件**であること。

理由: ホスト起動の失敗は 6 件すべてを同時に落とすため、テスト名の一覧だけを見ると
「防壁が壊れた」と読み違える。**落ちた層を取り違えると、防壁を疑って器を直さない。**

## 却下した案

- **`GraphServiceFactory` の引数を省略可能にし、ブローカがあれば渡す。**
  今回の欠落がそのまま再現する。「渡し忘れても動く」形が問題の本体である。
- **`BrokerRequired.SkipUnlessObtainable()` へ寄せる。** この 6 件は Postgres を必ず要るので、
  「ブローカだけ外から与える」経路は成立しない。判定を緩めると Postgres の無い環境で
  skip されずに落ちる（`BrokerRequired` が自ら避けている状態）。
- **注記を最新化して済ませる。** 5 日で腐った注記を、また注記で直すことになる。
  型で止められるものを注記で守らない。

## 結果

- 良い影響: 6 件が `integration.yml` で**防壁まで到達できる**状態になった。
  同じ退行はコンパイルエラーとして現れる。
- トレードオフ: 6 件が RabbitMQ コンテナを要求するようになる（クラスフィクスチャで 1 個共有）。
  Postgres だけで足りていた頃より起動が重い。**防壁へ到達しない試験に価値は無い**ので受容する。
- 🔴 **未確認: 本決定の是正そのものは実走していない**（作業環境に Docker daemon が無い）。
  根拠は他 6 サービスの器との対比・#441 E1 の実測記録・`Program.cs` の読み取りであり、実走ではない。
  埋めるのは `integration.yml` の初回実行である。

## 関連

- 前提: `IADR-0260`（DB 層の防壁は「発火」と「カタログ」の両方で確認する）—— 本決定はその
  確認手順に段 0 を足す。決定内容は覆さない。
- 作業仕様書: `.ai-context/specs/20260828_issue-941_edge-type-db-guard-verification.md`
