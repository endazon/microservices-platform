---
title: 作業仕様書 — 「担当 issue が無い計画 ID」を warn の並びから区別して出す（範囲表記の展開・切り出し版）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0141
  - IADR-0179
  - IADR-0188
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - "20260803_issue-472_test-traceability-plan-range.md"
  - "20260803_issue-453_regression-test-foundation.md"
  - "../adr/IADR-0179_unnumbered-nfr-for-meta-work.md"
  - "../adr/IADR-0188_unnumbered-nfr-applies-to-all-work.md"
  - "../adr/IADR-0141_audit-rounds-and-population-drawing.md"
---

# 作業仕様書: 無主の計画 ID を warn から区別する（#748・切り出し版）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**無採番 `NFR`**。実装作業の統制・検知装備）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: [IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) ／
  [IADR-0188](../adr/IADR-0188_unnumbered-nfr-applies-to-all-work.md)（メタ作業の無採番 `NFR`）、
  [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)（母集合の引き方）
- 計画書リンク: `planning/projects/microservices-platform/02_requirements/01_requirements.md`

### 起点 ID に無採番 `NFR` を用いる根拠（実読して判断した）

`git submodule update --init planning` で populate し、
`planning/projects/microservices-platform/02_requirements/01_requirements.md` の非機能要件表を実読した。
ID 列は **`NFR-01`〜`NFR-27` の 27 件**で、区分は 性能（01-04）／可用性（05-06）／
スケーラビリティ（07-08, 27）／セキュリティ（09-18）／運用・保守（19-21）／拡張性（22-26）である。
**いずれも「稼働する製品」の品質要件**であり、本作業（検査器の追加＝工程の統制）に当たる番号は無い。
よって `.claude/rules/traceability.md` の無採番許容 2（ID 列はあるが当たる番号が無い）に該当し、
**無採番 `NFR` を用い、計画へは環流しない**（IADR-0179 決定 2 / IADR-0188）。

## 目的・背景

`scripts/check-test-traceability.js` の逆方向検査は、計画レンジのうち `docs/tests/` にテスト仕様書が
無い ID を **1 本の warn の並び**で出す。この並びは「**まだ着手していない ID**」であって
「**引受先が未定の ID**」ではないが、**両者が区別できない**。この並びを見て
「担当 issue が無い」と結論する誤りが **3 回**起きた（#748 本文の表。SC-14 / SC-15 → #438、
UC-09 → #445、UC-10 → #450 が実際には引き受けていた）。

**機械的な核心は範囲表記である。** #438 の本文には `SC-13〜17` とあり、**`SC-14` という文字列は
存在しない**。素の文字列一致では 3 件とも検出できない。

`CLAUDE.md`「検査器・規約の追加は同型の事故が 2 回起きたら」に該当する（実測 3 回）。

## 対象範囲

### 対象（本 PR）

1. **範囲表記（`SC-13〜17` 等）の展開**（#748 受け入れ基準 1・論点 2）
2. **過去 3 件を「担当あり」と判定する回帰テスト**（同 2）
3. **無主 ID を warn の並びから区別して出す**（根拠つき出力。同 5）
4. **変異試験**（同 3）

### 対象外（本 PR で実装しない・**別 issue へ切り出すべき**）

- **「issue の起点 ID を JSON へ吐く定期ジョブ」**（#748 論点 1 の案 A）。
  - 理由: **GitHub API 依存**であり、`check-test-traceability.js` が冒頭で宣言する
    **「外部依存ゼロ」**（#453）に触れる。オフライン／レート制限で不安定になるという
    論点 1 の懸念そのものを、検査器の中へ持ち込むことになる。
  - よって本 PR は**突合材料の消費側だけ**を実装し、材料は
    **「JSON があれば読む、無ければ skip する」**（`check-doc-links.js` の submodule 未 populate と同型）。
  - **本作業では起票しない**（指示による）。切り出した範囲を本節に残すことで、起票時の根拠とする。
- closed issue の扱い（論点 3）: **生成側の責務**とする。本 PR の消費側は
  「JSON に載っている＝担当あり」とだけ解釈し、open 限定の絞り込みは生成側が行う。
- 修飾付き ID（`AST/FR-17`）の除外（論点 4）: 既存の `idsInText` と同じ規則を範囲展開側にも適用する。

## 母集合（着手時に自分で引いた・2026-08-15）

**問い**: 「計画 ID を扱っている検査器・baseline のうち、**範囲表記を解釈している箇所**が他に無いか」。
誤りの側（範囲表記そのもの・ID を扱う実行可能物）から引く。**`grep -l` のファイル一覧を母集合に
しない**ため、各軸は「引いた数 → 除外 → 残り」を書く。走査は**本仕様書を書く前**に実行した
（`git grep` は追跡ファイルのみを見るため、未 add の本書は元から入らない。念のため下に引き算を示す）。

| 軸 | コマンド（`-- . ':!planning' ':!src/ai-stock-trading'` は共通の除外） | 生の数 | 除外 | 残り |
| --- | --- | --- | --- | --- |
| 1 | `git grep -lIE '(FR\|UC\|SC\|NFR\|ADR\|IADR)-[0-9]+ *[〜～~-] *(FR\|UC\|SC\|NFR\|ADR\|IADR)?-?[0-9]+'` | 10 | 10（すべて `docs/specs/` `feedback/` `src/vitest.config.ts` の**散文・設定**。検査器なし） | **0** |
| 2 | `git grep -lIE '\(\?:?(FR\|NFR)\b[^)]*\)\|FR\|UC\|SC\|...'`（ID 種別の正規表現を持つもの） | 2 | 0 | **2**（`check-test-traceability.js` / `check-commit-messages.js`） |
| 3 | `git grep -lI -e '〜' -e '～'`（範囲表記に使う文字の全走査） | 287 | 285（日本語散文の「〜まで」「数万〜数十万」等） | **2**（軸 2 と同一） |
| 4 | `git grep -lIE '(FR\|UC\|SC)-[0-9]'  -- scripts .github .claude` | 47 | 45（プロンプト・テンプレート・ワークフロー・README の**例示**。ID を**解釈**しない） | **2**（軸 2 と同一） |
| 5 | `git grep -lIE '"(FR\|UC\|SC\|NFR)(-[0-9]+)?"'`（JSON に計画 ID を値として持つもの） | 2 | 0 | **2**（`check-test-traceability.js` / `test-traceability-allowlist.json`） |
| 6 | `git grep -lIE 'expandPlanIds\|parsePlanRanges' -- scripts .github .claude` | 2 | 0 | **2**（`check-test-traceability.js` / `scripts.repo.test.js`） |

**自己参照の引き算**: 本仕様書は軸 1・3・4 の検索語を含む（範囲表記 `SC-13〜17` 等）。
コミット後に同じコマンドを引くと **軸 1 は 10 → 11、軸 3 は 287 → 288、軸 4 は 47 → 47**
（軸 4 は `scripts` `.github` `.claude` に限るため本書は入らない）になる。上表は
**本書を書く前の時点の生の数**であり、追試時は自己参照 1 件を引いて読むこと（規則 8）。

### 引いた結論（除外の理由つき）

- **範囲表記（`〜` / `～` / `-`）を解釈している実行可能物は 1 つも無い**（軸 1・3・6 が一致）。
  つまり本 PR の展開ロジックは**新設**であり、他所と二重持ちにならない。
- **計画 ID のレンジを解釈しているのは `check-test-traceability.js` の `parsePlanRanges` /
  `expandPlanIds` だけ**で、書式は `` `FR-01..22` ``（バッククォート囲みの `..`）である。
  `check-commit-messages.js` は自前で持たず `readPlanIds` を**再利用**している（同ファイル 316 行）。
  よって**追随先は 1 箇所**であり、本 PR は既存関数を壊さず別関数として足す。
- baseline は `test-traceability-allowlist.json` のみ。本 PR は**そのスキーマを変えない**
  （無主判定は allowlist とは別軸のため。混ぜると allowlist の意味が二重になる）。
- 軸 4 で落とした 45 件は、いずれも**プロンプト（`.claude/`）・issue テンプレート・ワークフロー・
  README の例示**であり、ID を機械的に解釈しない。**黙って落とさず理由を残す**（規則 6）。

## 設計

`scripts/check-test-traceability.js` に、既存の逆方向検査（#472）と**直交する**判定を足す。

### 1. 範囲表記の展開（`claimedIds`）

issue の本文テキストから、**引き受けている計画 ID とその根拠表記**を取り出す。

- 単体 ID: 既存の `idsInText` と同じ規則（修飾付き `AST/FR-17` は除外、ゼロ埋め正規化）。
- 範囲: `SC-13〜17` / `SC-13〜SC-17` / `SC-13～17` / `SC-13-17` / `FR-01..22` を展開する。
  - 区切りは `〜`（U+301C）・`～`（U+FF5E）・`~`・`..`・`-` の 5 形（**あり得る形を列挙してから引く**。規則 2）。
  - 右辺に種別が付く場合は**左辺と一致するときだけ**範囲とみなす（`SC-13〜FR-17` は範囲ではない）。
  - `to > from` のときだけ展開する（誤って ID を分解した形を範囲と読まない）。
- 戻り値は `Map<ID, 表記>`。**表記をそのまま持つことが「根拠」の出力になる**（受け入れ基準 5）。
  同一 issue が同じ ID を複数の表記で挙げた場合（#438 は `SC-13〜16` と `SC-13〜17` の両方を書いている）、
  根拠は **1 件に畳む（後勝ち）**。引受先の特定に必要なのは issue 番号であり、表記の全列挙ではない。

### 2. 突合材料（`readIssueOwners`）

`scripts/plan-id-issue-owners.json` を読む。**無ければ `null` を返して skip**（外部依存ゼロを保つ）。

```json
{ "generatedAt": "2026-08-15T00:00:00Z", "issues": [{ "number": 438, "text": "…issue の題と本文…" }] }
```

- `issues[].text` は題と本文を連結した生テキスト。**open のものだけを載せるのは生成側の責務**（論点 3）。
- パスは環境変数 `PLAN_ID_OWNERS` で差し替えられる（**変異試験を spawn 単位で当てるため**）。

### 3. 無主の判定（`buildIssueOwnership` / `unownedPlanIds`）

- `buildIssueOwnership(issues)` → `Map<ID, [{ issue, notation }]>`
- `unownedPlanIds(planIds, ownership)` → 引受先が 1 件も無い ID（昇順）
- 出力は**従来の warn とは別の行**にする。無主が 1 件でもあれば **fail**（#748 の期待出力どおり）。
  担当ありは根拠つきで notice に出す（`SC-14 ← #438「SC-13〜17」`）。

材料が無いときは `notice` で「skip した」ことを明示する。**「無主 0 件」と「見ていない」を
読み分けられる出力にする**（`CLAUDE.md` の統制記述ルールと同じ作法）。

## 受け入れ基準

- [ ] `SC-13〜17` のような範囲表記を展開して突合できる（#748 AC1）
- [ ] 過去 3 件（SC-14 / SC-15 → #438、UC-09 → #445、UC-10 → #450）を**担当ありと判定する**回帰テストがある（AC2）
- [ ] 無主の ID を仕込んだ**変異**で `fail` することを自己試験／`scripts.repo.test.js` が確かめる（AC3）
- [ ] 材料 JSON が無くても落ちない（skip。AC4）
- [ ] 判定結果に**根拠**（どの issue のどの表記か）が出る（AC5）
- [ ] `node scripts/check-test-traceability.js` / `node scripts/scripts.test.js` /
      `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑

## テスト方針

テストは **`scripts/scripts.repo.test.js`** に書く（`scripts.test.js` はキット配布物・バイト一致のため変更しない）。
テスト名の直前コメントに起点 ID（無採番 `NFR`）と `#748` を記す。

1. **回帰（AC2）**: #438 / #445 / #450 の**本文の原文**（#748 本文が引用した文言）をフィクスチャにし、
   `SC-14` `SC-15` `UC-09` `UC-10` が担当ありになること。
2. **変異 M1（AC3・端から端まで）**: 上記 3 件だけを載せた材料 JSON を一時ファイルに置き、
   `PLAN_ID_OWNERS` で差し替えて検査器を spawn する。計画レンジの残りが無主になるので **exit 1**。
   同時に **SC-14 / SC-15 / UC-09 / UC-10 が無主の並びに現れないこと**を確かめる
   （＝正例と変異を 1 枚のフィクスチャで両立させる）。
3. **変異 M2（AC1 が効いていることの側）**: 範囲展開を使わず素の `idsInText` で同じ突合を行うと、
   **SC-14 / SC-15 / UC-09 / UC-10 が無主に混じる**ことを実測する。
   **これが落ちなければ、範囲展開は結論に効いていない。**
4. **skip（AC4）**: 材料が存在しないパスを指すと exit 0 のままで、出力に skip の明示があること。

## 実測（変異試験と検証の証跡・2026-08-15）

**正例だけの緑は受け入れない。** 実装を壊して落ちることを 2 通り実測した（実行後は原本へ復元済み）。

| 変異 | 壊し方 | 期待 | 実測 |
| --- | --- | --- | --- |
| **M-A** 範囲展開を殺す | `claimedIds` の範囲ループを無条件 `continue` にする | 回帰が落ちる | `--self-test` **FAIL 6 件** ／ `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` **exit 1**（`AssertionError: SC-14 が展開されない`） |
| **M-B** 無主の区別を殺す | `unowned = unownedPlanIds(...)` を `unowned = []` にする | 変異試験が落ちる | 同 **exit 1**（`AssertionError: 無主があるのに fail しない`） |

M-B は「区別そのもの」を消す変異である。**M-A だけでは、範囲展開が正しくても判定に使われて
いない実装が緑になり得る**ため、2 種を併走させる。

検証コマンドと結果:

```console
$ node scripts/check-test-traceability.js --self-test
[check-test-traceability] 自己試験 46 件 OK。
$ node scripts/check-test-traceability.js
notice: 担当 issue の突合は skip しました（突合材料 scripts/plan-id-issue-owners.json がありません）。
[check-test-traceability] OK: … 計画レンジ 54 件中 29 件にテスト仕様書あり（仕様書なし 25 件は warn …）。
$ node scripts/scripts.test.js                        # exit 0
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js   # exit 0 / ✓ 536 tests passed
$ for s in check-doc-links check-doc-updated check-doc-type-vocabulary check-doc-status-vocabulary \
    check-plan-id-qualification check-cross-repo-refs check-reading-budget check-kit-sync \
    check-test-spec-coverage; do node scripts/$s.js; done   # すべて OK
```

`scripts/scripts.test.js`（分類 A・キットとバイト一致）は**変更していない**（`git status` で未変更を確認）。
`CLAUDE.md` と `.claude/rules/` へは **1 バイトも足していない**（必読規約 50KB 予算のため）。

## 計画書との差異

- 差異: なし（計画書の要求ではなく、実装作業の統制装備である）

## 未決事項

- 材料 JSON の**生成側**（定期ジョブ）は本 PR の対象外。上記「対象外」の理由とともに別 issue へ
  切り出すべきである（**本作業では起票しない**）。生成されるまで本判定は skip のままであり、
  **skip であることは出力に明示される**ため「検査しているつもり」にはならない。
