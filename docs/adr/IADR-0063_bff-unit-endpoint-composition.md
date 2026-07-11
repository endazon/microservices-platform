---
title: IADR-0063 BFF のユニット別エンドポイント合成方式とナレッジ DTO の分離（設計・実装は段階適用）
type: impl-adr
status: Accepted
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

- 状態: Accepted（2026-07-11 合成方式 A＝ビルド時合成点で承認。段階実装は本 IADR の計画に沿って別 PR スライスで実施）
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
- **DTO**: `Platform.Shared.Contracts/Dtos/` は 14 **ファイル**（1 ファイルに複数 `record` 型が同居する場合がある。
  例: `AbacManagementDto.cs` は `AbacPolicyDto` / `AttributeDefinitionDto` 等を格納）。ファイル分類ではナレッジ固有が
  `DocumentDto` / `SearchDto` / `ConversionJobDto` / `DataSourceDto` / `DashboardDto` / `FeedbackDto` / `AnalysisDto` /
  `ChunkDto` / `SearchResultDto`、platform 横断寄りが `AbacManagementDto` / `AccessScopeDto` / `ConfigInfoDto` /
  `CompletionDto` / `EmbedDto`。**実装スライスでの移設時は型単位での再精査が必要**（ファイル単位分類は現状精査の目安）。

**問題**: 可変機能ユニットを追加すると、そのユニットの BFF エンドポイントを **platform の `Program.cs` と
`Foundation/Endpoints/` に手で追加**する必要があり、ユニット追加のみでの拡張ができない。DTO を
`Knowledge.Contracts` へ移すにも、BFF（platform）がそれらを参照しているため **platform→可変ユニットの依存禁止**
（[[IADR-0056]]）に抵触する（鶏卵）。BFF の合成手段（[[IADR-0027]] の合成ルート概念の BFF 版）が未整備。

> **ADR-0018 との関係**: ADR-0018 は「同期 API 依存（BFF→各サービス）は契約でバージョン管理し、**実行時の
> 構成による繋ぎかえの対象外**とする」と定める。本 IADR が扱うのは**コンパイル時の拡張点**（合成点への 1 行
> 追加でユニットの BFF エンドポイントを組み込む）であり、ADR-0018 が除外する「ランタイム構成による sync パスの
> 繋ぎかえ」ではない（＝ADR-0018 違反ではない）。フロントの合成点（[[IADR-0056]] 例外2）が同種の解釈で
> 「ユニット追加のみで拡張」を実現している前例と一致する。

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

> **型単位再精査の知見（slice-1 実施時に判明）**: DTO を型単位で精査した結果、**ナレッジ固有 DTO は事実上すべて
> BFF の集約エンドポイントから参照されている**（例: `SearchRequest`/`SearchResponse`・`AnalysisTaskRequest`・
> `DashboardUsageDto` 等が BFF から参照。`ChunkDto` は全域未参照）。したがって **DTO 分離を BFF エンドポイント
> 移設と独立に行うことはできない**（DTO を先に knowledge へ移すと BFF＝platform が可変ユニットを参照し依存禁止に
> 抵触する。鶏卵が型レベルで確定）。よって当初の「DTO 分離→エンドポイント移設」の二段を、**ドメイン単位で
> エンドポイント＋その DTO を同時移設**する形へ改める。

1. **合成点の器**（非破壊・slice-1 で実施済み）: `IBffEndpointModule`＋合成点 `Bff/Composition/BffEndpointComposition`
   を導入し、`Program.cs` の 9 ハードコードを `MapComposedBffEndpoints()` の 1 行へ置換（挙動不変・回帰テストで固定）。
2. **依存規則の例外3 準備**: `src/README.md` に BFF 合成点の例外3 を追記し、`check-unit-dependencies.js` に実装。
3. **ドメイン単位移設（反復）**: ナレッジ 1 ドメインずつ、BFF エンドポイントモジュール＋当該ドメインの DTO を
   knowledge ユニット（`knowledge/backend/Bff/`・`Knowledge.Contracts/Dtos/`）へ**同時**移設し、platform BFF の
   合成点の登録簿を当該ユニットのモジュール参照へ差し替える。各ドメインで BFF テスト・依存検査が緑を確認する
   （レビュー可能な粒度＝ドメイン単位）。platform 横断 DTO（`AbacManagementDto`/`AccessScopeDto`/`ConfigInfoDto`/
   `CompletionDto`/`EmbedDto`）は `Platform.Shared.Contracts` に残す。
4. **検証**: 全ドメイン移設後、BFF テスト・契約テスト・依存検査・OpenAPI 再生成が緑。追加ユニットのサンプルで
   「合成点 1 行」拡張を確認（#230 と連携）。

## 理由

- **一貫性**: フロントは既に合成点（`features/index.ts`）で可変ユニットを 1 行合成する。BFF も同型にすることで
  「ユニット追加＝合成点 1 行」を backend/frontend で揃える。
- **ドメインロジック保持**: ナレッジ固有の ABAC 集約（存在秘匿・スコープ交差）は knowledge の認可判断であり、
  型付きエンドポイントとして knowledge に置くのが正しい所在。汎用プロキシ（C）では失われる。
- **段階・低リスク**: BFF は全フロントの唯一の入口のため、器（1）→例外3 準備（2）→ドメイン単位移設（3・反復）と
  段階適用し各段で緑を確認する。

## フォローアップ（段階実装）

- 合成方式（選択肢 A＝ビルド時合成点）は 2026-07-11 に承認済み。器（slice-1）を実施済み。以降は上記「段階実装」
  3（ドメイン単位移設）を**レビュー可能な粒度の別 PR スライス**で反復する（一度に全 9 エンドポイント・DTO を移設しない）。
- 依存規則の例外3 追加は [[IADR-0056]] / [[IADR-0057]] の追補となる（例外3 準備スライスで `check-unit-dependencies.js` に反映）。
- **テスト戦略**: 移設した BFF エンドポイントは、合成後の実挙動を `Platform.Bff.Tests`（合成点経由・全 DI 込み・
  移設 DTO は推移参照で解決）が担保する。単純なプロキシ集約（例: DataSource）はこれで十分。一方、knowledge 固有の
  非自明なロジック（特に `BffScopeResolver` の Shared 切り出しを伴う **Document / Search** の ABAC 集約）は、移設時に
  **knowledge 側の BFF エンドポイント単体テスト**（当該ロジックを直接検証）を併設することを検討する。切り出す共通
  ヘルパ（BffScopeResolver 等）は Shared へ置き、knowledge から参照可能にする。
- **`BffScopeResolver` の配置決定**（Document/Search 移設の基盤・spec `20260712_issue-229_extract-bff-scope-resolver`）:
  `IHttpClientFactory`・`HttpContext`・`AccessScope*`（Contracts）に依存するため、契約専用の `Platform.Shared.Contracts`
  ではなく ASP.NET Core と Contracts の双方を参照する **`Platform.Shared.Infrastructure.Foundation.Authz`** へ切り出す。
  純ロジック（`Matches`/`ExtractUserAttributes`）は `Platform.Bff.Tests` に単体テストを新設し、`ResolveAsync`（HTTP）は
  Document/Search の BFF エンドポイントテストが回帰保証する。
- 追加ユニットの通し確認は #230（submodule 運用）のサンプルユニットと連携する。

## 関連

- Supersedes: なし
- Superseded by: なし
