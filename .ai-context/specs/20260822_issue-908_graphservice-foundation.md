---
title: GraphService 新設 — スキーマ・ホップごと ABAC 骨格・型ゲート・単一ノード読み取り
type: spec
status: draft
related_ids: [FR-17, UC-10, ADR-0033, ADR-0034, ADR-0004, ADR-0036, IADR-0238]
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
---

# 仕様書: GraphService 新設（#908 / 親 #450 子1）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-17**（文書間リンクの保持・探索。バックリンクを含む。型と出所を区別。探索は ABAC スコープ内に限定し**判定はホップごと**。閲覧権のない文書への辺は件数・匿名ノードを含め一切返さない）
- ユースケース（UC）: **UC-10**（関係を辿って根拠に到達する。hops 既定 2 / 上限 3）
- 画面（SC）: SC-18 / SC-21 は**本作業の対象外**（ADR-0039 が `Proposed`）
- 関連 ADR: **ADR-0033**（データモデルと格納先）・**ADR-0034**（探索時 ABAC 強制方式）・ADR-0004（Keycloak＋ABAC）・ADR-0036（所有者ベース裁量制御）
- 実装 ADR: **IADR-0238**（暫定番号。マージ直前に develop の最大＋1 へ付け替える）
- 計画書リンク: GitHub 上の `endazon/project-planning` `projects/microservices-platform/07_adr/` を直接参照する（本リポジトリは planning に依存しない。ADR-0048 決定 2）

### ゲートの実測（2026-08-22）

planning リポジトリ main から直接取得した状態欄:

| ADR | 状態 |
| --- | --- |
| ADR-0033 / ADR-0034 / ADR-0036 / ADR-0037 | `Accepted` |
| **ADR-0039**（SC-18 描画ライブラリ） | **`Proposed`** |

ADR-0033 / ADR-0034 の「着手可否の注記」は**保留理由の解消（2026-08-07）を記録するのみ**で、バックエンドに対する制限を残していない。よって本作業（バックエンド）は着手可。**SC-18 / SC-21 の画面は ADR-0039 が `Accepted` になるまで着手しない**（IADR-0179 決定 2「無いことは実装側で作ってよい、ではない」）。

## 目的・背景

FR-17 / FR-18 を実現する GraphService は**完全な新規実装**である（実測: `find src/ -iname '*Graph*'` のヒットは `WikiJsGraphQlClient` とそのテストの 2 件のみで、いずれも Wiki.js と話す GraphQL クライアント＝無関係。`Services/` 直下の実在 11 サービスに GraphService は無い）。

本作業は XL である #450 を 1 issue = 1 PR で着地させるために割った 11 単位のうちの**第 1 単位**であり、以降のすべての単位が依存する土台（スキーマ）と、**後付けにすると情報漏れの経路になる認可の骨格**を同時に置く。

## 対象範囲

### 対象

1. `src/knowledge/backend/Services/GraphService/` の新設（`Foundation/{Endpoints,Domain,Persistence,Ports,Services}`。空の `Composable/` は作らない）
2. 初回マイグレーション: `graph_documents` / `edge_types` / `edges` の 3 表と索引・制約、コア 5 種＋推奨 4 種の型 seed
3. **ホップごと ABAC の骨格**（後続へ先送りしない。理由は §設計 2）
   - `GraphAccessResolver`（deny-closed なスコープ解決）
   - `AbacNodeFilter`（既存 `AbacPageFilter` と意味論一致の述語）
   - **`AuthorizedNode` 型ゲート**（構築経路を述語に限定する）
   - **`AuthorizedGraphView.Seal` 出力ゲート**（未フィルタ結果の直列化を不可能にする）
4. ホップ 0 の読み取り API `GET /graph/{docId}`（起点ノード 1 件。非許可・欠落・不存在をすべて同一の 404 に倒す）

### 対象外

| 対象外 | 送り先 |
| --- | --- |
| 多ホップ BFS 探索・上限 200/500・ホップ超過の 400 | #909 |
| 辺の型辞書 CRUD（改名・削除ガード・使用件数） | #910 |
| `DocumentUpdated` 購読による属性同期 | #911 |
| Obsidian リンク抽出・差分更新 | #912 |
| 利用者作成の辺 API | #913 |
| AI 提案（`ai_suggestions` 表を含む） | #914 / #915 |
| RAG 統合・BFF 公開 | #916 |
| SC-18 / SC-21 の画面 | #917 / #918（ADR-0039 待ち） |

## 設計

### 1. なぜ本単位を第 1 にするか

1. 全単位がスキーマに依存する（唯一の真の前提）
2. **認可の骨格を最初の読み取り経路と同時に入れることで「未フィルタの結果が存在した歴史」自体を作らない**
3. **イベント配線を本単位から外せる。** ADR-0033 決定 2（属性の非正規化保持）が唯一のメッセージング接点であり、#911 に隔離すれば本単位は Wolverine 移行チェーンと一切干渉しない
4. 新規テストプロジェクト税は 1 回だけ発生し、以後の単位は同 PJ にテストを足すだけで済む

### 2. ホップごと ABAC —— 「探索してから濾す」を型で禁じる

#### 2.1 問題

本リポジトリの認可判定 API は `POST /authz/scope` **1 本のみ**で、返るのは**フィルタ集合**（`AccessScopeResponse`）であって資源ごとの可否ではない。実測:

- `AccessScopeRequest(string UserId, Dictionary<string,string> UserAttributes)` に **Action フィールドは無い**（`Platform.Shared.Contracts/Dtos/AccessScopeDto.cs`）
- エンドポイント側で `PolicyAction.Read` が**ハードコード**されている
- **バッチ判定 API は存在しない。キャッシュも存在しない**

リポジトリ共通の作法は「スコープを 1 回解決 → 資源ごとにローカル述語（`AbacPageFilter.Matches`）」である。**これを素朴に使うと、まさに ADR-0034 決定 1 が禁じる「終端でまとめてフィルタする」形になる。**

#### 2.2 判定 —— フィルタ集合方式は決定 1 に忠実か

**忠実である。** 論拠:

- 本プラットフォームにおける認可オラクルの定義そのものが「解決済みスコープ ＋ ローカル述語」である。`AbacPageFilter` のヘッダコメントが検索側 `AbacEvaluator` との意味論一致を宣言しており、仮に資源ごとのオラクル API が存在してもその内部は同じ述語評価になる。したがって**述語評価 1 回 ＝ 1 資源への認可判定 1 回**であり、「ホップごと判定」とは *HTTP 呼び出しの回数*の話ではなく、***探索のどの時点で判定を適用するか***の話である
- 決定 1 が禁じている形の害は 2 点に集約される:
  - **(a) 橋**: 非許可ノード X を経由して `A→X→B` の経路で B が浮上する（B への全許可経路が無くても）
  - **(b) 計数の漏れ**: 上限（200/500）や件数がフィルタ前の母集合で計算され、存在が漏れる（決定 4 違反）
- **prune-before-expand** では非許可ノードはフロンティアに一度も入らないため、その接続辺は展開されない。よって B が現れるのは「全ノードが許可された経路がホップ予算内に存在する」ときに限られ、資源ごとのオラクルを各ホップで呼ぶ実装と**観測上区別できない**

#### 2.3 型ゲート（本単位の中核）

出力ゲートだけでは足りない。**`Seal` 経由でしか応答を作れないという制約は「未フィルタが外に出ない」ことは保証するが、「濾したのがホップごとである」ことは保証しない**——全ホップ展開してから `Seal` に渡す実装が書けてしまい、それがまさに禁じられた形である。したがって 2 段構えにする。

**ゲート 1: `AuthorizedNode`（展開の入口を塞ぐ）**

```csharp
// private ctor。構築経路は Authorize ただ 1 つ。
public sealed class AuthorizedNode
{
    public GraphDocument Node { get; }
    private AuthorizedNode(GraphDocument node) => Node = node;
    internal static AuthorizedNode? Authorize(GraphDocument node, AccessScopeResponse scope)
        => AbacNodeFilter.Matches(node, scope) ? new AuthorizedNode(node) : null;
}
```

探索の接続辺ロードは `IReadOnlyList<AuthorizedNode>` しか受け取らない。**結果として、非許可ノードから展開することが型として書けない。**「探索してから濾す」形は表現不能であり、橋は構造的に成立しない。

**ゲート 2: `AuthorizedGraphView.Seal`（出力を塞ぐ）**

探索・永続化層は `internal` な `UnfilteredSubgraph` しか返さず、応答 DTO は `Seal(UnfilteredSubgraph, AccessScopeResponse)` からしか構築できない（`internal` コンストラクタ）。**未フィルタ結果はコンパイルが通らない。**

**なぜ検査スクリプトにしないか**: 本リポジトリの運用規約は「検査器・規約の追加は**同型の事故が 2 回起きたら**」であり、現在 0 回である。加えて型ゲートはビルド時に回避不能であり、文字列検査より強く安い。代わりにアーキテクチャテスト（構築経路の単一性をリフレクションで固定）を 1 本添える。

#### 2.4 述語の意味論（既存と一致させる）

`AbacNodeFilter.Matches` は `AbacPageFilter.Matches` と**同一意味論**とし、一致をテストで固定する:

- `Granted=false` → deny-by-default（何も可視でない）
- `AllowedFilters` が空 かつ `Granted=true` → 条件無しで全許可
- フィルタ間は **AND**、値集合内は **OR**、比較は `OrdinalIgnoreCase`
- **属性キーを持たないノードは不一致**（欠落は安全側に倒す＝fail-closed）

#### 2.5 存在秘匿

**非許可・属性レコード欠落・文書不存在をすべて同一の 404 にする。** 403 と 404 を打ち分けると存在が漏れる。ADR-0034 は「利用者がリンク切れと権限不足を区別できないこと」を**受け入れ済みの副作用**として明記しており、本実装はその線に従う。応答本文・ヘッダ・応答時間の差分でも区別できないことを否定形テストで固定する。

### 3. スキーマ

詳細（列・索引・制約・トレードオフ）は **データ仕様書 `docs/data/knowledge-graph.md`** と **IADR-0238** に置く。本単位で作るのは `graph_documents` / `edge_types` / `edges` の 3 表である（`ai_suggestions` は #914）。

要点のみ:

- **`edge_types` はサロゲートキー**。改名は 1 行 UPDATE で既存辺が追随し、削除は `ON DELETE RESTRICT` が防ぐ（ADR-0033 決定 9）。コード enum / DB enum は使わない（決定 3: 型の値集合は SC-09 で管理する）
- **新しい辺の型の追加は `edge_types` への INSERT のみ＝マイグレーション不要**
- `edges.source_anchor` / `target_anchor` を NULL 予約（決定 5 の Phase 3 チャンク粒度拡張）
- **デノーマライズ属性はノード表のみ**に置く（辺に複製すると属性変更 1 件が接続辺全行に増幅する）
- 対称型（`related`）は `(min, max)` に正規化して 1 行、バックリンクは `ix_edges_target` の逆引き（行を増やさない）

### 4. ストア選定

ADR-0033 の未決事項「GraphService が用いるストア製品」は**実測待ち**のままである。**本単位では PostgreSQL 隣接リストで進める**（EF Core + Npgsql + 起動時 `MigrateAsync` というプラットフォーム標準にそのまま乗る）。探索は `Foundation/Ports/IGraphStore` 越しに置き、交換可能に保つ。

**再訪トリガ**: ①実データ規模での探索 p95 が対話的利用に耐えない実測が出たとき、②計画側で hops>3 や全グラフ解析の要求が確定したとき。そのときの実測値を planning へ環流する。

## 受け入れ基準

- [ ] `dotnet build src/knowledge/backend/backend.slnx` が `Build succeeded` / 警告 0 / EXIT=0
- [ ] `dotnet test src/knowledge/backend/backend.slnx` が全件通過
- [ ] スコープ解決が失敗（非 2xx・通信例外）したとき `Granted=false` に縮退し、いかなるノードも返さない
- [ ] `AuthorizedNode` が `AbacNodeFilter.Authorize` 以外から構築できない（アーキテクチャテストで固定）
- [ ] 応答 DTO が `Seal` 以外から構築できない（アーキテクチャテストで固定）
- [ ] `AbacNodeFilter` と `AbacPageFilter` の意味論一致（deny-by-default / AND・OR / 属性欠落 fail-closed / 条件無し全許可 の 4 系統）
- [ ] 非許可・欠落・不存在がすべて同一の 404（本文・ヘッダで区別できない）
- [ ] `edge_types` の改名で既存 `edges` が 0 行も変化しない
- [ ] コア 5 種＋推奨 4 種が seed されている
- [ ] **現状の有効な認可軸が `confidentiality` のみであることを固定するテスト**（§未決事項 3）

## テスト方針

| 受け入れ基準 | テスト |
| --- | --- |
| deny-closed 縮退 | `GraphAccessResolverTests`（認可サービスが 500 / 接続拒否 / タイムアウトの 3 系統） |
| 述語の意味論一致 | `AbacNodeFilterTests`（`AbacPageFilterTests` と同型のケース群） |
| 型ゲート | `GraphTypeGateArchitectureTests`（リフレクションで public/internal 構築経路の単一性を assert） |
| 存在秘匿 | `GraphEndpointsSecrecyTests`（非許可 / 欠落 / 不存在の 3 系統が同一応答） |
| 改名追随 | `EdgeTypeRenameTests`（改名前後で `edges` 行の全列が不変） |
| 未強制の明示 | `AbacUnenforcedAxisTests`（`owner` だけ異なる 2 文書が区別されないことを assert。テスト名に未強制である旨を書く） |

**変異試験**: 型ゲートとアーキテクチャテストは、それが実際に落ちることを変異で確かめる。手順は「①変異を入れる ②`git diff` で当該箇所のみ変化したことを読む ③`dotnet build` が `Build succeeded` EXIT=0 であることを読む ④その後にテスト結果を読む」。**ビルドが落ちる変異はテストの検出力を何も示さない**ため、変異はビルドが通る形（例: `Authorize` の可視性を public に上げる／`Seal` を経ない構築経路を足す）で入れる。

## 計画書との差異

- 差異: **あり**。

1. **ADR-0034 決定 6・8・9（個人資料の境界）は本実装では強制できない。** `owner` は実データ 0% 充足で、3 分岐 OR は `AccessScopeResponse` に表現構造が無く、`/authz/scope` は `PolicyAction.Read` 固定である。**未強制であることを仕様・コードコメント・テスト名に明記する**（§未決事項 3）。planning 側の記録（`06_technical/07_abac-attribute-model.md` の 2026-08-15 追記）と #516 に既出であり、新規の環流は起こさない
2. **ADR-0033 未決事項「ストア製品」は未決のまま進める**（§設計 4）。実測後に環流する
3. **`docs/specs/` は存在しない。** #450 本文は「着手前に `docs/specs/` を作成する」と書いているが、`7b6a234`(#872) で `.ai-context/` へ移設済みであり `docs/specs/` は 0 件である。作業仕様書は `.ai-context/specs/`、データ仕様書は `docs/data/`、通信仕様書は `docs/api/` に置く（`docs/README.md` が正本）
4. **画面仕様書（screen）は作らない。** ADR-0039 が `Proposed` であり、SC-18 / SC-21 に着手しないため

## 未決事項

1. **辺の型辞書の所在** —— **解消した。** GraphService が所有する。IADR-0152 決定 1 がタグ辞書を DocumentService に置いた理由は「使用件数が文書の局所クエリになるため。サービスを跨ぐと削除拒否の判定のたびに同期呼び出しが要り、数え落としが『消してはいけないタグを消せる』事故になる」であり、**同じ論法で辺の使用件数は GraphService の局所クエリである**。SC-09 の UI が 2 サービスの 2 辞書を編集する形になるが、これは齟齬ではなく一貫
2. **リンク解決規則**（相対パス → 文書 ID・外部 URL の扱い・タイトル一致による解決の可否） —— ADR-0033 が実装側へ送った未決事項。**#912 で決めて IADR に残す**（本単位の対象外）
3. **個人資料の境界が未強制であること** —— 上記「計画書との差異 1」。本単位では**固定テストで可視化する**にとどめ、解消は #516 系の是正に委ねる
4. **AI 提案生成の実行主体** —— #915 の着手前に裁定が要る（本単位の対象外）

## 運用上の制約（本 PR の出し方）

1. 🔴 **床に触るコミットは #900 の着地後に最後に足す。** `scripts/scripts.repo.test.js` のテストプロジェクト数 ratchet（16 → 17）と `src/coverage-floor.json` を触るコミットだけを分離し、#900 マージ後に積んで PR を出す。理由: #900 はレポート跨ぎの重複計上（＝新規テストプロジェクトで床が割れる原因そのもの）を直しており、先に床を置き直すと #900 の定義変更で旧定義の床になり二度手間になる。加えて床の実測は `integration.yml` の run からしか出ず、`workflow_dispatch` の API 起動は 403 で人手が 1 回挟まるため、その人手を 2 回に増やさない
2. **本単位はメッセージングに触らない。** 新規プロジェクトでの `using MassTransit;` は `check-backend-libraries.js` の残件 ratchet（13 件）に対し `added` と判定され即 fail する。本単位はイベントを扱わないため該当しない。#911 / #912 は Wolverine 側で書く
3. **IADR 番号は暫定 0238。** マージ直前に develop の最大＋1 へ付け直し、`.ai-context/adr/README.md` の索引行（昇順・欠番なし）も併せて更新する
4. **`backend.slnx` への登録**（src / tests の 2 csproj）を忘れない。未登録だと CI から不可視になる。挿入位置は `FeedbackService` と `IngestionService` の間（アルファベット順）
5. **並行セッションとの共有作業ツリー**に注意する。本作業の着手時点で `scripts/scripts.repo.test.js` / `WolverineExtensions.cs` / `src/Directory.Build.props` が他セッションにより変更中である。コミットは自分が作ったファイルのみを対象に、パスを明示して行う
