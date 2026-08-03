---
title: 作業仕様書 — 写像検査のカバー範囲を計画レンジ全域へ広げる（逆方向検査）
type: spec
status: in-progress
related_ids: [NFR, IADR-0116]
author: Claude
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - ./20260803_issue-453_regression-test-foundation.md
  - ./20260803_issue-471_backend-libraries-detection-gaps.md
  - ./20260803_issue-474_backend-floor-iadr-and-0116-followup.md
  - "../tests/TEST_STRATEGY.md"
---

# 作業仕様書: 写像検査のカバー範囲を計画レンジ全域へ広げる

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性 — 退行検出のゲートが実際に検査していることを保証する）
- ユースケース（UC）/ 画面（SC）: なし（本作業は特定の FR/UC/SC を実装せず、**FR/UC/SC 全域を
  検査対象に載せる**検査器側の作業である）
- 関連 ADR / IADR: [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)
  （規約 6 の追記が各 PR の受け入れゲートとして本検査器のコマンドと判定を列挙している）。
  本作業は既存ゲートの検査範囲を広げるだけであり、新たな技術選定を伴わないため **IADR は起票しない**。
- 先行作業: [`20260803_issue-453_regression-test-foundation.md`](./20260803_issue-453_regression-test-foundation.md)
  （[`scripts/check-test-traceability.js`](../../scripts/check-test-traceability.js) と
  [`scripts/test-traceability-allowlist.json`](../../scripts/test-traceability-allowlist.json) の導入。PR #464）
- 計画レンジの単一情報源: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
  「起点 ID の種別」節
- 本リポジトリの起点: [#472](https://github.com/endazon/microservices-platform/issues/472)（親: #453 / #454）
- キットとの関係: `check-test-traceability.js` は `impl-handoff-kit` の配布物では**なく**本リポジトリ固有の
  スクリプトである。IADR-0115 の暫定デルタ／環流の対象外であり、`/plan-feedback` も要さない。

## 目的・背景

#453（PR #464）の写像検査は、**`docs/tests/` にファイルが在る起点 ID だけ**を突合対象にする
（`collectSpecIds()`）。そのため「テスト仕様書を作らなければ何も言われない」という fail-open が残る。

### 実測（着手時点・`origin/develop` = `1a16140`）

| 数え方 | 値 | 内訳 |
| --- | --- | --- |
| 計画レンジの ID 数 | **53** | `FR-01..21`（21）＋ `UC-01..11`（11）＋ `SC-01..21`（21）。`.claude/rules/traceability.md`「起点 ID の種別」節より |
| `docs/tests/` に仕様書のある ID | **27** | `FR-01..15`（15）＋ `SC-01..11`（11）＋ `NFR`（1）。`collectSpecIds()` の実測 |
| うち計画レンジ内 | **26** | 上記から `NFR` を除く（`NFR` は連番を持たずレンジの外にある） |
| 仕様書の無い計画 ID | **27** | `FR-16..21`（6）＋ `UC-01..11`（11）＋ `SC-12..21`（10）。26 ＋ 27 ＝ 53 で検算一致 |
| テストが参照する ID | **34** | 上記 27 ＋ `UC-01..07`（7） |
| **実装先行（テストは参照するが仕様書が無い）** | **7** | `UC-01`〜`UC-07` |

issue 本文の主張（53 / 27）は実測と一致した。ただし **27 の内訳には `NFR` が含まれ、`NFR` は計画レンジの
53 に含まれない**ため、「53 中 27」ではなく「**53 中 26**（＋レンジ外の `NFR` 1 件）」が正確である。
本作業の出力・文書はこの区別に従う。

`UC` の仕様書は 1 件も無いにもかかわらず、テストは既に `UC-01`〜`UC-07` を参照している。
つまり「実装は先行し、テスト仕様書だけが存在しない」状態が **7 件**すでに実在する。

さらに [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md)「各ドメイン issue が守ること」には
**テスト仕様書の新設義務が無い**ため、この沈黙は運用側でも塞がれていない。

## 対象範囲

- 含むもの:
  1. [`scripts/check-test-traceability.js`](../../scripts/check-test-traceability.js)
     - `.claude/rules/traceability.md` から計画レンジを読む（`parsePlanRanges` / `expandPlanIds` /
       `readPlanIds`）。**読めない・拾えないときは fail**（後述「方針」）。
     - **逆方向検査**: 計画レンジにあるのに `docs/tests/` に仕様書が無い ID を **warn** で列挙する。
     - **実装先行の検出**: そのうち `src/` のテストから参照済みの ID を、allowlist の
       `specMissing` と突合して **fail**（ratchet）。
     - 実行サマリ（`GITHUB_STEP_SUMMARY`）と標準出力に上記を出す。
     - 自己試験に逆方向検査の正例・負例を追加する。
  2. [`scripts/test-traceability-allowlist.json`](../../scripts/test-traceability-allowlist.json):
     `specMissing` キーを新設し、実測した既存 7 件（`UC-01`〜`UC-07`）を理由付きで登録する。
  3. [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js): 上記の回帰テストを追加。
  4. [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md): 「各ドメイン issue が守ること」の
     先頭へテスト仕様書の新設義務を追加し、ゲート一覧の判定欄を実装に一致させる。
  5. [`docs/adr/IADR-0116_reimplementation-branching-and-pr-policy.md`](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md):
     規約 6 追記のゲート表の該当行を実装に追随させる（判定条件が増えるため）。
- 含まないもの:
  - **不足しているテスト仕様書そのものの作成**（`FR-16..21` / `UC-01..11` / `SC-12..21` の 27 件）。
    各ドメイン issue（#438〜#452）が着手時に作る。とくに `FR-17..21` は
    [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) により**着手保留**であり、
    ここで仕様書だけ先に作るのは計画の先取りになる。
  - `EXCLUDED_UNITS` のヘルパ化・共通化（[#473](https://github.com/endazon/microservices-platform/issues/473) の範囲）。
  - 既存の写像 ratchet（`pending` / `classifyAgainstAllowlist` の判定）の変更。
  - 計画レンジ外の ID をテストが参照している場合（参照切れ）の検出。別の関心であり本作業では扱わない。

## 方針

### 1. 計画レンジの取得元 — `.claude/rules/traceability.md` をパースする（設定ファイル化しない）

issue の対応方針 2 は「`.claude/rules/traceability.md` を単一情報源としてパースするか、レンジを
設定ファイル化するか」を実装側の判断に委ねている。**パースを採る。**

- **二重管理を作らないため。** 本 issue が塞ごうとしているのは「レンジが広がったのに検査が追随せず
  黙る」ことである。レンジを別ファイル（例 `scripts/plan-id-ranges.json`）へ持つと**同じ事実の情報源が
  2 つ**になり、片方だけ更新されても誰も気付かない。塞ごうとしている穴と同型の穴を新設することになる。
- **`traceability.md` は既にレンジの正である。** 同ファイルは「計画側で ID が増減したら本節を追随させる
  （レンジは監査 / `trace-check` の突合基準であり、古いままだと新 ID の参照を『存在しない ID』と
  誤検出する）」と自ら宣言しており、監査・`trace-check` は既にこの節を基準に動いている。
  検査器だけが別の情報源を見る理由がない。
- **計画リポジトリを直接読むことはできない。** `planning/` は submodule であり、CI・ローカルともに
  未 populate があり得る（`check-doc-links.js` / `check-commit-messages.js` は未 populate 時に該当検査を
  skip する設計）。本検査は skip すると 0 件検査に戻るため、**常に読める本リポジトリ内のファイル**を
  正としなければならない。
- パースの脆さ（散文中の記述を読む）は次の 3 点で抑える。
  1. **節スコープを限定**する。`## 起点 ID の種別` 節（次の `## ` 見出しまで）だけを見る。
     同ファイルには後段に **AST の採番レンジ**（`FR-01..20` / `UC-01..07` / `SC-01..03`）が書かれており、
     ファイル全体を舐めると別プロジェクトのレンジを混ぜ込む。節スコープはこれを構造的に排除する。
  2. **バッククォート囲みの `X-nn..nn` だけ**を拾う（`FR` / `UC` / `SC` のみ。`ADR-0001..0039` は
     テスト仕様書の対象外なので採らない）。
  3. **拾えなければ fail**（後述）。

### 2. パース失敗は warn ではなく fail にする

節が見つからない・3 種のいずれかが拾えない・`to < from` のときは **exit 1** で止める。

逆方向検査だけを黙って skip すると「計画レンジ 0 件・不足 0 件」という**最も安全に見える出力**を出して
素通りする。これは本 issue が塞ごうとしている fail-open そのものであり、検査器が壊れたときに
いちばん静かになる設計は採らない。`.claude/rules/traceability.md` は submodule ではなく**本リポジトリの
追跡ファイル**なので、読めないのは環境差ではなく規約側の破壊（節の改名・書式変更）である。

### 3. 「仕様書が無い計画 ID」は warn（fail にしない）

未着手の FR/UC/SC が仕様書を持たないのは正当であり、fail にすると
[TEST_STRATEGY](../tests/TEST_STRATEGY.md#共通する設計原則-ratchet) が禁じる
「**成果物は正しいのに赤**」の常態化を招く。とくに `FR-17..21` は IADR-0119 で着手保留であり、
仕様書が無いことは正しい状態である。warn によりレンジ拡大時・着手時の取りこぼしを可視化する。

### 4. 「実装先行（テストはあるが仕様書が無い）」は ratchet 付きで fail にする（対応方針 3 の採否）

**採用する。ただし素の fail ではなく baseline ratchet 形にする。**

issue は「fail にする場合は既存状態で違反 0 であることを実測確認してから」と条件を付けている。
実測した結果 **違反は 0 ではなく 7 件（`UC-01`〜`UC-07`）** であった。したがって素の fail は
初回から赤くなるため採れない。一方で warn に留めると、本 issue の主眼である
「実装したのに仕様書が無い」状態が今後も無制限に増える。

そこで本リポジトリが既に 3 つのゲート（写像 allowlist / ライブラリ baseline / カバレッジ床）で使っている
**ratchet**（既知の残件を明示したうえで新規の悪化だけを止める）を同じ形で適用する。

- `scripts/test-traceability-allowlist.json` に `specMissing` キーを新設し、実測した 7 件を理由付きで登録する。
- 既存の `classifyAgainstAllowlist()` を**そのまま再利用**する（新しい判定ロジックを作らない）。
  - `specMissing` に無い実装先行 → **fail**（新規の悪化を止める）
  - `specMissing` どおり → **warn**（残件として実行サマリに出す）
  - 仕様書ができた／テスト参照が消えたのに `specMissing` に残る → **fail**（減らし忘れの検出）
- これにより「baseline 適用後の実効違反 0」が満たされ、素の fail と同じ締め付けを初回赤なしで得る。

判定対象は**計画レンジ内の ID に限る**。レンジ外の ID をテストが参照している場合（誤記・廃止 ID）は
参照切れという別の関心であり、本作業では扱わない（対象範囲の「含まないもの」）。

### 5. `NFR` はレンジ検査の対象にしない

`NFR` は計画側が連番を持たない（`specIdOf` も `NFR-01` を `NFR` へ丸める）。レンジ展開の対象外とし、
仕様書 27 件のうち 1 件は「レンジ外」として数える。既存の順方向検査（未写像の検出）では従来どおり
`NFR` を対象に含める（挙動不変）。

## 実装の詳細

`scripts/check-test-traceability.js` に追加する純粋関数（いずれも副作用なし・外部依存なし）:

| 関数 | 責務 |
| --- | --- |
| `planRangeSection(md)` | `## 起点 ID の種別` 節の本文だけを切り出す（次の `## ` 見出し直前まで）。見つからなければ `null` |
| `parsePlanRanges(md)` | 節内のバッククォート囲み `FR/UC/SC-nn..nn` を `{ FR: { from, to }, ... }` にする |
| `expandPlanIds(ranges)` | レンジをゼロ埋め ID の配列へ展開する（`FR-01` 〜 `FR-21` …） |
| `readPlanIds()` | 実ファイルを読んで展開する。パース不能なら例外（`main()` が fail に変換する） |
| `missingSpecIds(planIds, specIds)` | 仕様書の無い計画 ID |
| `implementedWithoutSpec(missing, testIds)` | そのうちテストが参照済みの ID |
| `readSpecMissingAllowlist()` | allowlist の `specMissing` を読む（既存 `readAllowlist()` は `pending` のまま無改変） |

実行時の出力（標準出力・`GITHUB_STEP_SUMMARY` の両方）は、既存の順方向検査の結果に続けて
逆方向の内訳を入れ子で出す（同じ ID が 2 つの見出しに現れて読み手が混乱しないようにする）。

```
- 計画レンジの ID: 53（FR-01..21 / UC-01..11 / SC-01..21）
- テスト仕様書あり: 26（レンジ外の NFR を除く）
- 仕様書なし（warn）: 27 — FR-16 / ... / SC-21
  - うち実装先行（テストは参照済み）: 7 — UC-01 / ... / UC-07
```

## 実行結果（実測）

変更後の `node scripts/check-test-traceability.js`（exit 0）の出力:

```
  warn  計画レンジ 53 件のうち 27 件に docs/tests/ のテスト仕様書がありません: FR-16 / FR-17 /
        FR-18 / FR-19 / FR-20 / FR-21 / SC-12 / … / SC-21 / UC-01 / … / UC-11。…
notice: テスト仕様書の無いまま実装が先行している起点 ID 7 件（allowlist 済み）: UC-01 / … / UC-07。…
[check-test-traceability] OK: 仕様書のある起点 ID 27 件中 27 件が写像済み（未写像 0 件はすべて allowlist 済み）。
  計画レンジ 53 件中 26 件にテスト仕様書あり（仕様書なし 27 件は warn。うち実装先行 7 件はすべて allowlist 済み）。
```

fail 側の経路も実測で確認した（allowlist を一時改変して実行し、確認後に戻した）。

| 実験 | 結果 |
| --- | --- |
| `specMissing` から `UC-02`〜`UC-07` を外す | `[実装先行・仕様書なし] UC-02 / … / UC-07` で **exit 1** |
| `specMissing` に `FR-16`（テスト参照なし）を足す | `[specMissing 減らし忘れ] FR-16` で **exit 1** |
| `.claude/rules/traceability.md` を一時退避 | 計画レンジ取得不能で **exit 1**（0 件検査へ退行しない） |

## 受け入れ基準

issue [#472](https://github.com/endazon/microservices-platform/issues/472) の受け入れ基準（3 件）を転記する。

- [x] 仕様書の無い計画 ID が warn として実行サマリに列挙される
      — 標準出力の `warn`（GitHub Actions では `::warning::`）と `GITHUB_STEP_SUMMARY` の両方に 27 件を列挙。
- [x] 「各ドメイン issue が守ること」に仕様書作成義務が明記される
      — [`TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) の同節の **1 番**へ追加し、以降を繰り下げた。
- [x] 自己試験に逆方向検査の正例・負例
      — 節スコープ・レンジ解析・展開・不足抽出・実装先行の判定に正例／負例を対で追加（14 件増。20 → 34 件）。

本作業で追加した不変条件:

- [x] 計画レンジのパースは AST のレンジ（`FR-01..20` / `UC-01..07` / `SC-01..03`）を拾わない
      — 自己試験・回帰テストとも、後段に AST レンジを置いたフィクスチャで固定した。
      「全文を渡すと AST レンジで上書きされる」ことも負例として明示し、節スコープが責務であることを示す。
- [x] 節が見つからない／種別が欠ける／`to < from` は fail する
- [x] 実ファイル（`.claude/rules/traceability.md`）から 53 件が読める（自己試験＋回帰テストで二重に固定）
- [x] 既存の順方向検査（仕様書 27 件・未写像 0）が退行しない
- [x] `node scripts/check-test-traceability.js --self-test` が exit 0（20 件 → **34 件**）
- [x] `node scripts/check-test-traceability.js` が exit 0
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑（186 件 → **191 件**）
- [x] `node scripts/check-doc-links.js` ／ `check-commit-messages.js` が緑

## 影響・リスク

- **CI が赤くなる条件が増える。** 今後、テストに `// FR-16` と書きながら `docs/tests/FR-16_*.md` を
  作らない PR は fail する。これは意図した締め付けであり、正しい入口は TEST_STRATEGY へ追加する
  義務 1（着手時に仕様書を作る）である。段取り上どうしても後回しにする場合の逃げ道として
  `specMissing` allowlist がある（理由必須）。
- **warn が 27 件出続ける。** 再実装が進むにつれ減る性質の warn であり、`notice` レベル（`::notice::`）で
  出す。fail ではないため CI は緑のままである。
- **`traceability.md` の書式変更で fail する。** 節の改名や書式変更を行った場合、検査器が止まる。
  黙って 0 件検査に戻るよりは望ましい挙動として意図している（方針 2）。エラーメッセージに
  期待する書式（``` `FR-01..21` ``` の形）と節見出しを明示する。
- **既存の 7 件（`UC-01`〜`UC-07`）は allowlist 済み**のため、本 PR 時点で新たな fail は発生しない。
