---
title: 作業仕様書 — fan-out 統合テスト 2 件の非決定的失敗を、同時実行の排除と失敗の決定化で塞ぐ
type: spec
status: done
related_ids:
  - FR-02
  - FR-06
  - FR-13
  - FR-14
  - UC-04
  - ADR-0018
  - ADR-0027
  - IADR-0232
  - IADR-0245
  - IADR-0302
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - "ADR-0027 移行チェックリスト手順 3（リスニングキュー名にサービス名を前置する）"
---

# 作業仕様書: fan-out 統合テストの非決定的失敗（#1038 / #1059）

## 対象と結論の先出し

- 対象: `Knowledge.IntegrationTests.Messaging.DocumentUpdatedFanOutTests.PublishOnce_BothSubscribersReceive`
  と `QueueOverrideFanOutTests.SharedQueueDeclaration_KeepsFanOut_ServicePrefixSeparatesQueues`
- **#1059（CI の Integration が失敗）は #1038 と同一原因である。** #1059 が指す run 33249756332 の
  失敗 2 件は #1038 の 2 件と完全に一致し、**同一コミット `0784dd2` が失敗（33249756332）と
  成功（33273064354）の両方を出している**。したがって #1059 を別件として扱わない。
- 対処: **待ち時間は 1 秒も伸ばさない。** ①2 つの fan-out クラスを同一 xUnit コレクションへ入れて
  **同時実行を止める**、②予算切れ時に**ホストが実際に購読しているキュー名一覧**を失敗メッセージへ載せる。

## 🔴 母集合の引き方（規則 9・10）と除外

**記憶で挙げない。文字列で走査してから挙げる。** 実行した走査と、その結果からの除外理由を残す。

### 走査 1 —— 「メッセージ到達を固定時間で待つ」形（#1038 の指摘する形）

`git ls-files` で追跡下のテスト `.cs` を出し（`src/ai-stock-trading` は submodule のため除外）、
`Knowledge.IntegrationTests` 配下は `FromSeconds|FromMilliseconds|Task.Delay` で全走査した。**当たり 25 行**。

| ファイル:行 | 形 | 判定 |
| --- | --- | --- |
| `Messaging/DocumentUpdatedFanOutTests.cs:201` | `ProcessingBudget = 30s` | **本作業の対象** |
| `Messaging/QueueOverrideFanOutTests.cs:206` | `ProcessingBudget = 30s` | **本作業の対象** |
| `Messaging/RawDocumentFetchedEdgeTests.cs:23` | `ReceiveTimeout = 60s` | **同型・別予算**。未発火のため本作業では触らない（後述） |
| `Messaging/WolverineBrokerEdgeTests.cs:23` | `ReceiveTimeout = 60s` | 同上 |
| `DocumentService/DocumentNormalizedSyncTests.cs:98-110` | 500ms × 60 回 = **実質 30s** | **同型**。ただし単一ホストの自発行・自消費（クロスホスト配送ではない）。未発火。触らない |
| `Messaging/WolverineBrokerReadinessTests.cs:29` | `DetectTimeout = 60s` | 除外。**readiness の状態遷移**を待つもので、メッセージ到達ではない |
| `WikiService/WikiSyncTests.cs:72` | `Task.Delay(2s)` | 除外。到達を assert していない（HTTP 200 のスモーク） |
| `GraphService/EdgeTypeDbGuardTests.cs:266` | `Task.Delay(2s)` | 除外。DB ロックの検査でメッセージングではない |
| `Fixtures/*.cs`（`StartTimeout` / `StopTimeout` / `StopAsync`） | 起動・停止の予算 | 除外。到達待ちではない |
| `Messaging/ListenerReadiness.cs:36` | `StartupBudget = 120s` | 除外。IADR-0302 が①②のために置いた枠であり、③とは別物 |

**`RawDocumentFetchedEdgeTests` / `WolverineBrokerEdgeTests` を本 PR で直さない理由**:
形は同じ（固定壁時計でクロスホスト到達を待つ）だが、**予算が倍（60 秒）で、実測で一度も発火していない**
（本作業で読んだ 5 run すべてで 2 秒前後の Passed）。#1038 が禁じているのは「測らずに手を入れる」ことであり、
**発火していないものを予防的に触るのは同じ誤りの裏返し**である。PR で申し送りにする。

### 走査 2 —— 本変更で新たに誤りになる自分の記述（規則 10）

`git grep -ln "1038"` を追跡下全ファイル（submodule 除く）へ掛けた。**当たり 9 ファイル**。

| ファイル | 扱い |
| --- | --- |
| `Messaging/DocumentUpdatedFanOutTests.cs` / `QueueOverrideFanOutTests.cs` | **本作業で書き換える**（コレクション属性と診断） |
| `Messaging/ListenerReadiness.cs` | **追記する**（診断の置き場） |
| `Fixtures/IntegrationTestFactory.cs` | 変更不要（IADR-0302 の訂正済み記述。本変更で誤りにならない） |
| `.ai-context/adr/IADR-0302_*.md` / `.ai-context/specs/20260829_issue-1038_*.md` | **凍結記録。本文は書き換えない。** 新しい決定は IADR-0304 として起こす |
| `.ai-context/adr/README.md` | **1 行追記**（IADR-0304） |
| `docs/how-to/session-handoff.md` | 引き継ぎメモ。本変更で既存記述が誤りになる箇所は無い（「再現しないことは直った証拠にならない」は今も真） |
| `DocumentService/Tests/NormalizedAssetLedgerTests.cs` / `Platform.Bff.Tests/SessionTokenRefresherTests.cs` / `WolverineExtensionsTests.cs` | 「#1038 と同じ型」への言及のみ。予算値へ依存していないため変更不要 |

## 🔴 実測 —— 本作業で新しく出た唯一の事実

過去の 5 ラウンドで潰れた仮説（購読開始の遅さ／キュー宣言の不一致／束縛の欠落／
ルーティングキー／クラス間の競合コンシューマ）はいずれも「なぜ落ちるか」を説明できていない。
**そこで今回は仮説を足さず、Integration の run ログを 5 本採って 2 件の実行窓を復元した。**

`gh run view <id> --log` から、xUnit の相対時刻（`[xUnit.net hh:mm:ss.ff]`）とテスト所要時間で窓を出した。

| run | commit | 結果 | `DocumentUpdatedFanOut` の窓 | `QueueOverrideFanOut` の窓 | 2 件の重なり |
| --- | --- | --- | --- | --- | --- |
| 33249756332 | `0784dd2` | **FAIL 2 件**（両方 wiki 側） | 所要 35s / 失敗時刻 `01:01.96` | 所要 33s / 失敗時刻 `01:01.95` | 🔴 **ほぼ全域が重複**（同一瞬間に両方が予算切れ） |
| 33233171135 | `b85f462` | **FAIL 2 件**（両方 ingestion 側） | 所要 30s / 失敗時刻 `01:04.66` | 所要 30s / 失敗時刻 `01:28.40` | 🔴 **重複あり**（約 6 秒。加えて一方の待ちの最中に他方が**コンテナ 2 つ ＋ ホスト 3 つを起動**していた） |
| 33273064354 | `0784dd2` | PASS | **2s** | **1s** | 重複なし（約 20 秒離れて実行） |
| 33226232176 | `5e3b1e0` | PASS（別件で赤） | **3s** | **646ms** | 重複なし（約 33 秒） |
| 33195657772 | `66f1778` | PASS（別件で赤） | **341ms** | **2s** | 重複なし（約 51 秒） |

**読み取れること 2 つ**:

1. 🔴 **分布が二峰である。** 緑のときは **341ms〜3 秒**、赤のときは**予算 30 秒を使い切る**。
   その中間（10 秒・20 秒）が 1 件も無い。**「負荷でじわじわ遅くなる」形ではない。**
   → **予算を 60 秒へ伸ばしても救われない。** #1038 が禁じた直し方は、実測でも無効である。
2. **観測 5 本すべてで、2 クラスの実行窓が重なった run だけが落ちている。**

### 🔴 この相関には交絡がある。断定しない

**落ちるテストは 30 秒居座るので、落ちれば重なりやすい**（逆向きの因果）。
本作業はこの交絡を解消できていない —— **「重なりが原因である」とは書かない。**

書けるのは次までである。

- 重なりを**取り除くことはできる**（コレクション属性）。副作用は実行時間だけで、**検査の強さは 1 ミリも落ちない**
- 各クラスは **Testcontainers 2 つ（Postgres / RabbitMQ）と Wolverine ホスト 3 つ**を起こす
  （`IClassFixture` なので**コンテナはクラス単位に別々**である。クラス間でブローカを共有していない
  ＝ **クラス跨ぎの競合コンシューマは原理的に起こり得ない**。これは本作業で新たに除外した仮説である）。
  2 クラスが重なる窓は、**2 コアのランナー上で最も重い局面**である
- したがって重なりの除去は「機序の候補を 1 つ確実に消す」変更であり、**症状を隠す変更ではない**
  —— fan-out が本当に壊れていれば、直列化しても同じように落ちる

## 対処（3 点。予算 30 秒は据え置く）

### 1. 2 つの fan-out クラスを同一コレクションへ入れて直列化する

`[CollectionDefinition(..., DisableParallelization = true)]` を置き、両クラスへ `[Collection(...)]` を付ける。
xUnit はコレクション単位で並列化するため、**この 2 クラスは互いに同時実行されなくなる**。
`DisableParallelization` を立てるのは、混雑の候補を消すのが目的だからである
（**当該コレクションの実行中は他のコレクションとも重ねない**。効き方が仮に「2 クラス間の直列化」
止まりだったとしても、本作業の最低要件はそれで満たされる）。

`IClassFixture` はコレクションに関係なくクラス単位のままなので、**コンテナの分離は変わらない**
（共有させればクラス跨ぎの競合コンシューマを自分で作り込むことになる）。

### 2. 予算切れの失敗を決定的にする

#1038 の最新コメントが「次の一手」として挙げているのは**キュー名そのものを採ること**である。
`ListenerReadiness` へ `DescribeListeners(IServiceProvider)` を足し、
**ホストが実際に購読しているキュー（`Endpoint.Uri` と `Tracker` の状態）を列挙**して失敗メッセージへ載せる。

- 名前が 2 本に分かれている → 前置は効いている。原因は配送／処理側
- 名前が 1 本に潰れている → 競合コンシューマ化（本命仮説）

**1 回の実走で分かれる。** 現在の失敗メッセージは「実処理側の遅さか受信そのものの欠落である」までしか言えず、
**次に落ちたときも同じ場所から仮説を建て直すことになる** —— それを止める。

### 3. 予算は 30 秒のまま

`ProcessingBudget` を触らない。緑の実測が 341ms〜3 秒である以上、**30 秒は 10 倍以上の余裕**であり、
足りないのは時間ではない。

## 新しい最悪ケースの実時間

- **1 件あたりの待ちは変わらない**（30 秒 × 2 assert ＝ 最悪 60 秒。据え置き）
- **アセンブリ全体**: 直列化されるのは 2 クラスだけである。緑のときの実測は
  `Knowledge.IntegrationTests` 全体で **1.5 分 / 77 件**、当該 2 クラスは合計 3 秒未満。
  直列化で増えるのは**当該 2 クラスのセットアップ〜破棄（各々コンテナ 2 つ ＋ ホスト 3 つ、
  実測 20 秒前後）が他と重ならなくなる分**である。`DisableParallelization` が
  「他コレクションとも重ねない」まで効く場合を上限として、**+40 秒程度**と見積もる。
  Integration ジョブ全体は 7〜9 分・`timeout-minutes: 30` なので余裕がある

## 🔴 本作業環境で確かめられないこと

**Docker daemon が無い**（Rancher Desktop の containerd バックエンド。Testcontainers は Docker Engine API を要求する）。
`Knowledge.IntegrationTests` の該当 2 件は **`DockerRequired.SkipUnlessAvailable()` で Skipped になる**。

- 実測（本作業環境）: `dotnet test src/knowledge/backend/backend.slnx` は
  **失敗 0 / 合格 1191 / スキップ 43**。うち `Knowledge.IntegrationTests.dll` が
  **合格 36 / スキップ 41 / 合計 77**（CI と同じ総数）であり、**当該 2 件はこの 41 に含まれる**
- **「ローカルで緑だったから直った」とは書かない。** ローカルの緑は**当該テストが 1 行も走っていない**ことの緑である
- 実走の場は `integration.yml`（develop への push と日次）である。**判定はマージ後の run で行う**
- 受け入れ基準③（複数回のフル実行で安定して緑）は**本 PR では満たせない**

## 受け入れ基準

- [x] 実行窓を run ログから復元し、**分布が二峰である**ことを記録する（＝「時間を伸ばす」が無効であることの実測）
- [x] クラス跨ぎの競合コンシューマ仮説を**コードで除外**する（`IClassFixture` によりブローカはクラス単位）
- [x] 2 クラスの同時実行を止める
- [x] 予算切れ時に購読キュー名を失敗メッセージへ載せる
- [x] 予算 30 秒を伸ばさない
- [ ] `integration.yml` が緑になる（**マージ後の実走待ち。本 PR では未確認**）
- [ ] 複数回のフル実行で安定して緑（**未実施。Docker が要る**）
