---
title: 作業仕様書 — U4 の封じ込めを独立に検証し、実測で過大と判明した live な記述を訂正する
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
related_adrs:
  - IADR-0233
issue: "#455"
---

# 作業仕様書: U4（#897）の封じ込めの独立検証と、live な記述の訂正

## 起点

- 実装 issue: https://github.com/endazon/microservices-platform/issues/455
- 検証対象の PR: https://github.com/endazon/microservices-platform/pull/897（commit `071e356`）
- 対象の実装 ADR: [IADR-0233](../adr/IADR-0233_wolverine-shared-helper-confinement.md)

**本作業は U4 の再実装ではない。** #897 が既にマージ済みであることを前提に、その封じ込めが
主張どおり効いているかを**変異試験で独立に検算**し、実測と食い違う live な記述だけを訂正する。

🔴 **U5（型制約の緩和）には着手しない。** 本作業はコードの挙動を 1 行も変えない
（変更はドキュメントとコードコメントに限る）。

## やること

1. 手順 3・4・5 それぞれについて「封じ込めが破れる形」の変異を作り、検査器／テストが実際に
   落ちることを実測する。変異は**当たったことを先に assert**し、C# の変異は**ビルド EXIT=0**を
   確認してから判定する（ビルドが落ちたなら、落ちたのはテストではない）
2. 規則 5(a) の走査範囲・許可リスト・照合定義の抜け道を洗い出し、実測で確定させる
3. 安全弁（`PartialMigrationSafetyValveTests`）が何を守っているのかを実測で切り分ける
4. 実測と食い違う **live な記述のみ**を訂正する。凍結記録（当時の実測ログ）は書き換えない

### スコープ外

- U5（型制約の緩和）、コードの挙動変更、検査器の穴の**是正**（本作業は限界の記録まで）
- #885 / #882 / #900 / #901（別セッション担当）

## 受け入れ基準

1. 手順 3・4・5 それぞれについて、複数形の変異が「検査器またはテスト」で捕捉されることを実測した
2. 捕捉されなかった変異があれば、それが live な記述の過大主張に当たるかを判定し、当たるものは訂正した
3. 訂正後も `dotnet build|test`・検査器一式が EXIT=0
4. 履歴（過去のコミットメッセージ）は書き換えていない

## 変異試験の実測

### H 系（ヘルパ本体の劣化。全件 ビルド EXIT=0 を確認済み）

| ID | 変異 | 当たり確認 | 検査器 | テスト | 捕捉 |
| --- | --- | --- | --- | --- | --- |
| A1 | 手順 4 の実装 1 行だけ削除（コメント残置） | シンボル 0 件 | **EXIT=1** 消失検出 | Failed 1 | ✅ |
| A2 | 手順 5 の代入 1 行だけ削除（コメント残置） | コメント 1 件のみ残存 | **EXIT=1** 消失検出 | Failed 1 | ✅ |
| A3 | 手順 5 の値のみ反転（`NotAllowed`） | 代入行を確認 | EXIT=0（**素通り**） | Failed 1 | ✅ テストのみ |
| A4 | 手順 5 の代入を `==` 比較へ | `==` 行を確認 | **EXIT=1**（`(?!=)` が効く） | Failed 1 | ✅ |
| A5 | 手順 3 の適用点から前置を落とす | `ListenToRabbitQueue(queueName)` | EXIT=0（**素通り**） | Failed 2 | ✅ テストのみ |
| A6 | 手順 3 の区切りを `-` へ | `{serviceName}-{queueName}` | EXIT=0（**素通り**） | Failed 3 | ✅ テストのみ |
| A7 | 手順 3 の適用点を削除（コメント残置） | コメント 1 件のみ残存 | **EXIT=1** 消失検出 | Failed 2 | ✅ |

**落ちなかった変異は 0 件**（7/7 がいずれかのゲートで捕捉）。A2・A7 は対象シンボルが
**コメントとしてのみ残る**状態を作っており、#897 の F1 是正（コメント除去＋呼び出し構文照合）が
実際に効いていることの直接証拠である。

### S 系（個別サービス側の逸脱。規則 5(a) / 規則 4）

| ID | 変異の場所 | 結果 |
| --- | --- | --- |
| B1 | 実サービス `.cs` の**コメント**に手順 4 シンボル | **EXIT=1**・ファイル名指し |
| B2 | 実サービス `.cs` の**文字列リテラル**に手順 3 シンボル | **EXIT=1** |
| B3 | `templates/` の雛形 `.cs` に手順 5 シンボル | **EXIT=1**（決定 3b どおり） |
| B4 | 孤児 `.cs`（どの `.csproj` にも属さない） | **EXIT=1**・違反 3 件（決定 3b どおり） |
| B6 | **同一テストプロジェクト内の許可外ファイル** | **EXIT=1**（決定 3 の「ファイル単位」が実効） |
| B11 | 実サービスへ `UseConventionalRouting` | **EXIT=1**（規則 4。決定 1c どおり） |

### C 系（検査器自身の劣化。`--self-test`）

| ID | 変異 | 結果 |
| --- | --- | --- |
| C-1 | 許可リストへ 3 件目を追加 | **EXIT=1** `FAIL ★ 許可リストはちょうど 2 件である` |
| C-2 | 許可リストの**中身**を差し替え（件数 2 のまま） | self-test **EXIT=0**（素通り）／本走査が **EXIT=1** で補完 |
| C-3 | `(?!=)` を除去 | **EXIT=1** `FAIL ★ (b) == 比較だけでは「在る」と見なさない` |
| C-4 | `usage` 定義を 1 つ削除 | **EXIT=1** 2 件 FAIL（全文一致への退化を検出） |

### 捕捉されなかった変異（＝実測した限界）

| ID | 変異 | 結果 | 判定 |
| --- | --- | --- | --- |
| SX-1 | `dist/` という名のディレクトリ配下の `.cs` に手順 3 シンボル | 検査器 **EXIT=0**。ただし **MSBuild は拾う**（`dist\Sneak.cs(1,57): error CS1061` がファイルを名指し） | **走査範囲の主張が過大** |
| SX-2 | UTF-16LE(BOM) の `.cs` に手順 3 シンボル | 検査器 **EXIT=0**。**MSBuild は拾う**（`Sneak16.cs(1,59): error CS1061`） | **走査範囲の主張が過大** |
| B5 | 除外ユニット `src/ai-stock-trading` 配下 | 検査器 EXIT=0 | 設計どおり（射程外と明記済み） |
| B7 | `"ListenTo" + "RabbitQueue"` の文字列連結 | 検査器 EXIT=0 | 字面走査の原理的限界（意図的回避のみ） |

`dist` / `coverage` は `SKIP_DIRS`（`scripts/check-backend-libraries.js:55`）に含まれ、
`walk()` が**任意の深さでディレクトリ名一致**により丸ごと飛ばす（同 :619）。MSBuild の
既定 glob が除外するのは `bin` / `obj` だけなので、**コンパイルされるのに走査されない**領域が作れる。
現時点で `src/` `templates/` 配下に該当ディレクトリは **0 件**、UTF-16 BOM を持つ `.cs` も **0 件**
（実測）。したがって現時点の実害は無く、**将来 fail-open になり得る**という位置づけである。

### 安全弁（`PartialMigrationSafetyValveTests`）の切り分け

| ID | 変異 | ビルド | 落ちたもの |
| --- | --- | --- | --- |
| C1 | `AddPlatformPipelineStep` から `IConsumer` を外す | **EXIT=1** | コンパイル（CS0311 ×2）。テストは 1 件も実行されない |
| C2 | 同 `IPipelineStep` を外す | **EXIT=1** | コンパイル（CS0704） |
| C3 | 同 `class` を外す | **EXIT=1** | コンパイル（CS0452 ×2） |
| C4 | `IntrospectionBuilder.AddStep` から `IConsumer` を外す | **EXIT=0** | **テスト 1 件**（`AddStep_は…`） |
| C5 | `AddPlatformPipelineStep` を改名 | テストアセンブリが **CS0117** で不成立 | コンパイル。テストは 1 件も実行されない |
| C6 | `IConsumer` 制約を外し、本体も U5 相当へ（`bus.AddConsumer` 撤去） | **EXIT=0** | **テスト 1 件**（`AddPlatformPipelineStep_は…`） |

🔴 **切り分けの結論**: `AddPlatformPipelineStep` のコンパイル時の強制力は**型制約そのものではなく
本体実装の副作用**である（`bus.AddConsumer<TConsumer>()` が `class` + `IConsumer` を、
`TConsumer.StepName` が `IPipelineStep` の static abstract を要求する）。**U5 が本体を Wolverine 化して
`bus.AddConsumer` を呼ばなくなった瞬間、コンパイル時の強制力は型制約と同時に失われる**（C6 で実測）。
`AddStep` 経路では現時点で既にコンパイラは何も強制しておらず（C4 が BUILD_EXIT=0）、
**テストが唯一の防壁**である。

## 実測で過大と判明した live な記述（訂正対象）

| # | 記述 | 実測 |
| --- | --- | --- |
| 1 | `docs/tech/tech-requirements.md` 防壁表「ビルド ✅ 現在は止まる … `IConsumer<T>` を捨てたコンシューマの登録をコンパイルエラーにする」 | 型制約は**非ジェネリックな `IConsumer` マーカー**である。`IConsumer` と `IPipelineStep` のみを実装し `IConsumer<T>` を持たない型は**制約を満たしてコンパイルが通る**（BUILD_EXIT=0 で実測）。さらにその場合 `PipelineExtensions.cs:113` の `inputType is not null` により **input 宣言の突合が黙ってスキップされる** |
| 2 | 同 防壁表「封じ込め検査 ✅ 止まる」／IADR-0233 決定 3b「走査範囲は `src/` と `templates/` の両方、所属プロジェクトを問わない」 | `SKIP_DIRS` の**ディレクトリ名一致**により `dist` / `coverage` 配下は不可視（SX-1）。`utf8` 固定読み込みのため UTF-16 の `.cs` は照合不能（SX-2）。いずれも MSBuild はコンパイルする |
| 3 | `PartialMigrationSafetyValveTests` の `NotBeNull("安全弁を持つ登録経路そのものが消えていない")` | **到達不能**。`nameof` はコンパイル時束縛のため、メソッドが消えるとテストアセンブリが CS0117 で組めず、assertion は評価されない（C5 で実測）。登録経路の消滅を守っているのはコンパイラであってこのテストではない |

**訂正しないもの**: 仕様書 `20260822_issue-455_wolverine-shared-helper.md` の
「変異試験の実測」節は**当時の凍結記録**であり、書き換えない。過去のコミットメッセージも同様。

## 検証

- `dotnet build` / `dotnet test`（platform）が EXIT=0・件数が減っていない
- `node scripts/check-backend-libraries.js` および `--self-test` が EXIT=0
- `node scripts/check-doc-links.js` が EXIT=0（相対リンクの検査）
