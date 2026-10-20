---
title: platform-backup の age の版が Alpine の APKINDEX から消えたことを日次で検知し、専用 issue を起票する（check-age-apkindex.js・age-apkindex-watch.yml。#2005）
type: spec
status: done
related_ids: [NFR-21, ADR-0107, IADR-0568, IADR-0471, IADR-0508, IADR-0232]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0107（イメージの固定）
issue: "#2005"
---

# 仕様書: age の版のずれを日次で検知する（#2005）

> 本仕様書は実装着手前に作成した（着手 2026-10-20）。実装の基点は MSP `origin/develop` `7e5ea7ba`。判断は IADR-0568。

## 起点となる計画書（トレーサビリティ）

- 非機能: NFR-21（可観測性・検知）
- 関連 ADR: ADR-0107（イメージの固定）
- 関連 IADR: IADR-0471（platform-backup のイメージ。2 回目の事故を追記する）・IADR-0508（専用 issue の upsert の作法）・IADR-0232（ワークフローの権限の表）

## 同型の事故（2 回）

| 回 | 日付 | 消えた版 | 気付くまで |
| --- | --- | --- | --- |
| 1 | 2026-10-08（#1825） | `1.3.1-r6` | Images の赤で発覚 |
| 2 | 2026-10-20（#2003） | `1.3.2-r0` | r1 の作り直し（10-14）から 6 日 |

運用規約の「検査器・規約の追加は同型事故 2 回から」に当たる。

## 形の選択

| 案 | 採否 | 理由 |
| --- | --- | --- |
| 週次の `backlog-audit.yml` の前段に置く | 採らない | 2 回目の見逃しは 6 日。週次では同じ幅の見逃しが残る |
| **独立の日次ワークフロー＋専用 issue の upsert** | **採る** | 先例 `check-planning-adr-range.js --upsert-issue`（IADR-0508）と同じ作法。PR を赤くしない |
| PR の CI（static-checks）で突き合わせる | 採らない | 外部の CDN の状態で無関係な PR を赤くする。自己試験だけを `scripts.repo.test.js` 経由で PR に載せる |
| ビルド時の失敗文言を改める | 採らない | 落ちてからの案内であり、検知を早めない（`lib/backup-image-build-cause.sh` が既に「版の解決の失敗」を分けている） |

## 判定

- 突き合わせるもの: Dockerfile の `ARG AGE_VERSION` と、`<ALPINE_MIRROR>/<ALPINE_BRANCH>/community/<arch>/APKINDEX.tar.gz` の `P:age` の `V:`。アーキテクチャは Dockerfile が sha256 を固定している `x86_64` / `aarch64` の 2 つ。
- `ok`（全部一致）: 何もしない。
- `drift`（どれかが違う・age が無い）: 専用 issue（ラベル `age-apkindex-drift`）を起票し、以後は本文を上書きする。自動クローズしない。版は自動で上げない。
- `unverified`（届かない・読めない）: warning だけ出し、起票しない。片方だけ読めて一致なら `unverified`、片方だけ読めて食い違えば `drift`。
- `error`（Dockerfile の ARG を読めない、または APKINDEX が 404 / 410＝取得元が恒久的に無い）: exit 1。起票が要るのに書けないときも exit 1（ci-failure-issue が拾う）。
  ［2026-10-20 / PR #2010 の AI レビュー 🟡］初稿は 404 も `unverified` にしていた。ブランチが EOL でミラーから消えると毎日緑のまま検知が止まるので、`error` へ分けた。5xx・ネットワーク断は `unverified` のまま。
- 実測（2026-10-20）: 実ネットワークで `ok`（両アーキテクチャ `1.3.2-r1`）。固定を `1.3.2-r0` にした Dockerfile では `drift`。`ALPINE_BRANCH=v3.99` では 404 で `error`・exit 1。プロキシを塞ぐと `unverified`・起票不要。
- 人が drift の issue を閉じても、ずれが残っていれば翌日にまた立つ（意図どおり。閉じてよいのは版を上げた PR がマージされたときだけ）。

## 母集合（規則 9・10）

- ワークフローの権限の表: `scripts/scripts.repo.test.js` の `JOB_SCOPES` と `docs/ai-workflow.md` の表。
- 検査器の母集合の本数（62 → 63）: `scripts/scripts.repo.test.js`。
- `ci-failure-issue.yml` の呼び出し側の本数: 「6 本」と書いた 3 か所（`ci-failure-issue.yml`・`docs/ai-workflow.md`・試験名）は、本 PR の前から既に 7 本で腐っていた。本数は導出値なので書かない形へ改めた。
- `scripts/README.md`・運用 Runbook §6・IADR-0471（2 回目の追記）。

## 受け入れ基準

- [x] 食い違いがあると起票され、一致していれば何もしないことを試験で固定する（自己試験 19 件。`scripts.repo.test.js` が PR で走らせる）。
- [x] ネットワークに届かないときは、起票せずに警告だけ出す（自己試験・実測）。
- [x] IADR-0471 に 2 回目の記録を日付つきで追記する。

## 変異の結果

| 変異 | 結果 |
| --- | --- |
| `resolve` の判定を常に ok にする | 陽性対照・age が無い・片方だけ食い違いの 3 件と、drift を前提にする upsert の 3 件、計 6 件が赤 |
| 届かない分を drift として数える | 届かない・壊れた圧縮・片方だけ一致・片方だけ食い違いの 4 件が赤 |
| `upsertDriftIssue` の status 判定を外す | 「ok / unverified / error では API を呼ばない」が赤 |
| 404 / 410 の判別を外す（すべて unverified） | 404 と片方だけ 410 の 2 件が赤 |
