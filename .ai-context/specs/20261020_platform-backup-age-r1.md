---
title: "platform-backup のイメージの age を 1.3.2-r1 へ上げる（Alpine が 1.3.2-r0 を消し、Images の build-local (platform-backup) が全 PR で 404 で落ちた）"
type: spec
status: done
related_ids: [NFR, IADR-0471]
author: claude
created: 2026-10-20
updated: 2026-10-20
---

# 仕様書: platform-backup のイメージの age を 1.3.2-r1 へ上げる

## 事象

- MSP#2001 / MSP#2002 などの PR で、Images の `build-local (platform-backup)` の「Build image」が
  `wget: server returned error: HTTP/1.1 404 Not Found` で落ちた（run 39422609528 / job 118627159141）。runner は ubuntu-24.04（20261012.334.1）で、
  失敗は Alpine のコンテナの中の取得である。runner のイメージ移行（ubuntu-latest → 26）とは関係しない。
- 2026-10-20T12:35:52Z の実測（`dl-cdn.alpinelinux.org/alpine/v3.24/community/<arch>/`）:
  - `APKINDEX.tar.gz` の `P:age` は `V:1.3.2-r1`（`t:1791969618`＝2026-10-14T09:20:18Z の再ビルド）。x86_64・aarch64 とも同じ。
  - `age-1.3.2-r0.apk` は両アーキテクチャで 404、`age-1.3.2-r1.apk` は 200。
  - sha256: x86_64 `d0ae31a65ecc6d3acb704bf3c7eacce3955b48c806bd15a505163c8c2ec09f8c` / aarch64 `fb36e1050864e1936c6c87807d5bcca08f5388921b9c2ce154bbc780feae9936`
    （別ミラー `dl-4.alpinelinux.org` から取った同じファイルの sha256 も一致。`.PKGINFO` の `pkgver = 1.3.2-r1`）。
- 同じ消え方は 2 回目である（1 回目は 2026-10-08 / #1825。`1.3.1-r6` が消えた。IADR-0471 の 2026-10-08 追記）。

## 対応（運用 Runbook §6 の手順どおり）

- Dockerfile の `AGE_VERSION` を `1.3.2-r1` に、`AGE_APK_SHA256_X86_64` / `AGE_APK_SHA256_AARCH64` を上の実測値にする。
- タグ `k3d-local/platform-backup:pg16.15-age1.3.2-r1` を `LOCAL_ONLY_IMAGES` と 2 つの CronJob へ揃える。`k8s-local-up.test.js` のスタブの返す参照も同じ値にする。
- `platform-backup.test.js` の変異「age の版を上げてタグを据え置く」は、上げた先が現行の値（1.3.2-r1）と同じになり変異にならないので、`1.3.2-r99` にする。
- ベース（digest）・取り方（直接取得・sha256 照合・`apk add <ファイル>` の署名検証）は替えない。

## 版の固定の方針（再発の扱い）

赤くなるのは意図どおりである（黙って別の版を入れない）。版・sha256・署名の 3 つで固定し、上流が `-rN` を上げたら Runbook §6 の手順で上げる。
**同じ消え方は今回で 2 回目**（#1825・今回）なので、検査器の追加（例: 週次の棚卸しで APKINDEX の `V:` と `AGE_VERSION` を突き合わせ、ビルドが赤くなる前に起票する）を検討する段に入った。検討は別 issue で行う。

## 受け入れ基準

- AC1: `node scripts/platform-backup.test.js` が通る（版・タグ・CronJob の食い違いを落とす試験）。
- AC2: PR の CI で Images の `build-local (platform-backup)` が緑になる（ビルドし、`age --version` と `pg_dump --version` を実行する）。
