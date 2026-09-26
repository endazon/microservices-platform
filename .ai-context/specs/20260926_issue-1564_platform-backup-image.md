---
title: 作業仕様書 — #1564 バックアップ CronJob の age を実行時の apk から、digest 固定のベースへ版とチェックサムを固定して同梱したローカルイメージへ移す
type: spec
status: done
related_ids: [NFR-21, NFR-05, NFR-18, ADR-0002, ADR-0008, IADR-0066, IADR-0068, IADR-0471]
author: Claude Opus 5.5 (worker)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - 20260926_issue-1560_platform-infra-encrypted-backup
issue: "1564"
---

# 作業仕様書 — #1564 platform-backup のイメージに age を同梱する

## 起点

- issue #1564（#1563 の監査の指摘 1〈中〉）と、その追記コメント（`backup.test.sh` の symlink 試験が
  prune_target の戻り値と出力を捨てている）。
- 現状: 2 つの CronJob（`platform-backup-postgres` / `platform-backup-vault`）は浮動タグ `postgres:16-alpine` で動き、
  実行のたびに `apk add age` をネットワーク越しに行う（`BACKUP_AGE_INSTALL=1`・`backup.sh` の `ensure_age`）。
  問題は 3 点ある。1 つ目、版が固定されていない。2 つ目、日次の回がインターネットへの到達に依存する。
  3 つ目、コンテナは root で hostPath への書き込み権と Vault の unseal 材料を持つため、パッケージかタグが侵害されると全部が見える。

## 編集前に確かめた事実（2026-09-26）

- ベース: Docker Hub の `library/postgres` を匿名トークンで照会した（`registry-1.docker.io/v2/library/postgres/manifests/<tag>`）。
  `16-alpine` と `16-alpine3.24` はどちらも image index `sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea`。
  amd64 の config は `PG_VERSION=16.15`、annotation `org.opencontainers.image.base.name=alpine:3.24`。arm64 も同じ版。
  稼働クラスタへは pull していない（レジストリの API だけを読んだ）。
- age: `dl-cdn.alpinelinux.org/alpine/v3.24/community/{x86_64,aarch64}/APKINDEX.tar.gz` の `P:age` は `V:1.3.1-r6`。
  パッケージファイルの sha256 は x86_64 `9e360d891d504924cc0996aa3fa7eabb7d93fc5d80c813139a4d2b3f7ad88b5b`（5,631,453 バイト）、
  aarch64 `38e2cd2bc60d5fbfcceab415a438b4ef3a24d083a029b7e469af04edb6e7e5a6`（5,110,721 バイト）。
- ビルド経路: `scripts/k8s-local-up.sh` の [2/7] が `scripts/k8s-local-images.sh` を呼ぶ。同スクリプトの `MAPPING` は
  compose の build 定義と `check-image-mapping.js`（IADR-0068）が 1 対 1 で突合する —— compose に無いものを `MAPPING` へ足すと
  `stale-mapping` で赤になる。**バックアップのイメージは compose に載らない（deploy/local 専用）ので、`MAPPING` とは別の配列に置く。**
  検査器は `MAPPING=(` から最初の行頭 `)` までしか読まないため、別の配列は突合の対象に入らない。
- CI のビルド可否: `images.yml` は compose の build 定義だけをビルドする。バックアップのイメージを CI でビルドする経路は無い。
- `platform-backup.test.js` の 7 は「CronJob のイメージ ＝ 本体 Postgres のイメージ」を固定している。本変更で意図して変わる。
- `backup.test.sh` 368 行（symlink の読み飛ばし）は `prune_target … >/dev/null 2>&1` で戻り値と出力を捨てている。
  prune_target の `[ -L "$p" ] && continue`（`backup.sh` 300 行）を外すと、リンクは回として扱われる。そのあと
  `remove_flat_dir` が拒んで「消せませんでした」を出し rc=1 になるが、試験はそれを見ないので緑のまま残る。

## 変更

1. `deploy/local/platform-backup/image/Dockerfile` を新設する。
   - ベースは `postgres:16.15-alpine3.24@sha256:7218…`（`16-alpine`・`16-alpine3.24`・`16.15-alpine` も同じ index を指していた）（index の digest。amd64・arm64 の両方で効く）。
   - age は `age-1.3.1-r6.apk` を Alpine のミラー（`v3.24/community/<arch>/`）から直接取り、アーキテクチャごとの sha256 を照合してから `apk add <ファイル>` で入れる。
     （［2026-09-26 追記 / #1564］当初は `apk fetch age=1.3.1-r6` としたが、CI で「unable to select package」となり、`apk update` を挟んでも同じだったため、直接取得へ改めた。ベースの `/etc/alpine-release` とブランチの一致も確かめる。）
     apk はパッケージの署名も検証する。つまり**版・チェックサム・署名の 3 つで固定する**。
   - `age --version` がパッケージの版を含むことを確かめる。知らないアーキテクチャではビルドを止める。
2. `scripts/k8s-local-images.sh` に `LOCAL_ONLY_IMAGES` 配列を足し、`k3d-local/platform-backup:pg16.15-age1.3.1-r6` を
   同じランタイム判定（nerdctl の k8s.io ／ docker＋k3d import）でビルドする。**タグは中身（PG と age の版）から作る** ——
   `:latest` のまま `IfNotPresent` で運ぶと、Dockerfile を上げても古いイメージが使われ続ける（MEMORY の陳腐化の事故と同型）。
3. 両 CronJob のイメージを上のタグへ替え、`imagePullPolicy: IfNotPresent` とし、`BACKUP_AGE_INSTALL` を外す。
4. `backup.sh` から `BACKUP_AGE_INSTALL` と `apk add` の分岐を撤去する（任意の退避路としても残さない —— 残すと、
   日次の回が再びネットワークとパッケージの侵害に晒される経路が 1 つの env で開く）。age が無ければ、イメージを確かめるよう告げて失敗する。
5. 試験。
   - `backup.test.sh` T-1560-46 を「age が無ければ失敗し、`BACKUP_AGE_INSTALL=1` を与えても apk を呼ばない」へ改める。
   - T-1560-45 は prune_target の戻り値が 0 で、「消せませんでした」が出ないことを確かめる形に直す（#1564 追記）。
   - `platform-backup.test.js` 7 を改め、9 を足す。7 では、Dockerfile の FROM が digest 固定で PG のメジャー版が本体と一致すること、
     CronJob のイメージが `k8s-local-images.sh` の `LOCAL_ONLY_IMAGES` と一致すること、`IfNotPresent` であることを見る。
     9 では、Dockerfile が age の版とアーキテクチャごとの sha256 を持つこと、タグが Dockerfile の版から導かれること、
     CronJob の env に `BACKUP_AGE_INSTALL` が無いこと、`backup.sh` に `apk add` が無いことを見る。変異も同じ試験で当てる。
6. CI: `images.yml` に `build-local (platform-backup)` を足す。Dockerfile をビルドし、`--network none` で
   `age --version` と `pg_dump --version` を実行する。集約ジョブ `image-build` の判定にも含める（必須チェック名は変えない）。
7. 文書。
   - Runbook `docs/operations/platform-infra-backup-runbook.md`: 前提（イメージは起動スクリプトがビルドする）、失敗の分岐
     （apk の行を撤去し、`ErrImageNeverPull` ／ `ImagePullBackOff` と「age がありません」の行を足す）、版の上げ方。
   - IADR-0471 決定 3 へ日付つき追記を置く（新しい番号は取らない）。

## 母集合（規則 9・10）

- 誤りの側の文字列で全追跡ファイルを走査した（`grep -rn "BACKUP_AGE_INSTALL\|apk add\|postgres:16-alpine\|k8s-local-images"`。
  `/obj/` `/bin/` `node_modules` `.git` `src/ai-stock-trading` を除く）。バックアップに関わる行:
  - `deploy/local/platform-backup/{postgres,vault}/cronjob.yaml`（image・env・コメント）→ 直す。
  - `deploy/local/platform-backup/script/backup.sh` 32・79〜94 行 → 直す。
  - `deploy/local/platform-backup/script/backup.test.sh` 103・346〜357 行 → 直す。
  - `scripts/platform-backup.test.js` 7 → 直す。
  - `docs/operations/platform-infra-backup-runbook.md` 214 行（apk の行）→ 直す。
  - `.ai-context/adr/IADR-0471_…` 80〜95 行 → 日付つき追記（本文は書き換えない）。
  - 除外: `IADR-0088` 75 行の `postgres:16-alpine`（compose の infra の記述。本件と無関係）。`IADR-0362` の `apk add gettext`
    （SPA 配信。無関係）。`k8s-local-images` を引く IADR-0067/0068/0070/0071/0072/0078/0081/0107/0288/0369 と確定済み仕様書
    （MAPPING の記録。`MAPPING` 自体は変えないので追随不要）。
- 規則 10（自分の変更で新たに誤りになる記述）: `cronjob.yaml` の securityContext のコメント「apk・drvfs 書き込み…」と
  IADR-0471 決定 3 の capabilities の段落の「apk の展開に要る CHOWN / FOWNER / FSETID」は、apk が実行時から消えるため前提が変わる。
  cronjob のコメントは直す。IADR は追記で扱う。

## 受け入れ基準

- [x] `node scripts/platform-backup.test.js` が通り、変異（digest を外す・タグをずらす・`BACKUP_AGE_INSTALL` を戻す・`apk add` を戻す）で落ちる。
- [x] `bash deploy/local/platform-backup/script/backup.test.sh` が通る。symlink の試験は Linux の CI で走る（Git Bash では skip）。
- [x] `node scripts/check-deploy-manifests.js`・`node scripts/check-image-mapping.js` が通る。
- [x] Dockerfile がビルドでき、`age --version` がパッケージの版を返す（手元の nerdctl は資格情報ヘルパのエラーでベースを取れなかったため、CI の `build-local (platform-backup)` で確かめた: sha256 OK・`v1.3.1`・`pg_dump (PostgreSQL) 16.15`）。
- [ ] 稼働クラスタへの反映（イメージのビルドと CronJob の再適用）はコーディネータが行う。
