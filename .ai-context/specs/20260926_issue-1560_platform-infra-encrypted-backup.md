---
title: 作業仕様書 — #1560 platform-infra の Postgres と Vault を日次で暗号化してクラスタ外 2 か所へバックアップする
type: spec
status: done
related_ids: [NFR-21, NFR-05, NFR-18, ADR-0002, ADR-0008, IADR-0066, IADR-0082, IADR-0369, IADR-0457, IADR-0471]
author: Claude Opus 5.5 (worker)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
issue: "1560"
---

# 作業仕様書 — #1560 platform-infra の暗号化バックアップ（deploy/local 専用）

## 起点

- issue #1560（AST#346 の利用者判断 2、2026-09-26）。`platform-infra` の Postgres（AST の 7 DB を含む）と
  Vault（file ストレージ・PVC `vault-data`）に**バックアップが 1 本も無い**。`postgres-data` は PV の回収方針
  `Delete` で 2026-09-15 に作り直されている。AST 側の台帳（7 年保持）の保全が前提を欠いている。
- **`deploy/local` 専用**（ローカル開発・PoC 環境）。本番のバックアップ設計ではない。
- 設計判断は IADR-0471 に置く。

## 事実（着手時に読んだもの）

- `deploy/local/infra/postgres.yaml`: Deployment `postgres`（`postgres:16-alpine`）。超利用者 `postgres` の
  パスワードは Secret `postgres` のキー `password`（`scripts/k8s-local-up.sh` が `apply_secret` で作る。
  ESO=1 では `deploy/local/vault/eso/externalsecret-postgres.yaml` が同じ Secret へ Merge する）。
  Service `postgres:5432`（同じ名前空間）。
- `deploy/local/infra-persistence`: base の `postgres` の data を PVC `postgres-data` へ差し替える永続化 overlay
  （**既定**。`PERSIST=0` で外れる）。
- `deploy/local/vault-persistence`: Vault を file ストレージ（`/vault/data`・PVC `vault-data`）で起動する overlay
  （VAULT=1 の既定）。Pod 内ラッパーが unseal 鍵と初期 root トークンを **PVC 上の平文ファイル**
  （`/vault/data/.local-dev-init`、uid 100・0600）に置く。**つまり `/vault/data` の写しは、それだけで Vault の中身をすべて
  復号できる**。
- `check-deploy-manifests.js` は `deploy/**/kustomization.yaml` を走査で見つけて `kubectl kustomize` ＋ `kubeconform -strict`
  を掛ける（overlay 名の列挙を持たない）。新しい kustomization は自動で CI の対象になる。
- マニフェストと実装の突き合わせは `scripts/reset-floor.test.js` 型（Node 標準のみ・正規表現）で、シェルの分岐は
  `vault-entrypoint.test.sh` 型（PATH 上のスタブ）で固定している。

## 設計（要点。理由は IADR-0471）

1. `deploy/local/platform-backup/` に 3 つの kustomization を置く（ディレクトリ名を `backup/` にしない ——
   `.gitignore` の `Backup*/`（Visual Studio 由来）が Windows の大文字小文字を区別しない照合で当たり、追跡から落ちる。実測）。
   - `script/` … ConfigMap `platform-backup-script`（`backup.sh`）。
   - `postgres/` … `../script` ＋ CronJob `platform-backup-postgres`。`infra-persistence` が取り込む。
   - `vault/` … `../script` ＋ CronJob `platform-backup-vault`（PVC `vault-data` を**読み取り専用**でマウント）。
     `vault-persistence` が取り込む（PVC が在る配備でだけ描く。無い配備で描くと Pod が Pending のまま残る）。
2. スケジュールは JST 12:00（Postgres）と 12:15（Vault）。`timeZone: Asia/Tokyo`。`concurrencyPolicy: Forbid`。
   `startingDeadlineSeconds` は取りこぼしの後追いが 22:30 JST（米国市場の開場）に掛からない長さにする。
3. Postgres: 非テンプレートかつ接続可の DB を列挙し、DB ごとに `pg_dump -Fc`、加えて `pg_dumpall --globals-only`。
   パスワードは env `PGPASSWORD` を Secret `postgres`/`password` の secretKeyRef で渡す（引数に出さない）。
4. Vault: `/vault/data` を tar.gz にする。**稼働中の file バックエンドの写し**なので、写す前後でファイル一覧
   （名前・サイズ・更新時刻）を比べ、変わっていたらやり直す（3 回まで。変わり続けたら失敗）。
5. すべての成果物を `age` で**公開鍵へ**暗号化し、平文をディスクへ書かない（`pg_dump | age -o`）。
   受取人（公開鍵）は **kustomize の外**の ConfigMap `platform-backup-age-recipients`（キー `recipients.txt`）。
   ボリュームは `optional: true` で、**無い・占位のまま・不正な行がある**ときは何もせずに失敗する（fail-closed）。
   `k8s-local-up.sh` は `BACKUP_AGE_RECIPIENTS_FILE` が与えられたときだけこの ConfigMap を作り直す
   （既定の再実行で上書きしない）。秘密鍵はクラスタにもリポジトリにも置かない。
6. 保管先は hostPath 2 本（`/mnt/c/platform-infra-backups`・`/mnt/e/platform-infra-backups`。
   `DirectoryOrCreate`）。**目印ファイル `.platform-backup-target` が無い保管先には書かない** —— ドライブが
   外れていると kubelet がディストリ内に空のディレクトリを作るため、目印でクラスタ外であることを確かめる。
   片方に書けなくてももう片方には書き、Job は失敗で終わる。書き込みは `.incoming-<名>` へ写して検証
   （SHA256SUMS）してから改名する。
7. 保持: 日次は直近 30 日付（失敗した日を数えない＝世代数）。月次は各月で最初に取れた回を 7 年。
   名前が `-keep` で終わる回（切替前など、運用者が改名したもの）も 7 年。一部失敗した回は `-partial` と名付け、
   月次の起点には完全な回を優先する。**消すのは名前の規則に合うディレクトリだけ**で、中身は平らなファイルとして
   消してから `rmdir` する（再帰削除をしない。想定外の中身があれば消さずに失敗として報告する）。
8. イメージは Postgres 本体と同じ `postgres:16-alpine`（pg_dump のメジャー版を本体と揃える）。`age` は
   Alpine の署名付きパッケージを実行時に入れる（`BACKUP_AGE_INSTALL=1`）。入らなければ失敗で終わる。
9. リストア試験: `scripts/backup-restore-drill.sh`。秘密鍵はファイルのパスで受け取り `age -d -i` にだけ渡す
   （表示しない）。使い捨ての Postgres をネットワーク無し（`--network none`）で起こし、globals と各 DB を戻して
   全テーブルの行数を取り、稼働 DB（読み取り専用トランザクション）か与えた TSV と突き合わせる。
   `--self-test` はスタブだけで走り、稼働クラスタにも Docker にも触れない。

## 母集合（規則 9・10）

- 「バックアップが無い」と書く live な文書を走査した:
  `git grep -n "バックアップ" -- docs deploy scripts` → `docs/operations/operations.md` の「バックアップ・リストア」節
  （未記入・案のみ）が唯一の追随先。ここへ本手順書へのリンクと現況を足す。
- `deploy/local/README.md` の overlay 一覧に `backup/` を足す（overlay を列挙している live 文書はここだけ）。
- `k8s-local-up.sh` の冒頭コメントの「機密の上書きは環境変数で」の列挙に `BACKUP_AGE_RECIPIENTS_FILE`（秘密ではない）
  は加えない —— 公開鍵は機密ではないため。代わりに永続化の注記の近くに書く。
- 本変更で新たに誤りになる自分の記述: `infra-persistence` / `vault-persistence` の冒頭コメント（描くものの列挙）
  を同じ変更で更新する。

## 受け入れ基準（issue の写像）

| # | 基準 | 確かめる試験 |
| --- | --- | --- |
| A1 | 既定の配備で日次の CronJob が動き、2 か所へ暗号化済みのダンプが並ぶ | `scripts/platform-backup.test.js`（永続化 overlay の描画に CronJob が出る・hostPath 2 本）、`backup.test.sh`（2 か所に同じ回が並ぶ・中身は age を通っている） |
| A2 | 秘密の値がログ・引数・平文のファイルに出ない | `platform-backup.test.js`（secretKeyRef・PASSWORD/TOKEN に `value:` が無い）、`backup.test.sh`（スタブの argv と stdout にパスワードが出ない・平文ファイルが残らない）、drill の self-test（鍵の中身が出力に出ない） |
| A3 | 片方に書けなくてももう片方には書き、失敗は Job の失敗として見える | `backup.test.sh`（目印なし・書けない保管先で、他方に書き exit≠0） |
| A4 | リストア試験の手順で使い捨ての Postgres に戻し、台帳の行数を突き合わせられる | `backup-restore-drill.sh --self-test`（比較の判定・読み取り専用・使い捨てコンテナの片付け）、手順書 |
| A5 | 受取人が占位・不在なら何もしない（fail-closed） | `backup.test.sh` |
| A6 | 保持（30 日付・月初 7 年・`-keep` 7 年）と、規則外の名前に触れないこと | `backup.test.sh` の prune の単体試験（固定の名前の fixture） |

## 範囲外

- 稼働クラスタへの適用（コーディネータが行う）。鍵の生成（コーディネータが行う）。
- 本番のバックアップ設計（PITR・WAL アーカイブ・オフサイト）。
