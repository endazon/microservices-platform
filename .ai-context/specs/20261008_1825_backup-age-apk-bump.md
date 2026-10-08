---
title: 作業仕様書 platform-backup イメージの age を 1.3.2-r0 へ上げる（#1825）
type: spec
status: done
related_ids:
  - NFR-21
  - ADR-0008
  - IADR-0471
  - IADR-0489
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1825"
---

# 作業仕様書 platform-backup イメージの age を 1.3.2-r0 へ上げる（#1825）

## 背景

Images の `build-local (platform-backup)` が `wget: server returned error: HTTP/1.1 404 Not Found` で落ちる（run 37794634852 / job 113370853562。develop の直近の成功は run 37793325196）。Alpine v3.24 community の `age` が `1.3.2-r0` へ上がり、固定していた `age-1.3.1-r6.apk` がオリジンから消えた。IADR-0471 決定 3 の追記が予告していた「意図して止まる」挙動であり、上げ方は運用 Runbook §6 にある。

## 事実（2026-10-08 に実測）

- `v3.24/community/{x86_64,aarch64}/APKINDEX.tar.gz` の `P:age` は `V:1.3.2-r0`。
- `age-1.3.1-r6.apk`: aarch64 は 404（`x-cache: MISS`）。x86_64 は CDN エッジの古い写しが 200 を返した（`age: 2828580`・`x-cache: HIT`）—— エッジによって 404 になる（CI はそちらを引いた）。
- `age-1.3.2-r0.apk` を新しい空ディレクトリへ取得して `sha256sum`:
  - x86_64 `1e304c3be6bb465a8f26946d1b5295b1497ef4c13aa9d794659786e2e465fd72`
  - aarch64 `2206f3c1a23dc06cd1205a95544eedf11f2224781ef82fd629ee426babea401e`
  - 依頼者の実測値と一致。x86_64 の `.PKGINFO` は `pkgver = 1.3.2-r0`、署名は `.SIGN.RSA.alpine-devel@lists.alpinelinux.org-6165ee59.rsa.pub`。

## 変更

1. `deploy/local/platform-backup/image/Dockerfile`: `AGE_VERSION=1.3.2-r0`、`AGE_APK_SHA256_X86_64` / `AGE_APK_SHA256_AARCH64` を上の値へ。ベースの digest・`ALPINE_BRANCH`・取り方は替えない。
2. タグ `pg16.15-age1.3.2-r0` へ揃える: `scripts/k8s-local-images.sh`（`LOCAL_ONLY_IMAGES`）、`deploy/local/platform-backup/{postgres,vault}/cronjob.yaml`。
3. 試験の固定値: `scripts/k8s-local-up.test.js` の CronJob イメージのスタブ（「実物のマニフェストと同じ形」）、`scripts/platform-backup.test.js` の変異「age の版を上げてタグを据え置く」（`1.3.2-r1`。現行と違う版であれば意味は同じ）。
4. IADR-0471 へ `［2026-10-08 追記 / #1825］` を足し `updated:` を進める（本文は書き換えない）。

## 受け入れ基準と試験

| 受け入れ基準 | 確かめ方 |
| --- | --- |
| イメージがビルドでき、`age --version` が `v1.3.2`、`pg_dump` が 16.x | 手元の docker（下記）。CI の `build-local (platform-backup)` |
| 版・タグ・CronJob の 3 か所が一致する | `node scripts/platform-backup.test.js`（`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 経由） |
| 否定形: 版だけ上げてタグを据え置くと落ちる | 同試験の変異（値を更新して維持） |

手元のビルド: サンドボックスのプロキシが TLS を差し替えるため、コンテナ内の `wget` は `certificate verify failed` で落ちた（Dockerfile の問題ではない）。そこでリポジトリ外の作業ディレクトリに、`wget` の 1 行だけを「事前に取得した同じ apk を置く」に替えた写しを作ってビルドした。sha256 の照合と `apk add <ファイル>`（`--allow-untrusted` なし＝署名検証あり）、最後の `age --version | grep 1.3.2` / `pg_dump --version` の検査は実物と同じ行が通り、`--network none` で `age v1.3.2`・`age-keygen v1.3.2`・`pg_dump (PostgreSQL) 16.15` を確認した。aarch64 は手元でビルドしていない（sha256 の実測のみ）。

## 母集合（規則 9・10）

- 規則 9: `grep -rn "1.3.1-r6\|AGE_APK_SHA256\|AGE_VERSION\|1\.3\.1" --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj` を全体に掛けた。
  - 変更した: 上の「変更」1〜4。
  - 変えない: `.ai-context/specs/20260926_issue-1564_*`・`20260929_issue-1699_*`・`20261001_1709_*`（他 issue の凍結記録）、IADR-0471 本文・IADR-0489 本文（当時の記録。0471 は追記で補う。0489 の「タグは変わらない」は #1709 時点の言明であり誤りにならない）、`scripts/k8s-local-up.test.js` の分類器の試料（`age-1.3.1-r6.apk: FAILED` 等。実物のログの形を固定する試料であり版の固定ではない）、`src/pnpm-lock.yaml` の `which@1.3.1`（無関係）、`CHANGELOG.md`（生成物）。
  - `docs/`: `docs/operations/platform-infra-backup-runbook.md` §6 は手順だけで版を書いていない（`age<age の版>` の形）。変更不要。
- 規則 10: 本変更で誤りになる記述 —— Dockerfile 冒頭の注記（取り方・3 か所の一致）は版に触れず、誤りにならない。

## 脆さを和らげる安価な手の検討

- **無い（リポジトリの中では）**: Alpine の安定版ブランチは最新のリビジョンしか置かず、古い `-rN` の保管庫も無い。`-rN` は依存（Go 等）の再ビルドでも上がる。
- **退けた案**: APKINDEX の最新へ自動で寄せる／古い版が無ければ最新を入れるフォールバック —— 「黙って別の版を入れない」（IADR-0471 決定 3）に反する。`--allow-untrusted` —— 署名検証を失う。
- **構造的な案（#1825 に提案として残し、実装しない）**: ① age を上流リリース（`FiloSottile/age` の `age-v<版>-linux-{amd64,arm64}.tar.gz`）から sha256 固定で取る（消えないので 404 にならないが、apk の署名検証を失う）。② `build-local (platform-backup)` を定期実行して PR を巻き込む前に気付く。どちらも決定 3 を変えるため、採るなら別 IADR。

## 範囲外

- ベースイメージ（`postgres:16.15-alpine3.24`）の更新、取得方式の変更。
