---
title: 作業仕様書 — 秘匿管理（OpenBao・raft）の日次バックアップを、稼働中の bbolt の tar から整合したスナップショットへ改める
type: spec
status: done
related_ids: [NFR-21, NFR-18, NFR-05, ADR-0132, ADR-0008, IADR-0471, IADR-0525, IADR-0457, IADR-0096, IADR-0548]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements（NFR-21 障害検出〜復旧・Runbook / NFR-18 シークレット管理）
  - planning:projects/microservices-platform/07_adr/ADR-0132（秘匿管理の製品を OpenBao へ）
---

# 作業仕様書 — 秘匿管理の日次バックアップを raft の整合したスナップショットへ改める（#1868）

> 本仕様書は実装着手前に作成した（着手 2026-10-16）。基点は MSP `origin/develop` `5e49af8d`。起点は #1868（PR #1866 の独立監査 🟡3）。

## 起点（トレーサビリティ）

- 要求: **NFR-21**（障害検出〜復旧・Runbook）・**NFR-18**（シークレット管理）。計画 ADR: ADR-0132（OpenBao）・ADR-0008。
- 実装 ADR: IADR-0471（日次バックアップ。決定 4 が本件で改まる）・IADR-0525（OpenBao・raft。フォローアップ 4 が本件）・
  IADR-0457（永続化と Pod 内 unseal）・IADR-0096（k8s auth と TokenReview の reviewer）。新しい決定は **IADR-0548**。

## 問題（issue の要約）

- CronJob `platform-backup-vault` は稼働中の `/vault/data` を tar で写し、前後のファイル一覧（名前・サイズ・秒）で整合を確かめていた。
- raft の `vault.db` / `raft/raft.db` は bbolt で、ページをその場で書き換える。同じ秒・同じサイズの書き換えは見分けられず、
  **破れた写しを成功として残し得る**。

## 着手前の実測（OpenBao 2.7.1・digest 固定のイメージ・raft 単一ノード・リポジトリのラッパーと local.hcl）

1. `sys/storage/raft/snapshot` の **read だけ**のポリシーのトークンで `GET /v1/sys/storage/raft/snapshot` が 200（34 KB 前後）。
   同じトークンで KV の read は 403。
2. スナップショットは gzip の tar で、中身は `meta.json`・`state.bin`・`SHA256SUMS`・`SHA256SUMS.sealed`。
   `SHA256SUMS` は `meta.json` と `state.bin` の sha256（`sha256sum` で検算して一致）。
3. CLI の `snapshot save -` は不可（`open -: permission denied`）。CLI を使うにはファイルが要る。
4. バックアップのイメージ（`postgres:16.15-alpine3.24`）には curl も bao も無い。bash 5.3（`/dev/tcp` あり）・busybox の wget / timeout / tar / gzip はある。
   - busybox wget の `--header` はトークンを argv に載せる（`/proc/<pid>/cmdline`）。bao は静的バイナリで 181 MB。
5. 戻し:
   - 同じ秘匿管理へ: `snapshot restore`（root）で書いた値が戻る（実測 after-backup → before-backup）。audit device が健全なら unseal のまま。
     collector に届かない socket device が残っていると post-unseal で失敗して sealed になる（Pod の再起動でラッパーが unseal して戻る）。
   - 失った後（新しい PVC）: ラッパーが新しい鍵で init → `snapshot restore -force`（root）→ sealed → init のファイルをバックアップの写しに差し替え
     → Pod の再起動でラッパーが元の鍵で unseal。固定 root トークン・KV・k8s auth の role まで戻る（実測）。
   - 別の鍵の秘匿管理へ `-force` なしで戻すと 400（`could not verify hash file`）。
6. role の `token_num_uses=1` で、ログインのトークンはスナップショットの 1 要求で使い切られる（2 回目は 403。実測）。

## 対象範囲

- `deploy/local/platform-backup/script/backup.sh` の `stage_vault` を置き換える（tar と前後の比較をやめる）。
- `deploy/local/platform-backup/vault/`: CronJob（SA・projected token・メモリの作業場所・env）、ServiceAccount（新）、
  ポリシー（新 `policy-raft-snapshot.hcl`）、runtime bootstrap（新 `bootstrap.sh`）。
- `scripts/k8s-local-up.sh`: 永続化の overlay を当てた後に bootstrap を呼ぶ（VAULT ブロック・ESO ブロック）。
- 試験: `backup.test.sh`（偽の秘匿管理で HTTP を通す）・`scripts/platform-backup.test.js`（11 を足す）。
- 文書: バックアップの Runbook（表・§1・§3 の 5・§5・失敗の表・限界）、移行の手順書（手順 1・8 の注記）、`deploy/local/README.md`、
  IADR-0471（決定 4 の追記）・IADR-0525（フォローアップ 4 の追記）・IADR-0548（新）・`.ai-context/adr/README.md`、`ci.yml` の注記。
- **変えないもの**: 暗号化（age・受取人）・保持期間・保管先（C: / E: と目印）・時刻・イメージ。

## 方式（詳細は IADR-0548）

1. **整合**: raft のスナップショット API。OpenBao が一貫した時点の FSM を返す。中の `SHA256SUMS` で検算する。
2. **認証**: k8s auth。専用の SA `platform-backup-vault`（RBAC なし・自動マウントなし）の projected token（10 分）で
   role `platform-backup-vault` にログイン。role は `token_no_default_policy=true`・`token_num_uses=1`・5 分。ポリシーは
   `sys/storage/raft/snapshot` の read だけ。root トークン・Secret `vault-dev-token` は Job に渡さない。
3. **HTTP**: bash の `/dev/tcp`（HTTP/1.0）。イメージを変えない。トークン・JWT は組み込みの printf でだけ扱い、argv・ログに出さない。
4. **fail-closed**: ログインが 200 でない・スナップショットが 200 でない・空・gzip / tar が壊れている・必須の中身が無い・
   `SHA256SUMS` と不一致・`state.bin` が空 → 回を書かない（exit 1）。init のファイルが無い・鍵の行が無い → スナップショットだけの `-partial`（exit 1）。
5. **作業場所**: メモリの emptyDir（256 Mi。メモリの上限を 256 Mi → 512 Mi）。検査の後に age へ渡して消す。
6. **旧 tar 経路**: 撤去する（退避路として残さない）。旧 Vault（file ストレージ）にはスナップショットの API が無く、新しい CronJob は
   失敗して何も書かない。移行前の写しは、移行の手順書の手順 1 を移行前の CronJob で取る（手順書に注記）。

## 母集合（規則 9・10）

- `git grep -n "1868\|vault-data.tar\|stage_vault\|tree_state\|BACKUP_VAULT_TRIES\|変わり続ける\|写しのやり直し"`（specs・CHANGELOG を除く）:
  IADR-0525（2 か所）・`ci.yml`（注記）・Runbook（表・§3 の 5・失敗の表・限界）・CronJob（注記）・backup.sh / backup.test.sh。→ すべて追随。
- `git grep -n "platform-backup-vault"`: 移行の手順書の手順 1（CronJob から Job を作る）→ 旧 Vault の下では新しい CronJob が失敗するので注記。
  手順 8「片付けるまでは旧データもバックアップの写しに入る」→ スナップショットは raft だけを写すので誤りになる（規則 10）。追随。
- `deploy/local/README.md` のバックアップの段落 → Vault の回の中身と bootstrap を 1 行足す。
- `scripts/README.md`: 新しい scripts/ のファイルは無い（bootstrap は deploy/ 側）。追随なし。
- 除外: `.ai-context/specs/` の過去の仕様書（凍結記録）・`CHANGELOG.md`（生成物）。

## 受け入れ基準 → 試験

| 受け入れ基準（issue） | 試験 |
| --- | --- |
| 稼働中の写しの整合を仕組みで保証する（ファイル一覧の比較に頼らない） | `backup.test.sh` T-1868-01（スナップショット API を呼び、返った本文をそのまま暗号化・tar を作らない）・T-1868-02（空・切れ・SHA 不一致・中身の欠け・gzip でない・500 で回を書かない。検査ごとの文言）。実測: 実 OpenBao で取得 → 戻しの往復 |
| 戻し方の手順書（§5）とリストア試験を改める | Runbook §3 の 5（復号・中の SHA256SUMS の検算・使い捨ての OpenBao での戻し）・§5（同じ秘匿管理へ／失った後）。手元で往復を実測 |
| IADR（IADR-0471 の Vault の回の改め） | IADR-0548・IADR-0471 決定 4 の追記 |
| （依頼）最小のポリシー・root を使わない・平文を置かない | `platform-backup.test.js` 11（ポリシー・role・SA・projected token・root の Secret なし・メモリの作業場所・curl / wget なし。変異 12 種）・T-1868-01（ログ・argv に秘密なし） |
| （依頼）fail-closed | T-1868-02〜07（ログイン 403・JWT なし・JWT の形でない・届かない・http 以外）・T-1868-10（暗号化の失敗で平文を残さない） |

## 検証

- `bash deploy/local/platform-backup/script/backup.test.sh` / `node scripts/platform-backup.test.js`（kubectl kustomize）/
  `node scripts/k8s-local-up.test.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` / 文書系検査。
- 変異: 検査（gzip・空・状態コード・必須の中身・SHA の突合・平文の後始末）を 1 つずつ外して赤になること。
- 実測: 実 OpenBao（ラッパー）＋偽の TokenReview で、CronJob と同じ backup.sh をバックアップのイメージ相当（同じベース・同じ age）で走らせる。
- helm: 本件は chart を変えない（kustomize のみ）。
