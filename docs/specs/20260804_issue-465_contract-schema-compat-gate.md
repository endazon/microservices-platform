---
title: 作業仕様書 — Shared.Contracts の後方互換検査（契約テスト基盤・check-contract-schema.js）
type: spec
status: done
related_ids: [NFR, FR-14, ADR-0018, ADR-0027, ADR-0029, IADR-0028, IADR-0049, IADR-0115, IADR-0116, IADR-0120, IADR-0122]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md"
related_specs:
  - ./20260803_issue-453_regression-test-foundation.md
  - ./20260804_issue-467_cpm-version-inline-check.md
  - "../tests/TEST_STRATEGY.md"
  - "../adr/IADR-0122_contract-schema-source-and-compat-gate.md"
  - "../adr/IADR-0049_composability-standards-phased-adoption.md"
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
  - "../adr/IADR-0116_reimplementation-branching-and-pr-policy.md"
  - "../adr/IADR-0120_excluded-units-from-gitmodules.md"
---

# 作業仕様書: Shared.Contracts の後方互換検査（契約テスト基盤）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-14（コンポーザビリティ）／NFR（保守性・可用性）
- ユースケース（UC）/ 画面（SC）: なし
- **本作業が機械化する規約の典拠**:
  [`06_technical/10_composability-design.md`](../../planning/projects/microservices-platform/06_technical/10_composability-design.md)
  §3（100 行目）の原文——

  > イベントスキーマはバージョン管理し、**後方互換の追加のみ許可**（フィールド削除・意味変更は
  > 新バージョン＋移行期間）とする。互換性は CI の契約テストで検証する。

  同 §3 には 2026-07-10 の段階適用注記（102 行目）があり、共通エンベロープと CI 契約テストを
  「到達目標として維持しつつ初期は代替策を許容」としている。ADR-0018（固定/可変区分）でも
  「イベント共通エンベロープの標準」は固定部である。
- 関連 ADR:
  - [ADR-0029](../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md)（Accepted）
    — east-west = gRPC + Protobuf、north-south = REST + OpenAPI。「proto 契約は呼び出される側の
    サービスが所有し、共有契約として公開する」。抽出方式の候補 D（proto）の典拠。
  - [ADR-0027](../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md)（Accepted）
    — 非同期メッセージングは Wolverine。**最も壊れやすい面（イベント契約）がどこにあるか**の典拠。
  - [IADR-0049](../adr/IADR-0049_composability-standards-phased-adoption.md)（Accepted）
    — 共通エンベロープ＋CI 契約テストの繰延。**本作業がその判断を見直す**（後述）。
  - [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md) — 逃げ道の無いゲートは無視される。
  - [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4 — issue 分割。
  - [IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md) — 検査対象外ユニットの導出。
- 本リポジトリの起点: [#465](https://github.com/endazon/microservices-platform/issues/465)
  （親: [#453](https://github.com/endazon/microservices-platform/issues/453) /
  [#454](https://github.com/endazon/microservices-platform/issues/454)）
- 本作業で起票した実装 ADR: [IADR-0122](../adr/IADR-0122_contract-schema-source-and-compat-gate.md)（Accepted）
  - **採番の注記**: `IADR-0121` は並行 in-flight の PR [#489](https://github.com/endazon/microservices-platform/pull/489)
    （SPA スタック移行・`feat/ADR-0031-spa-foundation-migration`）が使用中のため本件は `0122` を採った。マージ順が逆転した場合は
    [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「採番衝突時の改番手順」の
    **先着尊重**に従って改番する（ファイル名・本文の自番号・`docs/adr/README.md` の索引・本書の
    `related_ids` と本文・PR タイトルの 5 箇所を追随させる）。

## 目的・背景

全面再実装（[#454](https://github.com/endazon/microservices-platform/issues/454)）では 11 サービスを
作り直す。このとき最も危険なのは、サービス間の契約（`Shared.Contracts` のイベント・API スキーマ）が
**両側同時更新で気付かれずに壊れる**ことである。発行側と購読側を同じ PR で直せばコンパイルもテストも
通り、**壊れたことが表に出るのは旧版が動いている環境と混在した瞬間**である。

計画 §3 はこれを「後方互換の追加のみ許可、互換性は CI の契約テストで検証」と定めているが、
[IADR-0049](../adr/IADR-0049_composability-standards-phased-adoption.md) 決定 1 が共通エンベロープと
CI 契約テストを**セットで**繰延している。その根拠は

> 契約テストのみ先行しても検証対象（エンベロープ）が無く空振りになる。

である。本作業はこの判断の見直しにあたるため、新 IADR（[IADR-0122](../adr/IADR-0122_contract-schema-source-and-compat-gate.md)）が要る
（#465 が名指しで指定）。

## 対象範囲

- 含むもの:
  1. [`scripts/check-contract-schema.js`](../../scripts/check-contract-schema.js) の新設
     （外部依存ゼロ・`--self-test` / `--update` / `--print`）。
  2. [`scripts/contract-schema-baseline.json`](../../scripts/contract-schema-baseline.json)（スナップショット）と
     [`scripts/contract-breaking-allowlist.json`](../../scripts/contract-breaking-allowlist.json)（承認）の新設。
  3. [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) に独立ジョブ `contract-schema` を追加。
  4. [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) に単体テストを追加。
  5. [`docs/adr/IADR-0122_contract-schema-source-and-compat-gate.md`](../adr/IADR-0122_contract-schema-source-and-compat-gate.md) の起票と、
     [`IADR-0049`](../adr/IADR-0049_composability-standards-phased-adoption.md) への日付付き追記
     （[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) 先例の形式）。
  6. [`scripts/README.md`](../../scripts/README.md)（表・使い方・CI ジョブ表・**破壊的変更の手順**節）と
     [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md)（ゲート一覧・テスト種別表・切り出し項目の消し込み）の追随。
- 含まないもの:
  - **共通エンベロープの導入**。[IADR-0049](../adr/IADR-0049_composability-standards-phased-adoption.md) の
    繰延解除条件（(a) 後方互換判定の機械化不能による障害・(b) 段の挿抜による横断的変更の反復・
    (c) ABAC 属性ヒント／トレース ID の本体搬送要件の確定）はいずれも未成立であり、項目の確定は上流
    `07_abac-attribute-model.md` との整合を要する。
  - **フロントエンドの契約**（`orval` 生成物と OpenAPI の整合）。#446 系の管轄でスコープ外。
  - **proto の導入**。`.proto` は現在 0 件であり、east-west の gRPC 移行そのものは別 issue。
  - **`docs/api/openapi.yaml` の生成方式の変更**。本作業は同ファイルを読まない。
  - `ai-stock-trading`（submodule）配下。別プロジェクトであり本リポジトリの規約を適用しない
    （[IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)）。

## IADR-0115 の位置づけ確認

| ファイル | キット原本（`planning/tools/impl-handoff-kit/repo-template/scripts/`） | 位置づけ | 本作業での扱い |
| --- | --- | --- | --- |
| `check-contract-schema.js`（新設） | 無し | **固有デルタ種 3**（本リポにしか存在しない成果物） | 新規追加。キットへの環流はしない |
| `contract-schema-baseline.json` / `contract-breaking-allowlist.json`（新設） | 無し | 固有デルタ種 3 | 新規追加 |
| `scripts.repo.test.js` | 無し（companion の受け口はキット側にある） | 固有デルタ種 3 | 追記する |
| `scripts.test.js` | 有り・バイト一致（分類 A） | 分類 A | **触らない** |
| `lib/excluded-units.js` / `lib/ci-annotate.js` | 前者は無し・後者は分類 A | — | 参照のみ（改変しない） |
| `scripts/README.md` | 有り・差分あり | 分類 B（固有デルタ種 3 を含む） | 同じ作法で追記 |
| `.github/workflows/ci.yml` | 有り・差分あり | 分類 B（固有デルタ種 2・3） | ジョブ 1 件を追加 |

C# の契約解析は .NET 固有であり、キット（技術スタック非依存）の前提ではない。よって環流しない。

## 実測（着手時点）

### 測定条件

- 対象コミット: `origin/develop` = `875cb87`（`docs(NFR): Knowledge.Bff.Endpoints.csproj のコメントを
  IADR-0117 の 3 プロジェクトへ追随 (#487)`）から作成した worktree。
- **`planning` submodule は populate 済み**（`git submodule update --init planning` を実行）。計画 §3・
  ADR-0027・ADR-0029 の原文確認はこの状態で行った。**未 populate の環境ではこの確認は再現できない**
  （submodule の populate 状態で結果が変わる検査の扱いは #484 / #486 で確立した教訓）。
  `src/ai-stock-trading` は**未 populate**（空ディレクトリ）のため、除外の効き目は走査件数に現れない
  （除外規則そのものは `--self-test` と `scripts.repo.test.js` のパス判定で固定する）。
- **.NET SDK は存在しない**（`dotnet: command not found`。取得も proxy policy で拒否。#486 で実証済み）。
- Node: v22.22.2（CI は 20。本検査器は Node 標準モジュールのみを使い、両者で挙動差は無い）。

### 契約の棚卸し

| 項目 | 実測 |
| --- | --- |
| 契約プロジェクト | **2 件**（`src/platform/backend/Shared/Platform.Shared.Contracts` / `src/knowledge/backend/Shared/Knowledge.Contracts`） |
| `.cs` ファイル | **20 件**（platform 5 / knowledge 15） |
| public 型 | **56 件**（platform 24 / knowledge 32） |
| 型種別の内訳 | record **47** / class **7** / enum **2** |
| メンバー総数 | **269 件** |
| メンバーの内訳 | 位置引数 **226** / プロパティ **25** / `const` **13** / enum メンバー **5** |
| 型に付いた属性 | **1 件**（`AnalysisTaskType` の `[JsonConverter(typeof(JsonStringEnumConverter<AnalysisTaskType>))]`） |
| メンバーに付いた属性 | **0 件**（`[JsonPropertyName]` 等は未使用） |
| `partial` 型・入れ子 public 型・ソースジェネレータ生成型 | **0 件**（構文解析の限界が現時点で顕在化しない裏付け） |

### 抽出方式の候補が覆える範囲

| 項目 | 実測 |
| --- | --- |
| `.proto` ファイル（リポジトリ全体） | **0 件** |
| `docs/api/openapi.yaml` の行数 | 1267 行（BFF の REST のみ） |
| 同ファイルに現れるイベント型（`Knowledge.Contracts.Events` 相当） | **0 件** |
| `docs/api/openapi.yaml` の生成経路 | `scripts/gen-openapi-skeleton.js`（通信仕様書からの雛形生成）＋手書き。**コードからの生成ではない** |

**この 3 行が方式選定の決め手である。** イベント契約（最も壊れやすい面）を OpenAPI は 1 件も含まず、
proto は存在しない。過剰設計を避けるため、この実測を先に取ってから設計した。

## 抽出方式の選定（IADR-0122 決定 1）

| | A. C# ソースの構文解析（**採用**） | B. リフレクション | C. OpenAPI | D. proto |
| --- | --- | --- | --- | --- |
| イベント契約を覆えるか | **覆える** | 覆える | **覆えない**（0 件） | 現状は覆えない（0 件） |
| REST DTO を覆えるか | 覆える | 覆える | 一部（BFF 公開分のみ） | 覆えない |
| 正本としての妥当性 | 実装そのもの＝ドリフトしない | 実装そのもの | **手書き/雛形生成**でドリフトし得る | **未生成**。将来 east-west の正本 |
| .NET SDK 依存 | **無し** | **有り**（本環境で実行不能） | 無し | 無し（protoc 別途） |
| ローカルで自己試験・単体テストできるか | できる | **できない**（CI 専用になる） | できる | できる |
| 属性由来のシリアライズ差 | 属性の**記述**は見られる | 見られる（解決済みの型情報） | 見られる（生成された JSON 表現） | 該当なし |
| 精度の限界 | 後述「限界」 | 少ない | 生成の網羅性に依存 | proto の範囲のみ |

**B（リフレクション）を落とした主因は本環境に .NET SDK が無いこと**であり、精度の優劣ではない。
これを曖昧にしないため IADR にも本書にも明記する。SDK が要る検査器は、
(1) ローカルで実行できず「壊れたときにしか動かない検査」になる、
(2) `scripts.test.js`（#465 の受け入れ観点）から単体テストできない、
(3) 自己試験の負例を一時ツリーで実走査できない——の 3 点が同時に成立しない。

**D（proto）は将来の正本である。** [ADR-0029](../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md)
は east-west を gRPC + Protobuf と定めており、本決定はそれを否定しない。存在しない成果物を正本に
できない、という現時点の事実に基づく選定である（IADR-0122 フォローアップ 1 で切り替えを予定）。

## 検査仕様

### 走査対象

`src/<unit>/backend/Shared/` 配下で、**名前が `.Contracts` で終わり `.csproj` を持つ**ディレクトリ。
パスをハードコードせず規約から導出するのは、可変機能ユニットが増えたときに検査から漏れないため
（ユニット第一構成: IADR-0056）。`src/ai-stock-trading` は
[`lib/excluded-units.js`](../../scripts/lib/excluded-units.js) で除外する（[IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)）。
`bin` / `obj` / `node_modules` 等の生成物ディレクトリは走査しない。

### 抽出するもの（正規化スナップショット）

| 対象 | 記録する内容 |
| --- | --- |
| public 型（record / recordStruct / class / struct / interface / enum） | FQN・`kind`・型に付いた属性 |
| record の位置引数 | 名前・型（空白除去で正規化）・`required`（**既定値の有無**）・`position`・属性 |
| public プロパティ | 名前・型・`required`（`required` 修飾子の有無）・属性 |
| `public const` | 名前・型・**値**（`"queued"` 等は配線そのもの） |
| enum メンバー | 名前・**値**（明示値、無ければ C# の規則で直前 + 1・先頭 0） |

`internal` 型・public メソッド（`IsValid` / `Normalize` 等）は**含めない**。メソッドはスキーマではなく、
削除すればコンパイルが落ちるためスナップショットで守る意味が薄い。

### 判定（IADR-0122 決定 2）

| 変更 | 分類 |
| --- | --- |
| 型・メンバー・enum メンバーの削除、契約プロジェクトの消失 | **破壊的（fail）** |
| メンバー型の変更・型種別の変更 | **破壊的** |
| 省略可能 → 必須（既定値の除去） | **破壊的** |
| 位置引数の並べ替え | **破壊的**（record の primary constructor が壊れる） |
| 位置引数 ↔ プロパティの移動 | **破壊的** |
| `const` 値・`enum` 値の変更（**並べ替えによる暗黙序数の変化を含む**） | **破壊的** |
| 型・メンバーの属性の変更 | **破壊的**（`JsonConverter` の除去で enum が文字列 → 数値になる等） |
| **既定値の無いメンバーの追加** | **破壊的** |
| 型・メンバー・enum メンバー・`const` の追加（既定値付き）、必須 → 省略可能 | 非破壊 |

「既定値の無いメンバーの追加」を破壊的側へ置くのが素朴な分類との唯一の違いである。追加は計画 §3 が
許す形だが、**既定値が無ければ旧発行者のメッセージは必須項目を欠く**（かつ既存の位置引数
コンストラクタ呼び出しが壊れる）。既定値を付ければ非破壊に落ちるため、逃げ道は常にある。

**非破壊であっても baseline との差分がある限り exit 1 にする**（＝スナップショットテスト）。
`--update` で baseline を更新し、**差分そのもの（契約の変更）を PR のレビュー対象に載せる**のが主眼で
ある。「両側同時更新で気付かれない」失敗は、レビューアが変更に気付けないことで起きる。判定より前に
「契約が変わったことが PR の diff に必ず現れる」ことが効く。

### 逃げ道（IADR-0122 決定 3）

破壊的変更は [`scripts/contract-breaking-allowlist.json`](../../scripts/contract-breaking-allowlist.json) の
`approvals` へ `{ key, reason, approvedBy, issue, date }`（**すべて必須**）を書き、`--update` を実行する。

- `--update` は承認エントリを baseline の `$acceptedBreakingChanges` へ**移し**、allowlist を空へ戻す。
  承認の記録は baseline 側に残り、git 履歴で追える。
- `--update` は**未承認の破壊的変更があると baseline を更新しない**。承認を書かずに通す道は無い。
- 対応する変更が無い承認（stale）が残っていれば **fail** する。承認だけが残ると次の破壊的変更を
  黙って通すためである。既存 ratchet 群（`backend-library-baseline.json` /
  `test-traceability-allowlist.json` / `coverage-floor.json`）と同じ 3 判定である。
- 必須項目を強制するのは、**理由・承認者・issue・日付の残らない承認はただの無効化スイッチ**だから。
  `date` は `YYYY-MM-DD`、`issue` は `#123` か `owner/repo#123` の形式を検査する。

手順は [`scripts/README.md`](../../scripts/README.md)「契約の破壊的変更（`Shared.Contracts`）」節に
書き、検査器の失敗出力からも同じ手順を出す（読まれない場所にしか書かない手順は無いのと同じ）。

## 限界（正直に記録する）

構文解析ベースの方式が**追えない**ものを列挙する。着手時点でいずれも実測 0 件だが、増えれば盲点になる。

| 限界 | 内容 | 現在の件数 | 顕在化したときの対処 |
| --- | --- | --- | --- |
| `partial` 型 | 同一型が複数ファイルへ分かれると、ファイル単位の抽出結果が後勝ちで上書きされ得る | 0 件 | 型単位のマージへ改修する |
| 入れ子の public 型 | 親の本文に含まれるため、親のメンバーとして誤って拾われ得る | 0 件 | 本文からの入れ子除去を追加する |
| **式形（expression-bodied）プロパティ** | `public string Foo => "x";` はプロパティ判定が `{ get;` を要求するため**捕捉できない**。計算プロパティは JSON へ出力されるため、将来 `Shared.Contracts` に書かれると**契約に載るのにスナップショットへ現れず、削除・型変更が素通りする**（`public T M(...) => ...;` の式形**メソッド**は意図的に対象外であり、これとは別） | 0 件（`public .*=>` の実測 4 件はいずれも `static` メソッド: `CompletionStopReasons.IsRefusal` / `IsMaxTokens`、`FeedbackRating.Normalize`、`UsageEventType.Normalize`） | プロパティ判定へ `=>` 形を加える（引数リスト `(` の有無でメソッドと弁別する） |
| ソースジェネレータ生成型 | ソースに現れないため一切見えない | 0 件 | 該当時はリフレクション方式の併用を再検討（IADR 改定） |
| 外部シリアライズ設定 | `JsonSerializerOptions`（`PropertyNamingPolicy` 等）や `Wolverine` のメッセージ型解決規約による JSON 表現の変化は、契約ソースに現れないため見えない | — | 設定側の変更検査は別 issue（本検査の対象外であることを明示する） |
| 属性の**意味論** | 属性は「書かれているか」を見るだけで、`typeof(...)` の中身が何を意味するかは解決しない | — | 同上 |
| `#if` 等の条件付きコンパイル | 条件を解釈せず、書かれている宣言をすべて拾う | 0 件 | 条件付きの契約は禁止する運用で足りる見込み |
| baseline の hand-edit | JSON を手で書き換えれば検査は素通りする | — | 他の baseline 系 ratchet と同じ性質。歯止めはコードレビュー |

XML / C# パーサを持ち込まないのは**外部依存ゼロの原則**（`scripts/` 全体の作法）による。
コメント除去は文字列リテラル・逐語文字列を保護する小さなスキャナで行い、`"storage://a"` のような
リテラルを壊さない（`const` 値は契約そのものであるため保護が必須）。

## 実装（変更点）

| ファイル | 変更 |
| --- | --- |
| [`scripts/check-contract-schema.js`](../../scripts/check-contract-schema.js) | 新設。公開 API は `stripComments` / `matchBracket` / `splitTopLevel` / `precedingAttributes` / `leadingAttributes` / `parsePositionalParams` / `parseBodyMembers` / `parseEnumMembers` / `parseNamespace` / `extractTypes` / `findContractProjects` / `buildSnapshot` / `diffSnapshots` / `validateApprovals` / `evaluate` / `nextBaseline` / `emptyAllowlist` ほか |
| [`scripts/contract-schema-baseline.json`](../../scripts/contract-schema-baseline.json) | 新設（1865 行）。`$comment` / `$acceptedBreakingChanges` / `projects` |
| [`scripts/contract-breaking-allowlist.json`](../../scripts/contract-breaking-allowlist.json) | 新設。`$comment` / `approvals`（初期値は空） |
| [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) | ジョブ `contract-schema` を追加（`cpm-versions` と同形式: self-test → 素実行）。既存ジョブの本文は触らない |
| [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) | 単体テストを追加（抽出の境界・判定・承認・実リポジトリ・出力経路・`--self-test` の exit 0） |
| [`docs/adr/IADR-0122_*.md`](../adr/IADR-0122_contract-schema-source-and-compat-gate.md) | 新設（Accepted） |
| [`docs/adr/IADR-0049_*.md`](../adr/IADR-0049_composability-standards-phased-adoption.md) | 決定 1 へ日付付き追記・`Superseded by` 注記・`related_ids` / `updated` の更新。**本文は当時の判断としてそのまま残す** |
| [`docs/adr/README.md`](../adr/README.md) | 索引へ IADR-0122 の行を追加 |
| [`scripts/README.md`](../../scripts/README.md) | 表・ローカル実行例・CI ジョブ表・「契約の破壊的変更」節 |
| [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) | ゲート一覧へ 1 行・テスト種別表の「契約」行を実装済みへ・切り出し項目の消し込み・ratchet 原則へ追記 |

### CI ジョブ

```yaml
  contract-schema:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-node@v7
        with:
          node-version: "20"
      - name: Self-test contract schema checker
        run: node scripts/check-contract-schema.js --self-test
      - name: Check contract backward compatibility
        run: node scripts/check-contract-schema.js
```

submodule は取得しない（対象は platform / knowledge の `Shared.Contracts` で、いずれも本リポジトリの
実体である）。追加は独立位置に最小差分で行い、既存ジョブの行には触れない（`ci.yml` のシェア行が
インターリーブして衝突した事故があるため）。

## 受け入れ基準

- [x] 後方互換を壊す契約変更が CI で止まる（`--self-test` の負例が一時ツリーを実走査して固定。加えて
      `scripts.repo.test.js` が実ツリー・子プロセスで exit 1 と失敗メッセージを固定する）。
- [x] 破壊的変更を意図的に行う手順が文書化され、承認の記録が残る形になっている
      （`scripts/README.md`「契約の破壊的変更」節・検査器の失敗出力・baseline の `$acceptedBreakingChanges`）。
- [x] 承認エントリの必須 5 項目（`key` / `reason` / `approvedBy` / `issue` / `date`）が機械検査される。
- [x] 対応する変更が無い承認（stale）が fail する。
- [x] `node scripts/check-contract-schema.js --self-test` が exit 0。
- [x] `node scripts/scripts.test.js` が緑で、テスト件数が着手前から減っていない。`REQUIRE_REPO_TESTS=1` でも緑。
- [x] 現状のリポジトリで差分 0 件・exit 0。
- [x] `src/ai-stock-trading` 配下は走査対象外（`lib/excluded-units.js` から導出。ハードコードしない）。
- [x] 走査対象 0 件のときに「差分なし」で緑にならない（0 件検査への退行を止める）。
- [x] `node scripts/check-doc-links.js` が exit 0（本仕様書・IADR のリンクを含む）。
- [x] `.github/workflows/ci.yml` が YAML としてパースでき、既存ジョブが増減していない。
- [x] `node scripts/check-commit-messages.js --base origin/develop` が緑。

## 検証（実測）

測定条件は上記「実測 / 測定条件」と同じ。

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-contract-schema.js` | `OK: 2 プロジェクト / 20 ファイル / 56 型が baseline と一致（未消化の承認 0 件）` / exit 0 |
| `node scripts/check-contract-schema.js --self-test` | 自己試験 **66 件 OK** / exit 0 |
| `node scripts/scripts.test.js` | **225 tests passed** / exit 0（着手前 **209 件** → +16 件。着手前の値は `origin/develop` 版の `scripts.repo.test.js` へ一時的に戻して実測） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **225 tests passed** / exit 0 |
| `node scripts/check-doc-links.js` | 後述 |
| `ci.yml` の YAML パース | 後述 |
| `node scripts/check-commit-messages.js --base origin/develop` | 後述 |

### 負例の実効性（fail することの実測）

**(1) 自己試験（一時ツリーを `buildSnapshot()` で実走査）**

| 負例 | 結果 |
| --- | --- |
| `.Contracts` 2 件の発見（platform / knowledge） | 発見する |
| `.Contracts` で終わらないプロジェクト（`Knowledge.Support`） | 走査しない |
| `obj/` 配下の生成物 | 走査しない |
| `src/ai-stock-trading/.../Ast.Contracts` | **走査しない**（除外ユニット） |
| イベントのフィールド削除 | `memberRemoved:K.Events.Done.At` を **破壊的**として検出（1 件） |

**(2) 実ツリーへの一時注入（`scripts.repo.test.js` の子プロセステスト）**

| 操作 | 結果 |
| --- | --- |
| 契約に型を 1 件追加（`ContractCheckProbe.cs` を設置） | exit **1**（`typeAdded:...` と `--update` の案内）。撤去後 exit 0 |
| baseline 側に実在しないメンバーを足す（＝実ツリーとの差分が「削除」として現れる） | exit **1**（`後方互換を壊す契約変更が 1 件あります` ＋ `key` ＋ allowlist への案内） |
| 上記に承認エントリを与える | exit **1**（`--update` を促す）。`::warning::承認済みの破壊的な契約変更が 1 件あります` が stdout へ、`### 契約: 承認済みの破壊的変更` の表が `$GITHUB_STEP_SUMMARY` へ出る |
| 承認だけ残して baseline を戻す（stale） | exit **1**（`対応する変更が無い承認が 1 件残っています`） |
| すべて復元 | exit **0** |

一時ファイル・退避した JSON は `finally` で必ず復元し、`git status` がクリーンであることを確認している。
実ツリーの契約ファイル（`.cs`）は書き換えず、**新規ファイルの設置と撤去**で行う（テストが異常終了
しても既存の契約を壊さないため）。

**(3) 手動での承認フロー全体（実ファイルを一時的に編集して確認・復元済み）**

`Knowledge.Contracts.Events.IngestionCompleted` から `int ChunkCount` を削除した状態で:

1. 素実行 → exit 1（`memberRemoved` ＋ `memberReordered` の 2 件を報告）。
2. `--update` → **拒否**（`未承認の破壊的な契約変更が 2 件あるため baseline を更新しません`）／exit 1。
3. allowlist へ承認 2 件を記入 → 素実行は exit 1（`--update` を促す）＋ 承認済みの warn。
4. `--update` → exit 0。`$acceptedBreakingChanges` に承認 2 件が移り、allowlist は空へ戻る。
5. 素実行 → exit 0。
6. すべて復元し `git status` がクリーンであることを確認。

## 影響・リスク

- **非破壊の追加でも一度 CI が赤くなる**。意図的な設計（契約変更を必ず PR の diff に載せる）だが、
  初見では戸惑う。失敗出力に `--update` の手順を出し、`scripts/README.md` に節を設けて緩和する。
- **偽陽性のコストが小さい設計にしてある**。逃げ道（承認 1 行 ＋ `--update`）があるため、分類を
  破壊的側へ寄せている（属性変更・既定値なし追加・並べ替え）。逃げ道の無いゲートなら偽陽性は
  致命的だが、本設計では「疑わしきは止める」が正しい側である。
- **baseline は 1865 行**あり、人が通読する場面は少ない。読むのは**差分**である。
- **構文解析の限界**は上記「限界」のとおり。現時点で顕在化する要素は 0 件だが、`partial` や
  ソースジェネレータが入れば盲点になる。IADR-0122 のフォローアップで監視する。

## 教訓

- **docs 先行コミットから後続コミットの成果物へ張る前方リンクは、バッククォート表記が安全である**（squash マージ前提なら統合ブランチ上では解決するが、中間コミット単体では破損する。未マージ成果物への前方参照の扱いは [`docs/DEFINITION_OF_DONE.md`](../DEFINITION_OF_DONE.md) と
  [テスト戦略](../tests/TEST_STRATEGY.md) の同項と同じ作法）。

## フォローアップ（本作業では行わない）

- **east-west が gRPC へ移行した時点で proto を正本へ切り替える**
  （[ADR-0029](../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md)）。
  そのとき本検査はメッセージング契約と proto 未対応の面に縮退させ、新 IADR で改定する。
- **共通エンベロープの繰延解除条件の充足監視**（[IADR-0049](../adr/IADR-0049_composability-standards-phased-adoption.md) 決定 1）。
  解除時は本検査の対象へエンベロープを加える（型が増えるだけなので機構の変更は要らない見込み）。
- **`JsonSerializerOptions` / Wolverine のメッセージ型解決規約の変更検査**。契約ソースに現れないため
  本検査では見えない。必要になった時点で別 issue とする。
- **`Platform.Shared.Kernel`**（[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)）が
  `Result` / `Error` を持つようになったら、契約に載る型かを判断して走査対象を見直す。
