---
title: IADR-0500 AST の KB の読み手の read ポリシーの文書の条件へ機密区分の上限（public・internal）を足す。保存時の「文書の条件は 1 キーまで」に値まで固定した例外を 1 つ置き、名前は据え置いて投入済みの環境は PUT で書き換える（IADR-0492 決定 1 の部分改定）
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-03, FR-09, NFR-09, SC-09, ADR-0125, ADR-0085, ADR-0080, ADR-0127, IADR-0492, IADR-0253, IADR-0133, IADR-0497]
author: claude
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0125_ast-kb-reader-static-project-policy-with-confidentiality-ceiling.md 決定 2（上限）・決定 4（NetworkPolicy は別件）・決定 5（3 点セット）・フォローアップ 1〜3
  - planning:projects/microservices-platform/07_adr/ADR-0085_project-attribute-scope-and-non-axis.md 決定 2（ADR-0125 が部分改定）
  - planning:projects/microservices-platform/05_screens/01_screens.md SC-09「ポリシー定義のバリデーション」（planning#470。文書の条件は 1 キーまで。本 IADR 決定 4 が例外を 1 つ置く）
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md §選言（キー単位 union の過剰許可と暫定の統制）
related_specs:
  - ../specs/20261006_1755_ast-kb-reader-confidentiality-cap.md
---

# IADR-0500: AST の KB の読み手のポリシーへ機密区分の上限を足す（#1755）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-06
- 決定者: 利用者裁定（planning#712・2026-10-03 → 計画 ADR-0125 決定 2）を claude が実装の形へ落とした

## 起点・関連

- 起点 issue: #1755（親 #1696。対の AST 側は AST#1078）
- 関連する計画書 ID: FR-05（ABAC）・FR-03（検索）・FR-09（ポリシー）・NFR-09、AST/FR-08
- 関連する計画 ADR: **ADR-0125**（決定 2 が本 IADR の起点）・ADR-0085 決定 2（ADR-0125 が部分改定）・ADR-0080 決定 2（交差）・ADR-0127（高機密文書の語彙索引。ABAC は全系統に効く）
- 関連する実装 ADR: [[IADR-0492]]（**決定 1 の「`confidentiality` で絞らない」を本 IADR が改める**。決定 1 のその他・決定 2・3 は有効なまま）・
  [[IADR-0253]]（1 ポリシー = 1 分岐。分岐内は連言）・[[IADR-0133]]（dev seed は名前で冪等）・[[IADR-0497]]（高機密文書は語彙索引だけ）
- 基点コミット: `origin/develop` `c63351de`

## コンテキストと課題

[[IADR-0492]] 決定 1 は、AST の KB の読み手の read ポリシーの文書の条件を `project ∈ {ai-stock-trading}` だけとし、
「`confidentiality` で絞らない」と書いた。独立監査がその残余（同 残余 5）を挙げ、planning#712 で裁定を求めた。

裁定（2026-10-03）は計画 ADR-0125 として記録された。同 決定 2 は文書の条件を
`{project:[ai-stock-trading], confidentiality:[public, internal]}` とする。理由は、`project` のラベルが「AST の読み手へ開く」を兼ねるようになり、
管理者・operator は `project=ai-stock-trading` を持つ文書を区分を問わず新しく作れるからである（ADR-0125 実測 6）。

ADR-0125 実測 6 は、IADR-0492 残余 5 の「書き込みロールを持つ利用者は誰でも任意の文書へ `project=ai-stock-trading` を足せる」も正しくないと記録した。
既存の文書の属性を全置換できるのは管理者だけで、メタデータの更新は管理者か所有する機械クライアントだけである。新しく作ることは管理者・operator ができる。

### 決めること

| # | 論点 | 選択肢 |
| --- | --- | --- |
| 1 | 記録の形 | (a) IADR-0492 への追記だけ／(b) 新 IADR で部分改定し、IADR-0492 に日付つき追記で指す |
| 2 | 上限の置き方 | (a) 同じポリシーの文書の条件へ `confidentiality` を足す（連言）／(b) 別のポリシーを足す |
| 3 | 名前と既存の環境 | (a) 名前を据え置き、投入済みの環境は PUT で書き換える／(b) 名前を変え、seed の再投入で新しいものを足す |
| 4 | 保存時の検証（「文書の条件は 1 キーまで」。SC-09・planning#470）との衝突 | (a) 規則を外す／(b) 値まで固定した例外を 1 つ置く／(c) 実装を止めて計画へ戻す |

> **論点 4 は着手後に見つけた**（2026-10-06）。`AbacValidationTests.ValidatePolicy_DevSeedPolicies_AllPass` が、上限を足した seed の読み手のポリシーを
> 「documentConditions に指定できる属性キーは 1 つまで」で拒否した。seed は管理 API で投入され（[[IADR-0133]]）、本番の手順も同じ口を通るので、
> **例外が無いと dev でも本番でも上限を入れられない**（400）。計画 ADR-0125 の実測はこの検証に触れていない。

## 決定

### 決定 1 — 新 IADR（本 IADR）で IADR-0492 決定 1 を部分改定する（論点 1 は (b)）

- IADR-0492 決定 1 の「`confidentiality` で絞らない」は決定の中身である。凍結記録の本文を書き換えず、本 IADR が改め、IADR-0492 には日付つき追記で本 IADR を指す
  （先例: [[IADR-0117]] が [[IADR-0056]] 決定 3 を部分改定した形）。IADR-0492 の状態は `Accepted` のままである（決定 1 のその他・決定 2・3 は有効）。
- 同 残余 5 の「誰でも任意の文書へ足せる」の言い過ぎも、同じ追記で ADR-0125 実測 6 の記述へ正す。

### 決定 2 — 同じポリシーの文書の条件へ `confidentiality ∈ {public, internal}` を足す（論点 2 は (a)）

```json
{ "name": "dev: AST の KB の読み手は AST の文書を読める", "action": "read",
  "userConditions": { "projects": ["ai-stock-trading"] },
  "documentConditions": { "project": ["ai-stock-trading"], "confidentiality": ["public", "internal"] } }
```

- 評価器は 1 ポリシーを 1 分岐にし、分岐の中の文書の条件を連言で運ぶ（[[IADR-0253]] 決定 1）。読み手の束縛されない分岐は
  `project ∈ {ai-stock-trading} ∧ confidentiality ∈ {public, internal}` の 1 本になる。検索側（`ScopeNarrowing`・`InMemoryVectorStore`・
  `QdrantVectorStore.BuildAttributeConditions`）は分岐内の連言を既に扱えるので、本番コードは変えない。
- **(b) を採らない。** 評価器は分岐の和を取るので、別のポリシーを足しても既存の枝は狭まらない。狭めるには既存の枝そのものの条件を足すしかない。
- **上限が効くのはこの枝だけである**（ADR-0125 決定 2）。所有者・共有先の分岐（全主体に効く）は変えない。文書の所有者が読み手の識別子へ明示的に共有した文書は、
  区分を問わず届く。試験はこれを「広げも狭めもしない」形で固定する（`AstKbReaderSearchScopeTests`）。
- AST が自分で保存する文書は `internal` 以下（AST の `KnowledgeConfidentiality.Default = internal`・報告書は `internal`）なので、AST が失うものは無い。
- 高機密文書は語彙索引だけに載る（[[IADR-0497]]）。ABAC は全系統に効くので、語彙索引の confidential・restricted の文書も同じ枝で落ちる。

### 決定 3 — 名前を据え置き、投入済みの環境は既存のポリシーを書き換える（論点 3 は (a)）

- `scripts/seed-abac-policies.js` は**名前**で冪等である（同名があれば作らない。[[IADR-0133]]）。
- **(b) を採らない。** 名前を変えると、投入済みの環境では旧（上限なし）と新が並ぶ。評価器は分岐の和を取るので、旧い枝が残る限り上限は効かない。
  旧いものを消す手順を足すなら、結局は手作業が要る。並ぶ形は「足したのに効かない」を黙って生む。
- (a) の代価として、**投入済みの環境は seed の再投入では直らない。** 管理者が既存のポリシーを `PUT /authz/policies/{id}`（または SC-09 の編集）で書き換える。
  手順は `docs/operations/operations.md` §AST の KB の読み手のポリシーの投入「上限の無い旧い形が入っている環境」に置いた（開発環境も同じ手順）。

### 決定 4 — 保存時の検証に、値まで固定した例外を 1 つ置く（論点 4 は (b)）

- `AbacValidation.IsAstKbReaderCeilingPolicy` に当たるポリシーだけ、「文書の条件は 1 キーまで」（`ValidateSingleDocumentConditionKey`）を課さない。
  当たる形は次の**すべて**を満たすものに限る。
  1. action が `read`。
  2. 利用者の条件が `{ "projects": ["ai-stock-trading"] }` ちょうど（キー 1 つ・値 1 つ）。
  3. 文書の条件がちょうど 2 キーで、`project` が `["ai-stock-trading"]` ちょうど、`confidentiality` が `public`・`internal` の空でない部分集合（重複なし）。
- 🔴 **値まで固定するのは、例外が planning#470 の過剰許可（キー単位 union の混成）を生まない条件だからである。**
  評価器のキー単位 union（`AllowedFilters`。未移行の消費側が読む）で、例外に当たるポリシーと 1 キーのポリシーが同時にマッチしても、
  潰した連言を通る文書は `project=ai-stock-trading` かつ機密区分 v を持つ。v は (i) 例外に当たるポリシーのどれかが許した値か、(ii) 機密区分 1 キーの
  ポリシーが許した値である。(i) ならそのポリシー単独が許可し（例外のポリシーはどれも `project` が同じ 1 値なので、`project` の混成は起きない）、
  (ii) ならその 1 キーのポリシー単独が許可する。**どのポリシー単独も許可しない組は通らない。** 別の `project` や `confidential` 以上を許すと
  (i) が崩れるので、例外に当たらない。
- **(a) を採らない。** 規則を外す条件（消費側が分岐へ移行し終えたこと）は計画 SC-09 が定めた。全消費側の移行の確認は本件の射程を超える。
- **(c) を採らない。** 計画 ADR-0125 決定 2 は文書の条件の形（2 キー）を明示しており、裁定の求めは明確である。SC-09 の規則と ADR-0125 の食い違いは、
  例外を計画へ記録するよう環流した（planning#723）。
- 例外は保存の 2 経路（`POST` / `PUT /authz/policies`）と dry-run（`/authz/policies/validate`）が共有する `ValidatePolicy` に置くので、3 つは一致する。
- 試験: `AbacValidationTests`（形が通る 3 通り・1 箇所ずつ崩した 12 通りが従来どおり拒否）、`AstKbReaderPolicySaveTests`（実物の保存の口で seed の本文が 201・同じ本文の PUT が 200・上限に confidential を足した PUT が 400）。

## 統制と現在の実現手段

| 統制 | 現在の実現手段 | 配備までの暫定手段 |
| --- | --- | --- |
| confidential・restricted の文書は、`project` のラベルだけでは読み手へ届かない | **ある（宣言）**: dev seed の文書の条件。試験 `AstKbReaderPolicySeedTests`（実物の seed・実物の評価器・実物の端点）・`AstKbReaderSearchScopeTests`（検索側・Qdrant への写し）・`scripts.repo.test.js`（seed と運用文書の JSON の一致）。🔴 **稼働中の環境は未反映**（再投入で直らない。決定 3 の手順で書き換える） | 本番は投入していない（保留中。ADR-0125 決定 5）。保留はこの PR のマージ後に別に解く |
| 2 キーの文書の条件は読み手の形だけ | **ある**: 保存時の検証の例外を値まで固定（決定 4。`AbacValidationTests`・`AstKbReaderPolicySaveTests`） | — |
| ポリシーの形が崩れたら気付く | 🔴 **無い**（稼働中の環境の形の検査は置かない。IADR-0492 残余 3 と同じ） | 運用文書の「投入済みかの確かめ方」（文書の条件の 2 キー・分岐の形） |

## 結果

- 良い影響: `project=ai-stock-trading` を付けた confidential・restricted の文書が、取引判断の LLM のプロンプトへ載らない。計画 ADR-0125 決定 2 と dev seed・運用文書が一致する。
- 悪い影響 / トレードオフ: 既存の環境は手作業の書き換えが要る（決定 3）。internal 以下の文書による取引判断への内容の注入は射程の外（ADR-0125 決定 2 の明記どおり）。
  読み手へ明示的に共有された文書は区分を問わず届く（所有者の裁量）。

## 残余

1. **本番への投入の保留を解く**のは本 IADR のマージ後の別作業である（ADR-0125 フォローアップ 2）。本 PR では保留の記述の理由だけを改めた。
2. **本番の NetworkPolicy**（AST の名前空間からの ingress）は別件 #1756（ADR-0125 決定 4）。
3. **稼働中の開発環境**に上限の無い旧い形が入っていれば、決定 3 の手順で書き換えるまで上限は効かない。本 PR は稼働中の環境に触れない。
4. **PoC**（実データで取引判断の参考情報が 1 件以上載る）は #1696 に残る。
5. **計画 SC-09 の「文書の条件は 1 キーまで」に、本例外が記録されていない**（決定 4）。計画への環流 planning#723 で記録を求めた。
6. 採番: 本 IADR は 0500 を使う。0498・0499・0501 は並行の作業が予約しており、0498・0499 がマージされる前は `check-adr-numbering.js` が欠番として赤になり得る（PR に記した）。

## 関連

- Supersedes: なし（[[IADR-0492]] 決定 1 の「`confidentiality` で絞らない」を部分改定する。IADR-0492 は `Accepted` のまま）
- Superseded by: なし
- 作業仕様書: [`../specs/20261006_1755_ast-kb-reader-confidentiality-cap.md`](../specs/20261006_1755_ast-kb-reader-confidentiality-cap.md)
