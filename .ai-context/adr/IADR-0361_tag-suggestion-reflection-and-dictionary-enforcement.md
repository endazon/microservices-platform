---
title: IADR-0361 タグ提案の承認は承認者本人の資格で文書サービスへ反映してから確定し、辞書の値域は生成段・承認段の両方で強制し、認可は write または管理者ロールの選言にする
type: impl-adr
status: Proposed
related_ids: [FR-17, FR-18, UC-10, SC-03, SC-05, SC-09, SC-21, ADR-0033, ADR-0034, ADR-0036, ADR-0043, ADR-0059, ADR-0063, IADR-0044, IADR-0122, IADR-0152, IADR-0153, IADR-0266, IADR-0272, IADR-0299, IADR-0300, IADR-0323, IADR-0349]
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0063_ai-tag-suggestion-reflection-and-authorization.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md
  - planning:projects/microservices-platform/07_adr/ADR-0043_search-facets-and-attribute-exposure.md
issue: "#1187 / #1014"
---

# IADR-0361: タグ提案の反映経路・辞書の値域強制・承認の認可の選言

- 状態: Proposed
- 日付: 2026-09-03
- 決定者: claude（実装判断）／起点 issue #1187（planning#495 / ADR-0063 の受け皿）・#1014（同一 PR で閉じる）

## 起点・関連

- 関連する計画書 ID: FR-18 / UC-10 / SC-03（AI 提案の承認欄）/ SC-05（既定タグ辞書に整合）/ SC-09（タグ辞書）/
  SC-21 / `ADR-0033` 決定 7・10 / `ADR-0036` D-07 / `ADR-0043` 決定 1 / `ADR-0059` /
  **`ADR-0063` 決定 1〜5・§結果 フォローアップ 3**（Accepted / 2026-08-29）
- 関連する実装仕様書: `.ai-context/specs/20260903_issue-1187_tag-suggestion-reflection-and-dictionary.md`
- 前提 IADR: `IADR-0266`（提案生成の封と権限境界）/ `IADR-0272`（write 判定と 404）/
  `IADR-0300`（承認欄。**決定 4 は本 IADR で失効する**）/ `IADR-0323` / `IADR-0349`（生成段の構成）/
  `IADR-0152` `IADR-0153`（タグ辞書の契約・保存）/ `IADR-0299` 決定 4（メッシュ内部 API の統制）/
  `IADR-0044`（後段の最終防衛線）

## コンテキストと課題

`IADR-0300` 決定 4 の時点で、タグ提案の承認は「状態を `approved` にするだけで文書のタグは増えない」
no-op であり、SC-03 は承認だけを理由つきで実行不可にしていた（計画との差異として環流。planning#495）。
裁定（`ADR-0063`）は次を確定した。

1. 承認は**文書のタグへ反映するところまで**を要求とする
2. 反映できる値は **SC-09 のタグ辞書に定義済みのタグに限る**。辞書外は生成しない。生成されてしまったら承認できず却下のみ
3. 認可主体は**承認者本人**。①その文書への `write` **または** ②SC-05 の管理者経路のロール。サービスが代わりに書く形は採らない
4. 承認と却下は同じ権限に従う
5. 暫定表示は資格の有無で 2 つに分け、「準備中」の側は反映の実装をもって消す

同時に #1014（タグ提案がタグ辞書の値域に収まることをどの層も強制していない）を閉じる。
実装設計上の論点は **生成段が辞書をどの主体で読むか**（#1014 ⑦）と、**反映の権限伝播の方式**である。

## 検討した選択肢

### A. 反映の権限伝播

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A-1（採用）** | GraphService が承認者の `Authorization` ヘッダをそのまま DocumentService へ転送する（`RagOrchestrator` → RetrievalService と同型） | 決定 3 そのもの。後段が同じ選言を再判定でき（最終防衛線）、監査に承認者本人が残る |
| A-2 | GraphService のサービスアカウントで書く | 決定 3 が明示的に退けた（承認者の権限を超えた書き込み。誰の意思か追えない） |
| A-3 | イベント（`SuggestionApproved`）を発行し DocumentService が購読して付ける | 反映が非同期になり「承認したのに付かない」が再現する。認可主体もイベントに載せた文字列になる |

### B. 生成段が辞書を読む主体

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | 利用者の資格で `/tags` を引く | `/tags` は管理者・運用者限定（SC-05 Q18）。**一般利用者の生成が全件 403 で 0 件になる** |
| **B-2（採用）** | DocumentService に**名前だけ**を返すメッシュ内部 API `GET /internal/tags/names` を足し、GraphService 自身が読む | `IADR-0299` 決定 4 と同じ統制（mesh mTLS ＋ NetworkPolicy）。辞書はプロンプトへ入るだけで**利用者へ丸ごと返らない**（`ADR-0043` 決定 1 に触れない） |
| B-3 | GraphService が辞書を複製して持つ（イベント購読） | 権威が 2 つになり、改名・削除の追随に新しい経路が要る。生成頻度に対して過剰 |

### C. 承認段の辞書判定点

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C-1 | GraphService が承認前に辞書を引いて自分で拒む | 判断点が 2 つになる（生成段で通した値が承認段で別の比較で落ちる） |
| **C-2（採用）** | DocumentService の反映口が `TagResolver.ToIdsAsync` を権威として 400 を返し、GraphService はそれを `unknown_tag` に写す | 辞書の権威が 1 つ。**識別子化した以上、辞書に無い名前は物理的に保存できない** |

## 決定

### 決定 1: 反映経路 —— DocumentService `POST /documents/{id}/tags` を承認者本人の資格で呼び、反映が確定してから `approved` にする

- DocumentService に**文書へタグを 1 つ足す**口 `POST /documents/{id}/tags`（本文 `AddDocumentTagRequest(Name)`、
  応答 `DocumentDto`）を新設する。route group は**認証のみ**（`PutBody` と同じ。ロールを積むと①が死ぬ）。
  - 認可は決定 3 と同じ選言を**最終防衛線として再判定**する（`IADR-0044`）。拒否は **404**（存在秘匿。403 にしない）。
  - **認可は辞書照合より前**（書けない主体に「その名前は辞書に無い」を返さない）。
  - 辞書外は **400**（`UnknownTagsProblem`）。**冪等**: 既に付いていれば 200 を返すだけで版もイベントも進めない。
    付けたときは `Document.AddTag` が版を 1 つ進め（`changeNote: ai-suggestion-approved`）、`DocumentUpdated` を再発行する。
    本文指紋は変わらないので却下解除（`ADR-0050`）は発火しない。
- GraphService は `IDocumentTagWriter`（4 値の結果 `Applied / UnknownTag / NotWritable / Unavailable`。例外を投げない）
  を通して呼ぶ。実装 `HttpDocumentTagWriter` は**要求の `Authorization` ヘッダをそのまま転送する**（方式 A-1）。
  **サービスアカウントを持たない。** 転送を外すと後段が匿名として拒むので、転送の陽性対照を `HttpMessageHandler` 層で固定する。
- 承認の口（`Kind == Tag`）の順序: 既存の 4 段（read スコープ → 存在 → 端点の可視性 → 決定 3 の資格）→
  **`pending` でなければ後段を呼ぶ前に 409** → 反映 → `Applied` なら `TryApprove` → `SaveChanges`。
  - `UnknownTag` → **400 `unknown_tag`**（状態は `pending` のまま。決定 2 後段「承認できず却下のみ」。`unknown_edge_type` と同じ形）
  - `NotWritable` → 404（存在秘匿の一本道） / `Unavailable` → **502**（**成功へ縮退しない**）
  - 反映が成功して `SaveChanges` だけ失敗したときは文書にタグが付いたまま提案が `pending` に残る。再承認は後段が冪等なので二重には付かない。

### 決定 2: 辞書の値域は生成段で強制する —— 内部 API を GraphService 自身が読み、辞書外を落とし、引けなければ fail-closed

- DocumentService に `GET /internal/tags/names`（`TagNamesResponse(Names)`。**使用件数を返さない**）を足す。
  `/internal/knowledge-health/observations`（`IADR-0299` 決定 4）と同じ**メッシュ内部 API**（認証なし・OpenAPI に載せない・
  第一防御は mesh mTLS とネットワーク分離）。**利用者スコープで `/tags` を引かない**（案 B-1 を退けた理由）。
- ポート `ITagDictionaryReader.ReadNamesAsync` → `IReadOnlySet<string>?`。**null ＝引けなかった**（空集合とは別）。
- `AiSuggestionGenerator` は辞書を `SuggestionPrompt.Seal` へ渡し（辺の型と同じく「LLM に選ばせる値集合」。`Render` に「## タグ」節）、
  `PersistAsync` で**辞書に無い値を落とす**（比較は **Ordinal** —— DocumentService の `TagResolver.ToIdsAsync` と同じ比較でないと、
  生成段で通した値が承認段で落ちる）。引けなかったときは**タグ提案を 1 件も作らない**（リンク提案は影響を受けない）。
- 落とした件数は OTel カウンタ `graph.tag_suggestion_dropped.total`（理由 `out_of_dictionary` / `dictionary_unavailable`）で数える。
  **タグ値をタグにもログにも出さない**（LLM の自由文で基数が無界、本文由来の語を含み得る）。`EdgeTypeFallbackMetrics` と同型。0 が正常。

### 決定 3: 承認・却下の資格は「①起点文書への `write` または ②`platform-admin`」の選言。種別で分けない。拒否は 404

- `AiSuggestionEndpoints.CanDecideAsync` ＝ `IsSourceWritableAsync`（①。`ADR-0036` D-07）**または** `platform-admin`（②）。
  ②でも起点文書の実在は要り、**可視性の判定は先に立つ**（`ADR-0034` 決定 8。管理者でも見えない端点の提案は 404）。
- ②は **`platform-admin` のみ**。SC-05「作成・編集は管理者限定」・DocumentService `UpdateMetadata` の `AdminOnly` と揃える。
  運用者を含めると、承認欄は「できる」と描いたのに後段（最終防衛線）が 404 で拒む形になる。
- `Approve` / `Reject` の両方に適用する（決定 4）。**種別で分けない** —— リンク提案の承認にも②が効く
  （`ADR-0063` §結果「認可の判定が ADR-0059 と揃う。所有者、または管理者経路という同じ形」）。
- 拒否は **404** のまま（`IADR-0272` 決定 5）。既存の否定形テストは既定ロールが `platform-admin` のため、
  ロールを落として（`viewer`）否定形を保つ。

### 決定 4: 資格はサーバが判定し `AiSuggestionDto.CanDecide`（既定 `false`）で行ごとに運ぶ

- `AiSuggestionDto` の末尾に **`bool CanDecide = false`** を足す（既定値つき＝非破壊。`IADR-0122` 決定 2）。
  既定 `false` は deny 側（載せ忘れた経路は「できない」と描かれる）。
- 一覧は行ごとに `CanDecideAsync` の結果を載せる。write スコープは**要求ごとに 1 回**だけ解決し、**可視な行が 0 件なら解決しない**。
- 承認・却下の応答は `CanDecide = true`（通った以上、資格はある）。

### 決定 5: SC-03 はタグ提案の行を `canDecide` で 2 つに分けて描く。`IADR-0300` 決定 4 は失効

- **持つ**: 承認・却下とも押せる。「準備中」「未実装」の文言は**存在しない**（`ADR-0063` フォローアップ 4）。
- **持たない**: 承認・却下とも押せず「この文書のタグを編集する権限がありません。」を画面上のテキストとして出す（恒久）。
  却下も塞ぐのは決定 4（押せても 404 になる）。値が欠けていれば「持たない」に倒す。
- 承認が 400 `unknown_tag` で返ったときは「このタグは辞書に無いため反映できません。却下してください。」を出す
  （状態コードだけで判定せず、本文 `error` を見る。400 は検証エラー一般の器である）。
- **リンク提案の行は従来どおり**（ボタン有効・拒否は 404 → 汎用エラー）。決定 5 の射程はタグ提案である。
- **`IADR-0300` 決定 4「承認だけを実行不可にする」は本決定で失効する。** 旧記録は凍結（本文を書き換えない）。

### 決定 6: 辞書の削除・改名と承認済みタグ・提案行の関係（#1014 受け入れ基準 5）

- **改名**: 文書はタグを識別子で参照する（`IADR-0153` 決定 1）ので、反映済みのタグは自動で新しい名前になる。
- **削除**: 使用件数 1 以上のタグは削除できない（SC-09）ため、反映済みのタグは剥がれない。
- **提案行の `TagValue` は記録であり、改名に追随させない。** 改名後に `pending` の提案を承認すると、旧名は辞書に無いので
  400 `unknown_tag`（却下のみ）になる —— 「生成時点の辞書で通った値が、承認時点の辞書で通らない」を承認段の権威が拾う形であり、
  承認段を置く理由そのものである。

## 理由

- **反映が確定してから状態を進める**のは、「承認済みなのにタグが付いていない」が利用者にとって最悪の状態だからである
  （`IADR-0300` が承認を塞いだ理由と同じ）。502 で `pending` に留めれば、再試行が後段の冪等で安全に効く。
- **辞書の権威を 1 つにする**（案 C-2）ことで、生成段の照合は「LLM が値集合を守っているかの前段フィルタ」に留まり、
  承認段の 400 が最終的な真である。決定 6 の「改名後の旧名の提案」はこの構造が要る典型例である。
- **内部 API で GraphService 自身が読む**（案 B-2）のは、SC-05 Q18 と `ADR-0043` 決定 1 を両方満たす唯一の形である。
  残余リスク（メッシュ内から辞書の**名前**を無認証で読める）は `IADR-0299` と同じ受容とする。

## 結果

- 良い影響:
  - SC-03 の承認欄が計画どおり「その場で承認／却下できる」を満たす。planning#495 の差異が閉じる
  - 取り込み文書（`owner=system`）の提案を管理者が処理できる（②が無いと誰も承認も却下もできなかった）
  - #1014 が両段で閉じ、辞書外の値が文書に付く経路が無い（変異試験で照合の実効を確かめた）
- 悪い影響・トレードオフ:
  - GraphService → DocumentService の同期呼び出しが承認経路に入る（後段不達 → 502）
  - 辞書が大きくなるとプロンプト長が伸びる（値集合をそのまま渡す。フォローアップ 2）
  - `/internal/tags/names` の残余リスク（上記受容）
- フォローアップ:
  1. **リンク提案の行にも `canDecide` で表示を分けるか**（決定 5 の射程外。現状はタグ提案の行だけ）
  2. **辞書が大きくなったときのプロンプト長**（上限を置くなら「どの部分集合を渡すか」の決めが要る）
  3. `IADR-0300` 決定 4 を参照する live な文書が残っていないかは定期棚卸しで確かめる（本 PR で `docs/screens` `docs/tests` は追随済み）
