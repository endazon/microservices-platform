---
title: IADR-0314 Wolverine の発行経路は共通ヘルパで宣言し、exchange への束ねは購読側が行う（発行側に購読者一覧を持たせない）
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-06, UC-03, UC-04, NFR, ADR-0027, ADR-0030, IADR-0014, IADR-0233, IADR-0234, IADR-0239, IADR-0257]
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md
---

# IADR-0314: Wolverine の発行経路を宣言し、束ねは購読側が行う

- 状態: Accepted
- 日付: 2026-08-30
- 決定者: claude（実装判断。#992 の作業中に実測で発見した欠陥への対処）

## 起点・関連

- 発見の経緯: #992（統合スタックで「検索が実際に効くこと」を観測可能にする）の実測
- 関連する実装仕様書: [`20260830_issue-992_deterministic-local-embedding.md`](../specs/20260830_issue-992_deterministic-local-embedding.md)
- 同型の先例: [[IADR-0014]]（テストは緑・本番は壊れている）

## コンテキスト —— 実測で分かったこと

🔴 **Wolverine で発行したイベントは、どの配備でも 1 通もブローカへ出ていなかった。**

稼働 k3s（Rancher Desktop v1.35.4+k3s1）で文書を 1 件登録したときの `document-service` のログ:

```
No routes can be determined for Envelope #08df0693-4e27-db8a-2e49-940f1d270000
  (Knowledge.Contracts.Events.DocumentUpdated)
```

`UsePlatformMessagingDefaults`（手順 4）がプロセス内のローカル経路を切っている一方で、
**外向きの経路を誰も宣言していなかった**。したがって Wolverine は宛先を決められず、
`info` ログを 1 行出して**envelope を捨てる**。例外は出ず、ヘルスチェックも緑のままである。

### いつからか / なぜ誰も気づかなかったか

- `git log -S 'PublishMessage<'` は**本番コードで 0 件**（移行以来一度も存在しない）
- 宣言が在るのは統合テストの器（`DocumentUpdatedFanOutTests` など）だけで、
  **テストは自分で経路を足してから測っていた**
- その統合テストは Testcontainers を要し、**Docker の無い環境では skip される**
- `check-event-topology.js` は「発行側と購読側が同じトランスポートを名乗るか」しか見ておらず、
  **両側とも `wolverine` と名乗りながら経路が 1 本も無い状態を緑にしていた**

**結果として、取り込み（parse→chunk→embed→index）・Wiki 同期・グラフ同期・削除連携が
実配備で一度も起動していなかった。**

## 決定

### 決定 1: 発行側は共通ヘルパ `RoutePlatformEvent<TEvent>()` で経路を宣言する

exchange 名は**メッセージ型名そのもの**（前置も接尾も付けない）。
`ListenToPlatformQueue` の注記が既に「**exchange 名には前置しない**」と定めており、それに従う。

### 決定 2: 🔴 exchange への束ねは**購読側**が行う（`BindPlatformQueue<TEvent>()`）

発行側に購読者の一覧を持たせると、

- 購読サービスが増減するたびに**発行側**を直すことになる
- `pipeline.json` の `queue` 上書き（[[IADR-0239]] 決定 4）に追随できない
  （上書きを知っているのは購読側だけである）

統合テストの器は発行側で `BindQueue` していたが、**それは器の都合**であって配備の設計ではない。

**fan-out の保存は「キュー名が分かれていること」と「同じ exchange へ束ねられていること」の
両方で成り立つ。** 片方だけでは、分かれたキューに何も届かない。

### 決定 3: 命名は `Publish...` で始めない

`check-event-topology.js` の発行元検出は `Publish` に続く語 ＋ 型引数の形に一致するため、
`PublishPlatformEvent<T>` にすると**経路の宣言が「発行」として数えられ**、
`RawDocumentFetched` の発行元が増えたと誤検出した（実測）。
実際の発行元はイベントを構築して送る 1 箇所であり、ここは配線である。

### 決定 4: 同じ穴を機械が塞ぐ —— `check-event-topology.js` に経路の検査を足す

- 発行側（`wolverine` を名乗る owner が居る）→ どこかに `RoutePlatformEvent<Ev>` が要る
- 購読側（`wolverine` を名乗る owner）→ その owner に `BindPlatformQueue<Ev>` が要る

**「同型の事故が 2 回起きたら検査器を足す」という運用の例外にあたる**と判断した ——
本件は 1 回目だが、**事故の性質が「起きても誰も気づかない」**（例外なし・ログ 1 行・
テストは器が補って緑）であり、2 回目を観測できる見込みが無い。

## 結果

- **良い影響**:
  - 取り込み・Wiki 同期・グラフ同期・削除連携が**実際に動くようになった**（稼働 k3s で実測）
  - #992 の門（検索の命中）が成立する前提が揃った
  - 経路の欠落が CI で止まるようになった（変異 2 種で実測）
- **悪い影響 / トレードオフ**:
  - 購読サービスは `UseRabbitMq(...)` の戻り値を使うため、`UseWolverine` の記述が 1 段深くなる
  - **exchange 名の一致は静的検査で担保しているが、型名を変えたときの再索引は人の仕事である**
- **フォローアップ**:
  1. 稼働中の環境では、この修正を適用する前に登録された文書は**イベントが失われている**。
     再取り込みには `DocumentUpdated` の再発行が要る（運用手順の「再索引」節）。
  2. `IngestionCompleted`（MassTransit のまま）は購読 0 件であり、本決定の対象外である。

## 検証

- 単体: `Platform.Shared.Infrastructure.Tests` に 5 件追加
  （適用前は経路 0 本／宣言後に exchange が生える／exchange 名に前置しない／
  発行側と購読側が同じ名前を導く／束ねてもキュー名の前置が保たれる）
- 静的: `check-event-topology.js` の新規則を**変異 2 種で実測**
  （発行側の宣言を外す → 検出 / 購読側の束ねを外す → 検出）
- 実機（稼働 k3s）: 文書 1 件の登録で `Ingestion complete for ...: 3 chunks` を観測し、
  Qdrant のコレクションに 3 点が入った。**本修正の前は 0 点だった。**
