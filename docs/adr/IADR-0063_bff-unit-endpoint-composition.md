---
title: IADR-0063 BFF のユニット別エンドポイント合成方式とナレッジ DTO の分離（設計・実装は段階適用）
type: impl-adr
status: Proposed
related_ids:
  - FR-14
  - ADR-0018
  - IADR-0027
  - IADR-0056
  - IADR-0059
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
---

# IADR-0063: BFF のユニット別エンドポイント合成方式とナレッジ DTO の分離

- 状態: Proposed（合成方式の承認と段階実装をもって Accepted 化する）
- 日付: 2026-07-11
- 決定者: claude（実装・提案）

## 起点・関連

- 関連する計画書 ID: FR-14（構成変更のみで完結する疎結合ユニット）／ADR-0018（合成可能アーキテクチャ）
- 関連 ADR: [[IADR-0027]]（合成ルート概念）／[[IADR-0056]]（ユニット第一構成・依存方向）／[[IADR-0059]]（契約階層化・イベント分離済み）
- 関連仕様書: `docs/specs/20260711_issue-229-slice2_bff-composition-design.md`
- Issue: #229（フォローアップ 3・後続スライス。イベント契約分離は [[IADR-0059]] で完了）

## コンテキストと課題

[[IADR-0059]] で 6 イベント契約は `Knowledge.Contracts` へ分離済み。しかし **knowledge 固有の DTO と BFF 集約
エンドポイントが platform 内に同居**したままである。

- **BFF 集約エンドポイント**: `Platform.Bff/Foundation/Endpoints/` に 9 モジュール
  （`Search` / `Document` / `Analysis` / `Feedback` / `Dashboard` / `Conversion` / `DataSource`＝ナレッジ固有、
  `Config` / `Authz`＝platform 固有）。`Program.cs` が `app.MapXxxBffEndpoints()` を**ハードコードで 9 回**呼ぶ。
  各モジュールは名前付き `HttpClient`（下流サービス URL）＋共有 DTO でナレッジ集約（ABAC スコープ解決→下流呼び出し）
  を実装する。
- **DTO**: `Platform.Shared.Contracts/Dtos/` に 14 DTO。うち `DocumentDto` / `SearchDto` / `ConversionJobDto` /
  `DataSourceDto` / `DashboardDto` / `FeedbackDto` / `AnalysisDto` / `ChunkDto` / `SearchResultDto` はナレッジ固有。
  `AbacManagementDto` / `AccessScopeDto` / `ConfigInfoDto` / `CompletionDto` / `EmbedDto` は platform 横断寄り。

**問題**: 可変機能ユニットを追加すると、そのユニットの BFF エンドポイントを **platform の `Program.cs` と
`Foundation/Endpoints/` に手で追加**する必要があり、FR-14 の「構成変更のみで完結」に反する。DTO を
`Knowledge.Contracts` へ移すにも、BFF（platform）がそれらを参照しているため **platform→可変ユニットの依存禁止**
（[[IADR-0056]]）に抵触する（鶏卵）。BFF の合成手段（[[IADR-0027]] の合成ルート概念の BFF 版）が未整備。

## 検討した選択肢

### A. ビルド時合成点（フロント `features/index.ts` パターンの BFF 版）— **推奨**
- knowledge の BFF エンドポイントモジュール＋ナレッジ固有 DTO を **knowledge ユニット**（例: `knowledge/backend/Bff/`）へ移す。
- platform BFF に**合成点を 1 箇所**設け（例: `Bff/Composition/UnitEndpoints.cs`）、各ユニットの
  `IBffEndpointModule`（Shared に定義する抽象）を列挙して `Map` する。合成点のみ可変ユニットの BFF プロジェクトを参照する。
- 依存規則（[[IADR-0056]] / [[IADR-0057]]）に **BFF 合成点の例外**を追加する（フロントの合成点
  `platform/frontend/src/features/index.ts` と同型の「例外3」）。追加ユニットは合成点へ 1 行 import で組み込める。
- 長所: フロントの既存パターンと一貫。型安全・既存のナレッジ集約ロジック（ABAC）をそのまま保持。
- 短所: platform BFF のビルドが knowledge の BFF プロジェクトに（合成点で）依存する（フロントの SPA が
  `@knowledge` に依存するのと同型）。submodule ユニットは BFF ビルド時に当該プロジェクトの取得が要る。

### B. ランタイムプラグイン（アセンブリ読込＋リフレクション DI）
- platform BFF が起動時に `IBffEndpointModule` 実装を（設定で指定した）アセンブリからリフレクションで探索・登録。
  コンパイル時依存なし。
- 長所: platform→可変ユニットのビルド依存が皆無。
- 短所: アセンブリ読込・型探索・DI 配線の複雑さ、デプロイ（BFF ランタイムへ当該アセンブリ配置）と
  可観測性・起動時検証の追加コスト。既存の宣言的構成（pipeline.json）とは別系統の探索機構が増える。

### C. 宣言的汎用プロキシ
- knowledge が**ルート記述子**（route→下流サービスのマッピング）を宣言的構成（pipeline.json 類似）で提供し、
  BFF は型を持たず汎用プロキシする。
- 長所: DTO 結合が消える。
- 短所: **ナレッジ固有の ABAC 集約ロジック**（スコープ解決→下流→フィルタ、存在秘匿 404）が BFF から失われる。
  これらは knowledge ドメインの認可判断で、汎用プロキシでは表現できない。型安全・OpenAPI の質も落ちる。

## 決定（提案）

**選択肢 A（ビルド時合成点）を推奨**する。フロントエンドの合成点パターン（[[IADR-0056]] 例外2）と一貫し、
型安全とナレッジ集約ロジックを保ちつつ、可変ユニットの BFF 拡張を「合成点 1 行」に閉じる。

**DTO 階層化**: ナレッジ固有 DTO は `Knowledge.Contracts/Dtos/` へ移し、platform 横断 DTO
（`AbacManagementDto` / `AccessScopeDto` / `ConfigInfoDto` / `CompletionDto` / `EmbedDto`＝認可・構成・LLM 横断）は
`Platform.Shared.Contracts` に残す。

**依存規則の拡張**: 「例外3: BFF の合成点（`Platform.Bff/Composition/`）のみ可変ユニットの BFF エンドポイント
プロジェクトを参照してよい」を `src/README.md` へ追記し、`check-unit-dependencies.js` に合成点例外を実装する。

## 段階実装（本 IADR 承認後）

1. **合成点の器**（非破壊）: `IBffEndpointModule`（Shared 抽象）を定義し、platform BFF の既存 9 モジュールを
   これに適合させて `Program.cs` の 9 ハードコードを列挙ループへ置換（挙動不変・回帰テストで固定）。
2. **DTO 分離**: ナレッジ固有 DTO を `Knowledge.Contracts/Dtos/` へ移設（BFF はまだ参照するため、この時点では
   合成点の例外を先に用意）。
3. **BFF エンドポイント移設**: ナレッジ集約モジュールを knowledge ユニット（`knowledge/backend/Bff/`）へ移し、
   platform BFF の合成点から列挙。依存規則の例外3＋`check-unit-dependencies.js` 更新。
4. **検証**: BFF テスト・契約テスト・依存検査・OpenAPI 再生成が緑。追加ユニットのサンプルで「合成点 1 行」拡張を確認（#230 と連携）。

## 理由

- **一貫性**: フロントは既に合成点（`features/index.ts`）で可変ユニットを 1 行合成する。BFF も同型にすることで
  「ユニット追加＝合成点 1 行」を backend/frontend で揃える。
- **ドメインロジック保持**: ナレッジ固有の ABAC 集約（存在秘匿・スコープ交差）は knowledge の認可判断であり、
  型付きエンドポイントとして knowledge に置くのが正しい所在。汎用プロキシ（C）では失われる。
- **段階・低リスク**: BFF は全フロントの唯一の入口のため、器（1）→DTO（2）→移設（3）と段階適用し各段で緑を確認する。

## 状態が Proposed の理由・フォローアップ

- BFF は可用性上のクリティカルパスであり、エンドポイント移設は大きな変更のため、**合成方式（選択肢 A）の承認**を
  もって段階実装に入る。実装は本 IADR の「段階実装」に沿って別 PR（スライス）で行う。
- 依存規則の例外3 追加は [[IADR-0056]] / [[IADR-0057]] の追補となる。
- 追加ユニットの通し確認は #230（submodule 運用）のサンプルユニットと連携する。

## 関連

- Supersedes: なし
- Superseded by: なし
