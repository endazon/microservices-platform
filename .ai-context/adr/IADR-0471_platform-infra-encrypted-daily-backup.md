---
title: IADR-0471 platform-infra の Postgres と Vault を日次で age 暗号化し、目印のあるクラスタ外 2 か所へ置く（deploy/local 専用）
type: impl-adr
status: Accepted
related_ids: [NFR-21, NFR-05, NFR-18, ADR-0002, ADR-0008, IADR-0066, IADR-0082, IADR-0210, IADR-0369, IADR-0457]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - ../specs/20260926_issue-1560_platform-infra-encrypted-backup.md
---

# IADR-0471: platform-infra の暗号化日次バックアップ（deploy/local 専用）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（#1560。要件は AST#346 の利用者判断 2、設計の骨子はコーディネータの指示）

## 起点・関連

- 関連する計画書 ID: NFR-21（障害検出〜復旧・Runbook 整備）／NFR-05（可用性）／NFR-18（シークレット管理。Vault の中身）
- 関連する計画 ADR: ADR-0002（DB per service。1 つの Postgres に各サービスの DB が並ぶ）、ADR-0008（実行基盤 k3s。ローカルは Rancher Desktop 内蔵 k3s）
- 関連する実装 ADR: IADR-0066（dev 用 in-cluster インフラ）、IADR-0082 / IADR-0210 / IADR-0369（永続化 overlay と既定オン）、IADR-0457（Vault の file ストレージ永続化と Pod 内 unseal）
- 関連する実装仕様書: `.ai-context/specs/20260926_issue-1560_platform-infra-encrypted-backup.md`

## コンテキストと課題

`platform-infra` の Postgres（AST の 7 DB を含み、うち台帳は 7 年保持）と Vault（file ストレージ・PVC `vault-data`）に
バックアップが 1 本も無い。`postgres-data` は PV の回収方針 `Delete` で、2026-09-15 に一度作り直されている。
利用者は AST#346 で次を決めた: 対象は全 DB と Vault、保管先はクラスタ外の 2 か所（本機の C: と E:）、
age の公開鍵で暗号化して秘密鍵はクラスタに置かない、日次 30 世代・月初 7 年、リストア試験は四半期と切替前。
**本番のバックアップ設計ではない**（deploy/local 専用）。

判断が要ったのは次の 7 点である。

1. 配備単位（どの overlay に載せるか）
2. 暗号化の方式と受取人（公開鍵）の置き場
3. イメージ（pg_dump 16 と age を同じコンテナで持つには）
4. Vault の file バックエンドを稼働中にどう写すか
5. 保管先がクラスタ外であることの確かめ方と、片方が失敗したときの振る舞い
6. 保持の規則と、削除の安全
7. リストア試験の形

## 決定

### 1. 配備単位: 永続化 overlay が取り込む 2 つの CronJob

- `deploy/local/platform-backup/script/`（本体スクリプトの ConfigMap）、`postgres/`（CronJob `platform-backup-postgres`）、
  `vault/`（CronJob `platform-backup-vault`）の 3 つの kustomization に分け、**`infra-persistence` が `postgres/` を、
  `vault-persistence` が `vault/` を取り込む**。どちらも `../script` を含むので、片方だけ当てても本体が揃う。
- 永続化が既定（IADR-0369 / IADR-0457）なので、**既定の配備でバックアップが動く**。永続化しない使い捨てスタック
  （`PERSIST=0`）には描かない —— 写す価値のあるデータが無い。
- Vault の回を `infra-persistence` に載せない理由: PVC `vault-data` は `vault-persistence` にしか無く、無い配備で描くと
  Pod が PVC を待って Pending のまま残り、**失敗としても見えない**。
- 1 つの CronJob にまとめない理由: 同じ理由（Vault の PVC が無い配備で Postgres の回まで止まる）。

### 2. 暗号化: age の公開鍵へ、パイプで（平文をディスクへ書かない）

- `pg_dump -Fc | age -R <受取人> -o`、`pg_dumpall --globals-only | age`、`tar -czf - /vault/data | age`。
  staging（emptyDir）には暗号文だけが置かれる。
- 受取人は **kustomize の外の ConfigMap `platform-backup-age-recipients`**（キー `recipients.txt`、1 行 1 鍵）。
  ボリュームは `optional: true` で、スクリプトが**無い・占位のまま・不正な行がある**ときは何もせずに失敗する。
  - kustomize に置かない理由: `k8s-local-up.sh` は毎回 overlay を当て直すので、占位を置くと**運用者が入れた公開鍵を
    再実行のたびに占位へ戻す**（その後は毎日失敗する）。起動器は `BACKUP_AGE_RECIPIENTS_FILE` が与えられたときだけ
    作り直す。
  - optional にする理由: 必須にすると ConfigMap が無いとき Pod が ContainerCreating で止まり、Job の失敗にならない
    （受け入れ基準「失敗は Job の失敗として見える」に反する）。
- 秘密鍵はクラスタにもリポジトリにも置かない。リポジトリには占位の例示ファイルだけを置き、試験が「秘密鍵の形の値が
  deploy/ に無い」ことを固定する。
- `/vault/data` には IADR-0457 の Pod 内 unseal 用に unseal 鍵と初期 root トークンの平文ファイルがあり、**写しだけで
  Vault の中身をすべて復号できる**。暗号化は任意ではなく、この決定の前提である。

### 3. イメージ: 本体と同じ `postgres:16-alpine` ＋ 実行時に Alpine の age

- pg_dump のメジャー版は本体と揃える必要があり、本体のイメージ（`deploy/local/infra/postgres.yaml`）をそのまま使う
  （試験が一致を固定する）。age は Alpine の**署名付きパッケージ**を実行時に `apk add` で入れる（`BACKUP_AGE_INSTALL=1`）。
  入らなければ何も書かずに失敗する。
- 採らなかった案: 独自イメージのビルド（起動器にビルドと取り込みの段が増え、`:latest` の陳腐化の管理が要る）、
  init コンテナで age を別イメージから写す（age を同梱する公式イメージが無い）、GitHub のリリースを実行時に取得
  （チェックサムの管理が増え、署名付きパッケージより弱い）。
- apk と保管先（WSL の drvfs）への書き込み、および `/vault/data` の 0600 のファイル（uid 100）を読むため、
  コンテナは root で動かす。`allowPrivilegeEscalation: false`・SA トークンの自動マウントなし・写し元は読み取り専用。
- 本リポジトリの deploy/local は他のイメージもタグ固定（digest なし）であり、ここも揃える。

### 4. Vault: 稼働中の写しを、前後のファイル一覧の比較で守る

- PVC を読み取り専用でマウントする（RWO の local-path は単一ノードなら同じノードの別 Pod から読める）。
- file バックエンドはキーごとのファイルで、書き込み中に写すと混ざった写しになり得る。静止させる（Vault を止める）には
  Job に Deployment を操作する権限と kubectl が要り、安くない。代わりに**写す前後でファイル一覧（名前・サイズ・更新時刻）の
  要約を比べ、変わっていたらやり直す（3 回まで。変わり続けたら失敗）**。ローカルの Vault は書き込みが稀で、実際には
  1 回で通る。残る限界（秒未満で同じサイズに書き換わるファイル）は手順書に書く。

### 5. 保管先: 目印ファイルで「クラスタ外」を確かめ、片方の失敗で止めない

- hostPath 2 本（`/mnt/c/platform-infra-backups`・`/mnt/e/platform-infra-backups`）、型は `DirectoryOrCreate`。
  `Directory` にしない理由: ドライブが外れていると Pod ごと起動せず、**もう片方にも書けない**。
- ただし `DirectoryOrCreate` はドライブが外れているとき**ディストリ内に空のディレクトリを作る**（そこはクラスタ外ではない）。
  そこで運用者が準備で置く**目印ファイル `.platform-backup-target` が無い保管先には書かない**。
- 片方に書けなくてももう片方には書き、最後に exit 1 で Job を失敗にする。書き込みは `.incoming-<回>` へ写して
  SHA256SUMS を検証してから改名する（途中で落ちても半端な回が「回」として残らない）。`backoffLimit: 0`（失敗を再試行で上書きしない）。

### 6. 保持: 世代数と月の最初の回、消すのは名前の規則に合う平らなディレクトリだけ

- 回の名前は UTC の時刻 `YYYY-MM-DDTHHMMSSZ`（＋ `-partial` / `-keep`）。
- 日次: **回のある日付**の新しい方から 30 日分の回を残す（世代数。PC を止めていた日を数えない）。
- 月次: **各月で最初に取れた回**を 7 年残す（月初に PC を止めていてもその月の最初の回が月次になる）。完全な回を優先し、
  無ければ一部失敗の回。
- `-keep`: 7 年残す。切替前の回は運用者がこの接尾辞へ改名する（AST#346 の「切替前は 7 年」）。
- 一部失敗した回は `-partial` と名付け（Job は失敗）、日次としては数えるが月次の起点には完全な回を優先する。
- 削除は**規則に合う名前のディレクトリだけ**を対象にし、中身が通常ファイルだけのときに限ってファイルを名前で消してから
  `rmdir` する。サブディレクトリ等の想定外の中身があれば**1 つも消さずに**失敗として報告する（再帰削除をしない）。
- 保持の判定は純関数（`prune_list`）にし、固定の名前の fixture で試験する。

### 7. スケジュール

- Postgres は JST 12:00、Vault は 12:15（`timeZone: Asia/Tokyo`）。米国市場の時間帯（JST 22:30〜05:00）を避ける。
- `startingDeadlineSeconds` は取りこぼし（PC 停止）の後追いを 22:00 JST までに限る。`concurrencyPolicy: Forbid`。

### 8. リストア試験: 使い捨ての Postgres（ネットワーク無し）と行数の突き合わせ

- `scripts/backup-restore-drill.sh`。SHA256SUMS で暗号文を検証 → `--network none` の使い捨てコンテナ（ポートを開けない・
  trust 認証で外から届かない）→ globals と各 DB を復号しながら流し込む → 全テーブルの正確な行数（`count(*)`）を取り、
  稼働 DB（`default_transaction_read_only=on` の psql）か与えた TSV と突き合わせる。
- 判定: 稼働側にあって戻した側に無いテーブルは失敗。戻した側が多いのは、**台帳（追記専用。一覧を与える）なら失敗**、
  状態テーブルなら報告のみ。戻した側が少ないのは報告のみ（バックアップ後の増分）。`--exact` は差があれば失敗
  （切替前など書き込みを止めて取った回）。
- 台帳テーブルの一覧はリポジトリに持たない —— 正本は AST 側の移行仕様書（保持区分）であり、写すと片方が古くなる。
- 秘密鍵はパスで受け取り `age -d -i` にだけ渡す（環境変数では受け取らない・表示しない）。`--self-test` はスタブだけで
  走り、稼働クラスタにも Docker にも触れない。

## 結果・影響

- 既定の配備（永続化 overlay）で 2 つの CronJob が動く。受取人の ConfigMap と保管先の目印を運用者が用意するまでは
  **毎日失敗する**（何も書かない）—— これは意図であり、黙って暗号化しない写しを積まない。
- 保管先は Windows の固定ディスク上の平らなファイルであり、C: と E: が同じ筐体にある限りオフサイトではない
  （本機の喪失には耐えない）。本番の設計では別の手段を取る。
- 試験: `scripts/platform-backup.test.js`（描画の形）、`deploy/local/platform-backup/script/backup.test.sh`（本体の分岐と保持）、
  `scripts/backup-restore-drill.sh --self-test`（リストア試験）、`scripts/k8s-local-up.test.js`（受取人の門）。
