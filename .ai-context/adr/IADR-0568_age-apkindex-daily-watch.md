---
title: IADR-0568 platform-backup の age の版と Alpine の APKINDEX を日次で突き合わせ、ずれたら専用 issue を起票する（届かなければ起票しない・版を自動で上げない）
type: impl-adr
status: Accepted
related_ids: [NFR-21, ADR-0107, IADR-0471, IADR-0508, IADR-0232]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0107（イメージの固定）
related_specs:
  - ../specs/20261020_2005_age-apkindex-watch.md
---

# IADR-0568: age の版のずれを日次で検知する（#2005）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-20
- 決定者: claude（#2005。同型の事故 2 回: #1825・#2003）

## 起点・関連

- 起点 issue: #2005（Refs #1825・#2003）。
- 関連する計画書 ID: NFR-21。計画 ADR: ADR-0107（イメージの固定）。
- 関連する実装判断: [IADR-0471](./IADR-0471_platform-infra-encrypted-daily-backup.md)（age を版・sha256・署名で固定したイメージ）・
  [IADR-0508](./IADR-0508_plan-range-lag-weekly-audit-with-nfr.md)（ずれの専用 issue の upsert）・
  [IADR-0232](./IADR-0232_ci-pr-latency-reduction.md)（ワークフローの権限の表）。
- 作業仕様書: [20261020_2005_age-apkindex-watch](../specs/20261020_2005_age-apkindex-watch.md)（形の選択・判定・母集合・変異）。

## コンテキストと課題

platform-backup のイメージは、age を `AGE_VERSION`（`-rN` まで）と両アーキテクチャの sha256 で固定し、Alpine のミラーから apk を直接取る。
Alpine の安定版ブランチは最新のリリースしか置かないので、上流が `-rN` を作り直すと固定した apk が消え、Images の `build-local (platform-backup)` が 404 で落ちる。
2026-10-08（#1825）と 2026-10-20（#2003）の 2 回起きた。2 回目は作り直しから 6 日間、誰も気付かなかった。

## 検討した選択肢

1. 週次の棚卸し（`backlog-audit.yml`）の前段に置く。6 日の見逃しと同じ幅が残る。採らない。
2. **独立の日次ワークフローで突き合わせ、ずれたら専用 issue を upsert する**。採る。
3. PR の CI で突き合わせる。外部の CDN の状態で無関係な PR を赤くする。採らない。
4. 版を自動で上げる PR を作る。sha256 の確認を人から外すことになる（IADR-0471 の固定の意味が消える）。採らない。

## 決定

### 決定 1 — 日次で突き合わせ、ずれたら専用 issue を 1 件だけ起票・上書きする

- `scripts/check-age-apkindex.js` が Dockerfile の `AGE_VERSION` / `ALPINE_BRANCH` / `ALPINE_MIRROR` を読み、`<ミラー>/<ブランチ>/community/<x86_64|aarch64>/APKINDEX.tar.gz` の `P:age` の `V:` と突き合わせる（Node だけで gzip と tar を読む。外部依存ゼロ）。
- `.github/workflows/age-apkindex-watch.yml` が毎日 22:23 UTC に `--upsert-issue` で走らせる。ずれたら専用 issue（ラベル `age-apkindex-drift`・マーカー `<!-- age-apkindex-drift -->`）を作り、以後は本文を上書きする。自動ではクローズしない。
- 版は自動で上げない。上げるのは運用 Runbook §6 の手順で、人が sha256 を確かめてから。

### 決定 2 — 届かなければ起票しない。「一致」と「動いていない」を区別する

- APKINDEX に届かない・読めない（ネットワーク断・5xx・プロキシの HTML）は `unverified`・exit 0 で、warning だけ出す。誤った起票をしない。
- 結果に `scanned`（突合できたアーキテクチャの数）を必ず添える。片方だけ読めて一致なら `unverified`、食い違えば `drift`（読めた分の食い違いは本物である）。
- exit 1 は Dockerfile の ARG を読めないとき（本リポジトリの欠陥）と、ずれを検知したのに issue へ書けなかったときだけ。そのときは `ci-failure-issue.yml` が起票する。

### 決定 3 — PR では自己試験だけを走らせる

- 自己試験（16 件。陽性対照＝ #2003 の形を含む）は `scripts.repo.test.js` が PR で走らせる。実ネットワークは叩かない。

## 結果

- 上流の作り直しから最長およそ 1 日で専用 issue が立つ。Images の赤を待たずに Runbook §6 へ進める。
- ワークフローが 1 本増える（`issues: write` のジョブ。権限の表に載せた）。

### 残るもの

- 検知を早めるだけで、作り直しそのものは防げない（Alpine の安定版ブランチの性質。IADR-0471 の 2026-10-08 追記の「脆さ」）。
- 検知から上げるまでの間に Images が回れば赤くなる。

## 関連

- #2005・#1825・#2003
