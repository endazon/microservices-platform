---
title: AI 提案の類似度候補に決定的な供給元（語の共起）を置き、「提案が構造的に 0 件」を赤にする対照を固定する（#1244）
type: spec
status: done
related_ids: [FR-17, FR-18, UC-10, SC-03, SC-21, ADR-0033, ADR-0034, ADR-0051, IADR-0242, IADR-0266, IADR-0323, IADR-0364, IADR-0380]
author: Claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
  - planning:projects/microservices-platform/07_adr/ADR-0051_ai-suggestion-abac-boundary.md
---

# #1244: AI 提案の類似度候補の供給元が無く、提案生成が構造的に常に 0 件になっている

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-18`（埋め込み類似度および LLM によるリンク候補・タグ候補の提案）。`FR-17`（知識グラフ。提案の承認で辺になる）
- ユースケース（UC）: `UC-10` 代替フロー「AI が提案した関連（FR-18）を承認し、確定リンクへ昇格させる」
- 画面（SC）: `SC-03`（承認欄。主）・`SC-21`（一覧。従）—— **本作業は画面を変えない**（§対象範囲）
- 関連 ADR: `ADR-0051` 決定 1〜4（類似度は全文書横断で算出してよい／提示段階で完全に絞る／LLM へ渡す段階の越境禁止は不変／実行タイミングは実装設計）、`ADR-0034` 決定 1・2・5（ホップごと判定・完全秘匿・越境禁止）、`ADR-0033` 決定 7・10（3 状態・却下の永久保持と本文変更での解除）、`ADR-0035`（**未起案・実測待ちの GraphRAG 検索戦略の部分は射程外**）
- 先行 IADR: [IADR-0266](../adr/IADR-0266_ai-suggestion-llm-boundary.md)（論点 C-2「ポートで切り、実装は交換可能にする」＋ §結果「既定では提案が 0 件になる」）、[IADR-0242](../adr/IADR-0242_graph-hop-abac-and-typed-edge-schema.md)（型ゲート）、[IADR-0364](../adr/IADR-0364_tag-suggestion-reflection-and-dictionary-enforcement.md)
- 本作業の実装 ADR: [IADR-0380](../adr/IADR-0380_term-overlap-similarity-candidate-source.md)（仮番。マージ時に改番）

## 1. 事象（着手前の実測。issue の転記ではなく引き直した）

基点: `origin/develop` `68b38ec6`（`git rev-parse --is-shallow-repository` → `false`。出典に使える）。

### 1-1. `ISimilarityCandidateSource` の実装は「常に空」の 1 つだけであり、それが本番 DI に刺さっている

```console
$ grep -rn "ISimilarityCandidateSource" src --include=*.cs | grep -v "/bin/\|/obj/"
Services/GraphService/Domain/Ports/ISimilarityCandidateSource.cs:14:public interface ISimilarityCandidateSource
Services/GraphService/Features/AiSuggestions/Generate/AiSuggestionGenerator.cs:38:    ISimilarityCandidateSource similarity,
Services/GraphService/Infrastructure/ExternalServices/UnconfiguredSimilarityCandidateSource.cs:15:  …) : ISimilarityCandidateSource
Services/GraphService/Program.cs:80:builder.Services.AddScoped<ISimilarityCandidateSource, UnconfiguredSimilarityCandidateSource>();
Services/GraphService/Tests/…/AiSuggestionGenerationTests.cs:73:    private sealed class StubSimilarity(…) : ISimilarityCandidateSource
Services/GraphService/Tests/…/TagDictionaryEnforcementTests.cs:36:    private sealed class StubSimilarity(…) : ISimilarityCandidateSource
```

本番コード（`Tests/` 以外）で `: ISimilarityCandidateSource` を持つ型は `UnconfiguredSimilarityCandidateSource` **のみ**。
その本体は `Task.FromResult<IReadOnlyList<SimilarityCandidate>>([])` を返すだけである。

**陽性対照**（走査が効いていることの証拠）: 同じ走査を隣のポート `ISuggestionLlmClient` に当てると、本番実装
`LlmGatewaySuggestionClient` 1 件 ＋ テストの stub 2 件（`RecordingLlmClient` / `CapturingLlm`）が出る。
走査は実装型を拾えており、「0 件だから無い」ではなく「1 件しか無い」と言える。

### 1-2. 呼び出し経路

`GenerateAiSuggestionsEndpoint`（`POST /graph/suggestions/generate/{documentId}`。要求時・利用者スコープ。
IADR-0266 決定 B-1）→ `AiSuggestionGenerator.GenerateAsync` → **[2] `similarity.FindSimilarAsync` が空を返すと
`return []` で抜ける**。以後の [3] 候補列挙・[4] 封・[5] LLM・[6] 取り込みは 1 度も走らない。

### 1-3. 既存テストが固定しているもの

`AiSuggestionGenerationTests`（G-01〜G-11）・`TagDictionaryEnforcementTests` は**すべて `StubSimilarity` を注入**しており、
「供給元が候補を返す」ことを前提に境界（送信ペイロードの否定形・件数秘匿・却下除外）を測っている。
**本番 DI が何を解決するかを見るテストは 1 本も無い**（`grep -rn "UnconfiguredSimilarityCandidateSource" Tests/` → 0 件。
陽性対照: 同ディレクトリで `LlmGatewaySuggestionClient` は 3 ファイルにヒット）。
🔴 したがって**「常に空」へ戻しても全テストが緑のまま**である —— 今回の欠陥そのものであり、§5 の回帰対照で塞ぐ。

### 1-4. 生成の起動経路

BFF は生成を公開していない（`docs/api/openapi.yaml` 2609 行「計画に生成を起動する導線が無く…公開しない」）。
SC-03 の承認欄は `pending` の一覧を読むだけで、生成を呼ぶ画面要素は無い。計画側 `05_screens` §SC-21 は
「提案の生成頻度と滞留時の扱い」を**未確定**としている。**ADR-0051 決定 4 は実行タイミングを実装設計に委ねており、
IADR-0266 は「利用者リクエスト時」を採った** —— GraphService の端点はその形で既に在る。§4 参照。

## 2. 目的

`FR-18` の提案生成が**実際に提案を生む**状態にする。具体的には

1. `ISimilarityCandidateSource` の**実供給元を 1 つ置き、既定にする**（`Unconfigured…` は「供給元を切る」構成値として残す）
2. `ADR-0034` / `ADR-0051` の境界（候補列挙の段で絞る・件数も存在も出さない）を**壊さない**ことを、実供給元を通した陰性対照で示す
3. 🔴 **「提案が 1 件も生まれない状態」を赤にする回帰対照**を置く（同型の欠陥を二度作らない）

## 3. 母集合（提案の生成から表示までの全経路）

| 段 | 実体 | 本作業 |
| --- | --- | --- |
| 起動 | `POST /graph/suggestions/generate/{id}`（GraphService。BFF 未公開） | 変えない。§4 |
| [1] 起点 | `IGraphStore.FindNodeAsync` → `AuthorizedNode.Authorize` | 変えない |
| **[2] 類似度** | **`ISimilarityCandidateSource`** ← `UnconfiguredSimilarityCandidateSource` | **★ `TermOverlapSimilarityCandidateSource` を新設し既定にする** |
| 類似度の材料 | （無かった） | **★ `graph_document_term_profiles`（語の出現数。`DocumentUpdated` 購読で本文から作る）** |
| [3] 候補列挙 | `EfGraphStore.EnumerateAuthorizedCandidatesAsync`（ABAC・自己・却下済み・既存辺・既存提案を落とす） | 変えない（重複・自己参照・既存辺との衝突は**ここで既に決まっている**。§4-3） |
| [4] 封 | `SuggestionPrompt.Seal` | 変えない |
| [5] LLM | `LlmGatewaySuggestionClient` | 変えない |
| [6] 取り込み | `AiSuggestionGenerator.PersistAsync`（許可集合との突合・辞書照合・pending で保存） | 変えない |
| 却下解除 | `GraphDocumentSyncConsumer.ReinstateRejectedAsync`（本文指紋の変化） | 変えない。**同じ契機で語の出現数も作り直す**（§4-2） |
| 削除 | `DocumentDeletedConsumer` | **★ 出現数の行も掃除する** |
| 一覧・承認・却下 | `/graph/suggestions`（List / Approve / Reject）→ BFF → SC-03 / SC-21 | 変えない |
| 画面 | `AiSuggestionPanel`（SC-03）・SC-21 | 変えない（§4-4） |

**走査**: `git grep -l "ISimilarityCandidateSource\|SimilarityCandidate\b" -- src` の 8 ファイル（§1-1）＋
`AiSuggestionGenerator` の依存を呼ぶ側（`Program.cs` / `TestWebApplicationFactory`）＋ 文書（`docs/data/knowledge-graph.md`・
`docs/tests/FR-18_ai-suggestions.md`・`docs/screens/SC-21_*`・`docs/screens/SC-03_*`）。`docs/functional/FR-18_*` は
**存在しない**（`ls docs/functional | grep FR-18` → 0 件。陽性対照: `FR-19_private-notes.md` は在る）→ 本作業で新設する。

## 4. 設計

### 4-1. 供給元の選定 —— 語の共起（決定的・サービス内・外部依存なし）を既定にする

候補は 3 つあった（比較の全文は IADR-0380）。

| 案 | 内容 | 判定 |
| --- | --- | --- |
| A | RetrievalService に「ABAC を跨ぐ類似度」の内部口を作り、Qdrant の埋め込み（`knowledge_chunks_*`）で引く | **不採用（今回）。** 稼働環境の埋め込み経路は不安定（#1215 open）で、供給元が壊れていると提案が再び 0 件へ戻る。新規の公開面（サービス間限定の認可・契約）が要り、テストも Qdrant 前提になる |
| **B** | **GraphService 内で、文書の表題＋本文から**語の出現数（Latin 語 ＋ CJK 2-gram）**を持ち、IDF 重み付きコサインで似た文書を引く** | **採用。** 決定的（同じ入力なら同じ出力）・外部 LLM／埋め込み・他サービスに依存しない・`ADR-0051` 決定 1 の「自システム内の演算」そのもの。本文は既に `DocumentUpdated` 購読でリンク抽出のために読んでいる（`IGraphContentReader`）ので、**読み取り経路を増やさない** |
| C | 表題だけの共起 | 品質が低すぎる。**B の縮退形として採り込む**（本文が取れない／まだ同期していない文書は表題だけで出現数を作る） |

**B の中身**:

- `TermProfile.Extract(title, body)`（Domain。純粋）: Latin/数字の連なりを小文字化した語（2 文字以上）、CJK の連なりは
  `CjkBigramPayload.Encode`（`Knowledge.Contracts`。検索側と同じ切り方）の 2-gram。表題の語は重み 3。**出現数上位 128 語**に切る
  （同数は語の順序で決める。決定的）
- `TermProfile.Rank(origin, corpus, minScore, limit)`（Domain。純粋）: `idf(t) = ln(1 + N / df(t))`、`w = (1 + ln tf) × idf`、
  コサイン。`score < minScore`（既定 0.1）は落とす。降順・同点は文書 ID 昇順（決定的）。起点自身は返さない
- `GraphDocumentTermProfile`（`graph_document_term_profiles`。`document_id` PK・`terms jsonb`・`body_hash`・`updated_at`）。
  `graph_documents` への FK は張らない（`edges` と同じ理由）
- `TermOverlapSimilarityCandidateSource`（Infrastructure/Persistence）: 全文書の出現数を読み（出現数の行が無い文書は
  表題から作る）、`Rank` で上位 `limit` 件を返す。**ログは起点 ID だけ**（件数・存在を出さない。`ADR-0051` 決定 2）
- 構成 `AiSuggestions:Similarity:Source`（`term-overlap` 既定 ／ `none` = `Unconfigured…`）・`AiSuggestions:Similarity:MinScore`。
  未知の値は**起動時に落とす**（`ConnectionStrings` と同じ向き。黙って空へ倒すと本 issue の再演になる）

**規模**: 1 回の生成で全文書の出現数（実データ 2,368 件 × ≤128 語）を読む。生成は利用者の明示操作で LLM 呼び出し（秒単位）を伴う
経路であり、数 MB の読み取りは許容する。転置索引（SQL 側で内積）へ移す条件は IADR-0380 §結果に置く。

### 4-2. 出現数の作成契機 —— 却下解除・リンク抽出と同じ「本文指紋の変化」

`GraphDocumentSyncConsumer` は指紋が変わったときだけ本文を読む（`ADR-0050` 決定 3）。**同じ 1 回の読み取り**で
`TermProfileSynchronizer.UpsertAsync(id, title, body, hash)` を呼ぶ。本文が取れなければ表題だけで作る（縮退。辺と違い
「消える」ものは無いので縮退してよい）。出現数の行が無い文書は、指紋が変わらなくても表題から作る（既存文書の初回）。
`SaveChanges` は消費者が 1 回だけ呼ぶ（`LinkEdgeSynchronizer` と同じ規律）。

**既存文書の backfill**: `graph_documents` は `MarkdownUri` を持たないので本文からの backfill はできない。
供給元側の表題フォールバックにより**初日から全文書が候補になり**、本文が次に更新された文書から本文入りの出現数へ置き換わる。

### 4-3. 重複・自己参照・既存辺との衝突・却下（変えない。噛み合わせを確認した）

| 事象 | どこで落ちるか | 根拠 |
| --- | --- | --- |
| 起点自身 | 供給元でも返さないが、**正は候補列挙**（`id != originDocumentId`） | ポートの契約「起点自身を含んでもよい（呼び出し側が落とす）」 |
| 同一 ID の重複 | 生成器（最初＝最高スコアを採る）＋ 候補列挙の `Distinct` | 既存 |
| 既存辺がある組 | 候補列挙（`db.Edges` の両方向） | 既存 G-06 |
| 既存提案（pending / approved / **rejected**）がある組 | 候補列挙（状態を問わず除外） | `ADR-0033` 決定 7。既存 G-05 |
| 却下の解除 | 本文指紋の変化で `pending` へ戻る。**戻った提案は「既存提案」なので再生成では作り直されない**（同じ行が再提示される） | `ADR-0033` 決定 10。**出現数の更新契機と同じ指紋変化**なので、本文が変わった文書は新しい出現数で候補計算される |

### 4-4. 起動経路（射程外。理由を残す）

供給元が入っても、**利用者が生成を起動する導線は依然として無い**（§1-4）。BFF 公開 ＋ SC-03 のボタンは
計画 `05_screens` §SC-03「ここに置くのは次の 2 つのみ」に**無い要素**であり、実装で先取りしない
（issue 受け入れ基準 4 の指示どおり planning へ裁定依頼を出す。§8）。本作業の実測は GraphService の端点を直接叩いて行う。

### 4-5. 射程外

- `ADR-0035` の GraphRAG 検索戦略（二段検索・重み・要約）: 未起案・実測待ち。触らない
- RetrievalService への越境口（案 A）: 埋め込み経路の安定（#1215）を待つ。IADR-0380 §フォローアップ
- 応答時間の側チャネル（`ADR-0051` §フォローアップ 4）: 供給元は常に全文書を読む（要求元のスコープに依らない）ので相関源は増えない。**測っていない**（従前どおり）

## 5. テスト計画

| ID | 区分 | 内容 | 固定するもの |
| --- | --- | --- | --- |
| T-41 | 陽性 | 本文を共有する 2 文書から、実供給元（`TermOverlap…`）が候補を返す | 供給元が働く |
| T-42 | 陰性 | 語彙を共有しない文書は候補に**入らない**（T-41 と同クラス） | 無関係を提案しない |
| T-43 | 陰性・変異検出 | 全文書に共通する定型句だけを共有する文書は、しきい値を下回り候補に入らない。**IDF を外すと落ちる** | 共起の重み付け |
| T-44 | 決定性 | 同じ入力で 2 回引いて同じ順序・同じスコア | 決定的な供給元 |
| T-45 | 縮退 | 出現数の行が無い文書は表題で候補になる（陽性）／表題も語を持たなければ候補にならない | backfill 不要の理由 |
| T-46 | 🔴 秘匿 | 供給元のログに起点 ID 以外の構造化値（件数・候補 ID）が**現れない**（陽性対照: 起点 ID は現れる） | `ADR-0051` 決定 2 |
| T-47 | 🔴 境界 | 実供給元を通した生成で、**スコープ外の似た文書が LLM 送信本文に現れない**（陽性対照: スコープ内の似た文書は現れる） | `ADR-0034` 決定 5（供給元が越境しても列挙の段で落ちる） |
| T-48 | 🔴 **回帰対照** | 本番 DI（`Program`）が既定構成で解決する `ISimilarityCandidateSource` が `Unconfigured…` **ではない** | **「常に空」に戻すと赤** |
| T-49 | 🔴 **回帰対照（結合）** | 既定構成のホストで、実文書 2 件を同期 → `POST /graph/suggestions/generate/{id}` → **`pending` の提案が 1 件以上**（一覧にも出る） | **「0 件でも緑」にならない**（issue 基準 5） |
| T-50 | 構成 | `Source=none` で `Unconfigured…` が解決される／未知の値は起動が落ちる | 切り替えの両向き |
| T-51 | 同期 | 指紋が変わると出現数が本文から作り直される／変わらなければ本文を読まず出現数も変わらない（対） | 契機の一致 |
| T-52 | 削除 | `DocumentDeleted` で出現数の行も消える | 痕跡を残さない |
| T-53 | **変異** | (1) `Program.cs` の登録を `Unconfigured…` へ戻す → T-48・T-49 が落ちる。(2) `Rank` の IDF を 1 に固定する → T-43 が落ちる。**実走して記録する** | 検出力 |

## 6. 受け入れ基準（issue の 6 項目との対応）

| # | issue の基準 | 本作業 |
| --- | --- | --- |
| 1 | RetrievalService に ADR-0051 決定 1 準拠の口 | **形を変えて満たす**: 越境してよい類似度の算出を GraphService 内に置く（案 B）。サービス間限定の認可は**口が無いので要らない**（IADR-0380 §決定 1） |
| 2 | 実アダプタへ差し替え・`Unconfigured…` は残す | T-48〜T-50 |
| 3 | 件数・存在を出さない作法を否定形で固定 | T-46・T-47 |
| 4 | 起動経路を決める。計画に無ければ planning へ | 要求時（IADR-0266 決定 B-1）は既決。**利用者の導線は planning へ裁定依頼**（§8） |
| 5 | 結合テストで「0 件でも緑」にならない形 | T-49 |
| 6 | `dotnet test src/knowledge/backend/backend.slnx` 緑 | §7 |

## 7. 検証・実測

### 7-1. ローカル

- `GraphService.Tests`: **356 件緑**（着手前 332 → ＋24。T-41〜T-52 と G-12・G-13）
- `dotnet test src/knowledge/backend/backend.slnx`: 全プロジェクト緑（Knowledge.Contracts 47 / Feedback 38 / AiAnalysis 98 / DataSource 185 /
  Document 248 / Wiki 97 / Ingestion 52 / Dashboard 57 / Conversion 136（skip 6）/ Retrieval 182 / Graph 356 / IntegrationTests 36（skip 41））
- `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes`: exit 0
- `dotnet test src/platform/backend/backend.slnx`: 走ったプロジェクトは緑（NotificationService 53 / LlmGateway 244 ほか）。
  **`Platform.Bff` は本 worktree で `src/ai-stock-trading`（submodule）が未 populate のためビルド不能**（`AiStockTrading` 名前空間の CS0246。
  本変更と無関係で BFF には触れていない。CI は submodule 付きで走る）
- `check-trace-blocks` / `check-doc-links` / `check-doc-status-vocabulary` / `check-doc-type-vocabulary`: OK。
  `check-adr-numbering`: IADR-0378 / 0379 の欠番（仮番 0380 のため。マージ時に改番）
- `check-test-spec-coverage --update`: 新テストクラス 4 件を床へ入れた

**変異試験（T-53。実走）**

| # | 変異 | 結果 |
| --- | --- | --- |
| 1 | `Program.cs` の解決を常に `Unconfigured…` へ | **T-48・T-49 の 2 本だけが落ちた**（同時に走らせた供給元・生成器の 22 本は緑のまま）。**従前はこの変異を検出するテストが 0 本だった** |
| 2 | `TermProfile.Rank` の IDF を 1 に固定 | **T-43 の 1 本だけが落ちた**（12 本は緑のまま） |

いずれも変異を戻し、`grep` で原文に復していることを確認した。

### 7-2. 稼働 k3s（実測。生出力）

前提: `kubectl port-forward svc/graph-service` / `svc/document-service`、エッジ issuer（Keycloak）から `developer` の access_token
（`--cacert`。`-k` は使っていない）。他の Pod は再起動していない。

**(a) 差し替え前（`graph-service:latest`）—— 欠陥の再現**

```
POST /graph/suggestions/generate/3ba02952-… (msp-searchseed-tanpopo 検索導線の検証用文書)
[]
HTTP=200
```

graph-service のログに LLM ゲートウェイへの送信は無い（[2] で空のまま抜けている）。

**(b) 差し替え（`kubectl set image … graph-service=k3d-local/microservices-platform/graph-service:issue-1244`）**

```
deployment "graph-service" successfully rolled out
Applying migration '20260904170638_AddGraphDocumentTermProfiles'.
CREATE TABLE graph_document_term_profiles (
```

**(c) 実文書 3 件を DocumentService へ投入**（`POST /documents/`。本文入り。A・B は語彙を共有、C は英語で無関係）

```
Synced graph document ea1b2ddc-… (attributes=3 … termProfile=body)   # C: Quarterly budget planning
Synced graph document d313e469-… (attributes=3 … termProfile=body)   # A: 知識グラフの ABAC 判定設計
Synced graph document 75d8d4a2-… (attributes=3 … termProfile=body)   # B: グラフ探索の認可レビュー
```

**(d) 生成（A を起点）—— 供給元は働き、LLM の段まで到達した**

1 回目は LLM の段で落ちた: `HttpRequestException: Name or service not known (llm-gateway:5010)`。
🔴 **配備の欠陥**: `values.yaml` の `graph.extraEnv` に `Services__LlmGateway` が無く、コード既定 `http://llm-gateway:5010` は k8s に無い名前
（aianalysis 等は上書き済み。graph だけ漏れていた —— 供給元が空だったため今まで誰も LLM の段に到達せず、露見しなかった）。
`values.yaml` と `docker-compose.yml` を直し、稼働側は `kubectl set env deployment/graph-service Services__LlmGateway=http://llmgateway-service:8080`。

2 回目（ゲートウェイ到達）:

```
graph-service : Sending HTTP request POST http://llmgateway-service:8080/complete
llm-gateway   : LLM routing decision: sensitivity=Public purpose=graph-suggestion endpoint=claude-managed tier=B model=claude-opus-5
llm-gateway   : LLM call failed at endpoint claude-managed (claude-opus-5)
                AuthenticationException: … {"type":"authentication_error","message":"x-api-key header is required"}
→ POST /graph/suggestions/generate/d313e469-…  []  HTTP=200
```

**(e) 対照（供給元が候補を返しているかを LLM 送信の有無で測る）**

| 起点 | 期待 | LLM ゲートウェイへの送信（graph-service ログ） |
| --- | --- | --- |
| A（B と語彙を共有） | 候補あり → 送信する | **1 回** |
| C（語彙を共有しない） | 候補なし → 送信しない | **0 回**（`[]` HTTP=200） |

**結論**: 供給元は稼働環境で候補を返し、候補列挙・封を通って LLM ゲートウェイまで届いている（差し替え前は到達しない）。
🔴 **未測**: `pending` 行が実際に作られること。**依存先（外部 LLM の API キー）がこのクラスタで未構成**で、ゲートウェイの上流が
認証で拒む。検索・索引（#1215）には依存していない。**残した乖離**: graph-service の Deployment のイメージ（`issue-1244`）と
env（`Services__LlmGateway`。宣言は本 PR で追随済み）。門 G11 が検知する。

## 8. 計画書との差異・未決事項

- **差異**: 無し。`ADR-0051` 決定 1 が認めた越境演算を、RetrievalService ではなく GraphService 内で行う（計画は場所を定めていない）
- **未決（planning へ）**: 利用者が生成を起動する導線（SC-03 のボタン／文書更新時の自動起動／定期）。
  `05_screens` §SC-21 の未確定「提案の生成頻度と滞留時の扱い」と同件。**着手前に既存 issue を検索する**
  （planning#454 は「実行主体・タイミング」で closed。導線そのものの裁定 issue は本書作成時点で見当たらない）
