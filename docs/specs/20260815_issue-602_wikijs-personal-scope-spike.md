---
title: 作業仕様書 — Wiki.js の個人スコープ可視性制御を単独で検証する（#602）
type: spec
status: done
related_ids:
  - FR-19
  - FR-20
  - SC-19
  - ADR-0011
  - ADR-0036
  - ADR-0037
  - IADR-0021
  - IADR-0119
  - IADR-0142
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - "../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md"
  - "../adr/IADR-0142_fr19-20-scoped-release-by-overturn-range.md"
  - "../../feedback/20260815_wikijs-personal-scope-verification.md"
---

# 作業仕様書 — Wiki.js の個人スコープ可視性制御の検証（#602）

## 1. 起点

計画 `INDEX.md` **決定 33**（planning#250 の裁定・利用者裁定 2026-08-07）が
「**前提検証（Wiki.js の個人スコープ可視性制御）は #449 から切り出して先に単独で実施する**」と定めた。
`ADR-0037` の「着手可否の注記」も同じことを述べている。**本作業はその検証である。**

**懸かっているのは `SC-19` の「本文を編集（Wiki.js）」導線ただ 1 つ**であり、
`ADR-0037` 決定 1〜20・`SC-20` 全体・決定 14 は**覆らない**（計画 決定 33 が明示）。

## 2. ★ 最初の判定 —— **実環境は要らない**

本 issue は「**これが最初の判断項目である**」として、実環境（Wiki.js 稼働）の要否を先に決めることを求めていた。

**要らない。** 結論は次の 3 つの読解で確定し、Wiki.js を起動して試す必要が無い。

| 情報源 | 得られたこと |
| --- | --- |
| **本リポジトリの実装** | 認可の経路そのもの（後述 §3） |
| **`ADR-0011` / `IADR-0021` の決定** | Wiki.js に ABAC 権限を持ち込まない設計が既に確定している |
| **Wiki.js 公式ドキュメント（一次情報）** | 権限がグループ単位であり、ページルールの条件がパスのみであること |

**「動かしてみないと分からない」のは、設計上そこに依存がある場合である。** 本件は逆で、
**実装が Wiki.js の権限モデルに依存しないことを明示的な設計判断として持っている**（IADR-0021）。
したがって Wiki.js の挙動を実測しても、読み取り経路の結論は変わらない。

## 3. 実測 —— 現行 WikiService が何を担っているか

**issue 本文や過去の記録を転記せず、コードを読んだ。**

### 3.1 認可はゲートウェイが単一真実源である

`src/knowledge/backend/Services/WikiService/src/WikiService.Api/Foundation/Ports/IWikiJsClient.cs` の
インターフェース定義コメント（原文）:

> 認可は WikiService（ゲートウェイ）が単一真実源であり、**Wiki.js 側には ABAC 権限を持ち込まない**
> （本 IF は「内容の反映」と「本文の取得」のみを担う）

`GetRenderedContentAsync` にも「**ABAC 通過後にのみ呼ぶ**」と書かれている。

### 3.2 Wiki.js へ渡すのは粗粒度の `IsPrivate` だけ

`WikiJsPage(Path, Title, Markdown, Tags, IsPrivate)` —— **属性集合は渡していない**。同ファイルの原文:

> `IsPrivate` は機密区分由来の「粗粒度な表示制御」（ADR-0011: Wiki.js は表示制御に留める）。
> **ABAC の代替ではなく**、ネットワーク分離（IADR-0017）が退行・誤設定された場合でも public 以外の
> 文書が Wiki.js 上で無条件公開にならないための**多層防御**（deny-closed）

### 3.3 ABAC の判定は属性フィルタである

`AbacPageFilter.Matches` は `AccessScopeResponse.AllowedFilters`（`AttributeFilter(Key, AllowedValues)`）を
ページ属性へ突き合わせる。フィルタ間 AND・値集合内 OR・**属性欠落は不一致**（安全側）・
`Granted=false` は deny-by-default。

### 3.4 ★ 個人スコープに必要な部品が **3 つとも未実装**である

| # | 必要な部品 | 実測 |
| --- | --- | --- |
| 1 | 文書の **`owner` 属性** | **0 件**（#456 / PR #515。**測定時点の値**であり、同日再実行では 2,428 件中 0 件） |
| 2 | **`${current_user}` の動的束縛**（`ADR-0036` の判定規則） | **バックエンド全体で 0 件**（`current_user` / `${current` の全走査） |
| 3 | **認可スコープ契約が `ADR-0036` の `read`（選言）を表現できること** | **表現できない**（下記） |

`WikiAccessResolver.ExtractUserAttributes` が JWT から取り出すのも **`clearance` と `department` の 2 つだけ**で、
所有者は見ていない。

#### 部品 3 —— 契約が選言を運べない（当初 2 部品と数えていた誤り）

計画 `06_technical/07_abac-attribute-model.md` の `read` 判定規則は **3 節の選言**である（原文）。

> `read` 許可: 次の **いずれか** を満たす場合に許可する（OR）。
> 1. **属性ベース**: `doc.confidentiality in …` かつ `doc.department in …` かつ `doc.lifecycle in …`（すべて AND）
> 2. **所有者ベース**: `doc.owner ∈ { ${current_user} }`
> 3. **共有先ベース**: `${current_user} ∈ doc.shared_with` または `doc.shared_with ∩ ${current_groups} ≠ ∅`

対して実装の契約は**単一の連言**しか運べない。

| 箇所 | 実測 |
| --- | --- |
| `AccessScopeDto.cs` の `AccessScopeResponse(UserId, List<AttributeFilter>, Granted)` | ポリシーの**選言を表す構造が無い** |
| `AbacEvaluator` | 複数ポリシーがマッチしても**キー単位 union で 1 本の連言へ潰す** |
| `AbacPageFilter.Matches` | `AllowedFilters.All(...)` ＝ **フィルタ間 AND**・**属性欠落は不一致** |

**帰結**: 「個人資料（`owner=me`）」ポリシーと「組織文書（`confidentiality∈{internal}`）」ポリシーが同時にマッチすると、
スコープは `[owner∈{me}, confidentiality∈{internal}]` の **AND** になり、**両方を満たす文書しか見えない**。
`owner` を持たない既存文書は属性欠落で全滅する。**`${current_user}` を解決して `owner` を付けただけでは成立しない。**

さらに第 3 節（`shared_with`）は、`page.Attributes` の値が**単一文字列**であるため（`TryGetValue(f.Key, out var v)`）、
**複数値の共有先リストを表現すること自体ができない**。

**部品 3 は 1・2 より射程が広い** —— `Platform.Shared.Contracts`（platform ユニットの共有契約）・
`AbacEvaluator`・検索側フィルタに跨る。

## 4. 一次情報 —— Wiki.js の権限モデル

公式ドキュメント（`requarks/wiki-docs` の `groups.md`）で確認した。

| 問い | 原文が示すこと |
| --- | --- |
| 割り当ての単位 | 「**A group defines what users can see and what they can do**」「A user can be part of one or more groups」＝ **グループ単位** |
| ページルールの条件 | **`Path Starts With` / `Path Ends With` / `Path Matches Regex` / `Path Is Exactly` の 4 つのみ**（パスのみ） |
| 個人・所有者スコープ | **言及が無い**。権限管理はすべてグループレベル |

Wiki.js のフィードバックサイトには「Page ownership permissions」「Per Page Permissions」が
**未実装の要望として起票されている**（＝ 標準機能として存在しない裏づけ）。

## 5. 結論 —— **読み取りは成立する。編集は成立しない**

| 経路 | 成否 | 理由 |
| --- | --- | --- |
| **閲覧（一覧・本文プロキシ）** | **Wiki.js は障害にならない。ただし成立していない** | ゲートウェイが経路上にあり Wiki.js の権限モデルに依存しない（`IADR-0021`）。**しかし §3.4 の 3 部品が未実装**で、とくに**部品 3（契約が選言を運べない）は構造上の欠落**である。**「Wiki.js のせいではない」と「出し分けができる」は別である** |
| **編集（`SC-19` の「本文を編集（Wiki.js）」導線）** | **成立しない** | 利用者を Wiki.js UI へ送った時点で **WikiService が経路から外れる**。Wiki.js はグループ単位・パス条件のみで、所有者本人だけに見せる手段を持たない |

**したがって計画 決定 33 が想定した「出し分けが成立しない場合」に該当する。**
`SC-19` の当該 1 導線について、**編集手段の裁定（planning#74）を改める新 ADR を計画側が起案する**局面である。

## 6. 判断材料（計画側が新 ADR を起案するために）

**代替案を 3 つ挙げ、実装側の見立てを添える。判断者は利用者である。**

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A. 個人資料を Wiki.js へ同期しない** | `private-note` を push 対象から外す。`SC-19` の編集導線は「個人資料以外」に限定する | **実装側の見立てはこれ。** 現行の `IsPrivate`（粗粒度・多層防御）と同じ考え方の延長であり、**新しい機構を持ち込まない**。失うのは「個人資料を Wiki.js で編集する」体験のみ |
| B. WikiService が編集も仲介する | 編集 UI をゲートウェイ側に置き、Wiki.js を描画エンジンとしてのみ使う | 認可は保てるが、**Wiki.js の編集体験（`ADR-0011` の採用理由）を失う**。実装量も大きい |
| C. 利用者ごとに Wiki.js グループを作る | 1 利用者 = 1 グループ ＋ パス規約（`/private/<user>/…`）でページルールを当てる | 技術的には可能（ページルールはパス前方一致を持つ）。ただし**グループ数が利用者数に比例**し、参加・退職のたびに同期が要る。**Wiki.js 側に認可の一部が漏れる**ため `IADR-0021` の「持ち込まない」と衝突する |

**なお案 A を採る場合も、閲覧側の個人スコープには §3.4 の 3 部品が要る。**
**部品 1・2 は #516 / planning#344 が扱っているが、部品 3（契約が選言を運べない）はどちらの射程にも入っていない。**
planning#346 へ補足した（送付済み記録の本文は書き換えない —— `.claude/rules/traceability.md`）。

## 6.1 母集合（`.claude/rules/traceability.md` §是正・追随の母集合の取り方）

本作業は**条文上の事実（「前提検証は未了」）を反転させる**ものであり、規則 7・8 が掛かる。
**誤りの側＝否定する語で追跡下を全数走査した**（submodule・`planning/`・`CHANGELOG.md` を除外）。

### 走査語

`前提検証` / `実環境が要る` / `未了` / `個人スコープ` / `本文を編集` / `編集導線` / `編集手段` / `覆り得る` / `SC-19`

### 追随が要ったもの

| 文書 | 追随 |
| --- | --- |
| **`docs/adr/IADR-0142`** | **要る。** 同 IADR §結果 フォローアップ 1 が「**#602 の結果を本 IADR へ追補する**」と明文で要求し、§未確認のまま残したこと が「#602 が実環境を要するかは未判定」と述べている。**日付つき追記ブロックを足した** |
| `docs/adr/IADR-0119` | 要る（受け入れ基準⑤）。追補済み |
| `docs/adr/README.md` の `IADR-0119` / `IADR-0142` 行 | 要る。先例（#593 / #601）が同一コミットで索引を更新している |

### 追随不要と判定したものと理由（規則 6）

| 文書 | 理由 |
| --- | --- |
| `docs/adr/IADR-0170:119,127-128` | 着手条件（覆り得る範囲の外か）は本検証で変わらない。`:127-128` は**同型事故の記録**であり、むしろ本作業の 🔴 の根拠になった |
| `docs/adr/IADR-0121` / `IADR-0125` / `src/packages/ui/README.md` の `SC-19` 言及 | いずれも **`SC-19` / `SC-20` の確認ダイアログ**が対象で、「本文を編集（Wiki.js）」導線ではないと明記済み |
| `docs/screens/SC-01_search-chat.md` | `SC-01` の個人資料要素は**閲覧**側で、編集導線の帰趨に依存しない |
| `scripts/check-planning-pin-freshness.js` | 範囲基準の条件を正しく書いており変化なし |
| 確定済み `docs/specs/*`・`feedback/20260807_*`・`docs/how-to/plan-id-range-history-annex.md` | **書いた時点の記録**（live な権威文書とコードに限る） |
| `planning/` 配下 | 別リポジトリ。環流（planning#346）で扱う |

> **この節は当初なかった。** #602 は「やること」の 1 行目で
> **「作業仕様書を先に作る（IADR-0141 決定 1 に従い母集合を自分で引き直して記録する）」**と明示的に求めていたのに、
> 書かずに進めた。**その結果 `IADR-0142` の追補義務を落とし、監査 2 本が独立に検出した。**
> 規則 6 は記録の作法ではなく**検査そのもの**である、という #744 の教訓を、次の PR でそのまま繰り返した。

## 7. 成果物

| 成果物 | 内容 |
| --- | --- |
| `feedback/20260815_wikijs-personal-scope-verification.md` | 計画側への環流（本結論と判断材料） |
| planning への起票 | 新 ADR 起案の依頼（`decision-needed`） |
| `IADR-0119` の追補 | 保留範囲の現況を追記 |
| 本 issue へのコメント | 実環境要否の判定と結論の記録 |

## 8. 対象外

- **FR-19 / FR-20 の実装そのもの**（→ #451）
- **FR-06 / FR-13 の再実装**（→ #449）
- **`owner` 属性の付与と動的束縛の実装**（→ #516 / planning#344）
- **`SC-19` の導線を実際に差し替えること**（新 ADR の確定後）
