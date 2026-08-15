---
title: Wiki.js の個人スコープ可視性制御は編集導線で成立しない — SC-19 の 1 導線について新 ADR の起案が要る
type: plan-feedback
status: open
category: 前提検証の結果
related_ids: [FR-19, FR-20, SC-19, ADR-0011, ADR-0036, ADR-0037]
source_repo: microservices-platform
source_ref: "claude/issue-response-handoff-2hl25v / docs/specs/20260815_issue-602_wikijs-personal-scope-spike.md（実装側 issue #602）"
author: Claude（実装）
created: 2026-08-15
dispatched: true
planning_issue: 346
---

# フィードバック: Wiki.js の個人スコープ可視性制御の検証結果

## 依頼された検証

計画 `INDEX.md` **決定 33**（planning#250 の裁定・利用者裁定 2026-08-07）が
「前提検証（Wiki.js の個人スコープ可視性制御）は microservices-platform#449 から切り出して
**先に単独で実施する**」と定めた。`ADR-0037` の着手可否の注記も同じことを述べている。

**実施した。結論を報告する。**

## ★ 最初の判定 —— 実環境は要らなかった

planning#250 は案 C の欠点として「実環境（Wiki.js 稼働）が要る可能性がある。その場合は実装側では
実施できない」を挙げていた。**要らなかった。** 実装の読解・確定済み ADR・Wiki.js 公式ドキュメントの
3 つで結論が出る。

**理由は、実装が Wiki.js の権限モデルに依存しないことを明示的な設計判断として持っているためである**
（実装側 `IADR-0021`）。依存が無いので、Wiki.js の挙動を実測しても読み取り経路の結論は変わらない。

## 結論 —— 閲覧は成立する。編集は成立しない

| 経路 | 成否 |
| --- | --- |
| **閲覧（一覧・本文）** | **Wiki.js は障害にならない。ただし成立していない。** ゲートウェイは経路上にあるが、認可スコープ契約が `ADR-0036` の `read`（3 節の選言）を表現できない（後述） |
| **編集（`SC-19` の「本文を編集（Wiki.js）」導線）** | **成立しない。** 利用者を Wiki.js UI へ送った時点でゲートウェイが経路から外れる |

### 根拠 1: 実装は Wiki.js に認可を持ち込んでいない

`IWikiJsClient` の定義コメント（原文）:

> 認可は WikiService（ゲートウェイ）が単一真実源であり、**Wiki.js 側には ABAC 権限を持ち込まない**

Wiki.js へ渡すのは `WikiJsPage(Path, Title, Markdown, Tags, IsPrivate)` のみで、**属性集合を渡していない**。
`IsPrivate` は機密区分由来の粗粒度な表示制御であり、同コメントが「**ABAC の代替ではなく**多層防御」と明記している。

### 根拠 2: Wiki.js の権限はグループ単位・パス条件のみ（一次情報）

公式ドキュメント（`requarks/wiki-docs` の `groups.md`）:

- 「**A group defines what users can see and what they can do**」「A user can be part of one or more groups」
  → **割り当ての単位はグループ**
- ページルールの条件は **`Path Starts With` / `Path Ends With` / `Path Matches Regex` / `Path Is Exactly` の 4 つのみ**
- **個人・所有者単位のスコープへの言及は無い**

Wiki.js のフィードバックサイトに「Page ownership permissions」「Per Page Permissions」が
**未実装の要望として起票されている**ことも、標準機能として存在しないことの裏づけである。

## したがって新 ADR の起案が要る

計画 決定 33 は「**出し分けが成立しない場合、編集手段の裁定（planning#74）を改める新 ADR を起案する。
判断者は利用者である**」と定めている。**その局面に該当する。**

**覆るのは `SC-19` の「本文を編集（Wiki.js）」導線ただ 1 つである**（決定 33 の明示どおり）。
`ADR-0037` 決定 1〜20・`SC-20` 全体・決定 14（KB を唯一の正とする）は覆らない。

## 判断材料 —— 代替案 3 つ

| 案 | 内容 | 実装側の評価 |
| --- | --- | --- |
| **A. 個人資料を Wiki.js へ同期しない** | `private-note` を push 対象から外し、`SC-19` の編集導線を「個人資料以外」に限定する | **実装側の見立てはこれ。** 現行の `IsPrivate`（粗粒度・多層防御）と同じ考え方の延長で、**新しい機構を持ち込まない**。失うのは「個人資料を Wiki.js で編集する」体験のみ |
| B. WikiService が編集も仲介する | 編集 UI をゲートウェイ側に置き、Wiki.js は描画に留める | 認可は保てるが **`ADR-0011` が Wiki.js を採用した理由（編集体験）を失う**。実装量も大きい |
| C. 利用者ごとに Wiki.js グループを作る | 1 利用者 = 1 グループ ＋ パス規約でページルールを当てる | 技術的には可能（パス前方一致がある）。ただし**グループ数が利用者数に比例**し、参加・退職のたびに同期が要る。**認可の一部が Wiki.js 側へ漏れる**ため `IADR-0021` と衝突する |

## 併せてお伝えすること —— 閲覧側も現状は動かない

**「Wiki.js が障害ではない」と「出し分けができる」は別である。** 閲覧側は **3 つの部品**が欠けている。

| # | 部品 | 実測 |
| --- | --- | --- |
| 1 | 文書の **`owner` 属性** | **0 件**（実装側 #456 / PR #515。**測定時点の値**であり同日再実行では 2,428 件中 0 件） |
| 2 | **`${current_user}` の動的束縛**（`doc.owner ∈ { ${current_user} }`） | **バックエンド全体で 0 件** |
| 3 | **認可スコープ契約が `read` の選言を表現できること** | **表現できない** |

### ★ 部品 3 —— 契約が `ADR-0036` の `read`（選言）を運べない

`06_technical/07_abac-attribute-model.md` の `read` は「次の**いずれか**を満たす場合に許可する（OR）」で、
属性ベース・所有者ベース・共有先ベースの **3 節の選言**である。

対して実装の契約 `AccessScopeResponse(UserId, List<AttributeFilter>, Granted)` は**単一の連言**しか運べない。
`AbacEvaluator` は複数ポリシーがマッチしても**キー単位 union で 1 本へ潰し**、
`AbacPageFilter` は `All(...)`（**フィルタ間 AND・属性欠落は不一致**）で評価する。

**帰結**: 「個人資料（`owner=me`）」と「組織文書（`confidentiality∈{internal}`）」のポリシーが同時にマッチすると
**AND** になり、**両方を満たす文書しか見えない**。さらに第 3 節の `shared_with` は、文書属性の値が
**単一文字列**であるため**複数値の共有先リストを表現できない**。

**部品 3 は 1・2 より射程が広い**（`Platform.Shared.Contracts`・`AbacEvaluator`・検索側フィルタに跨る）。
**部品 1・2 は実装側 #516 / planning#344 が扱っているが、部品 3 はどちらの射程にも入っていない。**
**案 A を採る場合も閲覧側の個人スコープは必要になる**ため、ここは連動する。

## 参照

- 実装側 issue: microservices-platform#602（本検証）／ #516（`owner` 属性）
- 実装側 作業仕様書: `docs/specs/20260815_issue-602_wikijs-personal-scope-spike.md`
- 実装側 ADR: `IADR-0021`（Wiki.js へ認可を持ち込まない）／ `IADR-0119`（着手保留）
- 計画側 issue: **planning#346**（本記録の裁定依頼）
- 計画: `INDEX.md` 決定 33 ／ `ADR-0037` 着手可否の注記 ／ `ADR-0011` ／ planning#74 ／ planning#250
