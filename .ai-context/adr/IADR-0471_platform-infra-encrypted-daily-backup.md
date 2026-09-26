---
title: IADR-0471 platform-infra の Postgres と Vault を日次で age 暗号化し、目印のあるクラスタ外 2 か所へ置く（deploy/local 専用）
type: impl-adr
status: Accepted
related_ids: [NFR-21, NFR-05, NFR-18, ADR-0002, ADR-0008, IADR-0066, IADR-0068, IADR-0082, IADR-0210, IADR-0369, IADR-0457]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - ../specs/20260926_issue-1560_platform-infra-encrypted-backup.md
  - ../specs/20260926_issue-1564_platform-backup-image.md
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
  - 検査は **age の読み方と同じに読む**: 行末の CR だけを落とし（age は Go の `bufio.ScanLines` で読むため CRLF を許す）、
    空行と「`#` で始まる行」だけを飛ばす。前後に空白のある行・空白だけの行・字下げした `#` 行は age では不正な受取人に
    なるため、検査でも失敗にして受取人ファイルを名指しする（削って通すと、失敗が pg_dump のパイプ側で出て原因が読めない）。
- 秘密鍵はクラスタにもリポジトリにも置かない。リポジトリには占位の例示ファイルだけを置き、試験が「秘密鍵の形の値が
  deploy/ に無い」ことを固定する。
- `/vault/data` には IADR-0457 の Pod 内 unseal 用に unseal 鍵と初期 root トークンの平文ファイルがあり、**写しだけで
  Vault の中身をすべて復号できる**。暗号化は任意ではなく、この決定の前提である。

### 3. イメージ: 本体と同じ `postgres:16-alpine` ＋ 実行時に Alpine の age

- pg_dump のメジャー版は本体と揃える必要があり、本体のイメージ（`deploy/local/infra/postgres.yaml`）をそのまま使う
  （試験が一致を固定する）。age は Alpine の**署名付きパッケージ**を実行時に `apk add` で入れる（`BACKUP_AGE_INSTALL=1`）。
  入らなければ apk の標準エラー（理由）をログに出し、何も書かずに失敗する。
- 採らなかった案: 独自イメージのビルド（起動器にビルドと取り込みの段が増え、`:latest` の陳腐化の管理が要る）、
  init コンテナで age を別イメージから写す（age を同梱する公式イメージが無い）、GitHub のリリースを実行時に取得
  （チェックサムの管理が増え、署名付きパッケージより弱い）。
- apk と保管先（WSL の drvfs）への書き込み、および `/vault/data` の 0600 のファイル（uid 100）を読むため、
  コンテナは root で動かす。`allowPrivilegeEscalation: false`・`seccompProfile: RuntimeDefault`・SA トークンの自動マウントなし・
  写し元は読み取り専用。
- **capabilities の削減（`drop: [ALL]` ＋ 必要最小の `add`）はこの決定に含めない。** 必要な集合（apk の展開に要る
  CHOWN / FOWNER / FSETID か、drvfs への書き込み、uid 100 の 0600 ファイルを読む DAC_READ_SEARCH / DAC_OVERRIDE 等）は
  稼働クラスタで実測しないと確定できず、オフラインで推測して落とすと実行時に割れる。フォローアップとして稼働クラスタで
  最小集合を実測し、`platform-backup.test.js` へ固定する。
- 本リポジトリの deploy/local は他のイメージもタグ固定（digest なし）であり、ここも揃える。

> ［2026-09-26 追記 / #1564］**イメージの部分を改める。age は実行時に入れず、digest 固定のベースへ版・チェックサム・署名で
> 同梱したローカルイメージで動かす。**（#1563 の差分監査の指摘 1〈中〉。上の本文は起案時の判断として残す。）
>
> - **改める理由**: 実行のたびの `apk add age` は、版が固定されず、日次の回がインターネットへの到達に依存し（落ちれば
>   Job の失敗として見える）、root で hostPath への書き込み権と Vault の unseal 材料を持つコンテナへ、パッケージか浮動タグの
>   侵害がそのまま届く。上で退けた「独自イメージのビルド」の費用（起動器の段・`:latest` の陳腐化）は、次の 2 点で小さくなった。
>   ビルドは既存の `scripts/k8s-local-images.sh`（起動器の [2/7]）に載る。タグは中身の版から作るので、陳腐化は起きない。
> - **イメージ**: `deploy/local/platform-backup/image/Dockerfile`。ベースは `postgres:16.15-alpine3.24@sha256:721873c3…`
>   （image index の digest。2026-09-26 に `16-alpine` と同じ index であることをレジストリの API で確かめた）。age は Alpine v3.24
>   community の `age-1.3.1-r6.apk` をミラーから直接取り（`ARG ALPINE_BRANCH=v3.24`。ベースの `/etc/alpine-release` と
>   食い違えばビルドを止める）、アーキテクチャごとの sha256（x86_64 / aarch64）を照合してから `apk add <ファイル>` で入れる
>   （署名も検証される。`--allow-untrusted` は使わない）。`apk fetch age=<版>` はこのベースで「unable to select package」となり、
>   `apk update` を挟んでも同じだった（CI で 2 回実測）ため採らなかった。固定の強さ（sha256 と署名）は取り方に依らない。
>   ビルドの最後に `age --version` と `pg_dump --version`（16.x）を確かめる（CI の実測: `v1.3.1` / `16.15`）。Alpine が `-rN` を上げると取得が失敗してビルドが止まる —— **黙って別の版を入れない**
>   ための意図した挙動であり、上げ方は運用 Runbook §6 に置いた。
> - **配る経路**: `k8s-local-images.sh` の新しい配列 `LOCAL_ONLY_IMAGES` に置く（`MAPPING` ではない ——
>   `MAPPING` は IADR-0068 の検査器が compose の build 定義と 1 対 1 で突合し、compose に無い要素は `stale-mapping` になる）。
>   タグは `k3d-local/platform-backup:pg16.15-age1.3.1-r6`。2 つの CronJob はこのタグを `imagePullPolicy: IfNotPresent` で使う。
>   **Dockerfile・`LOCAL_ONLY_IMAGES`・CronJob の 3 か所の一致**を `platform-backup.test.js` の 9 が見る。
>   🔴 **起動器の中ではビルド失敗を致命にしない**（監査の指摘・中）。固定した `-rN` は Alpine が上げた日に 404 になり、
>   致命にすると新しい機械やキャッシュを消した環境で `k8s-local-up.sh` 全体が [2/7] で止まる（CronJob を置かない PERSIST=0 でも）。
>   失敗したら WARN（影響する CronJob が ImagePullBackOff で落ちること・Runbook §6）を出して続け、取り込み（k3d import）からも外す。
>   本体（`MAPPING`）のビルド失敗は従来どおり致命。**厳格な赤は CI の `build-local (platform-backup)` が担う**。
>   `k8s-local-up.test.js` が「backup だけ落ちる世界で 0 で終わり WARN を出す」と、陽性対照「本体が落ちれば止まる」を見る。
> - **`BACKUP_AGE_INSTALL` と `backup.sh` の `apk add` は撤去した。退避路としても残さない** —— env 1 つで同じ経路が開くからである。
>   age が無ければ、イメージの取り違えとして何も書かずに失敗する。
> - **CI**: `images.yml` に `build-local (platform-backup)` を足した。ビルドし、`--network none` で同梱のツールを実行する。
>   集約ジョブ `image-build` の判定に含めた（必須チェックの名前は変えていない）。
> - **pg_dump の版の一致**は、CronJob と本体のイメージの一致ではなく、Dockerfile の FROM のメジャー版と本体のメジャー版の一致で
>   見る（`platform-backup.test.js` の 7）。
> - 下の capabilities の段落が挙げる「apk の展開に要る CHOWN / FOWNER / FSETID」は、実行時の apk が無くなったので数えなくてよい。
>   削減の実測（フォローアップ）は drvfs への書き込みと uid 100 の 0600 ファイルの読み取りだけを見ればよい。
> - 変わらないもの: root で動かすこと、`allowPrivilegeEscalation: false`・`seccompProfile: RuntimeDefault`・SA トークンなし・
>   写し元の読み取り専用。

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
- **最新の完全な回は、上の規則に関わらず必ず残す。** 一部失敗の回が 30 日以上続くと日次の枠が一部失敗の回だけで埋まり、
  戻せる最後の完全な回を消してしまうため（監査の指摘）。
- 削除は**規則に合う名前のディレクトリだけ**を対象にし、中身が通常ファイルだけのときに限ってファイルを名前で消してから
  `rmdir` する。サブディレクトリ等の想定外の中身があれば**1 つも消さずに**失敗として報告する（再帰削除をしない）。
- **シンボリックリンクは辿らない。** `[ -d ]` はリンクを辿るため、回の名前をしたリンクを回として扱うと、リンク先の
  ファイルを消してしまう。走査ではリンクを飛ばし、削除ではディレクトリ自体・中身のどちらかがリンクなら拒む。
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
  シンボリックリンクの試験は Linux（CI）で走り、リンクを作れない環境（Windows の Git Bash）では飛ばす。

## フォローアップ（本決定の外に残したもの）

1. **age を実行時に版を固定せず入れている**（監査の指摘・中）。Alpine の署名付きパッケージなので改ざんには強いが、
   版は固定されず、取得できない日は失敗する。版の固定か、age を含むイメージへの置き換えを別 issue で扱う。
   ［2026-09-26 追記 / #1564］**済み**（決定 3 の追記。age を版・チェックサム・署名で同梱したローカルイメージへ置き換えた）。
2. **capabilities の最小化**（上の 3 を参照）。稼働クラスタで必要な集合を実測してから `drop: [ALL]` ＋ `add` にする。
