---
title: 作業仕様書 — Wolverine 移行 Phase 0: 部分移行を検出可能にする前提整備（#455 / #441）
type: spec
status: done
related_ids:
  - ADR-0027
  - ADR-0030
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - "ADR-0027（メッセージング基盤 = Wolverine。移行チェックリスト 8 手順）"
  - "ADR-0030（バックエンドアプリケーション層標準・MassTransit 不採用）"
related_adrs:
  - IADR-0217
  - IADR-0219
issue: "#455"
---

# 作業仕様書: Wolverine 移行 Phase 0 — 部分移行を検出可能にする前提整備

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: `ADR-0027`（メッセージング基盤）／ `ADR-0030`（不採用ライブラリ = MassTransit）
- 実装 issue: `#455`（ライブラリ標準の全面移行）／ `#441`（メッセージング基盤の再実装）

## なぜ「移行そのもの」より先にこれをやるのか

MassTransit → Wolverine は **`#455` に残る最後の ratchet 残件**（15 プロジェクト）である。
着手前に射程を実測したところ、**移行を安全に検証する土台が無い**ことが分かった。
先に土台を作らずに移行を始めると、**壊れたことに気付けないまま緑になる**。

### 🔴 発見 1: 私の記述が誤っていた —— 「ビルドも通ったまま」は現状成り立たない

`docs/tech/tech-requirements.md:328-329` に**私自身が**こう書いた（PR #881）。

> 🔴 部分移行は禁止——「MT 発行 → Wolverine 購読」の組が 1 つでもできると、
> **ビルドも**ユニットテストもトポロジ検査も通ったまま業務イベントが消える

**「ビルドも通る」は現在のコードでは成り立たない。** 根拠はソースにある。

```csharp
// Platform.Shared.Infrastructure/Foundation/Pipeline/PipelineExtensions.cs:75-77
public static void AddPlatformPipelineStep<TConsumer>(
    this IBusRegistrationConfigurator bus, PipelineOptions pipeline, ILogger? logger = null)
    where TConsumer : class, IConsumer, IPipelineStep

// Platform.Shared.Infrastructure/Foundation/Introspection/IntrospectionExtensions.cs:62-63
public IntrospectionBuilder AddStep<TConsumer>()
    where TConsumer : class, IConsumer, IPipelineStep
```

5 つのコンシューマはすべてこの経路で登録される。`IConsumer<T>` を捨てて Wolverine の
`Handle(T)` へ移すと**型制約を満たさなくなり、`Program.cs` の登録呼び出しがコンパイルエラーになる**。

🔴 **これは現存する唯一の実効的な安全弁である。** そして **`Platform.Shared.Infrastructure` を
Wolverine 対応にした瞬間に消える**。危険が本当に発現するのは**その後**であり、
警告文はその条件を書かねばならない。

### ✅ 発見 2: 「トポロジ検査も通る」は完全に成り立つ（しかも設計どおり）

`scripts/check-event-topology.js:112-120`:

```js
const massTransit = new RegExp(String.raw`IConsumer\s*<\s*${ev}\s*>`);
const wolverine   = new RegExp(String.raw`\b(?:Handle|Consume)\s*\(\s*(?:\[[^\]]*\]\s*)?${ev}\s+\w`);
if (massTransit.test(content) || wolverine.test(content)) hit.add(ev);   // ← 同じ集合へ入る
```

発行側（`:98-105`）も `Publish[A-Za-z]*` という語とイベント型名だけを見ており、
**レシーバの型（`IPublishEndpoint` か `IMessageBus` か）を見ていない**。
baseline のレコードは `{publishers:[owner], subscribers:[owner]}` だけで、
**トランスポートという次元が表に存在しない**（`:145-152`）。

**この検査器は原理的に部分移行を検出できない。** バグではなく射程外であり、
スクリプト自身が `:24` で「移行しても表が変わらないことが、移行が正しいことの証拠になる」
と**意図として**書いている。移行前後で表が不変であることは必要条件だが、十分条件ではない。

## スコープ（本 PR = Phase 0 のうち U1・U2 ＋ 記述の是正）

| # | 内容 |
| --- | --- |
| **D** | `docs/tech/tech-requirements.md` の「ビルドも通る」を実測に基づき是正し、**安全弁がいつ消えるか**を書く |
| **U1** | 未使用の `MassTransit` `PackageReference` 2 件を撤去（`Knowledge.Contracts` / `Platform.Shared.Contracts`）＋ baseline 2 行削除 |
| **U2** | `check-event-topology.js` を**トランスポート認識**にする。同一イベントで発行側と購読側のトランスポートが食い違ったら **fail** |

### スコープ外（Phase 0 の残り。別 PR）

- **U0**（統合テストを本番配線へ）—— 単独で ~250 行。`IntegrationTestFactory` の作り替えは
  独立したレビュー単位にする
- **U3**（手順 6 の静的検査新設）・**U4**（共通ヘルパ）—— いずれも別 PR
- 🔴 **U5（`IConsumer` 型制約の緩和）は本 PR に含めない。** U2 が入る前に安全弁を外してはならない

### 🔴 着手順の拘束

**U2 は U5 より先でなければならない。** 現存する唯一の安全弁（発見 1 の型制約）を外す作業が
U5 であり、トランスポート認識検査が無い状態で外すと、発見 2 の危険が現実の窓になる。
本 PR はその順序を守るための最初の 1 手である。

## 着手前の実測（母集合。誤りの側の語で引いた）

```
git grep -l 'PackageReference[^>]*MassTransit' -- '*.csproj' ':!src/ai-stock-trading' | wc -l   → 15
git grep -n -E 'ビルドもユニットテストも|部分移行' -- '*.md' '*.js' ':!src/ai-stock-trading'
```

| 項目 | 実測 |
| --- | --- |
| `MassTransit` を参照する `.csproj` | **15**（うち**未使用が 2**） |
| baseline の `MassTransit` エントリ | **15 プロジェクト**（`.csproj` と完全一致） |
| `using MassTransit` を持つ `.cs` | **36 ファイル / 41 行**（完全一致 `^using MassTransit;` は 30。差 6 は `MassTransit.Testing` のみ使用） |
| トークン `MassTransit` を含む `.cs` | **65**。うち **7 件が `using` 行を持たない実コード参照**（`global using` 2・完全修飾 5） |
| Wolverine の `src/` 利用 | **0 件**（版定義のみ。`PackageReference` 0） |
| 手順 3・4・5 の設定トークン | **リポジトリ全体で 0 件** |

**「実際に触るファイルは 43 件」**（36 + 7）である。`docs/tech/tech-requirements.md` の
「実測 36」は `^using MassTransit` の値としては正しいが、**移行の母集合としては 7 件足りない**。

### U1 の安全性を実測で確認した

2 プロジェクトは `PackageReference` を持つが、**配下の `.cs` に非コメントの `MassTransit` /
`MessageUrn` 参照が 0 件**である。`MessageUrn.ForType` を使うのは `Knowledge.Contracts.Tests`
側で、そちらは自前の `PackageReference` を持つ。

**推移参照で受け取っているプロジェクトが無いことも確認した** —— `MassTransit` を使う
`.cs` の所属プロジェクト **13 件すべてが自前の `PackageReference` を持つ**（走査で確認）。

## 受け入れ基準

1. `docs/tech/tech-requirements.md` の警告が、**型制約という安全弁の存在と、それが消える条件**を書いている
2. `Knowledge.Contracts` / `Platform.Shared.Contracts` が `MassTransit` を参照しない
3. baseline の `MassTransit` エントリが **15 → 13**
4. `node scripts/check-backend-libraries.js` が **EXIT=0**（ratchet の stale 判定に掛からない）
5. `check-event-topology.js` が**トランスポートを記録**し、baseline に欄が増えている
6. **同一イベントで発行側と購読側のトランスポートが食い違ったら EXIT=1**
7. `dotnet build|test` 両ユニットが **Failed 0**、件数が減っていない
8. `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が EXIT=0
9. `dotnet format --verify-no-changes` が両ユニットで EXIT=0

## 変異試験（EXIT はリダイレクトして読む）

| 変異 | 期待 |
| --- | --- |
| (a) 1 コンシューマを `IConsumer<X>` → `Handle(X e)` に書き換える（発行側は MT のまま） | 🔴 **U2 の新判定が fail**（従来は素通りしていた） |
| (b) 撤去した `PackageReference` を戻す | baseline の新規混入判定で fail |
| (c) baseline のエントリを消し忘れる | stale 判定で fail |

**復旧を確認し、復旧したことを報告に含める。**

## 実装後に確定した結果

| 項目 | 実測 |
| --- | --- |
| 撤去した `PackageReference` | **2**（`Knowledge.Contracts` / `Platform.Shared.Contracts`） |
| baseline の `MassTransit` エントリ | **15 → 13** |
| `check-event-topology.js` の自己試験 | **11 → 19 件** |
| baseline の形式 | `{owner: [transport]}` へ拡張（旧形式の owner 配列も読める） |

## 🔴 実装中に自分で fail-open を作り、実測で捕まえた

owner の配列を `{owner: [transport]}` へ変えたとき、`main()` に
`t.subscribers.length` が残った。オブジェクトに `.length` は無いので合計が **NaN** になり、

```js
if (totalSubs === 0) { /* 0 件走査で緑を返さない門 */ }   // NaN === 0 は false
```

**設計要点 3 の門が静かに開いた。** 同時に「購読 0 件のイベントを notice で必ず出す」も
黙って消え、走査結果の表示が `購読 NaN 件` になっていた。

**気付けたのは `--update` 後の出力を目視したからである**（`NaN` が表示に出ていた）。
検査器は EXIT=0 を返しており、**検査器自身は何も訴えなかった**。

是正として数え方を `countSubscribers()` へ 1 箇所に畳み、**新旧どちらの形でも数えられること・
合計が NaN にならないこと**を自己試験で固定した。形を変えたら数え方も追随する、という
規則 10 の機械版である。

## 変異試験（EXIT はリダイレクトして読む）

| 変異 | 期待 | 実測 |
| --- | --- | --- |
| 基準（無変異） | EXIT=0 | **EXIT=0** |
| **A' 購読側だけ Wolverine 記法へ移す** | **新判定が fail** | **EXIT=1** —— `「DocumentUpdated」の購読先 knowledge/WikiService は発行側とトランスポートを共有していない: 発行 [masstransit] / 購読 [wolverine]` |
| B 撤去した `PackageReference` を戻す | 新規混入で fail | **EXIT=1**（`[新規混入] Knowledge.Contracts.csproj`） |
| C baseline のエントリを消し忘れる | stale で fail | **EXIT=1**（`[baseline 減らし忘れ]`） |

🔴 **変異 A は 1 回目が「当たっていなかった」。** 置換対象に指定した
`Consume(ConsumeContext<DocumentUpdated> context)` は実ファイルでは引数名が `ctx` であり、
**メソッドの置換が無言で no-op になっていた**。その結果 `IConsumer` だけが消え、
検査は**新判定ではなく従来の「購読先が減った」で落ちた**。

一見「落ちたから成功」に見えるが、**証明したい命題（新判定が部分移行を捕まえる）は
まったく証明できていない**。置換後に `Handle(DocumentUpdated msg)` の存在と
`IConsumer<DocumentUpdated>` の不在を `assert` してから測り直した。
**変異が当たったことを確かめてから判定する。**

## 母集合（規則 9・10）

**是正後に「ビルドも通る」「残件 15」「実測 36」で引き直した。**

| 場所 | 従前 | 是正後 |
| --- | --- | --- |
| `docs/tech/tech-requirements.md`（移行残件の項） | 「ビルドもユニットテストもトポロジ検査も通ったまま業務イベントが消える」 | 警告を新節「Wolverine 移行の前提」へ移し、**防壁ごとの現状を表にした**。ビルドは**現在は止まる**ことと、**安全弁が消える条件**を明記 |
| `scripts/check-event-topology.js` ヘッダ | 設計要点 5 まで（トランスポートの次元が無い） | 要点 6 を追加。**なぜ従前は部分移行を検出できなかったか**と、**発行検出の既知の限界**を明記 |

**除外したもの（理由つき）:**

- **`.ai-context/specs/20260821_issue-455_xunit-v3-migration.md:97`** の「部分移行は禁止であり
  1 件も触らない」—— **凍結記録**であり、かつ**その PR の事実として正しい**（実際に 1 件も触っていない）。
- **`scripts/scripts.test.js:1075,1134`** の「部分移行」—— **別の話**（検査器の新旧名の受け口）。
  同じ語だが対象が違う。**語の一致だけで母集合に入れない。**
- **残件数「15」** —— 本 PR で 13 になったが、`docs/tech/tech-requirements.md` は
  **プロジェクト数ではなく `.csproj` / `.cs` の実測値**を持っており、そちらは変わっていない
  （撤去した 2 件は `using` を持たないため `.cs` の 36 に含まれない）。**`.csproj` 15 → 13 は是正した。**
- **「実測 36」** —— `^using MassTransit` の値としては**正しい**。ただし移行の母集合としては
  7 件足りない（`global using` 2・完全修飾 5）ことを新節に書いた。**数値は直さず、
  何を数えた値かを明示する**（数え方を書き残す方針と同じ）。
