---
title: FR-21 文書本文の直接受け入れ経路（受け入れ基準 ①〜⑧ と ⑨ の分離構造）
type: spec
status: draft
related_ids: [FR-01, FR-02, FR-12, FR-19, FR-21, UC-03, UC-04, UC-11, ADR-0014, ADR-0015, ADR-0036, ADR-0050, ADR-0054, IADR-0119, IADR-0142, IADR-0264]
author: Claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md
---

# 仕様書: FR-21 文書本文の直接受け入れ経路（#447）

> 先行の仕様書 `20260822_issue-447_fr21-hold-release.md` は**記録の更新**であり、プロダクトコードを
> 変えていない。本書はその §着手方針 が予告した**実装の単位**である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-21**（文書本文の登録時直接受け入れ）。issue #447 は FR-01 / FR-02 / FR-12 も担ぐ
- ユースケース（UC）: UC-03（文書を管理する）／UC-04（データソースを取り込む）／UC-11（自分の資料を管理する）
- 画面（SC）: **なし**（SC-19 の「本文を編集（Wiki.js）」導線は保留継続。IADR-0142 §関連 Amends）
- 関連 ADR: ADR-0014 / ADR-0015（オブジェクトストレージ・MinIO）、ADR-0036（所有者ベースの動的束縛）、
  ADR-0050（`DocumentUpdated` の本文指紋。**決定 4 の順序制約により本作業では契約を触らない**）、
  ADR-0054（`doc_scope`）
- 計画書: `02_requirements/01_requirements.md` §受け入れ基準（FR-21）

## 目的・背景

FR-21 の着手保留は 2026-08-22 に解除された（IADR-0119 ［2026-08-22 追補 / #447］）。
**要求は解除されたが、実装は 1 行も入っていない**（下記 §着手前の実測）。本作業は受け入れ基準
①〜⑧ を実装し、⑨ については「検索結果と RAG コンテキストを分離する構造」までを置く。

## 着手前の実測（母集合と走査の証跡）

**引いた軸は 4 本。除外したものと理由も併記する**（`.claude/rules/traceability.md` 母集合の規則 6）。

| 軸 | 走査語 | 結果 |
| --- | --- | --- |
| 1 | `CreateDocumentRequest`（拡張子で絞らず全追跡ファイル） | C# 実体 1（`DocumentEndpoints.cs`）／生成 TS 2 ファイル／記録 4 |
| 2 | `SetMarkdownUri` / `MarkdownUri =` | 設定点は ConversionService・DocumentService・WikiService の 3 サービス。**DocumentService 側に本文を受ける口は無い** |
| 3 | `413` / `RequestEntityTooLarge` / `PayloadTooLarge` | knowledge / platform の**実装コードに 0 件**（唯一の出現は LlmGateway のテストの `InlineData`） |
| 4 | `IObjectStorageClient` / `AddPlatformObjectStorage`（DocumentService 配下） | **0 件**。DocumentService はオブジェクトストレージの依存を持たない |

3 トグル（FR-19）の実在も、**陽性対照つき**で測った。

| 走査語 | ファイル数 |
| --- | ---: |
| `include_in_search` / `IncludeInSearch` / `includeInSearch` / `include_in_ai` / `IncludeInAi` | **6 形すべて 0** |
| `doc_scope` / `DocScope` | 3 / 2（WikiService の個人資料除外。ADR-0046 D-01 の実装） |
| 陽性対照 `confidentiality` | 78 |
| 陽性対照 `owner` | 17 |

**0 件は「無い」ではなく「その形では無い」しか意味しない**ため、陽性対照を対で置いた。
**3 トグルは実装のどの綴りでも存在しない。**

### 除外したものと理由

- `src/ai-stock-trading`（submodule。別プロジェクト）
- `bin/` `obj/` 配下（ビルド生成物。`.dll` のバイナリ一致は実体ではない）
- `src/platform/frontend/src/foundation/api/generated/`（orval 生成物。入力は `docs/api/openapi.yaml` で
  あり、本作業は同ファイルを変更しないため再生成差分は生じない）

## 対象範囲

### 対象

- **DocumentService**: 本文を伴う登録（`POST /documents` の `body` 欄）と、既存文書への本文投入
  （`PUT /documents/{id}/body`）。オブジェクトストレージへの格納・1 MB 上限・所有者ベースの書き込み認可
- **Knowledge.Contracts**: 検索結果と RAG コンテキストを**別の集合として扱う型**（⑨ の分離構造）
- テスト仕様書 `docs/tests/FR-21_*.md`（`check-test-traceability.js` が起点 ID の突合に使う）

### 対象外（理由つき）

| 対象外 | 理由 |
| --- | --- |
| **受け入れ基準 ⑩**（新規個人資料の 3 トグルが OFF） | 主語となる 3 トグルが実装のどの綴りでも存在しない（上表）。**#451 が FR-19 の 3 トグルモデルを作るまで陽性テストを書けない**。既定値を先に置くと、#451 が決める属性キーと衝突する |
| **受け入れ基準 ⑨ の陽性検証** | 同上。**分離構造そのもの**は #451 と独立に置けるので本作業で置く。トグルの属性キーの値域は #451 が決める |
| **BFF（`/bff/documents`）への本文欄の追加** | (a) `docs/api/openapi.yaml` は**手書きの契約**であり（`openapi.yml` 自身が「生成元の通信仕様書が存在しない」と明記）、BFF の面を増やすと同ファイルの手編集と orval 生成物の再生成が要る。(b) SC-19 の本文編集導線は**保留継続**（IADR-0142）。(c) FR-21 の実利用者である AST の KB 書き込みは**BFF を経由せず DocumentService を直接叩く**（`DocumentEndpoints.cs` の実装コメントが実測を記録している）。**したがって service 層が FR-21 の実面である** |
| `DocumentUpdated` への本文指紋の追加 | ADR-0050 決定 4 が「**移行 → 契約変更**」の順序を課しており、Wolverine への移行が先である |
| コネクタ・変換・取り込み（FR-01 / FR-02 / FR-12） | **既に実装済み**（軸 2 の走査と `Services/` の実測。コネクタ 4 種＋`ConnectorRegistry`、pandoc 変換、chunk / embed / Qdrant 登録がすべて実在する）。#447 の残件は FR-21 のみである |
| 変換のゴールデンファイルテスト（#447 §退行防止） | FR-12 側の残件。本作業（FR-21）の射程外。§未決事項 2 へ送る |
| **`src/` のコード・テストへ `UC-11` を書くこと** | `UC-11`（個人資料の作成・管理）に当たるのは ⑨⑩ 側であり、**本作業は個人資料の実装に着手していない**。`scripts/test-traceability-allowlist.json` の運用注記が「**保留対象の ID は、その機能に着手する issue が初めて書く**」と定めており、それに従う（本作業のコード注釈は `FR-21, UC-03` とする）。**計画のトレーサビリティ上の対応関係はテスト仕様書の trace ブロックと本 IADR が保持する** |

## 設計

### D-1 本文の格納先は既存の `MarkdownUri` とする（④）

基準 ④ は「本文はオブジェクトストレージへ格納され、**DB は参照のみ持つ**」である。
`Document.MarkdownUri` は既にその形（`storage://<bucket>/<key>` の参照のみ）であり、
**取り込み（`DocumentUpdatedConsumer`）はこの欄を見て parse → chunk → embed → index を起動する**。

**新しい欄を作らない。** 作ると取り込み側の分岐が 2 本になり、①（取り込みが起動する）と
②（RAG 検索の結果として返る）を成立させるために取り込み経路の改修が要る。
既存欄へ載せれば**取り込み経路は 1 行も変わらない**。

- オブジェクトキー: `documents/{documentId}/body.md`
- Content-Type: `text/markdown; charset=utf-8`
- `OriginalUri` は別列であるため、**本文と `OriginalUri` は構造的に併存する**（③）

### D-2 2 つの入口

| 口 | 認可 | 用途 |
| --- | --- | --- |
| `POST /documents`（`body` 欄を追加。**任意**） | 既存どおり（admin / operator 群） | 登録と同時の本文投入（①③④⑥⑦） |
| `PUT /documents/{id}/body`（新設） | **認証のみ**＋所有者ベースの動的束縛 | 既存文書への本文投入（⑤⑥⑦⑧） |

`PUT /documents/{id}/body` に**ロール判定を積まない**。FR-21 要求文が
「本文の書き込み権限は ABAC の**動的束縛**（`doc.owner ∈ { ${current_user} }`）で表現し、
**ロールによる判定を追加しない**」と定めており、ADR-0036 D-07 が同じことを述べている。

### D-3 所有者ベースの書き込み認可（⑤⑧）

`DocumentWriteAuthorization.CanWriteBody(attributes, subject)` を純関数として置く。
`attributes["owner"]` と主体（`HttpContext.User.Identity.Name`）の一致で判定する。

- 所有者不一致 → **403**（存在秘匿の 404 ではない）。この口に到達できる時点で文書の閲覧可否は
  別の軸で決まっており、**閲覧スコープの再導出は BFF の責務である**（IADR-0041）。
  「閲覧もできない利用者に存在を明かさない」は BFF 側の 404 が担う
- `owner` 属性が無い／空の文書は**誰も本文を書けない**（deny-by-default）。
  取り込み経路の文書は `owner=system`（ADR-0036 §未決 6 の当面の扱い）であり、
  編集は SC-05 の管理者経路で行う、という計画の記述と整合する
- **判定結果をキャッシュしない。** ADR-0036 D-14 は「認可判定の結果をキャッシュする箇所では、
  キャッシュキーに主体を必ず含める」と課している。**キャッシュを置かないことが最も安全な充足**である。
  将来キャッシュを置くときのために、判定関数の引数に主体があること自体をテストで固定する

### D-4 1 MB 上限（⑥⑦）

`DocumentBodyLimits.MaxBytes = 1024 * 1024`。**UTF-8 のバイト数**で測る（文字数ではない）。

- 超過 → **413**（`Results.StatusCode(413)`）。**切り詰めて成功を返さない**
- 上限以下 → **1 バイトも落とさずそのまま格納する**（⑦ は ⑥ の陽性対照）

### D-5 ⑨ の分離構造（`Knowledge.Contracts`）

計画が名指しした失敗（「**検索結果をそのまま LLM へ渡す構造では分離できない**」）は実在する ——
`RagOrchestrator` は `CitationMapper.ToCitations(results)` → `BuildContext(citations)` と、
**検索結果の集合をそのまま文脈へ流している**（実測）。

`RagContextSelection`（検索結果の集合／RAG 文脈の集合／除外されたチャンク ID を**別の欄として持つ**
レコード）と `RagContextPolicy.Select(results, isAiInputAllowed)` を置く。

**トグルの属性キーをここで決めない。** 判定は呼び出し側が渡す述語に委ね、
本作業は「**2 つの集合が別物である**」という構造だけを固定する。キーの値域は #451（FR-19）が決める。

> **配線は本作業では行わない。** `AiAnalysisService` / `RetrievalService` は本 issue の宣言ファイル領域の
> 外であり（並行作業中）、契約側に構造を置くところまでが本作業の射程である。

## 受け入れ基準

計画 `02_requirements/01_requirements.md` の FR-21 ①〜⑩ を転記し、本作業で満たす条件を確定する。

- [x] ① 本文を伴う文書を登録でき、取り込み・分割・埋め込みが起動する
- [x] ② 登録した本文が RAG 検索の結果として返る（**単体の範囲**。Qdrant まで通す結合は Docker 不在で skip）
- [x] ③ 本文と `OriginalUri` は排他ではなく併存できる
- [x] ④ 本文はオブジェクトストレージへ格納され、DB は参照のみ持つ
- [x] ⑤ 一般利用者が自分の文書の本文を投入できる（ABAC の動的束縛による）
- [x] ⑥ 本文が 1 MB を超える登録要求は 413 で拒否される
- [x] ⑦ 1 MB 以下の本文は切り詰められることなく全文が索引される
- [x] ⑧ 別の利用者として同じ文書 ID へ書き込みを試みると拒否される
- [ ] ⑨ 検索結果に現れるが RAG 回答のコンテキストには含まれない（**分離構造のみ**。陽性検証は #451 後）
- [ ] ⑩ 新規に登録した個人資料は 3 トグルがすべて OFF（**対象外**。#451 待ち）

## テスト方針

| 基準 | テスト | 種別 |
| --- | --- | --- |
| ① | 本文つき `POST /documents` → `MarkdownUri` が付き `DocumentUpdated` が発行される | 端点 |
| ② | 発行された `DocumentUpdated` が取り込みの起動条件（`MarkdownUri != null`）を満たす | 端点 |
| ③ | 本文と `originalUri` を同時に渡して両方が保持される | 端点 |
| ④ | 保存先がオブジェクトストレージであり、DB が持つのは `storage://` 参照のみ | 端点 |
| ⑤ | `owner` が自分の文書へ一般利用者（ロール無し）が本文を投入 → 200 | 端点 |
| ⑥ | 1 MB + 1 バイト → **413**、かつ**保存が呼ばれていない**（切り詰め成功の否定） | 端点 |
| ⑦ | 上限ちょうどの本文 → 201 / 200 で、**格納された文字列が入力と完全一致** | 端点 |
| ⑧ | alice が書けた同じ文書 ID へ bob → **403**。陽性対照として bob 自身の文書は 200 | 端点 |
| ⑧ | 同じ属性・違う主体で判定が変わる（主体が判定の入力であることの構造検証） | 単体 |
| ⑨ | 述語が false のチャンクが `SearchResults` に残り `ContextChunks` から落ちる | 単体 |

**変異試験**（ガードを反転させて落ちること）を ⑥・⑧ に対して行う。

## 計画書との差異

- 差異: **なし**（実装できない基準 ⑨⑩ は「差異」ではなく**依存待ち**である。§対象外）

## 未決事項

1. **⑨ の配線先**（`AiAnalysisService.CitationMapper` / `RagOrchestrator`）。本作業の宣言ファイル領域の外。
2. **FR-12 のゴールデンファイルテスト**（#447 §退行防止 の 1 項目）が無い。FR-21 とは別単位。
3. **カバレッジ床の引き上げ**を本環境で測れない（Docker 不在で統合テスト 26 件が skip され、
   CI と同じ母集合を作れない）。`src/coverage-floor.json` は据え置く。
4. `.claude/rules/traceability.repo.md` の計画 ADR レンジは `ADR-0001..0054` だが、計画側には
   **`ADR-0055` が実在する**（実測）。レンジの更新は本作業の射程外（規約ファイルは別担当領域）。
