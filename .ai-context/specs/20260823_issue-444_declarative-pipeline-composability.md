---
title: 作業仕様書 — 構成変更容易性（宣言的パイプライン構成・プラグイン拡張・構成情報 API）の再実装（#444）
type: spec
status: done
related_ids:
  - FR-14
  - FR-15
  - SC-11
  - ADR-0018
  - ADR-0019
  - ADR-0027
  - ADR-0039
  - IADR-0028
  - IADR-0029
  - IADR-0036
  - IADR-0234
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "ADR-0018（宣言的構成＋プラグイン規約。段・イベント接続・ポート実装・コネクタが構成のみで組み替え可）"
  - "06_technical/10_composability-design §1〜§6（宣言・プラグイン規約・イベント契約・ポート一覧・安全弁・構成情報 API）"
  - "02_requirements 受け入れ基準（段構成の変更がコア改修なしで適用・ロールバックできる／宣言と実効の不一致が検出時に警告される）"
  - "05_screens SC-11（実効構成・宣言との差分・構成バージョン履歴。管理者・運用者限定。値域は 5 分類・2 値深刻度・対象は段名）"
issue: "#444"
---

# 作業仕様書: 構成変更容易性の再実装（#444）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-14（システム構成をコア改修なしに宣言的構成の変更とプラグイン追加のみで組み替えられる）/
  FR-15（実効構成・構成バージョンの読み取り専用 API、宣言と実効の不一致の検出・警告、管理者・運用者限定）
- 画面: SC-11 構成ビューア（**本 issue では対象外**。理由は §射程外）
- 計画 ADR: ADR-0018（Accepted）。メッセージング基盤は ADR-0027 により Wolverine で読み替える
- ユースケース: —（運用・保守要求のため UC の割当なし）

## 着手前の実測（既存資産の棚卸し）

**#444 は「再実装」issue であるが、FR-14 / FR-15 の主要機構は既に実装済みである。** 走査で確認した実測:

| 機構 | 実体 | 状態 |
| --- | --- | --- |
| 宣言（正本） | `deploy/helm/microservices-platform/files/pipeline.json`（version/events/sources/steps） | 実在。5 段 |
| 宣言スキーマ | 同 `pipeline.schema.json` | 実在。ただし**レビュー・エディタ補完用**で CI は使わない（README 明記） |
| 宣言の CI 検証 | `scripts/validate-pipeline-config.js`（V1〜V6 ＋ `--self-test`） | 実在。`ci.yml` の `pipeline-config` ジョブが実行 |
| 束縛モデル | `Foundation/Pipeline/PipelineOptions.cs` | 実在 |
| 宣言の読み込み | `PipelineExtensions.AddPlatformPipelineConfig`（オーバレイ形／生 pipeline.json の 2 形式） | 実在 |
| 段登録（MassTransit 経路） | `PipelineExtensions.AddPlatformPipelineStep`（規則 1〜6・起動時 fail-fast） | 実在。document / wiki / ingestion が使用 |
| 段登録（Wolverine 経路） | `WolverinePipelineExtensions.AddPlatformWolverineStep`（規則 1〜9） | 実在。conversion が使用 |
| `enabled:false` の実効性 | 同 規則 8（**規約探索からの明示除外**） | 実在。回帰試験あり（`WolverinePipelineExtensionsTests` 規則 8/9 ＋前提試験） |
| 自己申告 | `Foundation/Introspection/IntrospectionExtensions`（`GET /internal/introspection`） | 実在。11 サービスに試験あり |
| 実効構成の組み立て | `ConfigInspectionService.Assemble`（段・イベント接続・ポート・コネクタ・構成バージョン） | 実在 |
| ドリフト検出 | `DriftDetector.Detect`（5 分類・2 値深刻度・対象＝段名） | 実在。単体試験 7 件 |
| 構成情報 API | `Platform.Bff/Foundation/Endpoints/ConfigBffEndpoints`（`/bff/admin/config`・`/drift`・`/history`・内部 `/internal/config/drift-run`） | 実在。認可 404 秘匿・監査あり |
| プラグイン拡張点（ポート） | `Composable/Adapters/Storage/ObjectStorageExtensions`（構成で S3 ↔ Null を選択） | 実在 |

> **「ConfigurationService」という名前のプロジェクトは実在しない。** 構成情報 API は IADR-0029 の決定どおり
> **BFF 配下の管理 API へ同居**しており、集約・突合の実体は `Platform.Shared.Infrastructure/Foundation/Introspection/` にある。
> 本作業ではこの 2 箇所を「構成情報 API の実体」として扱う。

### 実測で見つかった穴（本作業の対象）

1. **ドリフト検出の恒久的な盲点**: `DriftDetector` は宣言の段の担当サービスが「到達できない」とき
   `Unverifiable` / `Info` へ縮退する（誤検知抑制。IADR-0029）。ところが **`Introspection:Services` に
   その service が最初から登録されていない場合も同じ経路へ落ちる。** 一過性の到達不能と、
   **恒久的に検証されないという構成の誤り**が同じ深刻度で出るため、**宣言はあるのに永久に突合されない段**が
   静かに生まれる。FR-15 の「不一致を検出・警告する」が、そのサービスについてだけ効かない。
2. **突合基準そのものが空でも起動する**: `Pipeline:ConfigPath` を指定していてもファイルが無ければ
   `AddPlatformPipelineConfig` は黙って何もせず、宣言 0 件のまま BFF が起動する。この状態では実効の
   全購読が `UndeclaredSubscription` と誤判定される —— **#146 / #118 監査で実際に起きた回帰**であり、
   現在は compose / Helm のマウント配線を静的検査して守っているが、**使う側（BFF 起動時）には防壁が無い**。
3. **ポート実装の自己申告が実際の解決結果ではなくリテラル**: `AddPort("vector-store", nameof(QdrantVectorStore), …)` の
   ように**手書きの文字列**で申告している。構成でポート実装を差し替えても自己申告は変わらないため、
   実効構成の表示が実態とずれ得る。→ 本作業では**是正しない**（呼び出し元が担当外のサービスに在るため。§残件）。

## 目的（本作業の射程）

issue #444 の「退行防止（テスト必須）」3 項目を成立させ、**宣言が実際に効いていることを外形から確かめる**
試験を置く。実装は上記の穴 1・2 の是正に限る（既存機構の作り直しは行わない —— 動いているものを壊さない）。

## 実装内容

### A. ドリフト検出: 「未設定」を「到達不能」と区別する（穴 1）

`DriftDetector.Detect` で、宣言の段の担当サービスが `ReachableServices ∪ UnreachableServices`
（＝ `Introspection:Services` に登録済みの集合）に**含まれない**場合、`Unverifiable` を **`Warning`** で報告する。
登録済みだが応答が無い場合は従来どおり `Info`（一過性の誤検知抑制）。

- **値域は変えない**（種別 5 分類・深刻度 2 値・対象は段名）。SC-11 の確定値域・フロントの写像（`driftView.ts`）と整合する。
- 変えるのは**同じ値域の中での深刻度の割り当てのみ**である。

### B. 突合基準の空検出（穴 2）

`AddPlatformConfigInspection`（構成情報 API 側だけが呼ぶ拡張）で、`Pipeline:ConfigPath` が設定されているのに
束縛結果の `Steps` が 0 件なら**起動を失敗させる**。ConfigPath 未設定（ローカル・単体試験）は従来どおり素通り。

- 影響範囲は **BFF のみ**（`AddPlatformConfigInspection` の呼び出し元は `Platform.Bff/Program.cs` の 1 箇所。実測）。
- 段ホスト側の `AddPlatformPipelineConfig` の振る舞いは **1 バイトも変えない**（全サービスが依存する共有経路のため）。

### C. 試験（issue #444 の 3 項目への写像）

| issue の要求 | 置く試験 |
| --- | --- |
| 宣言スキーマのバリデーション（不正な宣言の拒否・エラーメッセージ） | 正の `pipeline.json` を実際に読み、束縛・段の実在・スキーマ整合を外形から確認。CI 検証器の `--self-test` は既存 |
| 宣言→実効構成の突合（差分検出の真陽性・偽陽性の固定） | 未設定＝Warning／到達不能＝Info／到達可能で欠落＝MissingApply の 3 分岐を固定。A の是正の回帰ガード |
| プラグイン差し替え（ポート実装の交換）で既存パイプラインが壊れない | `ObjectStorage` ポートを構成で差し替え、(1) 解決される実装が実際に変わること (2) 宣言的段登録・実効構成の組み立てが不変であることを同時に確認 |

さらに **「宣言が効いていること」の外形試験**として、正の `pipeline.json` に対し次を固定する。

- `enabled:false` にした段が**実効構成のイベント接続（購読者・発行者）から消える**こと（宣言の値が表示にまで届く）。
- 正の宣言が**有効な段を持つ全サービス**について、`docker-compose.yml` と Helm `values.yaml` の
  `Introspection__Services__<service>` が実在すること（宣言した段が**集約の対象になっている**こと）。

## 射程外（理由つき）

- **SC-11 構成ビューアの画面実装**: `src/*/frontend/` は本作業の担当領域外である（並行 issue との衝突回避）。
  API までに留める。なお SC-11 のフロント写像（`driftView.ts`）は既に契約の 5 分類・2 値を型で持っており、
  本作業の変更（深刻度の割当のみ）は契約の値域を変えないため画面側の追随を要さない。
- **ポート自己申告のリテラル是正（穴 3）**: 呼び出し元が `RetrievalService` / `LlmGateway` にあり、
  いずれも本作業の担当領域外である。§残件へ送る。
- **段の追加・削除そのもの**（新しい段の実装）: 計画外の機能追加に当たる。
- **JSON Schema と CI 検証器の二重管理の解消**: 規則集合が 3 箇所（schema / JS 検証器 / 起動時照合）に
  分かれているが、**同型の事故は 1 回も観測されていない**ため検査器を足さない（「2 回起きたら」規約）。

## 母集合（是正・追随の対象。着手前に自分で引いた結果）

走査は誤りの側の語で引き、パスで絞らず全ファイルを対象とした（規則 1・3・4）。**軸は 4 本**（規則 5）。

| 軸 | 検索語 | 件数 | 追随の要否 |
| --- | --- | ---: | --- |
| 1 | `Unverifiable` | 21 ファイル | 深刻度の割当を変えるため、**意味を述べている文書**のみ追随（下表） |
| 2 | `到達不能` | 37 ファイル | 大半は無関係な文脈（LLM 経路・ブローカー・埋め込み縮退）。ドリフト文脈は軸 1 の部分集合 |
| 3 | `ConfigPath` | 23 ファイル | 起動時ガードの追加。**振る舞いを述べている文書**のみ追随 |
| 4 | `AddPort(` | 3 ファイル | 穴 3。本作業では是正しないため追随なし（§残件へ記録） |

**追随する（＝本 PR で更新する）**:

- `docs/functional/FR-15_config-info-api.md`（ドリフト種別の意味・起動時ガード）
- `docs/tests/FR-15_config-info-api.md`（テストケース表）
- `.ai-context/adr/IADR-0268_…`（新規。仮番号）

**追随しないもの（除外と理由）**:

- `.ai-context/adr/IADR-0029` ほか `.ai-context/adr/*` ・ `.ai-context/specs/*` ・ `CHANGELOG.md`
  — **凍結記録**である（本文プロズを後から書き換えない）。IADR-0029 の決定を**変える**のではなく、
  同じ値域の中で深刻度を細分するため、新しい IADR に記録する。
- `src/*/frontend/**`（`driftView.ts` / `bff.schemas.ts` / i18n カタログ）— **契約の値域を変えないため追随不要**。
  かつ担当領域外。ただし `Unverifiable` の表示文言が「担当サービスへ到達できない」と書いており、
  未設定の場合にやや不正確である点は §残件 に記録する。
- `docs/api/openapi.yaml` — 種別・深刻度の**値域が変わらない**ため契約変更なし。かつ生成物である。
- `deploy/docker-compose.yml` / `values.yaml` — 現状すでに宣言の全 service を網羅している（実測）。
  変更ではなく**試験で固定する**対象である。
- `docs/screens/SC-11_configuration-viewer.md` — 画面が射程外であり、値域も変わらないため。
- `scripts/**` — 担当領域外（変更禁止）。
- `src/knowledge/backend/**` の段ホスト・統合試験 — 段ホスト側の振る舞いを変えないため。

## 受け入れ基準

- [x] 未設定サービスの宣言段が `Unverifiable` / `Warning` として区別される（真陽性）
- [x] 登録済みだが応答しないサービスは従来どおり `Unverifiable` / `Info`（偽陽性の抑制を壊さない）
- [x] `Pipeline:ConfigPath` 指定ありで宣言が空なら構成情報 API のホストが起動しない
- [x] 正の `pipeline.json` の全有効段の service が compose / Helm の自己申告設定に実在する
- [x] `enabled:false` が実効構成のイベント接続にまで効く
- [x] ポート実装を構成で差し替えても宣言的段登録・実効構成の組み立てが不変
- [x] 変異試験: 上記の各是正を外すと該当試験が落ち、戻すと通る（M1〜M5。下記）

## 検証

```
dotnet test platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Platform.Shared.Infrastructure.Tests.csproj
dotnet test platform/backend/Bff/Platform.Bff.Tests/Platform.Bff.Tests.csproj
dotnet format <対象 csproj> --verify-no-changes
node scripts/check-test-traceability.js / check-backend-libraries.js / check-contract-schema.js /
     check-trace-blocks.js / check-doc-updated.js
```

## 実測結果

`dotnet test platform/backend/Shared/Platform.Shared.Infrastructure.Tests/…` = **93 件全緑**（新規 16 件を含む）。
`dotnet format --verify-no-changes` は本体・試験の両プロジェクトで無出力（差分なし）。

### 変異試験（すべて戻した）

| # | 変異 | 結果 |
| --- | --- | --- |
| M1 | 未設定分岐の深刻度を `SeverityWarning` → `SeverityInfo` | `DriftServiceCoverageTests` 1 件が失敗（6 件中） |
| M2 | 起動時ガードの呼び出しを削除 | `ConfigInspectionDeclarationGuardTests` 2 件が失敗（4 件中。対照 2 件は緑のまま） |
| M3 | `Assemble` の購読者算出から `s.Enabled &&` を削除 | `PipelineDeclarationEffectivenessTests` 1 件が失敗（6 件中） |
| M4 | compose から `Introspection__Services__conversion-service` を 1 行削除 | 同上 1 件が失敗（compose の Theory ケースのみ。Helm ケースは緑＝どちらの配線が欠けたかまで分かる） |
| M5 | `ObjectStorageOptions.IsConfigured` を常に false | `PortSwapCompositionTests` 2 件が失敗（5 件中） |

### この環境で走らせられなかったもの

- `Platform.Bff.Tests`（既存の `DriftDetectorTests` を含む）は **submodule `src/ai-stock-trading` が
  populate されていないため BFF 自体がビルドできない**（`BffEndpointComposition.cs` の `AiStockTrading` 参照が
  未解決）。本作業の変更とは無関係の環境要因である。既存 `DriftDetectorTests` の到達不能ケースは
  `UnreachableServices` へ service を入れており（実測）、決定 1 の分岐では従来どおり `Info` に落ちるため
  影響しないことをソース上で確認した。
- 統合テスト（Docker 依存）は本環境では走らない。**skip を「通った」とは書かない。**

## 残件

- 穴 3（ポート自己申告のリテラル）。実効構成の表示が構成による差し替えに追随しない。
- `Unverifiable` の画面文言が「到達できない」に固定されており、未設定の場合に不正確。
