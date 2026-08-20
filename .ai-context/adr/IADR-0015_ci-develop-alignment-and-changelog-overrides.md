---
title: IADR-0015 CI トリガーの develop 整合・コミット規約チェック・CHANGELOG 誤帰属補正
type: impl-adr
status: Accepted
related_ids:
  - NFR
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - "../../CLAUDE.md（補助成果物の自動生成 / 自動化・検証・安全 / CI ゲート）"
related_specs:
  - ../specs/20260704_NFR_ci-develop-alignment.md
related_adrs:
  - IADR-0000 (実装判断を記録する)
---

# IADR-0015: CI トリガーの develop 整合・コミット規約チェック・CHANGELOG 誤帰属補正

- 状態: Accepted
- 日付: 2026-07-04
- 決定者: claude（実装）
- 関連: NFR（CI/CD）、Issue #60（親 #48）、PR #76

## コンテキストと課題

既定ブランチが `develop`（`main` は Initial commit のまま）である一方、自動化ワークフローの
`push`/`pull_request` トリガーが `branches: [main]` に限定され、複数の CI ゲート・補助成果物が
発火していなかった。あわせて (a) コミット規約（`種別(起点ID): 要約`）の逸脱が再発していること、
(b) 手書き `docs/api/openapi.yaml`（3.1.0 リッチ仕様）が雛形生成で破壊され得ること、
(c) 誤記コミット `b421761`（件名 `feat(FR-10)` は誤記・実体は P0 基盤スケルトン）が CHANGELOG で
FR-10 として誤帰属されること、が課題として挙がった。本 IADR は本 PR の重要な実装判断を記録する。

## 決定

### 1. ブランチ運用 — 各ワークフローのトリガーへ `develop` を追加する

CLAUDE.md は「`main` を安定版とする」とするが、実運用の既定ブランチは `develop`。二重管理を避けるため、
`ci` / `changelog` / `openapi` / `codeql` の各ワークフローの `push`/`pull_request` トリガーへ `develop` を
追加する（`main` は残す）。定期リリースマージ運用の確立は将来課題として `operations.md` に委ねる。
本判断は計画側の運用にも関わるため、必要なら `/plan-feedback` で計画リポへ環流する。

### 2. コミット規約の機械チェック — 起点 ID を必須化する

`scripts/check-commit-messages.js` が PR の追加コミット（`base..HEAD`）の件名を検査する。既存履歴は
書き換えず再発防止のみを目的とする。内容変更の種別（`feat`/`fix`/`perf`/`refactor`/`docs`/`test`）は
起点 ID（スコープ）を**必須**とし、計画 ID に紐づかない雑多・ツールチェーン変更は
`chore`/`style`/`build`/`ci`（`TYPES_ALLOW_NO_SCOPE`）で表現し ID 省略を許す。

> 経緯: 初版は「スコープが存在する場合のみ ID 書式を検証」する実装で、`feat: 説明`（ID 無し）が
> 素通りし再発防止の目的を満たさなかった（PR #76 レビュー 🔴）。起点 ID を必須化してこの抜け穴を塞いだ。

#### 2-1. 規約導入前の既存コミットは恒久適用除外リストで grandfather する

起点 ID を必須化した結果、本 PR のブランチに含まれる**規約導入前**の既存コミット
（`d1652dc`/`394fa1f`/`079490d`/`153810a`/`d4835097`。いずれも起点 ID 無し）が
`commit-messages` ジョブで失敗する。これらは規約が存在しない時点で作られており、
force push 禁止方針（CLAUDE.md）のため件名を書き換えられない。当初は squash マージのみで解消する
運用としたが、`commit-messages` を必須チェックにすると「マージ前の必須チェックが落ちるため
マージできず、squash マージでしか解消できない」という循環（chicken-and-egg）が生じる（PR #76）。

これを解くため、`scripts/commit-allowlist.json`（`changelog-overrides.json` と同型）に**完全 SHA と
理由を明記した恒久適用除外リスト**を設ける。`check-commit-messages.js` はこの列挙に一致した
コミットのみを `skip(allowlist)`（CI ログに理由付きで常時表示＝監査可能）として検査対象から外す。
**将来の新規コミットは通常どおり検査対象**であり、抜け穴（blanket loophole）ではない
（`.git-blame-ignore-revs` と同種の、遡及不能な既存履歴に対する明示的除外）。
本ファイルへ新規コミットの規約違反を安易に追加しないことを運用ルールとする。

### 3. OpenAPI 雛形フォールバックの `--force` を外す

生成コマンド未設定時の雛形フォールバックから `--force` を外し、既存 `docs/api/openapi.yaml`（手書き 3.1.0）を
上書きしない。生成元の通信仕様書が無い現状で `--force` を付けると空雛形で破壊されるため。

### 4. CHANGELOG 誤帰属は履歴を書き換えず生成時のみ補正する

`scripts/changelog-overrides.json` ＋ `gen-changelog.js` の `applyOverride` で、誤記コミットを
CHANGELOG 生成時のみ `remap`/`exclude` する。git 履歴は書き換えない（既存コミット改変を避ける方針）。
`b421761` は `type` を実体どおり **`feat` のまま**、`scope` のみ `FR-10 → P0` に補正する
（`docs` へ remap すると約 9,200 行の実装をドキュメントとして過小計上する新たな誤帰属を生むため不可。PR #76 レビュー 🔴）。
未知の `action`（タイプミス等）は黙って remap 扱いにせず警告して補正を無視する。

## 検討した選択肢（CHANGELOG 補正）

- **A. 履歴を rebase して誤記件名を修正**: CHANGELOG は綺麗になるが、共有ブランチの履歴改変・force push を
  伴い禁止事項に抵触する。不採用。
- **B. 生成時のみ補正（本決定）**: 履歴不変で表示のみ補正でき、Issue #60 の方針に沿う。
- **C. 何もせず注記のみ**: 追跡は残るが誤帰属が CHANGELOG 本文に残る。不採用。

## 結果

- 良い影響: develop 運用で CI ゲート・補助成果物・SAST が発火し、コミット規約逸脱を CI で機械的に検出でき、
  手書き OpenAPI 破壊と CHANGELOG 誤帰属を防げる。`validateSubject`/`applyOverride` は `scripts/scripts.test.js` で回帰を固定した。
- トレードオフ: 起点 ID 必須化により、既存の非準拠コミット（`feat:`/`fix:` の ID 無し）を含む PR は
  commit-messages ジョブで失敗する。履歴改変は行わないため、**規約導入前**の該当コミットは
  `scripts/commit-allowlist.json` に SHA と理由を明記して恒久適用除外し（決定 2-1）、CI を通す。
  将来の新規コミットは通常どおり検査対象。除外は完全 SHA の列挙に限り監査可能な形で残す。
- 安全性: いずれの補正も履歴を書き換えず、範囲解決不能時はチェックを `exit 0` でブロックしない（fail-open）。
