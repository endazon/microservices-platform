---
title: 作業仕様書 — 辺 RawDocumentFetched を Wolverine へ移す（E1・設計）
type: spec
status: draft
related_ids:
  - NFR
  - FR-01
  - FR-12
  - UC-04
  - UC-06
  - ADR-0027
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0027（メッセージング基盤 = Wolverine）"
related_adrs:
  - IADR-0234
  - IADR-0239
  - IADR-0245
issue: "#441"
---

# 作業仕様書: 辺 `RawDocumentFetched` の Wolverine 移行（E1）

## 起点

移行チェーンの **E1**（[IADR-0234](../adr/IADR-0234_wolverine-migration-boundary-455-441.md) 決定 3）。
切替手順は [IADR-0245](../adr/IADR-0245_mt-wolverine-interop-and-edge-cutover.md) 決定 4 の
**「停止 → 排出 → 切替」の 3 段**に従う。前提の W1・W2・W3・W4 は着地済み。

**本書は設計までである。実装は含まない。**

### 🔴 ［2026-08-22 追記 / #441］計画側の手順表が 8 段 → 11 段へ拡張された

裁定（`ADR-0052` / `ADR-0053`。ともに `Accepted`）により、移行チェックリストに**手順 9〜11 が追番で足された**
（既存の手順 1〜8 の番号は動かない —— 実装 IADR が番号を直接引いているため）。
**本書の受け入れ基準は 11 段に対して書き直してある**（後述 §受け入れ基準）。

| 新設 | 内容 | 本チェーンでの担い手 |
| --- | --- | --- |
| 手順 9 | 辺の片側移行を静的検査で違反とする（**実施順は手順 7 の前段**） | `check-event-topology.js`（既設） |
| 手順 10 | 再試行・デッドレターの等価性 ＋ デッドレター到達が通知経路へ届くこと | **W1**（着地済み）＋ `ADR-0053`（カウンタは未実装・別単位） |
| 手順 11 | ブローカ不達時に readiness が落ちる（**メッセージングに依存するサービスに限る**） | **W4**（着地済み。opt-in の別拡張にした判断が適用範囲の限定と一致する） |

**手順 10・11 で新たな実装は生じない**（W1・W4 が既に整備済みで、完了判定の材料として参照されるようになる）。

## 対象の辺

| 役割 | 場所 | 検査器から見えるか |
| --- | --- | --- |
| 発行 ①（定期同期・手動同期） | `DataSourceSyncService.cs:124` `bus.Publish(new RawDocumentFetched(…))` | ✅ 見える |
| 発行 ②（再変換のリトライ） | `ConversionJobEndpoints.cs:70` `bus.Publish(ev, ct)` | 🔴 **見えない** |
| 購読 | `RawDocumentFetchedConsumer`（`IConsumer<RawDocumentFetched>, IPipelineStep`） | ✅ 見える |

**辺は原子的**であるから、この 3 箇所を同一 PR で切り替える。

### 🔴 発行 ② を止め忘れると何が起きるか

再変換 API（`POST /jobs/{id}/retry`）が MassTransit で `RawDocumentFetched` を発行し続ける一方、
購読側は Wolverine へ移っている。[IADR-0245](../adr/IADR-0245_mt-wolverine-interop-and-edge-cutover.md) の実測により、
この組み合わせは **Wolverine がメッセージを受け取ったうえで捨てる**（キュー深さ 0・例外なし・ログなし）。

**再変換が「受け付けられたのに永久に実行されない」形になり、キュー深さのアラームも鳴らない。**
`transportMismatches()` は PR 時点で発行 ② を認識しないため、**検査は緑のまま**である。

## 問い 1 — drain 中に再充填されないことを何が保証するか

### 答え: **既存の `DataSourceSync:Enabled=false` を使う。新しい仕組みを作らない。**

実測で判明した経路は次のとおり。

| 発行元 | 起動契機 | 停止手段 |
| --- | --- | --- |
| ① `DataSourceSyncHostedService`（`BackgroundService` ＋ `PeriodicTimer`） | **定期実行** | 🟢 **`DataSourceSync:Enabled=false`**（既存。Helm の `DataSourceSync__Enabled` で注入） |
| ① `DataSourceEndpoints.cs:73`（`POST /sync`） | **手動 API** | 🔴 設定で止まらない |
| ② `ConversionJobEndpoints.cs:70`（`POST /jobs/{id}/retry`） | **手動 API** | 🔴 設定で止まらない |

`DataSourceSyncHostedService.StartSchedule()` は `opt.Enabled` が false なら
`定期同期は無効です（DataSourceSync:Enabled=false）。手動 /sync のみ有効。` を出して即 return する。
**既定は無効**であり、Helm の `deployment.yaml:91` が `DataSourceSync__Enabled` を注入している。

### 🔴 `enabled:false`（`pipeline.json`）は使えない

当初の候補だったが、**`PipelineSourceOptions` は `Event` と `Service` しか持たない**（実測）。
`enabled` は `steps[]`（コンシューマ）にのみ在り、`sources[]`（発行者）には**存在しない**。
`AddPlatformPipelineStep` の登録規則 5 はコンシューマの登録を止めるだけで、**発行には効かない。**
宣言モデルを拡張して `sources[].enabled` を足す案は成立するが、**E1 に混ぜない**
（E1 は既に L である。必要になったら別単位として切り出す）。

### 手動 API の 2 経路をどう扱うか

**設定では止まらない。**したがって次を採る。

1. **切替時に一時的に 503 を返す**のではなく、**運用手順として切替窓の間だけ呼ばないことを合意する。**
   両者とも**人間が明示的に叩く API** であり（定期実行ではない）、窓は後述のとおり短い。
2. **切替後に取りこぼしが無いことを確認できる形にする** —— 段 c の後、`messages` が
   0 のままであることを再確認する（段 b と同じ述語で 1 回追加観測する）。

⚠️ **これは機械的強制ではない。** 「窓の間に人が API を叩かない」という運用上の約束に依存する。
**この限界を承知のうえで採る**理由は、機械的強制（API の一時封鎖）を入れると E1 の射程が
「移行」から「一時的な機能停止機構の新設」へ広がるためである。**強制が必要と判断されたら別単位とする。**

### 🔴 強制できないなら、破られたことを検出できること

**運用合意に留めるからこそ、破られたら気付く形が要る。**
問い 3 の drain 検証（`T = 90 秒` / `N = 3` / 30 秒間隔）が**そのまま検出機構になる** ——
drain 中に誰かが手動 API を叩けば `messages` が 0 でなくなり、**連続の streak が途切れる**。

**これを「たまたまそうなる」で済ませず、手順の要件として固定する。**

- **drain 中に再充填が起きた場合、手順は完了せず中断する**（streak をリセットし、段 c へ進まない）。
- **判定は「N 回連続で 0」である。「N 回のうち 1 回でも 0 なら可」ではない。**
  後者へ緩めると、再充填が起きても途中の 1 回が 0 なら通過してしまう。

これで「**強制はできないが、破られたら気付く**」が**測った証拠つき**で成立する。

## 🔴 計画側が dual-subscribe を足場として許していた（［2026-08-22 解消 / #441］撤回済み）

planning#438 は受理・反映済み（`8928c4c`）で、**刻み幅がイベント辺へ訂正された**。結論は本書と一致する。
**ただし訂正文が「CI が緑になる最小単位＝サービス単位の二重購読の追加」という子箇条を新たに入れた。**

[IADR-0245](../adr/IADR-0245_mt-wolverine-interop-and-edge-cutover.md) の実測では
**MT 発行 → Wolverine 購読はメッセージを黙って捨てる**。よってこの子箇条は
**「CI は緑になるがメッセージは失われる」足場**を推奨していることになる。**2 回目の差し戻しを起票中。**

**E1 は dual-subscribe を採らない。**

🔴 **［2026-08-22 解消 / #441］計画側が撤回した。** `12_backend-application-stack.md` に
**「［2026-08-22 訂正 2］二重購読は成立しない。上の 3 行は撤回する。」**が入り、
当該箇条は取り消し線で残された（史実として残し、新しい行で訂正する形）。
**撤回の根拠として IADR-0245 の実測が引かれている。**
「計画側が訂正されるまで」という条件は満たされた —— **本節は以後、経緯の記録である。**

## 受け入れた挙動差 —— 発行時の CancellationToken

MassTransit の `Publish(msg, ct)` に対し、Wolverine の `IMessageBus.PublishAsync<T>` は
**`(T, DeliveryOptions)` のみで CancellationToken を取らない**（アセンブリで実測）。
したがって**発行のキャンセル伝播は失われる**。これは移行漏れではなく**Wolverine の API 上の差**であり、
回避手段が無いため受容する。**ビルドは通り、どのテストも気づかない種類の差なのでここに記録する。**

## 問い 2 — 見えない発行元をどう列挙するか

### 答え: **#921 を待たない。E1 の範囲で過剰包含から絞り、本書に固定する。**

**#921 は本問題を解かない。** 同 issue は「baseline diff が `[masstransit]` → `[wolverine]` を
見ていない」＝**トランスポート変化の ratchet** であって、**publish 呼び出しの型解決**ではない。
`findPublishers` は次のとおり `Publish<Event>` か `Publish(new Event(` にしか一致しない。

```js
const re = new RegExp(String.raw`Publish[A-Za-z]*\s*(?:<\s*${ev}\s*>|\(\s*new\s+${ev}\b)`);
```

**変数を渡す形（`Publish(ev, ct)` / `Publish(ToEvent(...))`）は構造的に不可視**であり、
regex の拡張では埋まらない（型解析が要る）。これは
[IADR-0245](../adr/IADR-0245_mt-wolverine-interop-and-edge-cutover.md) 決定 6-2 の
「機械的に列挙する手段が現状で無い」の裏付けである。

### 列挙の手順（過剰包含 → 理由つきで絞る）

```
git grep -nE "\.(Publish|PublishAsync|Send|SendAsync)\s*[<(]" -- \
  'src/knowledge/backend/Services/*/src/**/*.cs' 'src/platform/backend/**/*.cs' ':!*Tests*'
```

**17 ヒット**。ここから理由つきで 4 件を落とす。

| 落とすもの | 件数 | 理由 |
| --- | --- | --- |
| `llmClient.SendAsync` / `client.SendAsync` / `base.SendAsync` | 3 | **HTTP** の送信であってメッセージバスではない |
| `doc.Publish()` | 1 | ドメインの状態遷移メソッド（`DocumentEndpoints.cs:185`）であってバス発行ではない |

残る **バス発行 13 箇所**のうち、**検査器に見えるのが 6・見えないのが 7**。

| 見えない 7 箇所 | 実型 |
| --- | --- |
| `ConversionJobEndpoints.cs:70` | `RawDocumentFetched`（`PrepareRetryAsync` の戻り値型で確認） |
| `DocumentEndpoints.cs:92 / 128 / 159 / 188 / 205` | `DocumentUpdated`（`ToEvent` の戻り値型） |
| `TagDictionaryEndpoints.cs:136` | `DocumentUpdated`（同上） |

### 🔴 ［2026-08-22 是正 / #441］手順 1 は発行者の列挙を要求するようになった

**従前ここには「手順 1 は発行者の列挙を要求していない。したがって本節の列挙は実装側の判断であり
計画に根拠を持たない」と書き、続けて「本節の手順を**辺ごとにやり直すこと**」と指示していた。
🔴 **後者は要求の形を取り違えている。是正する。**

裁定（ADR-0052・`Accepted`）で手順 1 が改められた。

- **決定 1**: 対応表は「**イベント型 → 発行サービス／購読サービス**」とする。購読側だけでは
  計画が導いた検査の入力として不足である。
- **決定 2**: **発行の検出が網羅的であることを、移行前に担保する。**
  担保の方法を**移行前に定めて記録する**。**型名が発行行に現れない発行経路**を覆うことを条件に含める。
  **方法そのものは実装に委ねる。**

> **なぜ「列挙する」だけでは足りないか**（同決定の理由）: 検査は発行側トランスポートを和集合で取るため、
> **取りこぼしが 1 件あれば検査は通っても意味を持たない**。列挙の網羅性が示されない限り、
> **手順 7・9 の緑は「違反が無い」ではなく「見ていない」を意味する。**

🔴 **要求されているのは「辺ごとのやり直し」ではなく、「担保方法を一度定めて記録すること」である。**
**辺ごとにやり直しても、「取りこぼしが無い」ことの根拠は毎回その場限りで、記録に残らない。**
**方法そのものが成果物**として求められている。

**したがって本節の手順を、辺ごとの作業手順から「担保方法」へ昇格させ、
[IADR-0245](../adr/IADR-0245_mt-wolverine-interop-and-edge-cutover.md) 決定 8 として記録した**
（作業仕様書は PR ごとの文書であり、辺をまたいで参照される規範の置き場ではない。
ADR-0052 §結果 フォローアップ 2 が求める「記録の IADR 番号」も同 IADR である）。

**本節に残るのは、E1 時点での適用結果（13 / 6 / 7）だけである。**
次の辺の担当は、**本節ではなく IADR-0245 決定 8 を読む**こと。

### 🔴 この列挙が網羅であることを何が保証するか

**列挙そのものではなく、「別の手段で確立された既知の値を、教えられずに復元したこと」が保証である。**

Phase 0 の実測は「publish 検出が**実在 13 箇所のうち 7 箇所**を取りこぼす」と記録している。
上の手順は**その数字を独立に再現した**（バス発行 13 / 可視 6 / 不可視 7）。
**手順が既知の正解を復元できたことが、手順の妥当性の根拠である。**

⚠️ **ただしこの根拠は 1 回しか使えない**（既知の値は消費される）。以後の辺を支えるのは
**総数の保存**である —— 移行は発行箇所を消さず、トランスポートを移すだけなので、
**説明のつかない減少は移行の進捗ではなく列挙の取りこぼしである**（IADR-0245 決定 8 条件 B）。

⚠️ **限界**: 本手順は `Publish` / `Send` という**名前**に依存する。将来ラッパ経由の発行
（例: `IEventPublisher.Emit(...)`）が入れば取りこぼす。**名前に依存しない列挙は型解析が要る**という
IADR-0245 決定 6-2 の限界はそのまま残る。**E1 の時点での網羅**であって、恒久的な保証ではない。

## 問い 3 — `messages == 0` の瞬間性をどう扱うか

### 答え: **`T = 90 秒`・`N = 3`（30 秒間隔で 3 回連続 0）。**

#### 実測に基づく下限

W1 が固定した再試行ポリシー（[IADR-0234](../adr/IADR-0234_wolverine-migration-boundary-455-441.md) 決定 6）は
**間隔 2s / 10s / 30s・試行上限 4**である。したがって:

- **1 通が失敗し続けた場合、正当に unacked のままでいる時間は最低 42 秒**（2 + 10 + 30）である。
- **`messages` が一度 0 に見えても、42 秒以内に再試行で戻り得る**わけではない（unacked は
  `messages` に含まれるため 0 にならない）が、**42 秒は「1 通の処理が決着するまでの最大既知時間」**である。

#### 保守的な上乗せ（実測ではない）

| 項目 | 値 | 根拠 |
| --- | --- | --- |
| 再試行窓 | 42 秒 | **実測**（W1 が固定した間隔の総和） |
| 処理時間そのもの | 未測定 | 変換は pandoc / LLM を含み、**本番の実測値を持っていない** |
| → `T` | **90 秒** | 42 秒に**保守的な余裕**を乗せた値。**実測ではない** |
| → `N` | **3**（30 秒間隔） | 単発観測の偶然を排すため複数回。`T = N × 30 秒` |

🔴 **`T = 90` の根拠のうち、実測は 42 秒の部分だけである。**残りは保守的な上乗せであり、
**本番の処理時間を実測できたら見直す**。数字だけを根拠なしに置いていないことを明示するために、
実測部分と上乗せ部分を分けて書いた。

## drain の述語

[IADR-0245](../adr/IADR-0245_mt-wolverine-interop-and-edge-cutover.md) 決定 5 に従う。

- **使うのは `messages`**（= `messages_ready` + `messages_unacknowledged`）。
- 🔴 **`messages_ready` は使わない。** in-flight（unacked）が現れず、処理中を「排出済み」と誤判定する。
- 🔴 **`consumers == 0` を根拠にしない。** unacked を保持したまま 0 を返す（実測）。

取得は管理 API の読み取り専用 GET: `GET /api/queues/%2F/<queue>`。

## 手順（3 段）

| 段 | 内容 | baseline | PR |
| --- | --- | --- | --- |
| **a. 停止** | `DataSourceSync:Enabled=false` を注入。手動 API 2 経路は運用上叩かない合意 | 変化なし | 設定変更（PR か運用手順かは下記） |
| **b. 排出** | MT キューの `messages` が **30 秒間隔で 3 回連続 0** | 変化なし | PR 無し（確認手順） |
| **c. 切替** | 発行 ①②・購読を Wolverine へ一括切替 | **13 → 11** | 本体 PR |

**行が落ちるのは段 c だけである。** 段 a・b は baseline を動かさない。
**辺あたりの PR 数は 1 → 最大 3**（[IADR-0245](../adr/IADR-0245_mt-wolverine-interop-and-edge-cutover.md) 決定 7）。

**段 a を PR にするか運用手順にするか**: `DataSourceSync__Enabled` は Helm の値であるため
**設定変更 PR になる**（GitOps 経由）。段 b は観測のみで PR を伴わない。

### 🔴 ［2026-08-22 確定 / #441］段 a・b は本体 PR に含めない

**停止と排出はデプロイ時点の操作であって、コードの変更ではない。**
本体 PR に含めると **「マージした＝排出した」と読まれる** —— マージからデプロイまでの間に
再充填が起きても、PR の緑はそれを何も言わない。よって**運用手順として本節に置き、PR 本文へ転記する**。

### 排出すべきものが在るか（実測。**「たぶん無い」で済ませない**）

共有クラスタの RabbitMQ に対し**読み取りのみ**で測った（2026-08-22）。

```
kubectl exec -n platform-infra <rabbitmq-pod> --   rabbitmqctl list_queues name messages messages_ready messages_unacknowledged consumers
```

| キュー | messages | ready | unacked | consumers |
| --- | --- | --- | --- | --- |
| `RawDocumentFetched` | **0** | 0 | 0 | 1 |

**述語は `messages`（ready ＋ unacked）である**（[IADR-0245](../adr/IADR-0245_mt-wolverine-interop-and-edge-cutover.md) 決定 5。
`messages_ready` でも `consumers == 0` でもない —— 前者は unacked を落とし、後者は
**unacked を抱えたまま consumers が 0 になる**ため、どちらも「空」を誤って宣言する）。

**本辺のキューは全 60 キュー中この 1 本のみ**（前置つきの `conversion-service.RawDocumentFetched` は
本 PR のデプロイ後に初めて生える）。

🔴 **したがって本 PR のデプロイ時点では排出は不要である。ただしそれは「今回は 0 だった」であって、
「排出の手順が不要」ではない。** 次の辺（E2 / E3a / E3b）では在り得る ——
**手順は下に残す。**

⚠️ 参考（本辺とは無関係・**触っていない**）: `wolverine-dead-letter-queue` に 3 通、
`interop-b-*-q_error` に 1 通が滞留している。いずれも IADR-0245 の相互運用実測で
**こちらが出したもの**である。破壊的操作は行わないため、そのまま残してある。

### 運用手順（デプロイ担当が実施する）

1. **段 a（停止）**: `DataSourceSync__Enabled=false` を注入し、ロールアウト完了を待つ。
   **手動 API 2 経路（同期の手動起動・再変換）は窓の間叩かない**（運用合意。機械的強制は無い）。
2. **段 b（排出）**: 上のコマンドで `RawDocumentFetched` の **`messages` が
   30 秒間隔で 3 回連続 0**（＝ `T = 90 秒` / `N = 3`）になることを確認する。
   🔴 **1 回でも 0 以外が出たら streak をリセットして最初から数え直す**
   （「N 回中 1 回でも 0 なら可」に緩めると再充填を検出できない —— 変異 E・F）。
3. **段 c（切替）**: 本 PR をデプロイする。
4. **事後確認**: 前置つきキュー `conversion-service.RawDocumentFetched` が生え、
   旧キュー `RawDocumentFetched` に**新たな滞留が生じないこと**を確認する。

**段 a の設定変更は GitOps の値変更 PR になる**（本体 PR とは別）。段 b は観測のみで PR を伴わない。

## 受け入れ基準（段 c の本体 PR）

**［2026-08-22 改訂 / #441］11 段の手順表に対して引き直した。**
「本 PR で示すもの」と「前提として既に成立しているもの」を分ける ——
**混ぜると、既に緑なものを根拠に未検証のものまで緑に見える。**

### 辺そのもの（手順 1・3〜7・9）

- [ ] 発行 ①②・購読の 3 箇所すべてが Wolverine へ移っている（1 箇所でも残っていたら不可）
- [ ] `RawDocumentFetchedConsumer` が `IPipelineStep<RawDocumentFetched>` を実装し、
      **`Handle(RawDocumentFetched, Envelope, CancellationToken)`** を持つ（[IADR-0239](../adr/IADR-0239_wolverine-pipeline-step-registration.md)）。
      **`Envelope` を取るのは試行回数を知る唯一の口だからである**（手順 10 の判定に要る）
- [ ] `AddPlatformWolverineStep` 経由で登録し、戻り値の `Queue` を `ListenToPlatformQueue` へ渡している
- [ ] **登録経路が実際に使われることを試験が確かめている**（ハンドラ直接呼びだけにしない。
      変異 R で「直接呼びだけなら登録経路を削っても全部緑」を実証済み）
- [ ] `check-event-topology.js --update` の差分に、**両側のトランスポートが反転して現れる**
      （生の出力をサイズ確認つきで読む。行が変わらない＝整合ではなく「見えていない」可能性を先に潰す）
- [ ] 🔴 **発行 ②（不可視）の移行は、`--update` の差分では証明できない。** コード diff と
      実ブローカ試験で示す（変異 A の非変化証明）
- [ ] W3 の器を使った実ブローカ試験があり、**囮（publisher-local bait）を含む**（手順 8）
- [ ] `check-event-topology.js`（手順 9）が緑 —— ただし**緑であることの意味は下の「網羅性」条件に依存する**

### 🔴 発行の網羅性（手順 1・`ADR-0052` 決定 2）

**この節が満たされない限り、上の手順 7・9 の緑は「違反が無い」ではなく「見ていない」を意味する。**

- [ ] **担保方法が記録されている**（[IADR-0245](../adr/IADR-0245_mt-wolverine-interop-and-edge-cutover.md) 決定 8）。
      **辺ごとの作業ではなく、辺をまたいで使う方法として 1 箇所に定義されていること**
- [ ] 方法が**型名の現れない発行経路を覆う**（呼び出し名で引く段 1 ＋ 式の型を辿る段 3）
- [ ] 条件 A（較正）が満たされている: 既知の値（Phase 0 の 13 / 不可視 7）を**教えられずに復元**した
- [ ] 条件 B（各回）が満たされている: **バス発行の総数が保存**され、差は個別コミットで説明できる
- [ ] **限界が記録されている**（`Publish` / `Send` という名前への依存。ラッパ導入時の責任の所在）

### 再試行・デッドレター（手順 10 / `ADR-0053`）

- [ ] **判定が新トランスポート側で作り直されている** ——
      `envelope.Attempts >= WolverineExtensions.MaxAttempts`（**Wolverine は 1 始まり**。MT の `+1` は外す）
- [ ] 境界の**両側**（上限 -1 / 上限）が試験で固定され、変異（`>=` → `>`）で検出力を確認済み
- [ ] 契約定数と試行上限の一致が、**この辺を実際に駆動する側**（Wolverine）に対して束ねられている
- [ ] `deadLettered` の意味が**キュー名ではなく契機**（自動再試行を使い切ったこと）で記述されている
      （`ADR-0053` 決定 2。コード上の記述 5 箇所を是正済み）
- [ ] **宛先トポロジが実装側の記録に残っている**（`ADR-0053` 決定 4 の条件。IADR-0245 決定 9）
- [ ] （前提・本 PR の射程外）再試行の等価性そのものは **W1** が着地済み
- [ ] 🔴 **射程外として明示**: 通知カウンタ（`ADR-0053` 決定 1・3）は**未実装**。
      当面の通知手段は SC-07 のデッドレター標識と「失敗のみ」フィルタによる人手確認である（同決定 5）

### readiness（手順 11）

- [ ] `Platform.Shared.Infrastructure` の readiness（W4）が DataSourceService へ配線されている
- [ ] 適用範囲が**メッセージングに依存するサービスに限られている**（opt-in の別拡張。無関係なサービスを落とさない）

### 移行の刻みと安全弁

- [ ] baseline が **13 → 11**（DataSourceService の 2 行）
- [ ] `DataSourceService` の MassTransit 参照が **8 ファイルすべて**から消えている
- [ ] `PartialMigrationSafetyValveTests` が緑（安全弁に触っていない）
- [ ] 🔴 **drain 中の再充填で手順が中断すること**が、変異で実証されている（下記 変異 E・F）

## テストの器の選択（直接呼び vs Wolverine ホスト）

購読が Wolverine へ移り、MassTransit の `ITestHarness` が使えなくなった 3 ファイルについて、
**器をファイルごとに選び分けた**。両方を混ぜたのは、片方だけでは測れないものがあるからである。

| ファイル | 器 | 理由 |
| --- | --- | --- |
| `RawDocumentFetchedConsumerTests` | `Handle(...)` を直接呼ぶ | 測るのは**正規化結果 → 発行口へ渡す値**の写像であり、届くかどうかではない |
| `RawDocumentFetchedConsumerJobTests` | `Handle(...)` を直接呼ぶ（`Envelope.Attempts` を明示） | 測るのは**何回目を最後と見なすか**。ランタイムの再試行を待つ必要がない上、境界を正確に指定できる |
| `PipelineStepRegistrationTests` | **Wolverine ホストを起こす**（外部トランスポートは無効化） | 測るのは**登録経路そのもの**。直接呼びでは `AddPlatformWolverineStep` を一度も通らない |

### 🔴 「直接呼びだけにすると気づかない」を測定で示した

変異 R（`AddPlatformWolverineStep` の規則 9 から `options.Discovery.IncludeType<TStep>()` を削る）
を入れて全件を走らせた結果:

- **落ちたのは `PipelineStepRegistrationTests.有効な段は構成に従い登録されイベントを処理する` の 1 件だけ**
- 直接呼びのテスト（`RawDocumentFetchedConsumerTests` / `RawDocumentFetchedConsumerJobTests`）は**全て緑のまま**

つまり **3 ファイルすべてを直接呼びにしていたら、登録経路を丸ごと削っても CI は緑で通る。**
W2 で作った経路が実際に使われていることは、ホストを起こす器でしか確かめられない。

### 抽象を挟んだことで空いた穴を同じ PR で塞いだ

E1 で発行を `IDocumentNormalizedPublisher` へ切り出した結果、ハンドラ側のテストは
**発行口へ渡した引数**しか見なくなった（旧構成ではハーネスが発行済みイベントを直接見ていたので、
引数 → イベントの写像も一緒に測れていた）。この穴を `MassTransitDocumentNormalizedPublisherTests`
（新設）で塞いだ。

変異 M（アダプタで `Title` と `MarkdownUri` を入れ替える）の実測:
**落ちたのは新設したアダプタ試験の 1 件だけで、`RawDocumentFetchedConsumerTests` は緑のまま**だった。
穴が実在したこと、そして新設ファイルがちょうどそこを塞いでいることの両方が測れている。

### 器が本番から乖離すると、本番に無い失敗を作る

最初に書いたホスト器は `opts.AddPlatformWolverineStep(...)` だけを呼び、
`UsePlatformMessagingDefaults()` を省いていた。結果、Wolverine 既定の
`ServiceLocationPolicy.NotAllowed` のままコード生成が走り、EF の `AddDbContext`
（不透明なラムダ Factory）に依存する段が `InvalidServiceLocationException` で落ちた。

**本番（`Program.cs`）はヘルパの手順 5 でこれを許可済みなので、落ちたのはテストの器だけである。**
一瞬「本番の欠陥を捕まえた」と読みかけたが、`Program.cs:109` を読んで否定した。
器は本番の構成をなぞること —— 乖離した器は、本番に無い失敗を作り、本番に在る失敗を見逃す。

### 実ブローカ経路との分担

上記はいずれもブローカを使わない。**辺が実際に配送されること**は
`Knowledge.IntegrationTests` の実ブローカ試験が持ち、本 3 ファイルの射程ではない。

## 変異試験（計画）

| # | 変異 | 期待 |
| --- | --- | --- |
| A | 発行 ②（`ConversionJobEndpoints.cs:70`）を MassTransit のまま残す | 実ブローカ試験が落ちる（`--update` の差分では**捕まらない**ことも併せて記録する） |
| B | 購読側の `IPipelineStep<TIn>` を素の `IPipelineStep` へ戻す | 起動失敗（IADR-0239 規則 5） |
| C | `ListenToPlatformQueue` へ渡すキュー名の前置を落とす | fan-out ではないので競合は起きないが、**宣言と実装のずれ**として検出されるべき。落ちなければ穴として記録し塞ぐ |
| D（否定対照） | 実装後に「変えても何も落ちない」箇所を探す | 穴なら塞ぐ |
| **E** | 🔴 **drain の途中で 1 通発行する** | **手順が完了しない**（streak が途切れ、段 c へ進まない）ことを実測する |
| **F** | 🔴 **判定を「N 回連続で 0」から「N 回中 1 回でも 0 なら可」へ緩める** | 変異 E が**素通りする**ことを実測する。緩い判定では再充填を検出できないことの証明 |

**変異 F は「変異 E を検出する仕組みが本当に効いているか」を測るものである。**
E だけでは「連続」という条件が効いているのか、たまたま落ちたのかを区別できない。

### 実施済み（テスト 3 ファイルの移行に伴うもの・すべて実測）

いずれも **変異が着地したこと（`diff` が意図した 1 箇所だけ・`ビルドに成功しました` EXIT=0）を
先に確認してから**テスト結果を読んでいる。

| # | 変異 | 落ちたテスト | 緑のままだったもの（否定対照） |
| --- | --- | --- | --- |
| R | 規則 9 の `options.Discovery.IncludeType<TStep>()` を削る | `PipelineStepRegistrationTests.有効な段は…` の 1 件 | 直接呼びのテスト全件 |
| M | アダプタで `Title` と `MarkdownUri` を入れ替える | `MassTransitDocumentNormalizedPublisherTests` の 1 件 | `RawDocumentFetchedConsumerTests` |
| B | `IsLastAttempt` の `>=` を `>` にする | `Consume_failure_on_last_attempt_marks_dead_lettered` の 1 件 | 他 73 件 |

**R と M は「落ちたこと」より「緑のままだったもの」に意味がある。**
どちらも器の選択・新設ファイルの必要性がそこに現れている。

### 変異 A の非変化証明（実測）

**発行 ②（`ConversionJobEndpoints.cs:70`）を MassTransit のまま残す**変異を入れ、
**トポロジ検査に差が出ないこと**を測った。**非変化は省かれやすい側なので、対照を取って数値で示す。**

| | 検査の EXIT | 報告行 | `--update` 後の baseline | 対照との差 |
| --- | --- | --- | --- | --- |
| 対照（変異なし） | 0 | 発行 [DataSourceService(wolverine)] → 購読 [ConversionService(wolverine)] | **1908 バイト**（元 1912、差分 120 バイト） | — |
| **変異 A** | **0** | **同一** | **1908 バイト**（差分 120 バイト） | 🔴 **0 バイト** |

**変異はビルドを通ったうえで着地している**（`diff` は意図した 6 行のみ・`ビルドに成功しました` EXIT=0）。

🔴 **終了コードも報告行も baseline のバイト列も、1 つも動かない。**
`--update` の差分は「両側が masstransit → wolverine へ反転する」形で、**変異の有無にかかわらず同じ**である。
**辺の片側が旧トランスポートに残っていることを、この検査器はどの出力にも現さない。**

### union 変異は E1 の辺では作れない（前提を測った）

**前提**: `transportMismatches()` が購読側の不一致を隠すには、`pubTransports` に **2 つ以上**の
トランスポートが載る必要がある（1 つなら「和集合」に隠す相手がいない）。

**実測（全イベント・`buildTopology()` を直接呼んで測定）**:

| イベント | 可視な発行元 | `pubTransports` |
| --- | --- | --- |
| RawDocumentFetched | 1 | `["wolverine"]` |
| DocumentNormalized / DocumentUpdated / DocumentDeleted / IngestionCompleted | 各 1 | 各 1 要素 |
| IngestionRequested | 0 | `[]` |

🔴 **リポジトリ内のどのイベントも、可視な発行元は最大 1 である。** したがって
**union 変異は E1 の辺だけでなく、現在のどの辺でも自然には作れない。**
理由は単純で、**2 つ目の発行元は型名が発行行に現れないため `findPublishers` から見えず、
`pubTransports` に何も寄与しないから**である。

#### 機構そのものは実在する（合成で確認した）

「作れない」で終えると、union の危険が**未検証の伝聞**のまま残る。差分を 1 つだけにした 2 段で確認した
（**検査器は本文を走査するテキスト検査であり、実行可能性を読まない**。よって本実験の着地条件は
「検査器の入力が意図どおり変わったこと」＝`diff` であり、ビルドは通していない。直後に復元済み）。

| 段 | 変えたもの | 検査の EXIT | 結果 |
| --- | --- | --- | --- |
| U1 | 購読側の記法を `IConsumer<RawDocumentFetched>` へ（＝masstransit） | **1** | 違反 1 件: `発行 [wolverine] / 購読 [masstransit]` |
| U2 | U1 に**可視な masstransit 発行元を 1 つ足す**（他は U1 のまま） | **0** | 🔴 **トランスポート不一致の違反が消える**。報告行は `発行 [ConversionService(masstransit), DataSourceService(wolverine)] → 購読 [ConversionService(masstransit)]` |

**U1 → U2 の差は「可視な旧トランスポート発行元を 1 つ足した」ことだけ**であり、
それだけで**違反が消えて完全な緑になる**。union の機構は実在する。

#### 🔴 偽の緑は 2 種類あり、E1 が晒されているのは危険なほうである

| 型 | 残った旧発行元 | `pubTransports` | 検査 | 実害 |
| --- | --- | --- | --- | --- |
| (i) union 型 | **可視** | 2 要素 | 緑 | 購読者は旧発行元からは受け取れる（**部分的損失**） |
| (ii) 不可視型 | **不可視** | 1 要素 | 緑 | 🔴 **その発行元のメッセージは誰にも届かない（全損）** |

**(ii) では union は関係ない。** 発行元が `pubTransports` に載らないので、
**隠しているのは和集合ではなく不可視性そのものである。**
**E1 が現に晒されているのは (ii)** であり、変異 A が測ったのはこちらである。
**そして (ii) のほうが損失が大きい。**

これが `ADR-0052` 決定 2 の「**取りこぼしが 1 件あれば検査は通っても意味を持たない**」の実例である ——
本辺では取りこぼしが 1 件あり、検査は通り、そして意味を持っていない。

#### 副産物: 発行側と購読側でトランスポートの導出規則が違う

実験の途中で**購読側の `using` を書き換えても報告行が変わらない**ことに当たり、実装を読んで確かめた。

- **発行側**: `transportsOfFile()` が**ファイル先頭の `using` 行**から導く（粗い。ファイル単位）
- **購読側**: `findSubscribers()` が**記法**から導く（`IConsumer<T>` = masstransit / `Handle(T x` = wolverine。精密）

**E2 の pre-poisoning（同一ファイルに両方の `using` が並ぶと発行が両トランスポートとして記録される）が
発行側にだけ起きるのは、この非対称性のためである。** 購読側は記法で決まるので `using` の混在に影響されない。

## 実ブローカ結合試験（手順 8）

**器**: `Knowledge.IntegrationTests/Fixtures/RawDocumentFetchedEdge.cs`（新設）。
W3 の器（`WolverineBrokerEdge`）から**囮の作法だけを引き継ぎ、運ぶものを本物に替えた** ——
本物の契約型 `RawDocumentFetched` を、本物の `RawDocumentFetchedConsumer` へ、
**本番と同じ登録経路**（`AddPlatformWolverineStep` → `ListenToPlatformQueue` →
`UsePlatformMessagingDefaults`）で届ける。合成ハンドラでは登録経路の破れを測れない。

**主張は 3 つで 1 組**: ①本物のハンドラが**正規化まで進んで発行口へ到達**した（受信だけを見ない ——
受信して例外で落ちてもキューからは消える） ②**囮が受信しなかった**（＝ブローカを経由した）
③**型名が発行行に現れない形**（発行 ② と同じ形）でも同じ経路を通る。

### 実行の実測（skip を「通った」と数えない）

| 条件 | 結果 |
| --- | --- |
| ローカル・ブローカ無し | **skip 32 → 34**（＝ 2 件とも走っていない） |
| ローカル・実ブローカあり（`PLATFORM_TEST_RABBITMQ`） | **合格 2 / skip 0**。統合全体は 30 合格 → **38 合格・skip 34 → 26** |
| PR の CI（`ci.yml`） | `--filter "Category!=Integration"` により**設計どおり走らない** |
| 回収先（`integration.yml`） | `ubuntu-latest`・**`--filter` 無し**・push develop ＋ 日次。Docker があるので `BrokerRequired.SkipUnlessObtainable()` は skip しない |

🔴 **PR の CI では緑にならない（走らないから）。** 手順 8 の充足を主張する根拠は
**ローカルでの実ブローカ実行 ＋ 回収先が全量で回る配線**であり、CI 上の初回実行は develop への
マージ後（または `workflow_dispatch`）である。**「CI が緑だから満たした」とは書けない。**

### 🔴 ［2026-08-22 追随 / #441］skip の実現手段が develop で変わった

本器を書いた時点の skip 判定は `[BrokerFact]`（`FactAttribute` 派生のカスタム属性）だったが、
**`#997`（`IADR-0231`）が「`FactAttribute` 派生の skip 属性をやめる」として 81 箇所を移行し、
`BrokerFactAttribute` ごと削除した**（理由: **xUnit1051 は `FactAttribute` 派生のカスタム属性を認識しない**）。

**本 PR はその変更より前の develop へ rebase していたため、ローカルでは緑・CI では赤になった。**
CI（`pull_request`）は**head と base のマージ**をビルドするので、
**自分のブランチに無い削除が効く** —— **ローカルの緑は「自分の基点での緑」でしかない。**

`[Fact]` ＋ 各テスト冒頭の `BrokerRequired.SkipUnlessObtainable()` へ移した。

### 変異（実ブローカ上で実測）

| # | 変異 | 結果 | 診断の出力 |
| --- | --- | --- | --- |
| E-1（陽性対照） | 発行ホストから明示ルーティングと共通既定を外す | **2 件失敗** | `publisher-local-bait-rawdoc=1`（＝**ローカルへ閉じた**） |
| C | 手順 3 の前置を落とす（束縛済みキューと別のキューを待つ） | **2 件失敗** | `(この相関 ID を受け取った役は無い) / 全記録: (空)`（＝**届かなかった**） |

**2 つの失敗モードが、異なる診断で区別できる**ことまで確認した。

### 🔴 陽性対照が「死んだ表明」を 1 件見つけた

変異 E-1 で囮を実際に発火させたところ、**囮の件数が 0 のままだった**。
原因は**相関鍵の不一致** —— 囮は `ev.FetchId` で記録し、テストは `SourceId` で数えていた。
つまり `CountFor(囮, sourceId) == 0` という表明は、**囮が発火してもしなくても常に真**であり、
**何も検査していなかった**。鍵を `SourceId` へ揃えて修正し、再実行で `=1` を確認した。

**陽性対照を置かなければ、この器は「囮が受信しなかった」と永久に言い続けていた。**
W3 の器が陽性対照を要件にしていた理由が、そのまま E1 でも実証された形である。

### 副次的な是正: 診断が嘘をついていた

`EdgeRecorder.Snapshot()` が**役の一覧を直書き**しており、W3 の 3 役だけを並べていた。
E1 の器の役は「存在しない」かのように出て、本当の手掛かりは全記録の側にしかなかった。
**記録された役から取る**形へ直した（変異 E-1 の失敗出力で実際に踏んだ）。

### 本器が覆わないもの

**発行 ②（`ConversionJobEndpoints.cs` の再変換）の配線そのもの。**
あちらは API ホストの DI 解決を経るため、本器の発行ホストでは代替できない。
**`ConversionJobEndpointTests` が Wolverine バスの記録で固定する**（`RecordingMessageBus` を DI へ差し替え、
再変換が `RawDocumentFetched` をちょうど 1 通 Wolverine バスへ出すことを表明）。
変異 A の実測どおり**静的検査では捕まらない**ので、ここは試験でしか押さえられない。


## 計画書との差異

**差異: あり（1 件）。** planning `12_backend-application-stack` §リスク・未決事項 の
「サービス単位の段階移行」は成立しない（planning#438 で環流済み・未裁定）。
本書は辺単位・3 段で進める。

## 🔴 `enabled:false` は効いていなかった（CI の赤から見つかった本番欠陥）

**症状**: `PipelineStepRegistrationTests.無効化した段は登録されず購読されない` が
**手元（Windows・Debug も Release も）で緑・CI（Linux・Release）で赤**。

**原因（実測で特定した。当てずっぽうで直していない）**:

**Wolverine の規約探索（conventional discovery）は、明示登録とは独立にアセンブリを走査して
ハンドラを見つける。** 段の型は普通のハンドラの形をしているので、
**`AddPlatformWolverineStep` が `IncludeType` を呼ばないだけでは購読が生える。**

🔴 **そして走査対象の決まり方が環境に依存する** —— 同じコードが Windows では段の型を拾わず、
Linux の CI では拾った。**手元で再現しなかったのは環境の偶然である。**

**特定の手順**（推測ではなく実験で確かめた）:

1. Release でも手元は緑 → 構成の違いではない
2. 器が `DisableConventionalDiscovery()` を呼んでいないことに気付く（実ブローカ器は呼んでいる）
3. **`opts.Discovery.IncludeAssembly(...)` で規約探索を強制** → **手元で CI と同じ失敗文言を再現**
4. これで原因が確定した

**本番への影響**: `Program.cs` も `DisableConventionalDiscovery()` を呼んでいない。
つまり **`pipeline.json` の `enabled:false` は、段を止められていなかった可能性がある**。
**FR-14（構成のみで段を外せる）を無言で破っていた。**

**修正**: `AddPlatformWolverineStep` の規則 8（無効）で**明示的に除外する**。

```csharp
options.Discovery.CustomizeHandlerDiscovery(q => q.Excludes.WithCondition(
    $"パイプライン段 '{stepName}' は構成で無効化されている（pipeline.json enabled:false）",
    t => t == typeof(TStep)));
```

**「登録しない」ではなく「登録させない」でなければならない。**

**器の側も直した（弱めていない・むしろ厳しくした）**:
テストの器で `opts.Discovery.IncludeAssembly(...)` を**恒久的に**呼び、
**どの環境でも「規約探索が段の型を見つけ得る」側に固定する**。
無効化が効くことを、いちばん厳しい条件で測る。

**変異 D**（除外をやめ `IncludeType` を呼ばないだけに戻す）: **当該テストのみ失敗**（他 8 件は緑）。
**修正前は Windows で緑だった同じ変異が、修正後は Windows でも落ちる** ——
器が環境依存でなくなったことの実測である。

⚠️ **未実施**: 共通ヘルパ側（`Platform.Shared.Infrastructure.Tests` の
`WolverinePipelineExtensionsTests`）に、この除外を直接測るユニットテストは置いていない。
本 PR は ConversionService 側の結合的な試験で押さえている。

🔴 **これを含む追試を #1004 として起票した。** 同 issue は **「直っていない箇所がある」ではなく
「確かめていないことがある」を扱う** —— 実測した射程は次のとおりで、当初の見立てより狭い。

| 項目 | 実測 |
| --- | --- |
| 出荷中の `pipeline.json` の段 | **5 段すべて `enabled: true`** |
| **現時点で無効化に依存している段** | **0 段**（欠陥は潜在的で、稼働中の被害は無い） |
| 欠陥のある経路 | **Wolverine 版のみ**。MassTransit 版は `AddConsumer` を呼ばない形で自動走査が無く、同じ穴は無い |
| 修正の効き方 | **共通ヘルパ 1 箇所**なので、現在および将来のすべての Wolverine 段に効く |

## 🔴 引き継ぎ（次セッションへ・2026-08-22 時点）

### ［2026-08-22 更新］E1 は着地した（本節は当初「赤のまま止めた」と書いていた）

**PR #998 は CI 全緑（success 39 / skipped 3 / failure 0）でマージ済み**（`d9c21f6b`）。

当初ここには「PR #998（head `ace20eb6`）は赤である。直さずに止めた」と書き、
失敗の原因を**テスト側の表明の疑い**（「Wolverine が宛先なしのとき常に例外を投げるとは限らない」）
として引き継いでいた。**その見立ては外れていた。**

**実際の原因は本番の欠陥**であり、上の「`enabled:false` は効いていなかった」節が正本である ——
規約探索が段の型を独立に拾うため、`enabled:false` が段を止められていなかった。
**テストの表明は正しく、実装の側が間違っていた。**

🔴 **引き継ぎに「原因はたぶんこれ」と書くと、次の担当がそこから調べ始める。**
本件では「表明を疑え」と書いており、**実装を疑う方向を塞ぎかけていた。**
**未特定のときは「未特定である」と書くほうが安全である。**

**推測を引き継ぐなら、強度を添える。** 「これは推測であり、根拠は〈これだけ〉である」と書けば、
次の担当は**その根拠の薄さごと**受け取れる。断定形で書くと、根拠の強さの情報が落ちる。

### 🔴 同じ日に、同じ形で 2 度滑った（自己申告）

**「規約を知っている」は、その場で守れることを意味しない。** 本セッションで 2 度実証した。
**どちらも知識は持っていた。欠けていたのは「いま当てはまる」と気付くことである。**

| # | 何をした | 持っていた知識 | なぜ効かなかったか |
| --- | --- | --- | --- |
| 1 | **develop を merge した直後に再検証せず push した** | **この日に半日かけて「CI は head と base のマージをビルドする」を学んだ直後**だった | 「もう検証は済んでいる」という感覚が、**merge が入る前の検証**に対して残っていた。**merge は入力を変えるので、検証はやり直しである** |
| 2 | **統合テストの器の config 上書きが Wolverine に間に合わないことを踏んだ** | 🔴 **器のコメントがその罠を明記していた**（「『統合テストの config 上書きは効く』を一般化してはならない —— 読まれる時点で決まる」） | **読んでいなかったのではない。当てはまる場面だと気付かなかった。** 警告は「MassTransit の話」として読み流していた |

**1 は直後に気付いて回し直したので被害なし。2 はマージ後の `integration.yml` が検出した。**

🔴 **2 が示すのは、警告文を書くだけでは足りないということである。**
あの器のコメントは**正確で、具体的で、まさにこの罠を名指ししていた**。それでも踏んだ。
**「読めば分かる」形で置いた知識は、当てはまる場面を自分で判別できる人にしか効かない。**
機械検査に落とせるものは落とすこと —— それが本リポジトリが検査器を増やしてきた理由である。

### 🔴 本作業を通じて 1 行にすると

**「緑か赤か」より前に「*何について*の緑か」を問う。**

本作業で踏んだ・止めた事例は、すべてこの 1 つの問いの別の面だった。

| 事例 | 正しく答えていたもの | 問うべきだった対象 |
| --- | --- | --- |
| `git rev-parse HEAD` を見て push 成功と判断しかけた | **自分のローカルの HEAD** | **push 先のブランチの中身** |
| ローカルのテストが緑だから CI も緑だと考えた | **自分の基点でのツリー** | **head と base のマージ** |
| 古い SHA の `integration.yml` 緑を現 head の証拠に使いかけた | **`7d129c7e` のコード** | **現 head のコード**（差分を取って同一と確かめた） |
| 手元で `DataSourceTests` が落ちないので問題ないと考えかけた | **skip されたテストの不在** | **実際に走った結果**（手元では常に skip する） |
| `--update` の差分が変わらないので移行済みと読みかけた | **検査器に見える発行元** | **すべての発行元**（不可視のものが在る） |

🔴 **どれも「測定は正しく、問いが間違っていた」形である。**
測り方を疑う前に、**その結果がいま問うている対象について答えているか**を先に確かめること。

### 🔴 「ローカル緑・CI で CS0246」を見たら、真っ先に develop の新着を疑う

**`pull_request` の CI は head と base の *マージ* をビルドする。**
したがって**自分のブランチに無い削除・改名が効く**。**ローカルの緑は「自分の基点での緑」でしかない。**

**判別手順（この順で 1 分で切り分く）:**

```bash
git fetch origin
git log --oneline -10 origin/develop            # 自分の基点以降に何が入ったか
git merge-base --is-ancestor <自分のrebase基点> origin/develop   && echo "基点は最新" || echo "🔴 基点が古い（これが原因）"
git log --oneline --diff-filter=D origin/develop -- <見つからない型のファイル>
```

**実例（本 PR）**: `[BrokerFact]` が「見つからない」と CI だけが言った。
`#997`（`IADR-0231`）が `FactAttribute` 派生の skip 属性を廃止し `BrokerFactAttribute` を削除したのが、
**自分の rebase の後**だった。**同一アセンブリで片方のファイルだけ CS0246 は原理的にありえない**という
消去法が、最後に「前提（＝ CI と手元でツリーが同じ）の方が違う」へ導いた。

**移行先**: `[Fact]` ＋ 各テスト冒頭で `BrokerRequired.SkipUnlessObtainable()`。

⚠️ **移行すると xUnit1051 が露出する。** `Knowledge.IntegrationTests` は
`XUnit1051Migrated`（`src/Directory.Build.props`）に**既に載っており `WarningsAsErrors` が効く**ので、
`Task.Delay(...)` 等は `TestContext.Current.CancellationToken` を渡さないと**ビルドが落ちる**。
本 PR で 2 件出た —— **カスタム属性がアナライザを隠していた「81 箇所」の 82 件目**を新規に作りかけていた。

### 🔴 force push を使わずに PR ブランチへ develop を取り込む手順

CLAUDE.md は force push を禁じているが、**rebase は唯一解ではない**。
目的は「develop の最新を取り込んだ状態で CI を通す」ことであって「履歴を直線にする」ことではない。

```bash
git switch -c <work> <PR の現 head SHA>       # 履歴は温存
GIT_EDITOR=true git merge --no-edit origin/develop   # 追記なので非破壊
git read-tree -u --reset <完成済みツリーの SHA>       # 完成品をそのまま載せる
git commit -m "..."                            # 追記（amend しない）
```

**push 前の確認 3 点（すべて満たすこと）:**

1. `git diff <完成済みSHA> HEAD` が**空**（0 バイト。空でなければ `read-tree` が効いていない）
2. `git merge-base --is-ancestor <PR の現 head> HEAD` が **EXIT=0**（fast-forward の証拠）
3. **push 先は PR から取り直す** —— `gh pr view <N> --json headRefName`。
   `git push origin <work>:<その名前>`。**push 後に GitHub 側の head SHA を取り直して一致を確認する。**

> 🔴 **3 が要る理由（実際に踏んだ）**: `git branch -m` が「同名ブランチが既に存在」で失敗したのに、
> 続けて実行した `git push origin <その名前>` が**その既存のブランチ**を push した。
> **`git rev-parse HEAD` は自分のローカルの状態を正しく答えるだけで、push 先を教えない。**
> **前のコマンドが失敗して次のコマンドの前提が変わったのに、次のコマンドはそれを知らない。**

**副次効果**: この手順だとブランチが develop のマージを含むので、
**CI がビルドする「head と base のマージ」と手元のツリーが一致し、上の食い違いが構造的に塞がる。**

### E2（辺 `DocumentNormalized`）の着手前提

**射程**: ConversionService（発行）→ DocumentService（購読）。baseline は **11 → 9** の見込み。

🔴 **`#882`（xUnit1051 の段階採用 ratchet）との順番調整が要る。**

| プロジェクト | `remaining` | 状態 | E2 との関係 |
| --- | --- | --- | --- |
| `DocumentService.Api.Tests` | **94** | 未移行（`NoWarn` 継続） | **E2 が大幅に書き換える**。先に #882 が移行すると同じ行を 2 度触る |
| `ConversionService.Worker.Tests` | 138（**古い**） | 未移行 | **E1 が既に書き換えた**ので実数は動いている |
| `Knowledge.IntegrationTests` | — | **移行済み** | `WarningsAsErrors` が効く。新規テストは最初から `TestContext.Current.CancellationToken` |

- `remaining` は **informational で何もゲートしない**（`scripts/xunit1051-baseline.json` の `$comment` が正本）。
  **アナライザの報告ベースなので実数より小さく**、手更新なので古くなる。**測り直す手順も同 `$comment` にある。**
- **E1 が `ConversionService.Worker.Tests` を変更したので、138 は本 PR 時点で既に陳腐化している。**

**E2 で `DocumentService.Api.Tests` をどう扱うか（E1 の形をそのまま使える）:**
`AddMassTransitTestHarness` を `RecordingMessageBus`（`IMessageBus` のテストダブル）へ差し替える。
**同ダブルは既に 2 箇所に重複している**（DataSourceService / ConversionService）——
**E2 で 3 つ目が要るなら、そこで共通化を検討する**（各テストプロジェクトは自己完結しており共有ヘルパが無い）。

**E2 の pre-poisoning は E1 で解消済み** —— `DocumentNormalized` の発行は
`MassTransitDocumentNormalizedPublisher.cs` に隔離され、**1 ファイル 1 トランスポート**が保たれている。
`check-event-topology.js` の実測で `発行 [ConversionService(masstransit)]` 単独であることを確認済み。

### マージ後の検証（`integration.yml`・`d9c21f6b`）

🔴 **skip 件数を生で読んだ。** `Total tests: 65 / Passed: 62 / Failed: 2 / Skipped: 1`。

| 確認したいこと | 実測 |
| --- | --- |
| 実ブローカ試験は**走ったか** | ✅ **走って通った** —— `RawDocumentFetchedEdgeTests` の 2 件が `Passed`（5 秒 / 2 秒） |
| skip されたのは何か | **1 件のみ**。`WolverineBrokerEdgeTests.外部ブローカが設定されていればDockerが無くても実走する` —— 外部エンドポイント未設定時に skip する既存試験であり、**本 PR の分ではない** |

**これで手順 8 の充足が実測で確定した**（PR の CI は `Category!=Integration` で走らないため、
充足の根拠は最後までここに掛かっていた）。

### 🔴 ただし同じ実行が、本 PR が入れた回帰を 1 件検出した

```
Knowledge.IntegrationTests.DataSourceService.DataSourceTests（2 件）
Wolverine.Transports.BrokerInitializationException : Unable to initialize the Broker rabbitmq in time
```

**原因**: `DataSourceService.Api` が Wolverine ホストを起こすようになった（E1）。
Wolverine は**接続先をホスト構築時に読む**が、統合テストの器は接続先を
`ConfigureAppConfiguration` で差し替えており、**その上書きは読み取りに間に合わない**。
MassTransit は `UsingRabbitMq` のラムダ内で**遅延して**読むので間に合っていた。

🔴 **器のコメントが、まさにこの罠を明記していた** ——
「`RabbitMq:ConnectionString` が `ConfigureAppConfiguration` で効いていたのは、あちらが
遅延して読まれるからである。**『統合テストの config 上書きは効く』を一般化してはならない
—— 読まれる時点で決まる。**」**書いてある罠を踏んだ。**

**修正**: 器で `UseSetting("RabbitMq:ConnectionString", ...)` も行う。
`UseSetting` は**ホスト構成へ書くので `CreateBuilder` が構成を組む時点から見える** ——
`Pipeline:ConfigPath` が同じ理由で `UseSetting` を使っている（**これで 2 例目**）。
`ConfigureAppConfiguration` 側の上書きは残す（MassTransit 経路のサービスがまだ在るため）。
**両方の読み取り時点を満たす。**

⚠️ **この回帰は PR の CI では原理的に検出できない**（`ci.yml` は `Category!=Integration`）。
**辺の移行 PR は、マージ後に `integration.yml` を必ず確認すること。**

⚠️ **手元では再現できない**: `PostgresFixture` に外部エンドポイントの口が無く、
Testcontainers は containerd 環境で動かないため、`DataSourceTests` はローカルでは常に skip する。
**検証は `integration.yml` の `workflow_dispatch` をブランチに対して実行して行った。**

### やり残し

| # | 内容 | 状態 |
| --- | --- | --- |
| 1 | PR #998 の失敗テスト（`enabled:false`） | ✅ **直した**（本番欠陥だった。上記の節） |
| 1b | **マージ後に integration.yml が検出した回帰**（`DataSourceTests` 2 件） | ✅ **直した**（下記） |
| 2 | マージ後に `integration.yml` が実ブローカ試験を**本当に走らせたか**を skip 件数の生読みで確認 | ✅ **実施・走った**（下記） |
| 3 | `WolverineBrokerEdgeTests.cs:36` の表明メッセージが削除済みの `BrokerFact` を名指ししている | **#997 の担当へ回す。触らない** |
| 4 | 共有クラスタの滞留（`wolverine-dead-letter-queue` 3 通 / `interop-b-*-q_error` 1 通） | **利用者判断待ち。触らない**（由来は IADR-0245 に記録済み） |
| 5 | 段 a・b の運用実行（デプロイ時） | 本 PR の射程外。手順は本書「運用手順」節 |
| 6 | `ADR-0053` のデッドレター到達カウンタ | **未実装**。手順 10 の射程で別単位 |
| 7 | **`enabled:false` が段を止めることを外形から確かめる追試** | **#1004 で起票済み**（下記） |


## 未決事項

1. **手動 API 2 経路の機械的封鎖**を行うか。本書は運用合意で足りるとしたが、
   窓が長引く／人手が増える場合は**別単位で機構を入れる**。
2. **`T = 90 秒` の上乗せ部分**は本番の処理時間を実測できたら見直す。
3. **`sources[].enabled` の宣言モデル拡張**（問い 1）。必要になったら別単位。
