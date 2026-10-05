---
title: 作業仕様書 — AST の KB の読み手の read ポリシーの文書の条件に機密区分の上限（public・internal）を足す（#1755）
type: spec
status: done
related_ids: [FR-05, FR-03, FR-09, NFR-09, SC-09, ADR-0125, ADR-0085, ADR-0080, ADR-0127, IADR-0492, IADR-0500, IADR-0253, IADR-0497]
author: claude
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0125_ast-kb-reader-static-project-policy-with-confidentiality-ceiling.md 決定 2（上限）・決定 4（NetworkPolicy は別件）・決定 5（3 点セット）・フォローアップ 1〜3
  - planning:projects/microservices-platform/07_adr/ADR-0085_project-attribute-scope-and-non-axis.md 決定 2（ADR-0125 が部分改定）
related_specs:
  - 20261002_issue-1696_ast-kb-read-policy
issue: "#1755"
---

# 作業仕様書 — AST の KB の読み手のポリシーへ機密区分の上限を足す（#1755）

> 本仕様書は実装着手前に作成した（着手 2026-10-06）。判断の記録は **IADR-0500**（IADR-0492 決定 1 の部分改定）に置く。
> 計画は project-planning `c3ad458` を読んだ。基点は MSP `origin/develop` `c63351de`。

## 起点となる計画書（トレーサビリティ）

- 裁定: planning#712（利用者裁定 2026-10-03）→ 計画 **ADR-0125**（Accepted）。決定 2「文書の条件を
  `{project:[ai-stock-trading], confidentiality:[public, internal]}` とする」。フォローアップ 1（dev の seed と本番の手順の両方・
  IADR-0492 決定 1 と残余 5 の改め・試験）。
- 要求: **FR-05**（ABAC。アクセス可能な文書のみを検索・回答対象とする）。ブランチ名は FR-05 を起点に取った。関連は FR-03（検索）・FR-09（ポリシー）・NFR-09。
- 起点 issue: **#1755**（本件のために起票。親は #1696。#1696 は PoC の受け入れ〔実データで参考情報 1 件以上〕が残るので閉じない）。

## 射程と、触らないもの

- **触る**: 認可サービスの保存時の検証（`AbacValidation`。§着手後に見つけた制約。**本番コードはここだけ**）、dev の seed（`deploy/local/abac-seed/policies.json`・同 `README.md`）、本番の手順（`docs/operations/operations.md`
  §AST の KB の読み手のポリシーの投入）、`docs/security/security.md` の読み手の説明、IADR（新 IADR-0500・IADR-0492 への日付つき追記・索引）、
  試験（認可サービスの seed 試験・fixture・検索側の試験・`scripts/scripts.repo.test.js`）。
- **触らない**:
  - 本番への投入の保留を解くこと（ADR-0125 フォローアップ 2。**本 PR のマージ後**に別に行う）。保留の記述は「上限の改定が入るまで」→「本 PR のマージ後に解く」と
    条件を書き換えるだけで、保留そのものは残す。
  - 本番の NetworkPolicy（ADR-0125 決定 4）。**#1756** で扱う。
  - 評価器・検索の本番コード（分岐内の連言は既に評価できる。`AbacEvaluator`・`ScopeNarrowing`・`InMemoryVectorStore`・`QdrantVectorStore`）。
  - 「文書の条件は 1 キーまで」の規則そのもの（外さない。例外を 1 つ置くだけ）。
  - realm の宣言（主体の形は変えない）。
  - 稼働中のクラスタ（seed の再投入・PUT を含めて実行しない）。

## 設計（正は IADR-0500）

1. **文書の条件** = `{ "project": ["ai-stock-trading"], "confidentiality": ["public", "internal"] }`（1 本の分岐の中の連言）。
   利用者の条件・action・ポリシー名は変えない。
2. **名前を変えない理由**: `seed-abac-policies.js` は**名前**で冪等である。名前を変えると、既に投入済みの環境では旧（上限なし）と新が並び、
   評価器は分岐の和を取るので**上限が効かない**。名前を据え置けば並ばない。代わりに、**投入済みの環境は seed の再投入では直らない**
   （同名があると作らない）ので、既存の環境は管理者が既存のポリシーを `PUT /authz/policies/{id}`（または SC-09 の編集）で書き換える。手順を運用文書に書く。
3. **上限が効くのはこの枝だけ**（ADR-0125 決定 2）。所有者・共有先の分岐（全主体に効く）は変えない。読み手へ明示的に共有された文書は区分を問わず届く
   （所有者の裁量）。試験では「束縛されない分岐」についてだけ上限を固め、共有の枝は既存の形のまま残す。
4. **AST の文書は失わない**: AST が保存する文書は既定 `internal`（AST `KnowledgeConfidentiality.Default`・報告書 `ReportPolicyYaml.Confidentiality = "internal"`）。

## 着手後に見つけた制約（2026-10-06）

- seed を変えたあと、認可サービスの全試験で `AbacValidationTests.ValidatePolicy_DevSeedPolicies_AllPass` が赤になった。
  保存時の検証 `ValidateSingleDocumentConditionKey`（計画 SC-09「ポリシー定義のバリデーション」・planning#470。暫定統制）が、
  文書の条件 2 キーの読み手のポリシーを「documentConditions に指定できる属性キーは 1 つまで」で拒否する。
- seed は管理 API で投入する（`seed-abac-policies.js`）。本番の手順も `POST` / `PUT /authz/policies`。**このままでは dev でも本番でも上限を入れられない。**
- 計画 ADR-0125 の実測はこの検証に触れていない。SC-09 の規則と ADR-0125 決定 2 の食い違いである。
- 扱い（IADR-0500 決定 4）: 規則を外さず、**値まで固定した例外を 1 つ**置く（`AbacValidation.IsAstKbReaderCeilingPolicy`）。値を固定すると、
  キー単位 union の混成（planning#470 の反例）が生じないことを IADR-0500 に論証した。計画へは、SC-09 と 07_abac-attribute-model に例外を記録するよう環流した（planning#723）。

## 母集合（規則 9・10。2026-10-06 `c63351de` 時点）

### 規則 9（誤りの側の文字列で全文書を走査してから追随先を挙げる）

走査: `git grep -nE "confidentiality. で絞らない|機密区分で絞らない|project ∈ \{ai-stock-trading\}( だけ|\`? の 1 本)|文書の条件は .?project|区分を問わず|投入は保留|保留中|分岐が .?project ∈|documentConditions\": \{ \"project\""`
（`src/ai-stock-trading`・`.ai-context/specs` を除く）と、ポリシー名 `AST の KB の読み手は AST の文書を読める` の全出現。

| 当たり | 追随 |
| --- | --- |
| `deploy/local/abac-seed/policies.json`（注記 47 行・本体 103 行） | 追随（上限を足す・注記を改める） |
| `deploy/local/abac-seed/README.md` 64 行 | 追随 |
| `docs/operations/operations.md` 519（保留）・522（形）・541（JSON）・547（確かめ方） | 追随（保留の条件・形・JSON・確かめ方・既存環境の書き換え手順） |
| `docs/security/security.md` 331 行（`project = ai-stock-trading` の文書だけを許す） | 追随（走査の 2 回目〔`の文書だけを許す`〕で拾った） |
| `scripts/scripts.repo.test.js` 788 行 | 追随（文書の条件の期待値） |
| `AstKbReaderPolicySeedTests.cs`（T-2 の `ContainSingle` と「confidentiality を含まない」・T-6 の `DocumentConditions.ContainSingle`） | 追随（**規則 10: 本変更で新たに誤りになる**） |
| `Tests/Fixtures/owner-read-seed-scopes.json`（読み手の `allowedFilters`・分岐） | 追随（`OwnerReadPolicySeedTests` が実物と突き合わせる。実測で再生成） |
| `AstKbReaderSearchScopeTests.cs` | 追随（confidential・restricted・public の AST の文書を索引に足す） |
| `.ai-context/adr/IADR-0492_*.md` 決定 1（86 行）・残余 5（134 行） | **本文は書き換えない**（凍結記録）。日付つき追記で IADR-0500 を指す |
| `.ai-context/specs/20261002_issue-1696_*.md` | **除外**（point-in-time の記録） |
| `保留中` の他の当たり（IADR-0125・0201・0215・0267・0281・notification 等） | **除外**（別の保留。読み手と無関係） |

### 規則 10（この変更で新たに誤りになる自分の記述）

- 運用文書の「投入済みかの確かめ方: 束縛されない分岐が `project ∈ {ai-stock-trading}` の 1 本だけ」→ 2 キーの連言の 1 本へ。
- 運用文書の「`projects` / `project` を条件に持つポリシーをこれ以外に作らない」は変わらない（本数は 1 本のまま）。
- 「文書の条件は 1 キーまで」を述べる記述（走査 `1 つまで|2 つ以上の属性キー|多キー|複数キーの文書条件`）: `docs/functional/FR-09_*.md` 業務ルール ⑦・
  `docs/functional/FR-05_*.md` §閲覧規則の選言、`AbacValidation.cs` の注記。前 2 つへ例外を追記した。テスト仕様書 `docs/tests/FR-09_*.md`（単体 30・31）と
  `docs/tests/FR-05_*.md`（T-70〜T-72）に行を足した。**除外**: `.ai-context/specs/20260823_planning-adr-0056-0058-followup.md`（point-in-time）。
- IADR-0492 の索引タイトル（`project=ai-stock-trading` の文書だけを許す）は**索引のタイトルセルは本体 `title:` の要約**で、凍結記録の題を書き換えない。
  IADR-0500 の行を足して改定を示す。

## 受け入れ基準 → 試験

| # | 受け入れ基準（#1755） | 試験 |
| --- | --- | --- |
| 1 | 読み手の `/authz/scope`（read）で、束縛されない分岐は `project ∈ {ai-stock-trading}` ∧ `confidentiality ∈ {public, internal}` の 1 本だけ | `AstKbReaderPolicySeedTests` T-2（実物の seed・実物の端点・実物の評価器） |
| 2 | seed の読み手のポリシーの文書の条件がちょうどその 2 キー | `AstKbReaderPolicySeedTests` T-6・`scripts.repo.test.js` |
| 3 | 検索で、`project=ai-stock-trading` の confidential・restricted の文書が読み手に出ない。public・internal は出る | `AstKbReaderSearchScopeTests`（`ScopeNarrowing` → `InMemoryVectorStore`。主張あり・なしの両方） |
| 4 | 本番の索引（Qdrant）への写しでも、読み手の分岐は project と confidentiality の両方の `Must` を持つ | `AstKbReaderSearchScopeTests`（`QdrantVectorStore.BuildAttributeConditions`） |
| 5 | 上限はこの枝だけ: 読み手へ共有された confidential の文書は届く（ADR-0125 決定 2 の明記どおり。広げも狭めもしない） | `AstKbReaderSearchScopeTests` |
| 6 | 本番の手順の JSON が seed と同じ形 | `scripts.repo.test.js`（運用文書の JSON ブロックを seed と突き合わせる） |
| 7 | seed の読み手のポリシーが実物の保存の口を通る（POST 201・同じ本文の PUT 200）。上限に confidential を足した PUT は 400 | `AstKbReaderPolicySaveTests` |
| 8 | 保存時の検証の例外は読み手の形に限る（形 3 通りは通り、1 箇所ずつ崩した 12 通りは「1 キーまで」で拒否）。seed の全ポリシーが検証を通る | `AbacValidationTests` |

## 変異試験の結果（2026-10-06。scratch の `mut-1755.py`。1 変異ずつ当て、対象の試験を走らせ、戻す）

| # | 変異 | 走らせた試験 | 結果 |
| --- | --- | --- | --- |
| M1 | seed: 上限を外す（文書の条件を `project` だけに戻す） | 認可（読み手・seed・検証）／scripts | 殺した（5 件赤）／殺した |
| M2 | seed: 上限に `confidential` を足す | 同上 | 殺した（6 件赤）／殺した |
| M3 | seed: 上限を `internal` だけに狭める | 同上 | 殺した（4 件赤）／殺した |
| M4 | 運用文書: 本番の JSON から上限を外す | scripts | 殺した |
| M5 | fixture: 読み手の枝から上限を外す（消費側の入力） | 認可／検索 | 殺した（1 件赤）／殺した（6 件赤） |
| M6 | fixture: 上限に `restricted` を足す | 認可／検索 | 殺した（1 件赤）／殺した（5 件赤） |
| M7 | 評価器: 分岐の文書の条件を先頭 1 つだけにする | 認可 | 殺した（3 件赤） |
| M8 | InMemory 索引: 分岐内を AND でなく OR にする | 検索 | 殺した（5 件赤） |
| M9 | Qdrant への写し: 分岐の条件を先頭 1 つだけ写す | 検索 | 殺した（1 件赤） |
| M10 | 検証: 例外の上限の値の検査を外す（`confidential` を許す） | 認可 | 殺した（3 件赤） |
| M11 | 検証: 例外を無くす | 認可（全件） | 殺した（5 件赤） |
| M12 | 検証: 例外の `project` の値の固定を外す | 認可 | 殺した（2 件赤） |

12 変異すべてを殺した。変異はすべて戻した（`git status` で評価器・索引の本番コードに差分が無いことを確かめた）。

## 検証（2026-10-06。`origin/develop` `c63351de` 基点）

| 検査 | 結果 |
| --- | --- |
| `dotnet build` platform / knowledge | 警告 0・エラー 0 ／ エラー 0（警告 1 は既存の `IngestToSearchQdrantTests` の CS0618。本変更と無関係） |
| `dotnet test` platform | AuthorizationService 604 件（全件緑）ほか全プロジェクト緑 |
| `dotnet test` knowledge | RetrievalService 479 件ほか全プロジェクト緑 |
| `dotnet format --verify-no-changes` platform / knowledge | 差分なし |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 🔴 `check-adr-numbering` が IADR-0498・0499 の欠番で落ちる（並行の作業が予約。本 PR は 0500 を使う指示）。0498・0499 の仮置き（コミットしない）で 919 件全件緑 |
| helm template | 不要（seed・values は helm の chart に入らない。`deploy/helm` に `abac-seed` の参照は 0 件） |
| 文書系の検査 | PR の本文に記す |
