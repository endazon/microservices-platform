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

### 🔴 この列挙が網羅であることを何が保証するか

**列挙そのものではなく、「別の手段で確立された既知の値を、教えられずに復元したこと」が保証である。**

Phase 0 の実測は「publish 検出が**実在 13 箇所のうち 7 箇所**を取りこぼす」と記録している。
上の手順は**その数字を独立に再現した**（バス発行 13 / 可視 6 / 不可視 7）。
**手順が既知の正解を復元できたことが、手順の妥当性の根拠である。**

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

## 受け入れ基準（段 c の本体 PR）

- [ ] 発行 ①②・購読の 3 箇所すべてが Wolverine へ移っている（1 箇所でも残っていたら不可）
- [ ] `RawDocumentFetchedConsumer` が `IPipelineStep<RawDocumentFetched>` を実装し、
      `Handle(RawDocumentFetched, CancellationToken)` を持つ（[IADR-0239](../adr/IADR-0239_wolverine-pipeline-step-registration.md)）
- [ ] `AddPlatformWolverineStep` 経由で登録し、戻り値の `Queue` を `ListenToPlatformQueue` へ渡している
- [ ] `check-event-topology.js --update` の差分に、**両側のトランスポートが反転して現れる**
      （生の出力をサイズ確認つきで読む。行が変わらない＝整合ではなく「見えていない」可能性を先に潰す）
- [ ] 🔴 **発行 ②（不可視）の移行は、`--update` の差分では証明できない。** コード diff と
      実ブローカ試験で示す
- [ ] W3 の器を使った実ブローカ試験があり、**囮（publisher-local bait）を含む**
- [ ] baseline が **13 → 11**（DataSourceService の 2 行）
- [ ] `DataSourceService` の MassTransit 参照が **8 ファイルすべて**から消えている
- [ ] `Platform.Shared.Infrastructure` の readiness（W4）が DataSourceService へ配線されている
- [ ] `PartialMigrationSafetyValveTests` が緑（安全弁に触っていない）
- [ ] 🔴 **drain 中の再充填で手順が中断すること**が、変異で実証されている（下記 変異 E・F）

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

## 計画書との差異

**差異: あり（1 件）。** planning `12_backend-application-stack` §リスク・未決事項 の
「サービス単位の段階移行」は成立しない（planning#438 で環流済み・未裁定）。
本書は辺単位・3 段で進める。

## 未決事項

1. **手動 API 2 経路の機械的封鎖**を行うか。本書は運用合意で足りるとしたが、
   窓が長引く／人手が増える場合は**別単位で機構を入れる**。
2. **`T = 90 秒` の上乗せ部分**は本番の処理時間を実測できたら見直す。
3. **`sources[].enabled` の宣言モデル拡張**（問い 1）。必要になったら別単位。
