---
title: 作業仕様書 — クラウドの日次バックアップのイメージがビルドできない（rclone が ARG RCLONE_VERSION を自分の旗として読む）
type: spec
status: done
related_ids: [NFR-21, IADR-0675]
author: claude
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# 作業仕様書 — クラウドの日次バックアップのイメージの rclone の ARG の名前

> 本仕様書は、変更の前に作成した（着手 2026-11-07。develop `3d36d5ee9`）。小さな是正なので IADR は起こさない（方式の判断を伴わない）。

## 起点

- 非機能要件: NFR-21（運用）。関連 IADR: IADR-0675（クラウドのバックアップ・警報）。

## 現状（実測）

- `deploy/cloud/backup/image/Dockerfile` の `ARG RCLONE_VERSION=1.75.2-r0` は RUN の環境変数になる。rclone は `RCLONE_<旗>` の環境変数を
  自分の旗として読むので、ビルドの中の `rclone version` が `--version=1.75.2-r0` と読み、真偽に読めずに失敗する。
  - 再現: 既存のイメージで `docker run -e RCLONE_VERSION=1.75.2-r0 … rclone version` が `CRITICAL: Invalid value when setting --version from environment variable RCLONE_VERSION` で exit 1。
  - 旧の Dockerfile のビルドも同じ行で止まる（下の確かめ方）。
- `RCLONE_APK_SHA256_*` は rclone の旗に当たらず無視されるが、同じ接頭辞である。

## 決定

- rclone の ARG を `APK_RCLONE_VERSION`・`APK_RCLONE_SHA256_X86_64`・`APK_RCLONE_SHA256_AARCH64` へ改名する（`RCLONE_` で始めない）。値と固定（版・sha256・署名）は変えない。
  イメージのタグ（`pg16.15-age1.3.2-r1-rclone1.75.2-r0`）と CronJob は変わらない。
- 再発を止める: `scripts/cloud-ops.test.js` に、イメージの宣言に `RCLONE_` で始まる ARG・ENV が無いことの試験（陰性対照つき）を足す。
- ［2026-11-07 / オーケストレータの指示］見過ごした根本は CI がこのイメージをビルドしていなかったこと（`images.yml` はローカルのイメージだけ）。
  `images.yml` にジョブ `build-cloud-backup (platform-backup-cloud)`（push なし・`build-local` と同じ形）を足し、出来たイメージで
  `age --version`・`rclone version`・`pg_dump --version` をネット無しで確かめる。変更の判定に `^deploy/cloud/backup/image/` を足し、
  必須チェックの集約ジョブ `image-build`（名前は変えない）がその結果を拾う。形は `scripts/cloud-ops.test.js` が固定する。
  - 受け入れる性質: rclone の apk の `-rN` が上がると、Dockerfile を触る PR でこのジョブが赤になる（age と同じ。止まるのが正しい）。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | イメージがビルドでき、中の `rclone version` が通る | `docker build`（手元の環境は TLS の検証の都合で `--build-arg ALPINE_MIRROR=http://…` を与えた。apk は sha256 で照合する）が成功。旧の Dockerfile は同じ条件で `CRITICAL: … RCLONE_VERSION` で失敗 |
| AC2 | 宣言に `RCLONE_` で始まる ARG・ENV が無い | `scripts/cloud-ops.test.js`（旧の Dockerfile では赤） |
| AC3 | タグ・固定（ローカルのイメージと同じ base・age）は変わらない | `scripts/cloud-ops.test.js` T-2402-32・T-2402-33 |
| AC4 | CI がクラウドのイメージをビルドし、中の age・rclone・pg_dump をネット無しで確かめ、`image-build` が結果を拾う | `scripts/cloud-ops.test.js`。手元で同じ手順を流し、新は合格・旧の Dockerfile はビルドで `CRITICAL: … RCLONE_VERSION` で失敗 |

## 是正の母集合（規則 9・10）

`git grep -n "RCLONE_VERSION\|RCLONE_APK"`（CHANGELOG・凍結の仕様書を除く）: Dockerfile と `scripts/cloud-ops.test.js` だけ。両方を直した。
