---
title: 作業仕様書 — Wolverine 共通ヘルパへ retry/DLQ 既定を与え、MassTransit との等価性を挙動で固定する（W1）
type: spec
status: done
related_ids:
  - NFR
  - FR-12
  - SC-07
  - ADR-0027
  - ADR-0030
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0027（メッセージング基盤 = Wolverine）§決定「遅延実行・再試行・スケジュール配信は Wolverine の耐久メッセージ機能で賄い」"
  - "planning:projects/microservices-platform/06_technical/12_backend-application-stack.md（§Wolverine 移行チェックリスト = 8 手順の原典）"
related_adrs:
  - IADR-0233
  - IADR-0234
issue: "#441"
---

# 作業仕様書: Wolverine の retry/DLQ 既定と等価性の固定（W1）

## 起点

- 計画 ADR: `ADR-0027` §決定。**原典は planning の 12_backend-application-stack §Wolverine 移行チェックリスト**
  を GitHub API 経由で読んだ（**submodule の planning は pin が古く、同節を持っていない** ——
  二次資料から引く事故を避けるため。#889 で同型の誤りを実測済み）。
- 実装 issue: **`#441`**（メッセージング基盤の再実装）。境界の裁定は
  [IADR-0234](../adr/IADR-0234_wolverine-migration-boundary-455-441.md) 決定 1。
- 本 PR は移行チェーンの **W1**（同 IADR 決定 3 が定義）。

## 🔴 retry/DLQ は 8 手順に含まれない

原典の 8 手順を読み直した結果、**再試行・デッドレターは 1 つも手順に無い**（手順 1〜8 はトポロジと
ルーティングの正しさだけを扱う）。W1 の根拠は手順ではなく **ADR-0027 §決定** と FR-12 / SC-07 である。
「手順 N を満たした」と書かないこと。

## 着手前の実測（母集合を自分で引いた。規則 9）

誤りの側の語で走査した。生の出力は PR 本文に付す。

| 走査 | 結果 |
| --- | --- |
| `git grep -n "U5"`（`.ai-context/specs` を除く追跡下） | コード 5 行・`docs/tech/tech-requirements.md` 4 行・`IADR-0233` 8 行。`src/pnpm-lock.yaml` の一致は base64 ハッシュのため除外 |
| 「各サービスの再実装 issue が自プロジェクトの行を削除」 | **5 箇所**（`backend-library-baseline.json:5` / `check-backend-libraries.js:24`・`:1225`・`:1245` / マスタ仕様書 `:145`） |
| `AddPlatformPipelineStep` / `AddStep` の呼び出し | 8 件 / 5 件 |

**除外したものと理由**: `src/pnpm-lock.yaml`（`U5` は integrity ハッシュの一部で語ではない）。
`.ai-context/specs/` の 4 件（凍結記録。本文プロズを書き換えない。マスタ仕様書のみ日付つき追記で裁定を指す）。
`src/ai-stock-trading`（submodule。ADR-0030 の射程外）。

## 設計

### 1. 実装（`WolverineExtensions.UsePlatformMessagingDefaults`）

```csharp
options.Policies.OnAnyException()
    .RetryWithCooldown(RetryIntervals)   // 2s / 10s / 30s
    .Then.MoveToErrorQueue();
```

- `RetryIntervals` と `MaxAttempts` は **Wolverine 側に独立に置く**。MassTransit 側を参照しない ——
  共有すると「両方を同時に変える変異」が素通りし、**変異が当たらない試験**になる。
  数字を 2 か所に置いて試験で束ねる作法は IADR-0137 決定 3 の先例に倣う。
- `.Then.MoveToErrorQueue()` は**挙動としては冗長**（既定でも試行を使い切ればデッドレターへ行く）。
  **意図の明示と、変異試験のための面**として残す。後述の変異 C′ がこの判断の根拠である。

### 2. 等価性試験（`WolverineExtensionsTests` へ追加。新規ファイルを作らない）

観測点は Wolverine が実行時に実際に引く **`FailureRuleCollection.DetermineExecutionContinuation`**。
「規則が登録されたこと」ではなく「**試行 n 回目に何をするか**」を読む。

- **新規テストファイルを作らない理由が 2 つある。** (1) `check-backend-libraries.js` 規則 5(a) の
  許可リストは**ファイル単位**であり、新ファイルは封じ込め対象シンボルに触れられない。
  (2) 新テストプロジェクトはカバレッジ床を割る（#897 → #899 の実記録）が、**既存プロジェクトへの
  `[Fact]` 追加は Cobertura レポートを増やさない**ため床機構に触れない。
- **`using MassTransit;` を書かない。** 書くと残件 ratchet が 13 → 14 で fail する。
  完全修飾名での回避は IADR-0233 決定 6 が却下済み。`MassTransitExtensions.MaxAttempts` は `int`、
  `RetryIntervals` は `TimeSpan[]` で、**MassTransit の型は 1 つも現れない**（`PartialMigrationSafetyValveTests`
  が先例）。

### 3. 境界確定 IADR の同梱

CLAUDE.md「実装判断の記録は実装変更と同一 PR に置く」に従い、[IADR-0234](../adr/IADR-0234_wolverine-migration-boundary-455-441.md) を同梱する。
単独 PR にしない。

## 受け入れ基準

- [x] 適用前の既定（再試行 0 回・初回失敗で即デッドレター）を先に assert している
- [x] 再試行回数・間隔・デッドレター送りの条件・例外の範囲を固定している
- [x] MassTransit 側の値と一致することを、両側を独立に置いたうえで試験が束ねている
- [x] 変異 3 種（既定を外す／回数・間隔を変える／DLQ 送りを無効化）で**落ちること**を実測した
- [x] 各変異で **`Build succeeded`（EXIT=0）を先に確認**してから、テスト結果を読んだ
- [x] 復旧を `cmp` でバイト一致確認し、`git status` に残骸が無い
- [x] `backend-library-baseline.json` の誤った記述と、**それを再生成する検査器側 3 箇所**を訂正した
- [x] `--write-baseline` を通しても訂正が戻らないことを `cmp` で実測した
- [x] IADR を追加し `.ai-context/adr/README.md` の索引へ登録した

## 変異試験（実測）

すべて `Platform.Shared.Infrastructure.Tests` に対して実施。**ビルド成功を先に確認**した。
**基準は 24 件全通過**（変更前は 15 件）。

| # | 変異 | ビルド | 落ちたテスト |
| --- | --- | --- | --- |
| A | 再試行既定の 3 行を丸ごと削除 | ✅ `Build succeeded` EXIT=0 | **6 件** — `再試行既定_試行上限までは間隔つきで再試行する` / `再試行既定_試行上限に達して初めてデッドレターへ移る` / `再試行既定_例外の種類によらず同じ判定になる`（×3 InlineData）/ `等価性_再試行の間隔がMassTransit側と一致する` |
| B | 間隔 `10s` → `11s`（Wolverine 側のみ） | ✅ `Build succeeded` EXIT=0 | **1 件** — `等価性_再試行の間隔がMassTransit側と一致する`（`Expected 10s because 試行 2 回目の待ち時間, but found 11s.`） |
| C | `.Then.MoveToErrorQueue()` → `.Then.RequeueIndefinitely()` | ✅ `Build succeeded` EXIT=0 | **4 件** — `再試行既定_試行上限に達して初めてデッドレターへ移る` / `再試行既定_例外の種類によらず同じ判定になる`（×3） |
| **C′** | `.Then.MoveToErrorQueue()` を**削除**（棄却した素朴な変異） | ✅ `Build succeeded` EXIT=0 | 🔴 **0 件（24/24 緑）** |

**変異 A で `適用前は初回の失敗で即デッドレターへ送られる` と `等価性_試行上限がMassTransit側と一致する` が
落ちないのは設計どおりである** —— 前者は適用前の既定を測る試験、後者は定数の一致であり、
どちらも再試行既定の有無に依存しない。**全部落ちたら器が壊れただけ**であって検出力の証明にならない。

**変異 C′ は「デッドレター送りを無効化する」変異として採らない。** Wolverine は試行を使い切れば
**既定でもデッドレターへ送る**ため、この削除は挙動を変えず、**テストの検出力を何も示さない**。
`RequeueIndefinitely()` が意味論的な kill である（変異 C）。

**復旧**: 各変異のあと `cmp` でバイト一致を確認し、`git status` は意図した変更ファイルのみを示した。

## 🔴 検証の限界（正直に書く）

- **デッドレターの宛先トポロジは等価ではないし、単体テストでは固定できない。** MassTransit は
  エンドポイントごとの `<queue>_error` へ送るが、Wolverine の RabbitMQ 既定は**単一の共有
  `wolverine-dead-letter-queue`** である（実測）。`RabbitMqListenerConfiguration.DeadLetterQueueing` で
  キューごとに指定できるはずだが、**構成時点で読み出しても名前が変わらず**（実測）、
  単体テストからは観測できなかった。**W3（実ブローカ結合テスト）の射程とし、本 PR は等価と主張しない。**
- 試験は `Envelope { Attempts = n }` を合成して判定関数を駆動する。**ランタイムが実際にその番号を
  1 始まりで与えるという点は Wolverine 側の契約であり、本試験が再証明したものではない。**
- `dotnet build` は**プロジェクト単位**で行った。worktree に submodule を populate していないため
  `backend.slnx` 全体はビルドしていない（`Platform.Bff` が submodule を参照する）。全体ビルドは CI が行う。

## 計画書との差異

**差異: あり（1 件）。** planning の 12_backend-application-stack §リスク・未決事項は移行の段取りを
「**サービス単位の段階移行**」と書いているが、**baseline の行が減る単位はイベント辺**であり、辺は
サービスをまたぐ（[IADR-0234](../adr/IADR-0234_wolverine-migration-boundary-455-441.md) 決定 3）。
**計画側の記述と実装の実測が食い違うため、計画への環流（issue 起票）を要する。**
既存の同件 issue は検索したが見当たらなかった（`Wolverine 移行 サービス単位` / `サービス単位の段階移行`）。
**起票は本 PR の射程外とし、未決事項へ残す。**

## 未決事項

1. 上記の計画環流（`/plan-feedback`）。**未起票。**
2. デッドレター宛先の名前づけ（`<queue>_error` 相当へ寄せるか、Wolverine の共有 DLQ を運用の正とするか）。
   W3 で実ブローカに対して決める。
