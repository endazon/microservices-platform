---
title: 作業仕様書 — 統合テストで pipeline.json の段宣言を通す（#455 Phase 0 / U0d）
type: spec
status: done
related_ids:
  - ADR-0027
  - ADR-0018
  - FR-14
  - UC-04
author: claude
created: 2026-08-21
updated: 2026-08-22
plan_refs:
  - "ADR-0018（宣言的パイプライン構成）／ADR-0027（Wolverine 移行チェックリスト）"
issue: "#455"
---

# 作業仕様書: 統合テストで pipeline.json の段宣言を通す（#455 Phase 0 / U0d）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: `ADR-0018`（宣言的パイプライン構成）/ `ADR-0027`（移行チェックリスト）
- 機能要求: `FR-14`（コンポーザビリティ）
- ユースケース: `UC-04`
- 実装 issue: `#455` / `#441`

## なぜ要るのか —— 誰も検査していない結合がある

`AddPlatformPipelineStep` は**起動時 fail-fast** を 4 つ持つ:

| 規則 | 破ると |
| --- | --- |
| 2 | 宣言があるのに段が未宣言 → **起動失敗** |
| 3 | 宣言の `consumer` 完全名が実装と不一致 → **起動失敗** |
| 4 | 宣言の `input` が `IConsumer<TIn>` の `TIn` と不一致 → **起動失敗** |
| 5 | `enabled: false` → 登録しない（購読・キューを作らない） |

🔴 **この 4 つを検査するテストが 1 件も無い。** 統合テストは `Pipeline:ConfigPath` を設定しないため、
`pipeline.Steps.Count == 0` の経路（「宣言が無いので既定で登録」）しか通っていない。

したがって **コンシューマのクラス名や namespace を変えると、本番は起動時に落ちるのにテストは緑のまま**である。
これは U0a〜U0c で塞いできた「赤で分からない退行」と同じ形である。

🔴 **［2026-08-22 追記 / #892］上の「この 4 つを検査するテストが 1 件も無い」は誤りだった。**
規則 2〜5 は `ConversionService.Worker.Tests` の `PipelineStepRegistrationTests` が**合成した宣言に
対して**既に検査していた（2026-07-08 の #111 で追加）。無かったのは「**出荷される `pipeline.json`
に対する**検査」である。本文は凍結記録として書き換えず、追記で訂正する（#892 で docs 側は訂正済み）。

## 🔴 最大の落とし穴 —— これを潰さない U0d は無価値である

`AddPlatformPipelineConfig` の冒頭:

```csharp
var path = builder.Configuration["Pipeline:ConfigPath"];
if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
{
    return builder;   // 🔴 黙って何もせずに返る
}
```

**パス解決に失敗しても例外は出ない。** 宣言は 1 行も読まれず、テストは**全件緑のまま**になる。
つまり「`ConfigPath` を設定したつもり」で**何も検査していない**状態が、**成功と見分けがつかない**。

**対策**: **宣言が実際に読み込まれたことを assert するテストを必ず置く。**
起動したホストの `IConfiguration` から `GetPlatformPipeline().Steps` を読み、**5 件**あることを確かめる。
これが無ければ本作業は「緑を増やしただけ」になる。

## 母集合（着手前に自分で引いた）

| 軸 | 検索語 | 結果 | 追随 |
| --- | --- | --- | --- |
| 1 | `ConfigPath`（`docs/`） | 3 件 | `docs/tech/tech-requirements.md:364`（残る穴 1）のみ。`FR-14_composability.md` の 2 件は**配送の説明**であり本作業で偽にならない |
| 2 | `残る穴`（`docs/`） | 1 件 | 同上（`:362`「残る穴は 1 つである」） |

`.ai-context/specs/` の記載は**凍結記録**であり遡及書き換えしない（除外）。

## 設計上の判断

### 1. 実ファイルを指す。複製しない

`deploy/helm/microservices-platform/files/pipeline.json`（**本番が読む正本**）をそのまま指す。
テストプロジェクトへ複製すると、**複製が必ず腐る** —— 本番の宣言を変えてもテストの複製は古いままになり、
「宣言と実装の一致」を検査するはずのテストが**古い宣言との一致**を検査するようになる。

パス解決は既存 `Deployment/PipelineDeclarationMountTests.ReadRepoFile` と同じく
`AppContext.BaseDirectory` から親へ辿る。

🔴 **［2026-08-21 追記 / #890］当初ここには「同じ作法を 2 つ持たない」と書いていたが、これは誤りだった。**
**作法は揃えたが、実装は重複させている。** 走査すると「親へ辿る」ループは本アセンブリに
**6 箇所**ある（`Deployment/` の 5 テストクラス ＋ 本作業の `FindRepoFile`）。
集約は #891 へ切り出した（6 ファイルに跨り、5 件は本作業と無関係なため射程外）。

🔴 **この誤りは規則 10 の破れである。** コード側のコメントを是正したとき、
**同じ文言を持つ自分の記述をコード外まで引き直さなかった**。「是正のたびに、この変更で
新たに誤りになる自分の記述を引き直す」は**仕様書・PR 本文まで含む**。実際、是正後に
`git grep "同じ作法を 2 つ持たない"` を全ファイルへ掛けて初めて本箇所が出た
（AI レビューの指摘で気づいた。**自分では引き直していなかった**）。

### 2. 既存テストへの影響

実測により、統合テストがホストする全サービスの登録コンシューマは **5 件**で、
**5 件すべてが `pipeline.json` に宣言されている**（下表）。よって規則 2 に抵触するサービスは無い。

| サービス | 登録コンシューマ | 宣言 |
| --- | --- | --- |
| ConversionService | `RawDocumentFetchedConsumer` | ✅ convert |
| DocumentService | `DocumentNormalizedConsumer` | ✅ catalog |
| IngestionService | `DocumentUpdatedConsumer` | ✅ ingest |
| WikiService | `DocumentSyncConsumer` / `DocumentDeletedConsumer` | ✅ wiki-sync / wiki-delete |
| DataSourceService | なし（発行のみ） | — |

`AuthorizationService` は `AddMassTransit` を呼ばない。

### 3. 既存テストとの重複ではない

`Deployment/PipelineDeclarationMountTests` は **compose / Helm が宣言を配送する**ことを
YAML テキストで静的に見る。**宣言を積んだサービスが実際に起動するか**は見ていない。補完である。

## やること

1. `IntegrationTestFactoryBase.ConfigureWebHost` で `Pipeline:ConfigPath` を実 `pipeline.json` へ向ける
2. 🔴 **宣言が実際に読み込まれたことを assert する新規テスト**を置く（`Steps` が 5 件）
3. 宣言経由でも `DocumentUpdated` の 2 購読者が生きていることを確かめる（U0c のテストが緑のまま）

## 受け入れ基準

1. **宣言が読み込まれたことを assert するテストがある**（`Steps.Count == 5`）
2. 既存 44 件が緑のまま（**1 件も減らない**）
3. **変異試験**: `pipeline.json` の `consumer` 完全名を 1 文字変えると**起動が落ちる**
   - 🔴 変異が当たったことを先に確認する
   - 落ち方が `InvalidOperationException`（規則 3 のメッセージ）であることを確かめる
4. **変異試験 2（fail-open の確認）**: パス解決をわざと壊すと **1 で置いたテストが落ちる**
   - 🔴 **これが落ちなければ、本作業は何も検査していない**
5. `dotnet test` 両ユニット Failed 0 / `dotnet format` EXIT=0 / 検査器一式 EXIT=0

## 実測

### 🔴 最初の実装は「成功と見分けのつかない失敗」に陥っていた —— 置いたテストが検出した

`ConfigureAppConfiguration` の `overrides` へ `Pipeline:ConfigPath` を入れた最初の版は、
**既存 44 件が全て緑のまま**だった。しかし**新規テストだけが落ちた**:

```
Failed: 2, Passed: 44, Total: 46
Expected pipeline.Steps not to be empty because Pipeline:ConfigPath が解決できていれば
段宣言が載る。空なら AddPlatformPipelineConfig が黙って return しており、
**段宣言は 1 行も通っていない**（テストが緑でも何も検査していない）.
```

**原因は読まれる時点である。**

| 設定 | 読まれる時点 | `ConfigureAppConfiguration` で効くか |
| --- | --- | --- |
| `RabbitMq:ConnectionString` | `UsingRabbitMq` のラムダ内（**遅延**） | ✅ 効く |
| `Pipeline:ConfigPath` | `builder.AddPlatformPipelineConfig()`（**ビルダ構築中に即座**） | ❌ **間に合わない** |

🔴 **U0a の「config 上書きは効く」という確認を一般化していた。** 実際には
**遅延して読まれる値について確かめただけ**であり、即座に読まれる値には当てはまらない。
`UseSetting`（ホスト構成へ書く）に変えて解決した。

**この誤りが無害だったのは、受け入れ基準 1 のテストを先に置いていたからである。**
置いていなければ「44 件緑」を見て着地させ、**何も検査していない U0d** が残っていた。

### 変異試験

| 変異 | 期待 | 実測 |
| --- | --- | --- |
| **1**: 宣言の `consumer` 完全名を 1 文字変える（`…Consumer` → `…ConsumerX`） | 規則 3 で起動が落ちる | ✅ **EXIT=1** — 「段 'wiki-sync' の consumer 宣言 '…DocumentSyncConsumerX' が実装 '…DocumentSyncConsumer' と一致しません」/ Failed 5 |
| **2**: パス解決をわざと壊す | **受け入れ基準 1 のテストが落ちる** | ✅ **EXIT=1 / Failed 2, Passed 0** — `FindRepoFile` の fail-closed が発火（`System.IO.FileNotFoundException`） |

いずれも**変異が当たったことを先に確認**した（`git diff` の生出力で該当行を確認、変異 2 はビルド EXIT=0）。
**復旧確認**: `pipeline.json` の差分なし・変異残骸 0 件。

🔴 **変異 1 は本番ファイル（`deploy/helm/.../pipeline.json`）を書き換える。**
実施後に `git diff` が空であることを必ず確かめた。

### 受け入れ基準

| # | 基準 | 実測 |
| --- | --- | --- |
| 1 | 宣言が読み込まれたことを assert するテストがある | ✅ `PipelineDeclarationLoadedTests`（2 件） |
| 2 | 既存 44 件が緑のまま | ✅ **44 → 46**（純増 2・既存は 1 件も減らない） |
| 3 | 宣言の `consumer` を壊すと起動が落ちる | ✅ 変異 1 |
| 4 | パス解決を壊すと基準 1 のテストが落ちる | ✅ 変異 2 |
| 5 | 検査器一式 | 下記 |

## 残る穴（本作業の射程外）

- **`queue` 上書きの経路**。正本 `pipeline.json` の 5 段はいずれも `queue` を持たないため、
  実ファイルを指すだけでは通らない。試験には `queue` を設定したフィクスチャが要る。
  🔴 **U0c の変異試験はコード側で競合状態を作ったが、宣言経由の同じ状態はまだ試験していない。**
