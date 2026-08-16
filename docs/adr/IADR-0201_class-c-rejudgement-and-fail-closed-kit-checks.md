---
title: IADR-0201 分類 C を「置換点を埋めているか」で再判定し traceability.md を companion 分離で分類 A へ戻す。キット同期・status 突合の実データ走査は planning を取得するジョブで --require-planning 付きに行う
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0115
  - IADR-0172
  - IADR-0192
  - IADR-0193
  - IADR-0198
  - IADR-0204
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/tools/impl-handoff-kit/repo-template/scripts/kit-sync-classification.example.json (分類 C の新定義)"
  - "../../planning/draft/feedback/20260815_kit-class-c-definition-ambiguous.md (裁定 planning#363)"
  - "../../planning/draft/feedback/20260815_kit-checker-fail-open-flag-lost.md (裁定 planning#343)"
  - "../../planning/tools/impl-handoff-kit/repo-template/.claude/rules/traceability.md (companion 機構の宣言)"
related_specs:
  - "../specs/20260816_issue-755_planning-pin-4d6a7d6-catchup.md"
---

# IADR-0201: 分類 C の再判定と companion 分離、キット検査の fail-closed 配線（#755 / #751）

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: 計画側の裁定（planning#363 / planning#343）＋ claude（実装）

## 起点・関連

- **NFR**（文書統制・検査器。**当たる番号が無い＝場合 ②** なので無採番・環流しない。[[IADR-0189]] 決定 1）
- 実装 issue: **#755**（計画 pin `4d6a7d6` の追随）。**#751**（`--require-planning` の追随）を束ねた（束ね判定は作業仕様書 §7）
- 作業仕様書: [20260816_issue-755](../specs/20260816_issue-755_planning-pin-4d6a7d6-catchup.md)
- 改定対象: [[IADR-0192]] 決定 4（CI では skip になる）／[[IADR-0193]] 決定 3 の配線（同）／[[IADR-0192]] 決定 1 の分類表（C 17 件）
- 維持するもの: [[IADR-0172]] 決定 2（入口 `.claude/rules/traceability.md` の**パスは変えない**）

## 文脈 —— **C に置いた 2 件がキットの是正 3 件を止めていた**

planning#363 の裁定で分類 C は **(a) キットに対応物が無い／(b) 雛形から書き起こし、置換点を実際に埋めている** の 2 つに限られた（「持つ」ではなく「埋めている」）。**C は同期しないため、置いた瞬間に検査が止まる。誤った分類は、誤りを検出する機構ごと無効化する。**

本リポは `.claude/rules/traceability.md` と `scripts/check-cross-repo-refs.js` を C に置いており、キット側の是正 3 件 —— planning#349（`planning issue #NNN` の表記是正）／planning#350（母集合の規則 8）／planning#354（配布物の中で他リポの IADR を番号で引かない）—— が**届いていなかった**。

加えて `check-kit-sync.js` は Windows で `path.relative` が `\` 区切りを返すため **108 件すべてが偽の unclassified** になり（本 PR の作業開始時に実測）、CI の `kit-sync` / `feedback-status-sync` ジョブは planning を populate しないため**常に warn ＋ skip で緑**だった（#751）。

## ★★ 決定 1: **C 17 件を新定義で全件再判定する。C に残るのは 4 件、13 件は A / B へ移す**

再判定の表は作業仕様書 §4 が持つ（**記憶で挙げず、`git diff --numstat` と置換点の有無で全件を引いた**）。要点:

| 移動 | ファイル | 判定 |
| --- | --- | --- |
| **C → A** | `.claude/rules/traceability.md` | 置換点なし。固有分は companion へ（決定 2） |
| **C → B（X）** | `scripts/check-cross-repo-refs.js` | 値はソース直書きで、キットの置換点も環境変数注入も使っていない。型 4（#590）等の本リポ先行分を持つため A にできない。追跡 #756 |
| C → B（X） | `scripts/check-commit-messages.js` / `scripts/check-plan-id-qualification.js` | 本リポ originate・キットが後追いで別実装。追跡 #756 |
| C → B（X） → **A** | `scripts/scripts.test.js` | 設計上は A であるべきだがキット版が +750 行先行。追跡 #757 → **#757 で解消し A へ戻した**（下の追記） |
| C → B（1〜5） | `.gitignore` / `AGENTS.md` / `CHANGELOG.md` / `CLAUDE.md` / `docs/README.md` / `docs/ai-workflow.md` / `scripts/README.md` / `scripts/changelog-overrides.json` | いずれも「キット土台 ＋ 固有デルタ」。土台はキットが正で追随対象 |
| **C のまま** | `docs/adr/README.md` / `docs/operations/operations.md` / `docs/security/security.md` / `docs/tech/tech-requirements.md` | (b) 雛形から書き起こし、`<作成者>` / `<YYYY-MM-DD>` / 索引の置換点を埋めている |

> ［2026-08-15 追記 / #757］**`scripts/scripts.test.js` は A へ戻った**（キット pin `4d6a7d6` とバイト一致）。
> 併せて **`scripts/kit-sync-classification.example.json` を `notApplicable` から A へ移した** ——
> キット版のテストが**雛形そのもの**を検査対象にするため、「本リポが実表を持つ」ことは
> 雛形を持たない理由にならない。追随の前提として `check-commit-messages.js` の
> `isBotAuthorName` をキットの正準名 `isBotLogin` へ改名し、`check-planning-pin-freshness.js` へ
> `freshness` を、`check-cross-repo-refs.js` へ `createChecker`（置換点 ＋ 設定の妥当性検査）を
> 足した。**後 2 者は X のままである**（本リポの検出力が先行しているため。追跡 #749 / #756・
> 環流先 planning#374）。集計は **A 78 / B 25 / C 4 / 対象外 8**。

> ［2026-08-16 追記 / #790］**上の 2 件はいずれも 1 世代で覆った。** 計画 pin を `8cae89d` へ進めた
> 時点で、キットが planning#373 / planning#374 の環流を受理し、キット `traceability.md` が
> +3,002B 育った。**実測の結果は次のとおり**（[[IADR-0204]] が正本）。
>
> - **`scripts/scripts.test.js` は A → B（X）へ戻した。** キット版の新試験が「拡張点を持たない構成」を
>   断定しており、**拡張点を埋めた本リポでは原理的に通らない**（`loadExistingPlanIds()` が Set を返す）。
>   固有デルタは 1 か所。追跡 planning#380。
> - **`.claude/rules/traceability.md` も A → B（X）へ移した。** キット原文を取り込むと必読規約が
>   予算 51,200B を 1,995B 超えて予算試験が fail する。**期限つきの暫定**であり、減量（#793）が
>   着地すると `scripts.repo.test.js` のラチェットが落ちて追随を促す。**決定 2 の設計（companion 分離）は覆っていない。**
> - **`scripts/check-commit-messages.js` は X → B（種 5）へ落ちた**（置換点 `PLAN_PROJECT` のみ）。
>   **`scripts/check-cross-repo-refs.js` は X のまま**だが理由が変わった（検出力は同値。キットに
>   0 件走査の門が無い。追跡 planning#379）。集計は **A 76 / B 27 / C 4 / 対象外 8**。

**`CLAUDE.md` は置換点（末尾「技術スタック別ルール」）を埋めているので C(b) も成立するが、B に置く。** 土台の規約文（§8 予算・§11 パリティ等）は運用ガイドの改定のたびにキットが正となって追随が要り、「同期しない」と宣言する C は実態に合わない。

## ★★ 決定 2: **`traceability.md` はキット版とバイト一致にし、本リポ固有の規範は companion `.claude/rules/traceability.repo.md` へ置く**

> ［2026-08-16 追記 / #790］**バイト一致は保留中である**（分類 B の X。上の追記）。**companion へ置く設計は不変。**

- **companion 機構はキットが定めている**（キット `traceability.md` 冒頭「リポジトリ固有の規約は `traceability.repo.md`（同じディレクトリ）へ書く。同ディレクトリの `*.md` は自動適用される」）。ai-stock-trading は既に同じ形（`AST/IADR-0202`）である。
- **入口のパスは変えない**（[[IADR-0172]] 決定 2）。確定済み記録が `traceability.md §Superseded…` の形で節名を引いているが、節見出しは companion に**同名で残した**（`### Superseded / Deprecated な ADR を引用するときの書式（#580）` 等）。キット `traceability.md` の冒頭が companion を指すので導線は切れない。
- **companion には規範だけを置き、経緯・実測は別紙へ出した**（[[IADR-0173]] 決定 1 の設計どおり。`cross-project-id-refs-annex.md` / `adr-supersede-citation-annex.md` へ「入口から移した補足」を追加）。**移しても総量は減らない**（キット CLAUDE.md が明記するとおり companion も母集合に入る）ため、companion は 5,408B に収め、必読の余白 1,000B（[[IADR-0190]]）を保った（[[IADR-0200]] 決定 3）。
- **母集合の規則は改番した**: キット版が規則 7（数値・名前を直したら全走査し直し、出力を加工して読まない）・規則 8（自分の記録が母集合を動かす）を持つため、**本リポが先に足していた旧 7・8 は 9・10 へ改番**（内容不変。`population-drawing-annex.md` に改番の注記。2026-08-16 より前の ADR が引く「規則 7 / 8」は旧番号）。
- **計画 ID レンジの単一情報源も companion へ移った**（`check-test-traceability.js` の `RULES_FILE` / `PLAN_RANGE_HEADING` を `traceability.repo.md`「起点 ID の種別（固有）」へ）。`check-commit-messages.js` の実在性検査（#579）はこのパーサを共用するので同時に追随した。

## ★★ 決定 3: **`check-kit-sync.js` / `check-feedback-status-sync.js` はキット版へ差し替え、分類 A にする**

HOWTO の手順どおり**機能差を実走で突合した**（作業仕様書 §5）。キット版は本リポ版の機能をすべて持ち（3 点検査・0 件走査の門・分類 A 0 件の門）、さらに **`/` 区切りへのパス正規化（Windows の 108 件偽陽性を解く）・`--require-planning`・未知の引数の拒否・`--self-test`（13 件 / 16 件）・`KIT_DIR` / `PLANNING_FEEDBACK_DIR` と隣接クローンの探索** を持つ。**本リポ版にあってキット版に無い機能は無い**（`compare()` の差はメッセージ文言と `#664` の出典表記だけ）。**キット版が優る → キット版へ差し替えて A**。#751 の受け入れ基準 1〜3 はこれで満たす。

## ★★ 決定 4: **実データ走査は planning を populate する `doc-links-planning.yml` で `--require-planning` 付きに行い、`ci.yml` には自己試験だけを残す**（[[IADR-0192]] 決定 4 ／ [[IADR-0193]] 決定 3 の配線の改定）

- 従前の決定は「CI では skip する。それを warn で隠さない」だった。**planning#343 の裁定はこれを改めた** —— fail-open のままだと「配線したのに一度も検査していない」状態が緑で固定される。
- **配線先は既に在った**: `doc-links-planning.yml` は `PLANNING_REPO_TOKEN` で submodule を取得し `check-doc-links.js --require-planning` を走らせている。ここへ 2 本を足した。失敗時の issue 起票の導線（同ワークフロー）も共用する。
- `ci.yml` の `kit-sync` / `feedback-status-sync` は `--self-test` だけにした（planning 不要で必ず実効する）。**フラグ無しの実データ走査を `ci.yml` に残さない**ことを回帰テストで固定した（残すと skip して緑になる形が戻る）。
- **決定の性質**: [[IADR-0192]] 決定 4 の「隠さない」は維持し、「skip する」を「populate するジョブで fail-closed に走らせる」へ置き換えた。夜間ジョブなので PR ではブロックしない —— **PR 段階で守るのはローカル実行と `scripts.repo.test.js`（`check-kit-sync.js` の実走を含む）である**点は従前と同じ。

## 決定 5: **束ねる範囲は #751 まで。#749 は束ねない**

- **#751 は #755 の受け入れ基準 5 が「併せて解消」と明記**しており、同じ資源（キット同期検査器とその配線）に閉じる。
- **#749（pin-freshness の逆方向比較）は束ねない**。[[IADR-0139]] 決定 1 の 6 条件は「裁定済みの同型な**契約追加**」向けであり、条件 A（同一資源）を検査器に読み替えても `check-planning-pin-freshness.js` は別の資源であり、条件 B（裁定が済んでいる）も満たさない（案 A / 案 B / キット版への差し替えのどれを採るかが未決）。**分類表では B（X）のまま追跡先 #749 とし、理由欄に「キット版への差し替えも俎上」を追記した。**

## 検討した選択肢

| | A. companion 分離（採用） | B. traceability.md を C のまま維持 | C. B（X）にして手動追随 |
| --- | --- | --- | --- |
| キットの是正が届くか | **バイト一致で機械検査** | 届かない（planning#363 の実測） | 人手（3 件が既に漏れた） |
| 入口のパス | 不変（[[IADR-0172]] 決定 2） | 不変 | 不変 |
| 総量予算 | 変わらない（規範は移動）。**別紙へ出して 1,000B の余白を保った** | 変わらない | 変わらない |
| 節名の参照 | companion に同名見出しを残す | そのまま | そのまま |

## 結果

- 良い影響: キット是正 3 件が届いた。Windows の偽 unclassified 108 件が消えた。実データ走査が実際に走る（夜間）。C が 17 → 4 に減り、X の追跡先が全件 issue を持つ（#749 / #756 / #757）。
- 悪い影響・トレードオフ: `traceability.md` の固有節を探す読者は companion へ 1 段辿る。規則番号の改番で過去 ADR の「規則 7 / 8」は旧番号を指す。X が 5 件ある（環流債務の測定値）。
- フォローアップ: #756（本リポ先行の検査器 3 本）／#757（`scripts.test.js` の追随）／#749。**doc-links-planning の初回実走ログで 2 本が実際に走ったことを確認する**（#751 受け入れ基準 4。夜間または `workflow_dispatch`）。

## 検出しないこと（明示する）

- **分類 B のデルタの妥当性**は従前どおり人が見る（[[IADR-0192]]「検出しないこと」）。
- **companion の規範がキット版と重複していないか**は機械では見ない（バイト一致は配布物側だけ）。
