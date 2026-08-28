---
title: 作業仕様書 — MCP サーバー統合の再実装（宣言的公開構成・動的ツール連携・サービスアカウント実行時の個人資料一律除外）
type: spec
status: done
related_ids: [FR-16, UC-08, UC-09, SC-12, ADR-0024, ADR-0034, ADR-0036, ADR-0054, ADR-0004, ADR-0018, ADR-0021, ADR-0030]
author: claude
created: 2026-08-23
updated: 2026-08-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0024_mcp-server-integration.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md
  - planning:projects/microservices-platform/06_technical/11_mcp-server-integration.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
---

# 作業仕様書: MCP サーバー統合の再実装（issue #445）

## 走査基準

| 対象 | ref |
| --- | --- |
| 実装 | `claude/implementation-repo-all-issues-hilvbs` = `d1bdeea`（**shallow clone**。`git log` / `git blame` は出典に使わない） |
| 計画 | 隣接クローン `../project-planning` = `b6c3cc0` |

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-16（MCP サーバー公開・動的ツール構成・ABAC スコープ内実行・文書単位の越境フィルタ）
- ユースケース（UC）: UC-08（外部 AI エージェント連携）／ UC-09（MCP クライアント登録・管理）
- 画面（SC）: SC-12（MCP クライアント登録管理。本作業ではバックエンド API のみ）
- 関連 ADR: ADR-0024（宣言的公開構成＋自己申告）／ ADR-0034 決定 9（サービスアカウント実行時の個人資料一律除外）／ ADR-0054（`doc_scope` 属性）／ ADR-0004（ABAC・deny-by-default）／ ADR-0018（コンポーザビリティ）
- 計画書: `06_technical/11_mcp-server-integration.md`（`draft`）／ `06_technical/08_data-egress-policy.md`

## 0. 着手前の実測（母集合の引き方と結果）

**規則 1〜3・5（誤りの側から引く／形を列挙する／拡張子で絞らない／軸を 1 本で終わらせない）に従い、6 軸で走査した。** 走査対象は `git ls-files` から `src/ai-stock-trading`（submodule・別プロジェクト）を除いた全追跡ファイルである（拡張子で絞っていない）。

| 軸 | 検索語 | 件数 | 内容 |
| --- | --- | ---: | --- |
| 1 | `mcp`（大文字小文字無視） | **49 ファイル** | **すべて Claude Code 自身の `.mcp.json`・GitHub MCP・ワークフロー設定・過去の作業仕様書**であり、製品としての MCP サーバーは 1 件も無い |
| 2 | ファイル名に `mcp` | 3 | `.mcp.json` / `IADR-0222`（`.mcp.json` の scope）/ 同件の作業仕様書。**いずれも開発環境側** |
| 3 | `ModelContextProtocol` / `McpServer` / `McpTool` / `mcp-server` を含む `.cs` / `.csproj` / `.slnx` | **0** | **C# の実装は 0 行**（軸 1 の 49 件に `.cs` / `.csproj` は 1 件も含まれない） |
| 4 | `internal/mcp-tools` | **0** | 自己申告エンドポイントは**どのサービスにも実装されていない** |
| 5 | `mcp-clients` / `McpClient` | **0** | クライアント登録管理も未実装 |
| 6 | `FR-16` | **6 ファイル** | 作業仕様書 3・別紙 1・検査器 2（レンジ表）。**実装・必須仕様書は 0 件** |

> 🔴 **数え直しの記録（規則 7・8）。** 着手時の 1 回目の走査は `head -40` で出力を切っており、軸 1 を
> 「38 ファイル」と誤って数えた。**切ったものを見たことにする**のは規則 7 が名指しで禁じている事故である。
> 生の出力で数え直した結果が上表の 49 件である。
>
> **測定の時点も明示する。** 数えたのは追跡下ファイル（`git ls-files`。`src/ai-stock-trading` を除く）であり、
> **本作業で追加した文書は未追跡のため母集合に入っていない**。ただし本作業は `src/Directory.Packages.props` と
> `src/platform/backend/backend.slnx` を**その場で書き換えている**ため、走査をいま実行すると軸 1 は 51 件・
> 軸 6 は 7 件を返す。**「51 → 自分の書き換え 2 件を引く → 49」「7 → 1 件を引く → 6」**が上表の値である。
> 並行して別 issue も同じ作業ツリーへ書いているため、追試の時点によっては数がさらに動く。

**結論: MCP 統合は完全に未着手である**（統括のトリアージ結果と一致することを自分で確認した）。

**除外したものと理由**:

| 除外 | 理由 |
| --- | --- |
| `src/ai-stock-trading/**` | submodule・別プロジェクトであり本リポジトリの規約が及ばない（`scripts/lib/excluded-units.js` と同じ扱い） |
| `src/node_modules/**`（軸 1 の初回走査で `zod` の `package.json` が 1 件ヒット） | 依存パッケージのメタデータ。追跡下でないため `git ls-files` 起点の走査では最初から母集合外 |
| `.mcp.json` ほか軸 1・2 の 49 ファイル | **Claude Code の開発環境設定**であり、製品の MCP サーバー（FR-16）とは別物。**名前が同じだけで射程が違う**ため本作業では触らない |

**個人資料の既存実装（軸 4 の副産物として `private-note` / `doc_scope` でも走査した。40 ファイル）のうち、本作業が従うべき先例は次の 1 件である。**

- `src/knowledge/backend/Services/WikiService/src/WikiService.Api/Composable/Steps/DocumentSyncConsumer.cs` —— 個人資料の判定を **`doc_scope == "private-note"` の集合帰属**で書き、否定（`!= "organization"`）で書かないことを 🔴 付きで明記している。本作業も**同じ向き**で書く（§3）。

## 1. これは「実装に閉じた判断」か

| 論点 | 判定 | 根拠 |
| --- | --- | --- |
| サービスアカウント実行で個人資料を除外するか | **計画が決定済み。実装は従うだけ** | ADR-0034 決定 9 ／ ADR-0024 2026-08-02 注記 ／ UC-08 例外フロー |
| 除外の適用範囲（探索系だけか、検索・取得系もか） | **計画が決定済み**（**全経路**） | `11_mcp-server-integration` §6「探索系ツールに限らず、検索・文書取得系にも同様に適用する」 |
| 個人資料の判定軸（属性キーと値） | **計画が決定済み** | ADR-0054 決定 1・2（`doc_scope` = `private-note` / `organization`） |
| 公開は許可リスト方式か | **計画が決定済み** | ADR-0024 §決定「既定は非公開（許可リスト方式）」 |
| 初期公開ツールの範囲 | **計画が決定済み** | `retrieval.*` / `document.*` ＋ Phase 4 のグラフ探索系。`ai.*`・`get_cluster_summary` は**公開しない** |
| ツール定義スキーマ・自己申告の具体形 | **実装の裁量**（計画が明示的に委任） | ADR-0024 §結果「ツール定義スキーマ・自己申告エンドポイント規約・C# SDK 選定の詳細は実装リポジトリで設計する」 |
| ツール応答の共通エンベロープ（文書単位フィルタの足場） | **実装の裁量** | 同上。ただし UC-08 基本フロー 5 が「**MCP サーバーが**文書単位に送信可否を判定し応答をフィルタする」と定めるため、**MCP サーバーが文書単位のメタデータを読める形が必須**である |
| `notifications/tools/list_changed` の配信 | **本作業では実装しない**（§8 残件） | 計画にはあるが、SDK の動的ハンドラ方式ではセッション横断の通知配線が別途要る。**未実装であることを記録に残す** |

## 2. 対象範囲

- **対象**: platform ユニットへ新サービス `McpServer` を追加する。宣言的公開構成（許可リスト）の読み込みと検証、サービス自己申告の集約、実効ツール一覧、MCP プロトコル面（`tools/list` / `tools/call`）、主体解決、**サービスアカウント実行時の個人資料一律除外**、文書単位の越境フィルタ（本文 → 参照リンク縮退）、MCP クライアント登録管理 API（SC-12 / UC-09）、監査ログ。
- **対象外**:
  - 各サービス側の `GET /internal/mcp-tools` の実装（`RetrievalService` / `DocumentService` / `GraphService` は**他 issue が作業中**であり触らない。本作業は**収集側**を実装し、収集元は構成で与える）
  - SC-12 のフロントエンド画面（`src/*/frontend/` は触らない）
  - Istio Ingress のルーティング・レート制限（`deploy/` は触らない）
  - Keycloak のクライアント登録そのもの（本サービスは**プラットフォーム側の有効・無効と ABAC 属性割当**を持つ）

## 3. 🔴 中核: サービスアカウント実行時の個人資料の一律除外

### 3.1 判定は集合帰属で書く

**除外は `doc_scope == "private-note"` で書く。`doc_scope != "organization"` で書いてはならない。** 理由は `DocumentSyncConsumer` と同一である —— `doc_scope` は 2026-08-22 新設で実データ 0 件、既存文書へ遡及付与しない方針（ADR-0054 §結果）であり、否定で書くと**属性を持たない組織文書がすべて除外側へ倒れる**。ADR-0036 D-04 が評価の性質を「集合帰属」と定めているのと同じ理由である。

**代償**: `doc_scope` を持たない個人資料があれば漏れる。ただし ADR-0054 決定 3 が `doc_scope` を必須属性とし、決定 5 がシステム投入経路の既定を `organization` と定めているため、**計画上そのような文書は生じない**。既存実装（WikiService）と向きを揃えることを優先する。

### 3.2 2 層で強制する（どちらか一方では足りない）

| 層 | 何をするか | これだけでは足りない理由 |
| --- | --- | --- |
| **要求側**（`ToolInvocationScope.ExcludePrivateNote`） | サービスアカウント実行時、下流サービスへ渡す実行スコープに除外制約を立てる | 下流の実装を信用することになる。下流は**他 issue が作業中**で、まだ 1 つも存在しない |
| **応答側**（`ServiceAccountDocumentFilter`） | 応答エンベロープの文書のうち `doc_scope == private-note` を落とし、**件数からも外す** | 要求側を無視した下流・将来の下流が返してきた場合に**ここで止まる**（fail-closed） |

**件数から外す**のは ADR-0034 決定 2・4（存在秘匿。件数自体がサイドチャネルになる）に従う。打ち切り件数は「**認可判定を通したあとの件数**」である。

### 3.3 全経路へ一律に適用する

適用点をツール種別で分岐させない。`ToolInvocationService` は**ツール名を見ずに**除外を適用する。`retrieval.*` / `document.*` / `graph.*` のいずれも同じ経路を通る（テストで 3 種を回す）。

## 4. 設計

```
McpServer.Api
├─ Foundation/Contracts/          ツール申告スキーマ・公開構成・応答エンベロープ・管理 API DTO
├─ Foundation/Domain/             McpClient（登録・有効/無効・ABAC 属性）・McpSubject（主体）
├─ Foundation/Persistence/        McpDbContext（クライアント登録）
├─ Foundation/Services/
│   ├─ ToolPublicationConfig      宣言的公開構成（許可リスト）の読み込みと**スキーマ検証**
│   ├─ ToolCatalog                自己申告 ∩ 許可リスト → 実効ツール一覧（既定は非公開）
│   ├─ McpSubjectResolver         トークン → 主体（有人 / サービスアカウント）
│   ├─ ServiceAccountDocumentFilter  🔴 個人資料の一律除外（応答側）
│   ├─ EgressPolicy               文書単位の送信可否 → 本文を落として参照リンクへ縮退
│   └─ ToolInvocationService      登録確認 → 公開確認 → スコープ → 呼び出し → フィルタ → 監査
├─ Foundation/Endpoints/          /mcp-clients（SC-12 / UC-09）・/mcp-tools（実効一覧）
└─ Composable/Mcp/                MCP プロトコル面（ListTools / CallTool ハンドラ）
```

- **SDK**: 公式 C# SDK `ModelContextProtocol.AspNetCore`（`11_mcp-server-integration` §前提が指定）。**動的ハンドラ**（`WithListToolsHandler` / `WithCallToolHandler`）を使い、ツールをコードへ固定しない（ADR-0024 §決定「コア改修不要の追従」）。
- **公開構成**: JSON（`Mcp:PublicationConfigPath`）。Git 管理・GitOps 適用の実体で、既定は**非公開**。検証項目は §5。
- **自己申告の集約**: `IToolDeclarationSource`（既定は HTTP で各サービスの `GET /internal/mcp-tools` を叩く）＋ 定期更新（既定 5 分）。**申告が無い公開宣言はドリフトとして警告**する（ADR-0024 §5）。

## 5. 公開構成のスキーマ検証（CI で弾く項目）

ADR-0024 §5 と 2026-08-02 注記が要求する検証を `ToolPublicationConfigValidator` に置き、**起動時に fail-fast**（検証を通らない構成は適用不可）とする。

1. 公開名の一意性
2. `egress_class` 必須
3. 🔴 **サービスアカウントの ABAC 属性割当に `doc_scope=private-note` を含めてはならない**（ADR-0024 2026-08-02 注記「個人資料を読ませる属性割当を構成上禁止し、CI のスキーマ検証で弾く」）。同じ検証を管理 API（属性割当）にも適用する。
4. `ai.*` および `get_cluster_summary` を公開構成に書けない（初期公開範囲の逸脱をここで止める）

## 6. 受け入れ基準

- [ ] 公開構成に列挙されたツールだけが `tools/list` に現れる（**既定は非公開**）。申告があっても構成に無ければ現れない
- [ ] 構成に無いツールの `tools/call` は**「不明なツール」として拒否**される（「権限が無い」と区別させない＝存在秘匿）
- [ ] サービスアカウント実行では、**検索・文書取得・グラフ探索のいずれの経路でも** `doc_scope=private-note` の文書が返らない。**所有者本人のサービスアカウントでも返らない**
- [ ] 同じ文書が**有人実行では返る**（陽性対照。除外が主体種別に依存することの証明）
- [ ] `doc_scope` を持たない文書はサービスアカウント実行でも返る（陽性対照。否定形で書いていないことの証明）
- [ ] 除外された文書は**件数にも含まれない**
- [ ] 越境不可（機密区分が高い）の文書は本文を返さず参照リンクのみになる
- [ ] 無効化したクライアントは**次の呼び出しから即座に**拒否される
- [ ] サービスアカウントへ `doc_scope=private-note` を割り当てる構成・API 要求は拒否される
- [ ] `ai.*` / `get_cluster_summary` は公開構成に書けない

## 7. テスト方針

- **否定形テスト（必須）**: サービスアカウント実行で個人資料が返らないこと。**陽性対照と対で置く** —— 対にしないと「全部落としている実装」と区別できない。
  - 陽性対照 1: 同じ文書が有人実行では返る
  - 陽性対照 2: `doc_scope` 無しの文書は返る
  - 陽性対照 3: `organization` の文書は返る
- **変異試験**: 除外を外す（`ExcludePrivateNote` を常に false にする／応答フィルタを素通しにする）と否定形テストが落ち、戻すと通ることを実測する。
- **経路の網羅**: `[Theory]` で `retrieval.search_documents` / `document.get_document` / `graph.traverse` を回す。
- **契約テスト**: 許可リスト外の露出が無いこと、無効化の即時反映。
- テストの直前コメントに起点 ID（`// FR-16, UC-08, ADR-0034 決定 9: …`）を書く（`check-test-traceability.js`）。

## 8. 計画書との差異・残件

| # | 内容 | 扱い |
| --- | --- | --- |
| 1 | `notifications/tools/list_changed` の配信 | **未実装**。カタログ更新の検知（バージョン変化）までは実装し、セッションへの通知配線は残件とする。環流ではなく実装側の残件である |
| 2 | 各サービスの `GET /internal/mcp-tools` | **他 issue の担当領域**（`RetrievalService` / `DocumentService` / `GraphService` は作業中）。本作業は収集側のみ |
| 3 | ツール応答の共通エンベロープ | 計画が実装へ委任した範囲。IADR へ記録する（仮番号 `IADR-0269`） |
| 4 | ツール申告スキーマの置き場所 | `Platform.Shared.Contracts` ではなく `McpServer.Api` 内に置く。理由は IADR 参照（**現時点で in-process の生成側が 1 つも無い**。生成側が実装された時点で共有契約へ昇格させる） |
| 5 | `docs/tests/FR-16_*.md`（テスト仕様書） | **本 PR では作成しない**。作成すると `scripts/test-spec-coverage-baseline.json` の更新が必要になり、`scripts/` は本作業の担当領域外である（並行作業と競合する）。残件として報告する |
| 6 | Istio のレート制限初期値・`/mcp` ルーティング | `deploy/` 配下であり担当領域外。残件 |

- 差異: **なし**（計画の決定に反する実装は無い）。上記はいずれも**射程外**または**残件**である。

## 8.5 変異試験の実測

**否定形テストは「落ちること」を確かめて初めて意味を持つ。** 5 つの変異で実測した（いずれも実施後に戻した）。

| 変異 | 落ちたテスト | 件数 |
| --- | --- | ---: |
| 応答側の除外を素通しにする（`if (true) return result;`） | 否定形 6 件 ＋ 経路網羅 3 件 | 9 |
| **判定を否定で書き換える**（`!= organization`） | **陽性対照「`doc_scope` を持たない文書は返る」** ＋ 属性割当の拒否 | 2 |
| 要求側の除外制約を常に `false` にする | 「下流へ除外制約を渡す」 | 1 |
| 許可リストを無視して申告をすべて公開する | カタログの契約テスト | 6 |
| 無効化の判定を外す | 「無効化したクライアントは即座に拒否される」 | 1 |

**2 番目が要点である。** 否定形テストは 1 件も落ちず、**陽性対照だけが落ちた** —— 集合帰属で書いたか
否定で書いたかを分けられるのは陽性対照だけである、という §3.1 の主張が実測で裏付けられた。

変異をすべて戻したあとの実測は **57 件すべて成功**である。

## 9. 未決事項

- なし（着手を止める論点は無い）。§8 の残件はいずれも本作業の受け入れ基準を損なわない。
