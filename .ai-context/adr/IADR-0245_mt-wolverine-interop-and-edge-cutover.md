---
title: IADR-0245 MassTransit と Wolverine はエンベロープ非互換であり、二重購読も二重発行も採れない。辺の切替は排出を必須段とする 3 段手順にする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0027
  - ADR-0028
  - IADR-0116
  - IADR-0234
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (§Wolverine 移行チェックリスト・§リスク・未決事項)
---

# IADR-0245 MT ↔ Wolverine の相互運用は成立しない。辺の切替は排出を必須段にする

## 状況

### 1. 辺を原子的に移しても、実行時には混在の窓が開く

[IADR-0234](./IADR-0234_wolverine-migration-boundary-455-441.md) 決定 3 は移行の単位をイベント辺（発行元＋全購読先を一括）と定めた。
これは**ソース上の整合**を保証するが、**実行時の整合は保証しない**。実測:

- **順序制御が無い。** Helm チャートに `argocd.argoproj.io/sync-wave` も `depends-on` も無く、
  唯一の hook は `drift-postsync-job.yaml` の PostSync だけである。`strategy:` の上書きも無い。
  **全サービスが既定の RollingUpdate で同時に転がる。**
- **キュー名が違う。** MassTransit は既定の `ConfigureEndpoints(ctx)`（フォーマッタ上書き無し）で
  キュー `RawDocumentFetched` を使い、exchange は URN 由来の
  `Knowledge.Contracts.Events:RawDocumentFetched` である（`EventMessageUrnTests` が固定）。
  Wolverine 側の共通ヘルパは `conversion-service.RawDocumentFetched` を購読する。

したがって窓は 3 つ開く。

| # | 窓 | 性質 |
| --- | --- | --- |
| 1 | 発行側 pod が先に切り替わる（新 = Wolverine 発行 / 旧 = MT 購読） | レース |
| 2 | 購読側 pod が先に切り替わる（旧 = MT 発行 / 新 = Wolverine 購読） | レース |
| 3 | **切替時点で MT のキューに残っているメッセージ。新購読者は別のキューを見ているので永久に排出されない** | 🔴 **決定論的**（レースではない） |

🔴 **`transportMismatches()` は静的検査であり、この 3 つを 1 つも見ていない。**
PR 時点では両側が Wolverine で整合して見えるため**検査は満たされる**。
**検査が満たされている瞬間こそデプロイが危険**という構造であり、
これまで本チェーンで見つけてきた「緑なのは誰も見ていないから」と同じ形である。

### 2. 相互運用は実測するまで誰も知らなかった

走査の結果、リポジトリ全体に **MT ↔ Wolverine 相互運用の記述は 0 件**、
**試験も 0 件**であった。W3 の実ブローカ器（#924）は **Wolverine ↔ Wolverine のみ**を試験する。

一方 `WolverineExtensions` は相互運用を意識した設計になっている
（「exchange 名には前置しない。前置すると発行側の exchange と食い違い『誰にも届かない』形になる」）。
**経路の命名は揃う方向に作られているが、エンベロープが解釈できる保証はどこにも無かった。**

## 実測

クラスタの RabbitMQ へ `kubectl port-forward` し、実ブローカ越しに両方向を測った。
**MassTransit の entity 名を自前の一意名へ上書きし、本番 exchange には一切触れていない。**
全フィールドへ非既定値を詰め、受信側で全項目を照合する形にした。

| 方向 | キューへ到達 | ハンドラ実行 | 失敗の現れ方 | 復旧可能性 |
| --- | --- | --- | --- | --- |
| **MT 発行 → Wolverine 購読** | ✅ 到達する | ❌ されない | 🔴 **黙って捨てる**（キュー深さ **0** / consumer **1 本** / 例外なし / ログなし） | **不可**。再生すべきものが残らない |
| **Wolverine 発行 → MT 購読** | ✅ 到達する | ❌ されない | `<queue>_error` へ **1 件**（`_skipped` ではない） | **可**。本体と理由が保全される |

### 方向 1 の詳細 —— 最悪の失敗モード

生 AMQP タップで、エンベロープがキューへ**届いていること**を確認した。

```
ContentType = application/vnd.masstransit+json
"messageType": [ "urn:message:Knowledge.Contracts.Events:RawDocumentFetched" ]
"attributes": { "confidentiality": "internal", "owner": "probe-team" }
"tags": [ "tag-alpha", "tag-beta" ]
```

**失敗はルーティングではなくエンベロープの解釈である。** MassTransit は本体を自前の封筒
（`messageId` / `sourceAddress` / `messageType[]` / `message: {…}`）で包む。Wolverine は自分の
フレーミングを期待し、解釈できないものを**受け取ったうえで捨てる**。

🔴 **運用上の帰結: 移行窓の間、メッセージは痕跡なく消える。**
キュー深さのアラームは鳴らない —— **キューが空だから**である。
再生すべきバックログも残らない。

なお本件では `messageType` に URN が 1 件しか載らず、**複数 URN の解決に至る前に封筒ごと捨てられた**。
基底型・インタフェースを含む複数 URN の扱いは**未測定**である（採らない方針が決まったため深追いしない）。

### 方向 2 の詳細 —— 生き残るし、見える

`_error` キューのメッセージヘッダ（`BasicGet` で覗き、**`requeue: true` で戻した**）:

```
MT-Fault-ExceptionType = System.ArgumentNullException
MT-Fault-Message       = Value cannot be null. (Parameter 'envelope')
MT-Fault-StackTrace    = at MassTransit.Serialization.SystemTextJsonSerializerContext..ctor(… MessageEnvelope envelope …)
MT-Reason              = fault
```

`_skipped`（consumer 不在）ではなく `_error`（逆シリアライズ失敗）である。
**本体と失敗理由が保全されるため、運用者は数えられるし再生できる。**

### 🔴 測定器の教訓 —— 本番と同じ構成で測らないと、自分のバグを製品のバグとして報告する

最初の probe の生ダンプは `"attributes": { }` / `"tags": [ ]` を示し、
**MassTransit の本番データ欠落バグ（ABAC 属性が落ちる）を報告する一歩手前だった。**

MT → MT の対照を先に走らせたところ `Attributes.Count=2 Tags.Count=2` で保存された。
空だったのは **probe が `Bus.Factory.CreateUsingRabbitMq` を使い、本番の
`AddMassTransit` ＋ DI 構成を使っていなかった**ためのアーティファクトである。
**欠陥は製品ではなく測定器の側にあった。**

**次に同種の測定をする人へ: 本番と同じ構成で測ること。対照を先に置くこと。**

### 排出完了の検証手段は実在する（実測）

RabbitMQ 管理 API（読み取り専用 GET）で、自前の一意名キューに 3 件投入し 1 件を unacked のまま保持して観測した。

```
GET /api/queues/%2F/<queue>
  messages                = 3
  messages_ready          = 2
  messages_unacknowledged = 1     ← in-flight が見える
  consumers               = 0
```

🔴 **`messages_ready` だけを見てはならない。** in-flight（unacked）は `messages_ready` に現れない。
排出の判定に使うのは **`messages`（= ready + unacknowledged）** である。

⚠️ **`consumers` は「処理中が無いこと」の証拠にならない。** 上の観測では 1 件を unacked で保持したまま
`consumers = 0` であった（`BasicGet` はポーリングで consumer 登録を伴わないため）。
**`consumers == 0` を「誰も処理していない」と読まないこと。**

## 決定

### 決定 1: dual-subscribe を採らない

実測により、**MT 発行 → Wolverine 購読はメッセージを痕跡なく破棄する**。
二重購読は必ずこの経路を含むため、**採れない。**

### 決定 2: dual-publish も採らない

各スタックが自分のフレーミングを読む限り動くが、窓 1・2 のいずれかで交差配送が起きた瞬間に
方向 1（黙って消える）を踏み得る。**重複配送と冪等性の負担を払ってなお、最悪の失敗モードが残る。**

### 決定 3: 🔴 IADR-0234 状況 2 の「二重購読は移行手順である」は、実行時には成立しない

同 IADR は `check-event-topology.js` が二重購読を意図的に違反にしないこと
（`:44-46` / `:303-305`「これが『切替』を『追加』に分解して 1 PR を小さく保つための前提になる」）を
分解の根拠にしていた。**検査器の設計判断そのものは変えない**（静的検査として二重購読を止めない、は正しい）。
**しかし「だから二重購読で安全に刻める」という含意は、実測により否定された。**
検査器が許すことと、実行時に安全であることは別である。

### 決定 4: 辺の切替は「排出 → 切替」を必須段とする 3 段手順にする

| 段 | 内容 | baseline |
| --- | --- | --- |
| **a. 停止** | 発行元の新規発行を止める（該当イベントのみ） | 変化なし |
| **b. 排出** | MT 側キューの `messages`（ready + unacked）が 0 になるまで待つ | 変化なし |
| **c. 切替** | 発行元と全購読先を Wolverine へ一括で切り替える | **ここで落ちる** |

**窓 1・2 は段 a により消える**（発行が止まっているので交差配送が起きない）。
**窓 3 は段 b が正面から扱う。**

### 決定 5: 排出完了の述語は `messages` である。`messages_ready` と `consumers` を使わない

**述語の選択は今ここで決められる**（未解決ではない）。いずれも実測で出た**偽陰性**であり、
将来 drain を実装する人が最初に踏む罠である。

- 🔴 **`messages_ready` は誤った述語である。** in-flight（unacked）のメッセージは
  `messages_ready` に**現れない**。処理中のものを残したまま「排出済み」と誤判定する。
  **判定に使うのは `messages`（= `messages_ready` + `messages_unacknowledged`）である。**
- 🔴 **`consumers == 0` は「処理中のものが無い」を意味しない。** `BasicGet` は
  コンシューマを登録せずにポーリングするため、**メッセージを unacked で保持したまま
  `consumers` は 0 を返す**（実測: `messages_unacknowledged=1` かつ `consumers=0`）。
  **`consumers` を排出完了の根拠にしない。** 補助情報に留める。

実測（自前の一意名キューへ 3 件投入し、1 件を unacked で保持して観測）:

```
GET /api/queues/%2F/<queue>
  messages                = 3
  messages_ready          = 2      ← これだけ見ると「あと 2 件」に見える
  messages_unacknowledged = 1      ← in-flight
  consumers               = 0      ← 保持中なのに 0
```

### 決定 6: 未解決として残すもの（E1 の作業仕様書で決める）

**断定しない。ただし先送りもしない** —— 下記 3 点は **E1 の作業仕様書で決着させる**ものとし、
「E1 の設計で決める」と書いたまま実装へ入ることを禁じる。

1. **再充填の防止を何が保証するか。** 段 a の「発行を止める」を機械的に強制する手段は現時点で無い。
   設定フラグ / デプロイ手順 / `enabled:false` の宣言経由のいずれを採るかを、根拠つきで決める。
2. 🔴 **見えない発行元を機械的に列挙する手段が現状で無い。**
   `RawDocumentFetched` は `ConversionJobEndpoints.cs:70` の `bus.Publish(ev, ct)` からも発行されるが、
   **型名が発行行に現れないため `check-event-topology.js` からは見えない**（IADR-0234 状況 4）。
   これは同検査器の publish 検出が持つ既知の取りこぼしであり、**#921（トランスポート ratchet）が
   対処として起票済み**である。E1 は **#921 の着地を待つのか、E1 の範囲で手作業により列挙して
   作業仕様書へ固定するのか**を決める。**「止め忘れやすい」という注意喚起では足りない。**
3. **観測の瞬間性。** `messages == 0` はその瞬間の値であり、直後に増えない保証にはならない。
   **「T 秒間に N 回連続で 0」の N と T を決める。決め方の根拠も示す**
   （たとえば観測されたメッセージ処理時間の最大値から導く）。

### 決定 7: チェーン算術を改める

[IADR-0234](./IADR-0234_wolverine-migration-boundary-455-441.md) 決定 3 の `13 → 11 → 9 → 9 → 3 → 2 → 1 → 0` は
**各辺が 1 PR で落ちる前提**だった。決定 4 により **各辺が 3 段**になるため、刻みは次のように変わる。

- **baseline の行が落ちるのは段 c だけである。** 段 a・b は行を動かさない。
- したがって**到達値の列は変わらない**が、**PR 数が辺ごとに増える**（1 → 最大 3）。
- 段 a・b を 1 PR に束ねるか、運用手順（PR を伴わない）にするかは**未決**であり、
  **決定 6 の 3 点と同じく E1 の作業仕様書で決着させる**（実装へ入る前に答えを書く）。

## 結果

- **E1 は本 IADR の着地後に着手する。** 前提（相互運用の可否）が実測で確定したため、設計をやり直せる。
- W3 の実ブローカ器は **Wolverine ↔ Wolverine のみ**を覆う。**MT ↔ Wolverine の試験は追加しない** ——
  採らない方式を試験しても保守負担が増えるだけである。本 IADR が実測の記録を持つ。
- 🔴 **決定 6 の 3 点は E1 の作業仕様書で決着させる。** 先送りすると、実装時に誰も決めないまま
  「たぶん大丈夫」で切り替わる。**仕様書に答えが書かれていない状態で実装へ入らないこと。**
- 見えない発行元の機械的列挙は **#921** の射程であり、本 IADR はその依存関係を記録するに留める。

## 関連

- [IADR-0234](./IADR-0234_wolverine-migration-boundary-455-441.md)（決定 3 のチェーン算術を本 IADR 決定 7 が改める。状況 2 の含意を本 IADR 決定 3 が否定する）
- [IADR-0116](./IADR-0116_reimplementation-branching-and-pr-policy.md)（レビュー可能な変更単位）
- **#921**（トランスポート ratchet。publish 検出の取りこぼし＝見えない発行元を機械的に列挙する手段）
