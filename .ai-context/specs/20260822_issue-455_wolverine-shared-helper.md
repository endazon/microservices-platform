---
title: 作業仕様書 — Wolverine 移行 Phase 1 / U4: 共通ヘルパで ADR-0027 手順 3〜5 を封じ込める
type: spec
status: done
related_ids:
  - ADR-0027
  - ADR-0030
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0027（メッセージング基盤 = Wolverine。移行チェックリスト 8 手順）"
  - "planning:projects/microservices-platform/06_technical/12_backend-application-stack.md（§Wolverine 移行チェックリスト = 8 手順の原典）"
related_adrs:
  - IADR-0217
  - IADR-0233
issue: "#455"
---

# 作業仕様書: Wolverine 共通ヘルパの新設と手順 3〜5 の封じ込め（U4）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: `ADR-0027`（メッセージング基盤）。移行チェックリストの原典は planning
  `06_technical/12_backend-application-stack.md` §Wolverine 移行チェックリスト である
  （**二次資料から引かない** —— #889 でその誤りを実測済み）。

| # | 手順（原典） | 本 PR |
| --- | --- | --- |
| 3 | 共通ヘルパで**リスニングキュー名にサービス名を前置**する。**`PrefixIdentifiers` は使わない** | ✅ 実装 |
| 4 | 共通ヘルパで **`DisableConventionalLocalRouting()`** を適用する | ✅ 実装 |
| 5 | 共通ヘルパで **`ServiceLocationPolicy.AlwaysAllowed`** を設定する | ✅ 実装 |
| 6 | 3〜5 を共通ヘルパへ封じ込め、**個別サービスでの逸脱を静的検査で禁止**する | ✅ 検査を実装 |

- 実装 issue: `#455`（ライブラリ標準の全面移行）／ `#441`（メッセージング基盤の再実装）

## 🔴 着手順の拘束 —— 本 PR は安全弁を外さない

`#883`（U2）以降くり返し記録してきたとおり、**部分移行（MT 発行 → Wolverine 購読）に対する
現存する唯一のコンパイル時安全弁**は次の型制約である。

```csharp
// Platform.Shared.Infrastructure/Foundation/Pipeline/PipelineExtensions.cs
public static void AddPlatformPipelineStep<TConsumer>(...)
    where TConsumer : class, IConsumer, IPipelineStep      // ← 安全弁
// Foundation/Introspection/IntrospectionExtensions.cs
public IntrospectionBuilder AddStep<TConsumer>()
    where TConsumer : class, IConsumer, IPipelineStep      // ← 安全弁
```

**本 PR はこの 2 つの制約を 1 文字も変えない。** 新設する Wolverine ヘルパは
**既存の MassTransit 経路と併存する別 API** であり、既存の登録経路には触れない。
安全弁が消えるのは **U5（型制約の緩和）** であって U4 ではない。

🔴 **従前この拘束は申し送りの散文にしか無く、機械では守られていなかった。**
本 PR で**安全弁の存在を assert するテスト**を置き、U5 が「意図して落とす」形にする
（[IADR-0233](../adr/IADR-0233_wolverine-shared-helper-confinement.md) 決定 3）。

## 着手前の実測（母集合。誤りの側の語で全走査してから挙げた。規則 9）

走査対象は追跡下の全ファイル（`obj/` `bin/` および submodule `src/ai-stock-trading` を除く）。

### 軸 1: 「Wolverine はまだどこからも参照されていない」と述べる live な記述

```
git grep -n "どの .csproj からも参照されて\|未参照エントリ\|PackageReference は各サービス" \
  -- . ':!src/ai-stock-trading' ':!*/obj/*' ':!*/bin/*'
```

| 出た場所 | 扱い |
| --- | --- |
| `src/Directory.Packages.props:63` | 🔴 **本 PR で偽になる**（`WolverineFx` / `WolverineFx.RabbitMQ` が参照される）→ 是正する |
| `src/Directory.Packages.props:73` | 対象は `WolverineFx.RuntimeCompilation` と `CSharpFunctionalExtensions`。**本 PR では依然として未参照**（手順 2 はホストを起こす各サービスの射程）→ 変更不要。ただし主語が「WolverineFx 系」と読めないよう限定する |
| `src/Directory.Packages.props:84` | 同上（RuntimeCompilation の話）→ 変更不要 |
| `.ai-context/adr/IADR-0217_*.md:134-137`（決定 4） | 対象は**同 ADR が足した 2 件**（RuntimeCompilation / CSharpFunctionalExtensions）であり、本 PR で偽にならない → 変更不要 |
| `.ai-context/specs/20260816_issue-455_wolverine-codegen-mode.md:115,182` | 凍結記録。当時の実測として正しい → 遡及書き換えしない |

### 軸 2: 手順 4・5 を「まだ配線していない」と述べる記述

```
git grep -n "まだ配線していない\|Wolverine 配線を待たず\|共通ヘルパ内に 1 箇所\|Wolverine 対応にした瞬間"
```

| 出た場所 | 扱い |
| --- | --- |
| `.ai-context/specs/20260821_issue-455_forbidden-api-and-stale-counts.md:47,48,50` | 凍結記録（`status: done`）。**2026-08-21 時点の事実として正しい** → 遡及書き換えしない |
| `.ai-context/specs/20260821_issue-455_wolverine-phase0-preconditions.md:57` | 同上。かつ**本 PR でも依然として正しい**（安全弁が消えるのは U5） |
| `docs/tech/tech-requirements.md`（§Wolverine 移行の前提の 防壁 表・訂正ブロック） | 🔴 **live な権威文書**。手順 3〜5 が配線済みになる／手順 6 の検査が入る → **追随する** |
| `scripts/README.md`（規則一覧・self-test 件数） | 🔴 **live**。規則 5 の追加と件数の前進 → **追随する** |
| `scripts/check-backend-libraries.js:121-124`（`FORBIDDEN_APIS` の極性コメント） | 🔴 **live**。「共通ヘルパに在るべきもの」の在り処が確定する → **追随する** |

### 軸 3: Wolverine の現時点の使用実績

```
git grep -l 'Wolverine' -- '*.cs' ':!src/ai-stock-trading'   → 3 件（すべて templates/ のコメント）
git grep -n 'PackageReference[^>]*WolverineFx' -- '*.csproj' '*.props' ':!src/ai-stock-trading' → 0 件
```

**実コードでの Wolverine 利用は 0 件**である。本 PR が最初の 1 件を作る。

### 軸 4: 手順 3〜5 の API が既に呼ばれていないか（封じ込め検査の既知違反）

```
git grep -n '\bDisableConventionalLocalRouting\b\|\bServiceLocationPolicy\b\|\bListenToRabbitQueue\b' \
  -- '*.cs' ':!src/ai-stock-trading'   → 0 件
```

**0 件から始まる**ため、規則 5 は ratchet を持たず最初から fail で強制できる
（規則 3・規則 4 と同じ形）。

## 実測で確かめた Wolverine の API（記憶から書かない）

パッケージを実際に復元し、**リフレクションで実測した**（`WolverineFx` / `WolverineFx.RabbitMQ` 6.24.4）。

| 手順 | API | 実測結果 |
| --- | --- | --- |
| 3 | `RabbitMqTransportExtensions.ListenToRabbitQueue(WolverineOptions, String queueName, Action<RabbitMqQueue>)` | 存在する |
| 3（禁止） | `Wolverine.Transports.BrokerExpression<..>.PrefixIdentifiers(String prefix)` | 存在する（＝**書けてしまう**ので規則 4 が要る） |
| 4 | `Wolverine.IPolicies.DisableConventionalLocalRouting()` | 存在する |
| 5 | `WolverineOptions.ServiceLocationPolicy` : `JasperFx.CodeGeneration.Model.ServiceLocationPolicy` | **enum**。`AllowedButWarn=0` / `AlwaysAllowed=1` / `NotAllowed=2` |

🔴 **手順 5 の既定値は `NotAllowed` である**（実測）。計画 ADR が
「`internal` 実装型に依存するハンドラが**最初のメッセージ受信時に**落ちるのを防ぐ」と書いた
根拠がここで裏取りできた —— 既定のままなら落ちる。**設定が no-op でないことが既定値で示される。**

🔴 **手順 4 の効果は公開 API から観測できない。** `DisableConventionalLocalRouting()` が変えるのは
`WolverineOptions.LocalRoutingConventionDisabled`（**internal** プロパティ）だけである
（全インスタンスフィールドの前後差分を取って特定した。変化したのは 1 個だけ）。
したがってテストはリフレクションで観測する。**名前が消えれば `GetProperty` が `null` を返して
テストが落ちる**ので、版更新で静かに no-op 化することはない（[IADR-0233](../adr/IADR-0233_wolverine-shared-helper-confinement.md) 決定 4）。

## やること

1. **`Platform.Shared.Infrastructure.csproj`** へ `WolverineFx` / `WolverineFx.RabbitMQ` の
   `PackageReference` を足す（版は書かない。CPM）。
2. **`Foundation/Extensions/WolverineExtensions.cs`** を新設し、手順 3・4・5 を実装する。
   **ここが 3 手順の唯一の実装箇所**である。
3. **`Platform.Shared.Infrastructure.Tests`** を新設する（`.slnx` へ登録。xUnit v3 ＋ AwesomeAssertions）。
   - 手順 3・4・5 が実際に効いていることを、**適用前 → 適用後の差分**で試験する
   - 🔴 **安全弁（`IConsumer` 型制約）が現存することを試験する**
4. **`scripts/check-backend-libraries.js` へ規則 5（封じ込め API）** を足す。
   - (a) 許可ファイル以外での使用 → **fail**
   - (b) 🔴 **許可ファイル（実装の本拠）に全シンボルが在ること** → 無ければ **fail**
5. live な追随: `src/Directory.Packages.props` / `docs/tech/tech-requirements.md` /
   `scripts/README.md` / `check-backend-libraries.js` のコメント。
6. **IADR-0233** に設計判断を残す。

### スコープ外

- 🔴 **U5（`IConsumer` 型制約の緩和）** —— 本 PR は安全弁を外さない。
- 手順 2（`WolverineFx.RuntimeCompilation` の各サービス参照）・手順 7・手順 8（実ブローカ結合）。
- 既存 5 コンシューマの Wolverine への移し替え。**本 PR は器だけを作る。**

## 受け入れ基準

1. `WolverineExtensions` が手順 3・4・5 を実装し、**リポジトリ内で唯一の実装箇所**である
2. 手順 4・5 が**適用前は既定値・適用後は目的の値**であることをテストが示す（no-op でないことの証明）
3. 手順 3 がキュー名にサービス名を前置し、**同一イベントの 2 購読者で異なるキュー名になる**
4. **安全弁の型制約 2 箇所が現存すること**をテストが assert する
5. `check-backend-libraries.js` 規則 5 が (a) 許可外の使用 と (b) 本拠からの消失 の**両方**で fail する
6. `dotnet build|test` 両ユニットが **Failed 0**、**件数が減っていない**
7. `dotnet format --verify-no-changes` が両ユニットで EXIT=0
8. `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が EXIT=0
9. 既存の全検査器が EXIT=0

## 変異試験（EXIT はリダイレクトして読む。**変異が当たったことを先に assert する**）

| # | 変異 | 期待 |
| --- | --- | --- |
| A | 実在の `.cs`（許可外）へ `opts.Policies.DisableConventionalLocalRouting();` を 1 行足す | 規則 5(a) が **EXIT=1** し、ファイルを名指しする |
| B | `WolverineExtensions.cs` から手順 5 の行を削る | 規則 5(b) が **EXIT=1**（本拠からの消失） |
| C | `WolverineExtensions.cs` から手順 4 の呼び出しを削る | **テストが落ちる**（`LocalRoutingConventionDisabled` が false のまま） |
| D | 手順 5 の代入を `AllowedButWarn` に変える | **テストが落ちる** |
| E | 安全弁（`where TConsumer : class, IConsumer, IPipelineStep`）から `IConsumer` を外す | **安全弁テストが落ちる** |
| F | 規則 5 の自己試験の assert を必ず偽にする | `--self-test` が **EXIT=1**（#889 で踏んだ「評価ループより後ろに置く」fail-open の再発防止） |

**各変異は「当たったこと」を先に確認してから判定する**（#883 で置換が無言で no-op になり、
証明したい命題を何も証明していなかった実例がある）。**復旧を確認し、報告に含める。**

## 実装後に確定した結果

| 項目 | 実測 |
| --- | --- |
| 新設した共通ヘルパ | `Foundation/Extensions/WolverineExtensions.cs`（手順 3・4・5 の唯一の実装箇所） |
| 新設した試験プロジェクト | `Platform.Shared.Infrastructure.Tests`（**13 件**すべて Passed） |
| `check-backend-libraries` 自己試験 | **78 → 91 件**（規則 5 の 13 件を**評価ループより前**へ置いたことを変異 F で実測） |
| 不採用ライブラリ残件 | **13 件のまま**（増やしていない。下記「自分の作り込みを検査器に捕まえられた」参照） |
| 安全弁（型制約 2 箇所） | **無改変**（`git diff --stat` が空であることで証明） |
| テストプロジェクト数の ratchet | `scripts.repo.test.js` **15 → 16** |

## 🔴 実装中に、自分の作り込みを検査器 2 つに捕まえられた

**どちらも「緑を確かめる」ではなく「実際に走らせた出力を生で読む」ことでしか出なかった。**

### (1) 規則 4 が、自分が書いたコメントを禁止 API として検出した

共通ヘルパのコメントへ「`PrefixIdentifiers` は使わない」と**禁止の理由を書いた**ところ、
規則 4 が fail した。規則 4 は**コメント中の言及も拾う**設計であり（「コメントに書いてから外す」
経路を塞ぐため）、**設計どおりの挙動**である。名前を書かず「exchange 名まで前置するブローカ側の
一括前置 API」と記述し、名前と経緯は `docs/tech/tech-requirements.md` 側へ置いた。

🔴 **禁止 API の理由を書きたい場所と、書いてよい場所は違う。** これは規則 4 を入れた #889 の
時点では現れなかった —— **最初にその API に触れる作業（本作業）で初めて現れる形**だった。

### (2) 規則 1 の ratchet が、安全弁テストによる MassTransit 残件の**増加**を検出した

安全弁の型制約には MassTransit の `IConsumer` が含まれる。素直に書くと新規プロジェクトへ
`using MassTransit;` が要り、**残件が 13 → 14 へ増える**。ratchet はこれを新規混入として fail させた。

🔴 **完全修飾名（`MassTransit.IConsumer`）で書けば同検査は素通りする**（`bannedInSource` は
`using` 行しか見ない。実測で確認した）。**それは検査の回避であって遵守ではない。**
型を取らず制約の型名で照合する形に改め、残件を 13 のまま保った（IADR-0233 決定 6）。

## 変異試験の実測（6 件すべて当たり、復旧も確認済み）

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| A | 許可外の実ファイル（`WikiService.Api/Program.cs`）へ `DisableConventionalLocalRouting` を 1 行 | 規則 5(a) が fail | ✅ **EXIT=1** — `[封じ込め API] src/knowledge/.../WikiService.Api/Program.cs` |
| B | 本拠から `ServiceLocationPolicy` を消す | 規則 5(b) が fail | ✅ **EXIT=1** — `[封じ込め API の消失]` |
| C | 本拠から手順 4 の呼び出しを削る | テストが落ちる | ✅ **Failed 1 / Passed 12** — 落ちたのは `手順4_…` のみ |
| D | 手順 5 を `AllowedButWarn` に変える | テストが落ちる | ✅ **Failed 1 / Passed 12** — 落ちたのは `手順5_…` のみ |
| E | 安全弁から `IPipelineStep` を外す（U5 の模擬） | 安全弁テストが落ちる | ✅ **ビルド EXIT=0 を確認したうえで** Failed 1（`AddPlatformPipelineStep_は…`） |
| F | 規則 5 の自己試験 assert を必ず偽にする | `--self-test` が fail | ✅ **EXIT=1** — `FAIL 許可ファイルの外での使用を検出する` |

🔴 **変異 E は「ビルドが通ること」を先に確認してから判定した。** 型制約を素朴に外すと本体
（`AddConsumer<TConsumer>()` / `TConsumer.StepName`）がコンパイルできず、**テストではなく
ビルドが落ちる**。それでは「安全弁テストが退行を捕まえる」ことを何も証明できない。
本体も併せて U5 が行う形へ書き換え、**Build succeeded を確認してから**テストの失敗を見た。

**復旧確認**: 変異した 4 ファイルすべてを `cmp` でバイト一致確認した（本拠の 1 件のみ、
規則 4 の是正を挟んだため意図した差分あり）。`git status` に変異残骸なし。

## 検証（ローカル実測）

| 検査 | 結果 |
| --- | --- |
| `dotnet build` platform / knowledge | **両方 Build succeeded** |
| `dotnet test` platform | **Failed 0** — 13（新規）＋ 68 ＋ 26 ＋ 183 ＋ 232 |
| `dotnet test` knowledge | **Failed 0** — 全 11 プロジェクト。件数の減少なし |
| `dotnet format --verify-no-changes` 両ユニット | **EXIT=0** |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **EXIT=0 — 576 tests passed** |
| 検査器 13 本 ＋ `gen-knowledge-graph --check` | **すべて EXIT=0** |

⚠️ **`check-deploy-manifests` はこの環境では EXIT=1 になるが、本変更とは無関係である。**
`helm` / `kubectl` が PATH に無いためであり、**変更を stash した develop の状態でも同じく
EXIT=1 になることを実測して確かめた**（CI には両ツールがある）。
