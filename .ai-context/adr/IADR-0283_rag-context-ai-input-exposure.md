---
title: IADR-0283 「AI の入力に含める」トグルは ABAC 文書属性へ写し、分離は RAG 経路の候補絞りで行う（FR-21 受け入れ基準 ⑨）
type: impl-adr
status: Accepted
related_ids:
  - FR-04
  - FR-11
  - FR-19
  - FR-21
  - UC-01
  - UC-11
  - ADR-0034
  - ADR-0036
  - ADR-0054
  - IADR-0253
  - IADR-0264
  - IADR-0270
author: claude
created: 2026-08-28
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-19 / FR-21 受け入れ基準 ⑨⑩)
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
related_specs:
  - ../specs/20260828_issue-447_fr21-criteria-9-10.md
---

# IADR-0283: 「AI の入力に含める」トグルの写し先と、⑨ の分離を行う層

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: 実装（claude）。**計画は判定の主語（3 トグルと既定 OFF）を確定させており、
  ABAC 属性としての表現と分離を行う層は実装へ委ねられている**（`ADR-0054` が
  `doc_scope` の新設で採ったのと同じ形）

## 起点・関連

- 関連する計画書 ID: `FR-21` 受け入れ基準 ⑨（「横断検索に含める」ON・「AI の入力に含める」OFF の
  個人資料は**検索結果に現れるが RAG 回答のコンテキストには含まれない**）/ `FR-19`（3 トグルと
  既定 OFF）/ `FR-04`・`FR-11`（RAG 回答と越境判定）/ `UC-01`・`UC-11`
- 関連する実装 ADR:
  - [IADR-0264](IADR-0264_document-body-intake-path.md) 決定 5 —— **分離の構造**
    （`RagContextSelection` / `RagContextPolicy`）を契約側へ置いた。**判定条件と配線は残件**と明記
  - [IADR-0270](IADR-0270_private-note-obsidian-sync-backend-core.md) 決定 5 —— 個人資料は
    `DocumentUpdated` を発行しない。**ON の消費側配線はフォローアップ 2**
  - [IADR-0253](IADR-0253_authz-scope-disjunction-contract.md) —— 認可スコープの選言（段 3 = 消費側の分岐評価）

## コンテキストと課題

計画は ⑨ について **「実装が素直に作ると満たされない」「⑨＝検索結果をそのまま LLM へ渡す構造では
分離できない」** と名指ししている。実測でもそのとおりであった（`origin/develop` 由来の作業ブランチ
`8253134`）。

| 段 | 実測 |
| --- | --- |
| 台帳 `PrivateNote` | 3 トグルを保持する。**既定は `bool` の既定値 = false**（⑩ は構造として成立） |
| 文書属性 | `PrivateNoteDefaults` は `doc_scope` / `owner` / `confidentiality` の 3 つだけ。**トグルは無い** |
| チャンク属性 | `DocumentUpdatedConsumer` が `ev.Attributes` を**キーの制約なくそのまま**チャンクへ載せる |
| 検索 | `HybridSearchService.BuildFilters` は ABAC スコープだけを見る。**トグルを見る口が無い** |
| RAG | `RagOrchestrator` が**検索結果をそのまま**出典・文脈へ流す（4 行。2 経路） |

すなわち **トグルは索引・チャンク属性・ABAC 属性のいずれにも流れていない。**
⑨ に要るのは「属性へ載せること」と「RAG 経路で落とすこと」の 2 点である。

## 検討した選択肢

### 論点 A: トグルをどこへ写すか

| # | 案 | 評価 |
| --- | --- | --- |
| **A-1** | **既存の `Attributes` 辞書へ属性キー `ai_input` を 1 つ足す** | ✅ **採用**。属性は Qdrant のペイロードへネスト構造体で入り（`AttributeValueKeys`）**キーの値域は自由**。契約型を 1 バイトも変えずに RAG 経路まで届く |
| A-2 | `SearchResultDto` / `SearchRequest` へ専用の欄を足す | ❌ 契約型の変更（`contract-schema` の baseline 差分・全消費面へ波及）。`ConfidentialityLevels` が属性から読む先例と不揃いになる |
| A-3 | RAG 経路が DocumentService の台帳を都度引く | ❌ サービス境界を跨ぐ同期呼び出しを検索の hot path へ足す。`IADR-0153` が「検索の hot path に辞書引きを増やさない」と決めたのと逆向き |

### 論点 B: ⑨ の分離をどの層で行うか

| # | 案 | 評価 |
| --- | --- | --- |
| **B-1** | **RAG 経路（`AiAnalysisService`）の候補絞り** —— 検索結果を `RagContextPolicy.Select` に通し、`ContextChunks` だけを出典・文脈へ流す | ✅ **採用** |
| B-2 | Retrieval への「要求属性」—— `SearchRequest` に AI 入力用の絞り込みを足し、`RetrievalService` が索引の側で外す | ❌ 下記の 3 点 |
| B-3 | 索引を分ける（AI 入力可のチャンクだけの別コレクション） | ❌ 同じ本文を 2 重に索引する。埋め込み費用と再索引の整合が倍になり、`ADR-0016` の機密区分別コレクションと軸が交差する |

**B-2 を採らない理由**:

1. 🔴 **基準の向きと逆である。** ⑨ は「**検索結果には現れる**」ことを要求している。
   検索側を絞る形は、**⑨ の前半を壊す方向へ実装を引っ張る**（絞り込みを 1 段強めると
   「検索にも出ない」になり、基準が静かに半分だけ満たされる）。
2. **表現できない。** `AttributeFilter` は**許可リスト（肯定形）**であり、
   「個人資料のうち AI 入力 OFF **だけ**を外す」は書けない。`doc_scope` を持たない既存の組織文書が
   一斉に落ちる（`IADR-0270` 決定 2 が禁じた「否定で書く」形になる）。
   実現するには `must_not` 相当の否定形をポート `IVectorStore` と 2 つのアダプタ
   （Qdrant / InMemory）へ導入する必要がある。
3. **fail-closed の置き場として弱い。** 検索側で絞ると、**絞りを渡さなかった呼び出しが黙って
   全件許可へ倒れる**（`ScopeFilter` が「任意の追加引数にしない」と決めたのと同じ危険）。
   B-1 は `RagContextPolicy.Select` が**述語を必須引数**にしているため、渡し忘れがコンパイルで止まる。

## 決定

### 決定 1: トグルは ABAC 文書属性 `ai_input`（`included` / `excluded`）へ写す

- キーは `doc_scope`（`ADR-0054`）と同じスネークケース。値は 2 値。
- 値域と判定は `Knowledge.Contracts` の **`AiInputExposure` に 1 か所だけ**置く
  （`ConfidentialityLevels` と同じ形）。供給側（DocumentService）と消費側（AiAnalysisService）が
  同じ語彙を引く。
- **`includeInAi`（API の欄名）と `ai_input`（属性キー）は別物である。** 前者は台帳の状態を
  画面へ配る形、後者は**索引・検索の側から読める形へ写した投影**である。

### 決定 2: 判定は「明示値を優先し、既定は文書スコープで分ける」

| 条件 | 結果 | 理由 |
| --- | --- | --- |
| `ai_input == "included"` | true | 明示的な opt-in |
| `ai_input == "excluded"` | false | 明示的な opt-out |
| 欠落・空・未知値 **かつ 個人資料** | **false** | 🔴 **fail-closed**。トグル属性が欠落したら OFF 扱い |
| 欠落・空・未知値 **かつ それ以外** | **true** | **組織文書は従来どおり**（`ADR-0054` §結果「既存文書へ遡及付与しない」を壊さない） |

- **判定は集合帰属で書く**（`doc_scope == "private-note"`）。否定で書くと `doc_scope` を持たない
  既存の組織文書が一斉に該当する（`IADR-0270` 決定 2 と同じ作法）。
- **未知値を無条件に拒否しない。** 綴り間違い 1 つで組織文書が RAG から静かに落ち、
  「検索には出るのに回答に使われない」という原因の見えない縮退になる。個人資料側は逆に倒す。

### 決定 3: 分離は `RagOrchestrator.SearchAsync` の 1 点で行う

- `SearchAsync` は検索結果をそのまま返すのをやめ、**`RagContextSelection` を返す**。
- `GenerateAsync` / `AskStreamAsync` は **`ContextChunks` だけ**を出典（`CitationMapper.ToCitations`）と
  文脈（`BuildContext`）へ流す。
- **越境判定（`FR-11`）も `ContextChunks` で測る。** LLM へ渡さないチャンクの機密区分で送信先を
  決めると、**送っていない資料のせいで回答が縮退する**。
- 除外したチャンク ID は**ログへ残す**（静かに落とさない）。

**なぜ 2 経路それぞれではなく `SearchAsync` へ寄せるのか。** RAG が検索結果を得る口はここ 1 つで
あり、寄せておけば**経路を足した人が配線を落としても、そもそも生の検索結果を受け取れない**。
計画が名指しした「素直に作ると満たされない」を、規律ではなく型で防ぐ。

### 決定 4: 属性の供給は「作成既定」と「トグル変更」の 2 点。**版は進めない**

- `PrivateNoteDefaults` に `ai_input = excluded` を足す —— ⑩ を「値の不在」ではなく
  **明示された OFF** として持つ（決定 2 の fail-closed 分岐が消えても倒れない多層防御）。
- `PUT /private-notes/{id}/exposure` が台帳を更新した後、文書属性 `ai_input` を同じ値へ写す。
- 🔴 **`Document.UpdateMetadata` を使わない。** 同メソッドは `Touch()` で `Version++` し版履歴へ
  スナップショットを積む。**露出トグルは本文の編集ではない** —— `FR-19` は「編集の回数だけ版を
  保持」と定めており、トグルで版が増えると（a）版履歴が編集以外で膨らみ、（b）Obsidian 同期の
  `baseVersion` が動いてプラグインが 409 を受ける。**版・時刻を動かさない設定点**を `Document` へ
  置く（先例: `RecordContentFingerprint`）。

## 理由

- **決定 1・2** は「判定規則の真実源を 1 か所へ保つ」ため。`ConfidentialityLevels` が
  「出典表示（FR-04）と越境判定（FR-11）が同じ規則から導かれるよう読み取りを 1 か所へ寄せる」と
  したのと同型で、**供給側と消費側が別々に綴りを解釈する余地を残さない**。
- **決定 3** は計画が名指しした失敗の形（検索結果をそのまま LLM へ渡す）を、**型で塞ぐ**ため。
- **決定 4** は `FR-19` の「版 = 編集の回数」という定義を守るため。

## 結果

### 良くなること

- ⑨ が**否定形と陽性対照の対**で機械検査できる（AI 入力 OFF は落ち、ON は入る）。
- **検索経路は 1 バイトも変わらない** —— ⑨ の前半（検索結果には現れる）が構造的に保たれる。
- 属性キーが 1 つ増えるだけなので、**契約型・スキーマ・マイグレーションはいずれも不要**。

### 悪くなること・受け入れる制約

- **RAG の文脈は topK より小さくなり得る**（除外した分を補充しない）。
  補充すると「AI 入力 OFF の資料があるかどうか」が結果件数から推測できてしまう
  （`IADR-0009` の存在秘匿と同じ向き）ため、**補充しない**ことを選ぶ。
- **属性はチャンクへ焼き込まれる。** トグルを変えても**再索引されるまで古い値が残る**。
  現時点では個人資料が索引へ流れない（下記 残件）ため実害は無いが、生産側を開けるときは
  **トグル変更が再索引（`DocumentUpdated` の再発行）を伴う**必要がある。これは残件の一部である。

### 🔴 残件（本 IADR の射程外。引き継ぎ条件つき）

1. **生産側の配線** —— 露出トグル「横断検索に含める」ON で個人資料を索引へ流すこと
   （`DocumentUpdated` の発行）。`IADR-0270` 決定 5 が発行しないと決めており、**覆すには同 IADR の
   改定が要る**。同決定が発行を止めた理由は「**ON を安全に絞る消費側（Retrieval / AiAnalysis の
   分岐評価）が未了**」であり、**本 IADR がその消費側である**。
   引き継ぎ条件: (a) `IADR-0253` 段 3（所有者ベースの分岐）が検索経路で効いていること、
   (b) トグル変更が再索引を伴うこと、(c) OFF へ戻したときの索引からの撤収経路があること。
   **消費側を先に入れるのは安全側の順序である**（生産側が無い間、個人資料はそもそも索引に無い）。
2. **「ナレッジグラフへの表示」トグルの配線**（`GraphDocumentSyncConsumer`）。⑨ は検索と AI 入力の
   2 つだけを名指ししており、本 IADR の判定対象ではない。
3. **他の LLM 入力面**（AI 提案 `FR-18`・MCP `FR-16`）。前者は承認前提の別経路、後者は
   `ADR-0034` によりサービスアカウント実行時に個人資料を対象外としており（`ServiceAccountDocumentFilter`）、
   **いずれも別の統制で閉じている**。本決定はそれらを変えない。

［2026-09-05 追記 / #1184］**残件 1（生産側の配線）と残件 2（グラフのトグルの配線）は閉じた。**
計画 `ADR-0061`（planning#492）の裁定を受け、`IADR-0270` 決定 5（後継
[IADR-0396](./IADR-0396_private-note-exposure-index-production.md)）を解除して
露出 3 トグルを索引の生産側へ配線した。引き継ぎ条件 (a)(b)(c) はいずれも満たしている ——
(a) 段 3 は検索経路で効いており、(b) トグル変更は `DocumentUpdated` の再発行を伴い、
(c) 全 OFF へ戻すと索引・グラフから**削除**される。
**本 ADR の決定 1〜4 は現行である**（判定の実体は `AiInputExposure` から
`DocumentExposure` へ移り、`AiInputExposure` は委譲する別名として残っている。IADR-0396 決定 2）。
**本文は当時の記録として書き換えない。**
