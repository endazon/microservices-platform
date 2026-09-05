---
title: 個人資料の露出 3 トグルを索引の生産側へ配線し、判定軸に doc_scope / owner / shared_with を載せる（#1184）
type: spec
status: done
related_ids: [FR-19, FR-21, UC-11, SC-19, SC-20, ADR-0036, ADR-0046, ADR-0054, ADR-0057, ADR-0061, IADR-0122, IADR-0253, IADR-0270, IADR-0278, IADR-0283, IADR-0296, IADR-0358, IADR-0388, IADR-0395]
author: Claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - "ADR-0061 決定 1〜6（露出 3 トグルのうち 1 つでも ON なら索引へ載せる／3 つとも OFF なら載せない／用途の別は文書属性で表す／ON → OFF は索引からの削除まで及ぶ／判定軸は doc_scope / owner / shared_with / confidentiality / 露出トグルの投影／confidentiality だけで判定してはならない）"
  - "FR-19（個人資料の露出 3 トグル・既定 OFF）"
  - "FR-21 受け入れ基準 ⑨（横断検索 ON・AI 入力 OFF の個人資料は検索結果に現れるが RAG 回答のコンテキストには含まれない）"
  - "ADR-0036 D-05・D-06（所有者ベース裁量制御と共有先）"
---

# #1184: 露出 3 トグルの索引生産側への配線

## 起点となる計画書（トレーサビリティ）

- 機能要求: `FR-19`（個人資料）／`FR-21` 受け入れ基準 ⑨
- ユースケース: `UC-11`
- 画面: `SC-19`（個人資料の一覧・容量）／`SC-20`（Obsidian 連携設定）
- 計画 ADR: `ADR-0061`（本件の裁定。planning#492）／`ADR-0036` D-05・D-06／`ADR-0054`／`ADR-0057` 決定 1
- 先行記録: `IADR-0270` 決定 5（発行しない）／`IADR-0283`（`ai_input` の写しと RAG 分離）／
  `IADR-0253`（認可スコープの選言・段 3 / 段 4）／`IADR-0358`・`IADR-0388`（索引の本文有無）

## 1. 母集合（自分で走査した。issue の数えは転記していない）

走査は本作業ブランチ（`origin/develop` = `facebfe9`。`git rev-parse --is-shallow-repository` → `false`）で行った。

### 1-1. 露出トグルが**宣言**される場所

```console
$ grep -rn "IncludeInSearch\|IncludeInGraph\|IncludeInAi" src --include=*.cs --include=*.ts --include=*.tsx \
    | grep -v "/Tests/" | grep -v "\.test\."
```

| 層 | 箇所 | 備考 |
| --- | --- | --- |
| 台帳（真実源） | `DocumentService/Domain/PrivateNote.cs`（3 プロパティ＋`SetExposure`） | 既定は `bool` の既定値＝ OFF |
| 永続化 | `Migrations/20260822212832_AddPrivateNotes*`＋以降の Designer / Snapshot 4 本 | 列は既に在る（本作業でマイグレーションは増やさない） |
| 契約 | `Knowledge.Contracts/Dtos/PrivateNoteDto.cs`（`PrivateNoteDto` / `UpdateExposureRequest`） | BFF が同じ形を配る |
| 書き込み経路 | `Features/PrivateNotes/SetExposure/Endpoint.cs` | 台帳を更新し、**`ai_input` だけ**を文書属性へ写す |
| 読み出し | `Features/PrivateNotes/PrivateNoteEndpoints.ToDto` | 画面へ配るだけ |
| 画面 | **在る** | `sc19-private-notes/components/PrivateNotesPage.tsx` が 3 つのチェックを持ち、`PUT /bff/private-notes/{id}/exposure` を呼ぶ |

🔴 **ここは 1 度数え違えた。** 最初に `IncludeInSearch`（PascalCase）で全体を走査して .ts/.tsx が
0 件だったため「画面は無い」と書きかけた。**フロントは camelCase（`includeInSearch`）である。**
陽性対照（.cs では当たる）を「フロントにも当たるはず」と読み替えてしまった誤りで、
**綴りの揺れを跨ぐときは、当たる側の綴りでもう一度引き直す**（規則 10）。
**含意は小さくない** —— 画面が在る以上、本作業の配線は着地と同時に利用者の手に届く。

### 1-2. 露出トグルが**属性へ写る**場所（陽性対照つき）

```console
$ grep -rnE '"(ai_input|search_exposure|graph_exposure|search_input|graph_input|include_in_search|include_in_graph)"' \
    src --include=*.cs --include=*.ts --include=*.tsx | grep -v "/Tests/"
src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/AiInputExposure.cs:46:    public const string AttributeKey = "ai_input";
```

**陽性対照（`ai_input`）が 1 件当たっているので、他の 6 綴りの 0 件は「無い」である。**
横断検索・グラフの 2 トグルに対応する文書属性キーは 1 つも無い。

### 1-3. 露出トグルが**読まれる**場所（消費側）

| 消費面 | 判定 | 実体 |
| --- | --- | --- |
| RAG 文脈（AiAnalysis） | `ai_input` を読む（**ここだけ配線済み**） | `RagOrchestrator.SearchAsync` → `RagContextPolicy.Select(results, AiInputExposure.IsAllowed)` |
| 横断検索（Retrieval） | **読まない** | `HybridSearchService` は ABAC スコープ（`ScopeFilter`）だけを見る |
| グラフ（Graph） | **読まない** | `AuthorizedGraphView.Seal` は ABAC と `doc_scope`（描き分け）だけ |
| Wiki | 個人資料を集合帰属で除外済み（`DocumentSyncConsumer`。露出とは無関係） | 変更しない |

### 1-4. `shared_with`（判定軸の第 3 節）

```console
$ grep -rn "shared_with" src | grep -v "/Tests/"
src/knowledge/backend/Services/DocumentService/Features/Documents/DocumentShareEndpoints.cs:15  ← 散文のみ
```

共有先の実体は `DocumentShare`（文書 ID × 被共有主体。`IADR-0253` 決定 4）であり、
**属性辞書にも索引ペイロードにも載っていない**。`DocumentShareEndpoints.cs:28-29` が
「共有先ベースの分岐を認可スコープへ載せる配線は別段とする」と明記しており、**本件がその段である。**

一方で選言（分岐）の器は既に全段揃っている ——
`AccessScopeBranch` / `ScopeFilter.Branches` / `InMemoryVectorStore.MatchesFilters` /
`QdrantVectorStore.BuildBranchDisjunction`。**足りないのは「索引の側に `shared_with` が無い」ことだけ。**

### 1-5. 是正で誤りになる自分の記述（規則 10。是正**後**の語ではなく是正**前**の語で引いた）

```console
$ grep -rn "発行しない" src docs .ai-context --include=*.cs --include=*.md | grep -iE "private|個人資料|DocumentUpdated"
```

| 追随先 | 扱い |
| --- | --- |
| `PrivateNoteEndpoints.cs:33`（「本経路は DocumentUpdated を発行しない」） | **書き換える** |
| `SetExposure/Endpoint.cs:8`（「依然として発行しない」） | **書き換える** |
| `docs/functional/FR-19_private-notes.md:76` | **書き換える** |
| `.ai-context/adr/IADR-0270.md`（決定 5 本体） | **本文は書き換えない。**日付つき追記で後継 `IADR-0395` を併記する |
| `.ai-context/adr/IADR-0283.md:50,179` | 同上（追記のみ） |
| `.ai-context/specs/20260828_issue-447_*.md` | **確定済み仕様書。書き換えない**（凍結の射程。`traceability.repo.md`） |
| `IngestionService/Tests/.../DocumentUpdatedConsumerTests.cs:217` | 別事象（埋め込み一時障害）。**対象外** |

## 2. 判定軸を 1 か所に寄せる（本作業の要）

🔴 **生産側と消費側で同じ述語を 2 度書かない。** 新設する
`Knowledge.Contracts/Dtos/DocumentExposure.cs` が**唯一の純関数**であり、次の全員がこれを呼ぶ。

| 呼ぶ側 | 使う関数 | 役割 |
| --- | --- | --- |
| DocumentService `SetExposure` / `PrivateNoteDefaults` | `Project` / `FromToggle` | 台帳 → 文書属性の投影 |
| DocumentService（発行の門） | `IsIndexable` | 1 つでも ON のときだけ `DocumentUpdated` を出す |
| IngestionService（索引の生産） | `IsIndexable` | 偽なら**索引から削除**して抜ける |
| RetrievalService（横断検索） | `IsSearchAllowed` | 検索結果から落とす |
| GraphService（同期・出力） | `IsGraphAllowed` | ノードを作らない／消す・出力から落とす |
| AiAnalysisService（RAG 文脈） | `IsAiAllowed`（`AiInputExposure.IsAllowed` の実体） | 既存配線のまま |

`IsIndexable` は**定義そのものが 3 つの選言**（`IsSearchAllowed || IsGraphAllowed || IsAiAllowed`）である。
片方だけ改名されて静かに無効化される形にならない。

## 3. 決定（詳細は `IADR-0395`）

1. 属性キーは `search_exposure` / `graph_exposure` / `ai_input`（既存）。値は `included` / `excluded` の 2 値。
   **否定形の名前を新たに持ち込まない**（`#1253` / `#1254` が `bodyAbsent` → `hasBody` で寄せた向きと同じ）。
2. `AiInputExposure` は**残し、`DocumentExposure` へ委譲する別名**にする（既存の呼び出し面と既存テストを壊さない）。
3. `shared_with` は `DocumentUpdated` へ **`List<string>? SharedWith = null` を末尾・既定値付きで**足し
   （`IADR-0122` 決定 2）、索引ペイロードには **`tags` と同じ最上位のリスト項目**として載せる。
4. 発行の門は DocumentService、索引生産の門は IngestionService に**両方**置く（多層防御。同じ純関数）。
5. ON → OFF の撤収は「属性で弾く」ではなく**索引・グラフからの削除**で行う。

## 4. 受け入れ基準（issue の 8 項目 → テスト）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | 3 トグル OFF の本文作成・更新で `DocumentUpdated` を発行せず索引に 0 件 | `PrivateNoteExposurePublishTests.露出が全てOFFのあいだ本文を書いてもイベントは発行されない` / `PrivateNoteIndexProductionTests.露出が全てOFFの個人資料は索引されない_組織文書は索引される` |
| 2 | 検索 ON で発行され、ペイロードに `doc_scope` / `owner` / `shared_with` ＋ 3 投影が載る | `PrivateNoteExposurePublishTests.横断検索をONにすると判定軸を載せたイベントが発行される` / `PrivateNoteIndexProductionTests.横断検索がONの個人資料は判定軸を載せて索引される` |
| 3 | 所有者は見え、共有外の他者（restricted 保持者）は見えない | `PrivateNoteIndexExposureTests.所有者は自分の個人資料を横断検索で見つけられる` / `…共有されていない他者には見えない_同じスコープで組織文書は見える` |
| 4 | **共有先は見える（肯定テスト）** | `PrivateNoteIndexExposureTests.共有された相手は横断検索で見つけられる` |
| 5 | 検索 ON・AI OFF は RAG 文脈に入らない | 既存 `RagContextAiInputExclusionTests`（主語の属性を `search_exposure=included` へ揃えた） |
| 6 | 全 OFF へ戻すと索引から**削除**される（索引を直接引いて 0 件） | `PrivateNoteIndexProductionTests.全てOFFへ戻すと索引から削除される` |
| 7 | 露出変更で版が進まない | `PrivateNoteExposurePublishTests.露出の変更では版が進まない` |
| 8 | `confidentiality` だけの構成では 3 が守れない（決定 6 を検査で固定） | `PrivateNoteIndexExposureTests.機密区分だけの分岐は個人資料を許可しない` ＋ 変異試験（下記 §6） |

## 6. 変異試験（配線を外すと陰性が落ちること）

| # | 外したもの | 落ちたテスト |
| --- | --- | --- |
| M1 | `DocumentUpdatedConsumer` の索引の門（`IsIndexable`） | `PrivateNoteIndexProductionTests` 3 件（全 OFF で索引されない／撤収／欠落の fail-closed） |
| M2 | `InMemoryVectorStore` の裁量分岐の規則（`PrivateNoteVisibility.BranchMayGrant`） | `PrivateNoteIndexExposureTests` 2 件（共有外の他者に見えない／機密区分だけの分岐が許可しない） |

出力は PR 本文に貼る。**どちらも外すと陰性テストが落ちる** —— 陰性が配線に依存していることの実測である。

## 5. 計画書との差異・環流候補

- **既存の個人資料の遡及索引**（やること 6）: **実データで確認した。稼働クラスタの個人資料は 4 件、
  露出トグルがどれか 1 つでも ON のものは 0 件**である（`document_svc."PrivateNotes"`）。
  索引側も `attributes.doc_scope = private-note` の点は 0 件で、陽性対照
  （`attributes.confidentiality = public` → 全 6 件）でフィルタの経路が生きていることを確かめた。
  **したがって遡及索引の対象は 0 件であり、backfill は書かない。**（出力は `IADR-0395` §結果）
- 露出トグルの**画面は既に在る**（SC-19 の一覧の「露出」列）。したがって本作業の配線は
  着地と同時に利用者の手に届く —— 「口が無いから当面は影響が無い」と考えないこと。
