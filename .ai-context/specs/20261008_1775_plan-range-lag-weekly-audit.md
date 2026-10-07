---
title: 作業仕様書 — 計画 ID レンジ・NFR 採番の宣言の遅れを週次棚卸しで検知し、専用 issue へ起票する（#1775）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0093
  - ADR-0048
  - IADR-0423
  - IADR-0508
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0093_plan-id-ranges-derived-and-published.md (決定 2・決定 3・フォローアップ 3・4)
  - planning:projects/microservices-platform/07_adr/ADR-0048_impl-docs-restructure.md (決定 2)
related_specs:
  - 20261007_1769_plan-range-adr-0129
issue: "#1775"
---

# 作業仕様書 — 計画 ID レンジ・NFR 採番の宣言の遅れを週次棚卸しで検知する（#1775）

## 目的と射程

#1769 の受け入れ基準 3（任意）の後続。`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節の宣言（計画 ID レンジと NFR 採番）が
計画側に遅れたときに、週次の棚卸しで検知して報告し、黙って緑にしない。対の issue は AST#1208。

**着手前の実測**:

- `scripts/check-planning-adr-range.js`（IADR-0423）は PR CI（`ci.yml` の `static-checks`）で毎回走り、FR / UC / SC / ADR を突き合わせている。
  develop の直近の実行（`static-checks` のアノテーション）に `unverified` の警告は無く、`PLANNING_REPO_TOKEN` は効いている（ok は標準出力に出るだけで注記を残さない）。
- それでも ADR-0129 の遅れ（2026-10-06 マージ）は第 4 回全体監査 B-1（2026-10-07）まで残り、#1769 / #1774 が手で引き直した。
  **ずれても警告が出るだけで、作業にならない。** 週次棚卸し（`backlog-audit.js`）は本件を見ていなかった。
- **NFR 採番の宣言（`NFR-01`〜`NFR-29`）は、どこからも突き合わされていない。** 計画側の公開レンジ表（`kg-ranges.json`）は NFR を持たない
  （計画 ADR-0093 決定 2。足すかは同フォローアップ 3 の裁定待ち）。計画リポの `gen-plan-ranges.js` は要求一覧の定義表の行頭セルから NFR を導いて参考行に出している。

**射程**:

- `scripts/check-planning-adr-range.js`: `--with-nfr`（計画側の要求一覧から NFR のレンジを導いて突き合わせる）、`--upsert-issue`（ずれたら専用 issue を upsert）、
  宣言不読を `error`・exit 1 へ、ADR の宣言が読めない（`planAdrRange()` が null）も `error` へ、`unverifiedKinds`・`expected`、`--rules <path>`、結果 JSON に `lagIssue`
- `scripts/backlog-audit.js`: 節 8「計画 ID レンジ宣言の鮮度」（`--plan-range <json>`）
- `.github/workflows/backlog-audit.yml`: 前段ステップ `Resolve planning ID range`、棚卸しステップへ `--plan-range` と `!cancelled() && 自己試験の成功`
- `scripts/scripts.repo.test.js`: 実バイナリの fail-loud・未確認の報告・配線
- `scripts/README.md` の 2 行、`ci.yml` のコメント 1 箇所、IADR-0423 への日付つき追記、IADR-0508 新設と索引行

**射程外**:

- PR CI（`ci.yml`）の挙動: 4 種のまま・起票しない（NFR は週次の報告に限る。計画 ADR-0093 決定 2 の裁定待ちの射程を PR 経路まで広げない）。宣言不読の exit 1 だけは同じ検査器なので PR CI にも及ぶ（宣言が崩れていれば `check-trace-blocks` / `check-commit-messages` も既に落ちる）
- 引き直し PR の自動作成、実行頻度の変更（週次のまま）
- 計画側へ「NFR を公開レンジ表へ足す」裁定を求める環流（フォローアップ 3 は計画側の残件として既にある）

## 設計

選んだ形と比較は IADR-0508。要点:

| 状況 | JSON の `status` | 前段の終了コード | 棚卸し報告の節 8 |
| --- | --- | --- | --- |
| 5 種一致 | `ok`（`scanned: 5`） | 0 | 指摘なし（数えない） |
| 遅れ / 先走り | `behind` / `ahead` | 0（起票に失敗したら 1） | 種別ごとに 1 件・専用 issue の番号 |
| NFR だけ取れない | `ok` 等 ＋ `unverifiedKinds: [NFR]` | 0 | 🔴 未確認（NFR）を 1 件 |
| 計画側に届かない | `unverified`（`scanned: 0`） | 0 | 🔴 未確認（5 種）を 1 件 |
| 宣言が読めない | `error` | 1（ジョブ赤 → `report-failure` が ci-failure issue） | 🔴 宣言を読めない を 1 件 |
| 前段が結果を書かなかった | — | — | 🔴 未確認（結果ファイルを読めない）を 1 件 |

NFR の導出は計画リポの `gen-plan-ranges.js` と同じ規則（`02_requirements/*.md` の定義表の行頭セル `| NFR-NN |`）。取得は `gh api …/contents`（一覧 1 回 ＋ `.md` を raw で 1 回ずつ。現状 1 ファイル）で、読み取り専用の HTTP に限る。

## 母集合（規則 9・10）

**軸 1（「宣言不読も exit 0」「常に exit 0」）**: `git grep -n -E "宣言不読|宣言が読めない、|常に exit 0" -- . ':!.ai-context/specs' ':!.ai-context/superpowers' ':!CHANGELOG.md'`

| ヒット | 扱い |
| --- | --- |
| `scripts/check-planning-adr-range.js` 冒頭注記 | **対象**（直した） |
| `scripts/README.md:28`（当該行） | **対象**（直した） |
| `.github/workflows/ci.yml:263`（PR CI のコメント） | **対象**（「計画側の取得失敗では exit 0、宣言不読だけ exit 1」へ） |
| `scripts/scripts.repo.test.js:8516`（検査器の母集合の経緯コメント） | **対象**（#1775 の 1 文を足した。経緯は残す） |
| `.ai-context/adr/IADR-0423…:91`（決定 3） | 凍結記録。本文は残し、日付つき追記で IADR-0508 を指した |
| `.claude/hooks/check-impl.js:6`・`IADR-0258`・`scripts/k8s-local-up.test.js` | 除外。別の検査器・スタブの話 |

**軸 2（「NFR は突き合わせない」）**: `git grep -n -E "NFR は突き合わせない|NFR\` は突き合わせない|NFR は入れない" -- …（同上）`

| ヒット | 扱い |
| --- | --- |
| `scripts/check-planning-adr-range.js` の `KINDS` 注記 | **対象**（`--with-nfr` のときだけ足すと書いた） |
| `scripts/README.md:28` | **対象** |
| `.ai-context/adr/IADR-0423…:85・120` | 凍結記録。日付つき追記で「週次の報告に限って NFR を見る」を指した |

**軸 3（自己試験の件数）**: `check-planning-adr-range.js` 14 → 34 件、`backlog-audit.js` 8 → 13 件。件数を書く箇所は `scripts/README.md` の 2 行だけ（直した）。IADR-0423 §結果 の 14 件は当時の記録。

**軸 4（検査器の母集合 60 本）**: 新しい `scripts/*.js` は足していない（既存 2 本の拡張）。`scripts.repo.test.js` の 60 本の固定は不変。

**この変更で新たに誤りになる自分の記述**: `backlog-audit.yml` 冒頭の「何を出すか」に節 8 を足した。`backlog-audit.js` 冒頭の「見るもの」に 8 を足した。

## 受け入れ基準

1. 採った形を IADR に残す → IADR-0508
2. 宣言が遅れた状態を模した入力で検知が発火し、出力が読める → 自己試験（FR / UC / SC / ADR / NFR それぞれ 2 件先へ進めた陽性対照、issue 本文、節 8 の描画）と、
   宣言を `ADR-0001..0128`・`NFR-01`〜`NFR-27` へ戻した写しを `--rules` で与えた実走（計画側は実ネットワーク）で
   `behind`・「ADR: 宣言 0001..0128 が実物 0129 に 1 件遅れている / NFR: 宣言 01..27 が実物 29 に 2 件遅れている」
3. 計画リポジトリに到達できないときに黙って緑にならない → `unverified`（または `unverifiedKinds`）を棚卸し報告の節 8 が「🔴 未確認」として指摘に数える（実バイナリで確認）
4. `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 全件 pass、変異試験で新しい試験が落ちる

## テスト方針

- `check-planning-adr-range.js --self-test`（14 → 34 件）、`backlog-audit.js --self-test`（8 → 13 件）。API・`gh` は差し替え、実ネットワークを叩かない。
- `scripts.repo.test.js`（3 件）: 書式の崩れた宣言で exit 1・`error`、計画側に届かない結果を受けた棚卸し報告の節 8、ワークフローの配線と PR CI が 4 種のままであること。

## 計画書との差異

なし。計画 ADR-0093 決定 3 の 4 条件（ID レンジの突合だけ・読み取り専用の HTTP・計画側の取得失敗で落とさない・ビルドやテストの前提にしない）を維持する。
NFR は決定 2 の「公開レンジ表へ足すか」には触れず、週次の報告に限って計画側の実物（要求一覧）から導いて見る。

## 未決事項

- 計画側が NFR を公開レンジ表へ足す裁定（ADR-0093 フォローアップ 3）が出たら、`fetchPlanningNfrRange` を公開表の読み取りへ置き換える（取得が 1 回に戻る）。
