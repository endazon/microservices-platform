---
title: 運用 Runbook — platform-infra の暗号化バックアップ（準備・日々の確認・リストア試験）
type: runbook
status: draft
author: claude
created: 2026-09-26
updated: 2026-09-26
---
<!-- trace:
ids: [NFR-21, NFR-05, NFR-18]
adrs: [ADR-0002, ADR-0008]
iadrs: [IADR-0066, IADR-0369, IADR-0457, IADR-0471]
specs: [20260926_issue-1560_platform-infra-encrypted-backup, 20260926_issue-1564_platform-backup-image]
issues: [#1560, #1564, AST#346]
-->

# 運用 Runbook: platform-infra の暗号化バックアップ

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。** 同書「バックアップ・リストア」節の
> ローカル環境の実装がこれである。
>
> 🔴 **ローカル開発・PoC 環境（`deploy/local`）専用である。本番のバックアップ設計ではない。**
> 保管先は本機の C: と E: であり、本機そのものを失えば両方を失う（オフサイトではない）。
>
> 🔴 **秘密鍵（age の identity）はクラスタにもリポジトリにも置かない。** 本書のどの手順も秘密鍵の中身を表示しない。

## 何がどこに置かれるか

| 対象 | CronJob（名前空間 `platform-infra`） | 時刻（JST） | 成果物（回のディレクトリの中） |
| --- | --- | --- | --- |
| Postgres の全 DB（非テンプレート・接続可）と globals（ロール） | `platform-backup-postgres` | 毎日 12:00 | `pg-<DB 名>.dump.age`（`pg_dump -Fc`）・`pg-globals.sql.age`・`SHA256SUMS` |
| Vault の file ストレージ（`/vault/data`） | `platform-backup-vault` | 毎日 12:15 | `vault-data.tar.gz.age`・`SHA256SUMS` |

- 保管先は 2 か所: `C:\platform-infra-backups\` と `E:\platform-infra-backups\`（WSL のディストリからは
  `/mnt/c/...`・`/mnt/e/...`）。その下に `postgres\<回>\` と `vault\<回>\` が並ぶ。回の名前は UTC の時刻
  `YYYY-MM-DDTHHMMSSZ`（例 `2026-09-27T030000Z` は JST 12:00 の回）。
- 時刻は米国市場の時間帯（JST 22:30〜05:00）を避けている。PC を止めていて取りこぼした回は、22:00 JST までに
  クラスタが戻れば後追いで走り、それを過ぎたら翌日の回を待つ。
- **すべての成果物は age の公開鍵で暗号化されている。** 平文はディスクへ書かれない（生成器から age へパイプで渡す）。
  🔴 Vault の写しには unseal 鍵と初期 root トークンの平文ファイルが含まれる（Pod 内で自動 unseal するため）。
  **復号した Vault の写しは、それだけで Vault の中身をすべて読める**。復号は必要なときだけ、使い捨ての場所で行う。
- 永続化しない使い捨てスタック（`PERSIST=0`）には CronJob を置かない。Vault の回は Vault を永続化して立てたとき
  （`VAULT=1` の既定）にだけ置かれる。

### 保持

| 種類 | 残す期間 | 決め方 |
| --- | --- | --- |
| 日次 | 30 世代 | 回のある日付の新しい方から 30 日分（PC を止めていた日は数えない） |
| 月次 | 7 年 | 各月で**最初に取れた回**（月初に PC を止めていても、その月の最初の回が月次になる） |
| 長期保持（切替前など） | 7 年 | 運用者が回の名前の末尾に `-keep` を付けたもの（下の「切替前の回を長期保持にする」） |
| 一部失敗 | 日次と同じ | 名前の末尾が `-partial`（その回の Job は失敗している）。完全な回がある月では月次にならない |
| 最新の完全な回 | 常に | 上の規則に関わらず、`-partial` でも `-keep` でもない最新の回は必ず残る（一部失敗が 30 日以上続いても、戻せる回を失わない） |

- 消すのは**上の名前の規則に合うディレクトリだけ**で、中身が通常のファイルだけのときに限る。規則に合わない名前・
  想定外の中身（サブディレクトリ・シンボリックリンク等）には触れない（Job の失敗として報告する）。**保管先のディレクトリへ別のファイルを
  置いても消されないが、置かないこと。**

## この手順を実行する条件（いつ走らせるか）

| 手順 | いつ |
| --- | --- |
| §1 準備 | 初回の配備のとき・受取人（公開鍵）を替えるとき・保管先のドライブを替えたとき |
| §2 日々の確認 | 週に 1 度（または Job の失敗に気付いたとき） |
| §3 リストア試験 | **四半期に 1 度と、切替（再実装版への移行）の前** |
| §4 切替前の回を長期保持にする | 切替の直前に取った回を 7 年残すとき |
| §6 イメージの版を上げる | CI のイメージのビルド（`build-local (platform-backup)`）が age の取得で落ちたとき・ベースを更新するとき |

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | クラスタへの `kubectl`（`platform-infra` の ConfigMap の作成・Job の閲覧、リストア試験の `--live` では `deploy/postgres` への exec） |
| 必要なツール | `age` と `age-keygen`（運用者の端末）、`kubectl`、コンテナの CLI（Rancher Desktop の `docker`）、Git Bash か WSL の bash、`sha256sum` |
| 所要時間の目安 | 準備 15 分・日々の確認 5 分・リストア試験 20〜40 分（DB の大きさによる） |

## 1. 準備（初回・鍵の交換・ドライブの交換）

1. **鍵の組を作る（クラスタの外で）。** 運用者の端末で:

   ```bash
   age-keygen -o <秘密鍵のファイル>
   ```

   表示される `Public key: age1...` の 1 行だけを控える。🔴 **秘密鍵のファイルはクラスタ・リポジトリ・保管先の
   ディレクトリに置かない**（リムーバブルメディアやパスワード管理ツールへ）。秘密鍵を失うと、それまでの
   すべての回が復号できなくなる。**2 つ目の鍵（別の場所に保管する予備）を受取人に並べることを勧める。**
2. **受取人ファイルを作る。** `deploy/local/platform-backup/age-recipients.example.txt` を任意の場所へ写し、占位の行
   （`REPLACE_WITH_AGE_PUBLIC_KEY`）を公開鍵の行に置き換える（1 行 1 鍵。`#` で始まる行と空行は読み飛ばす。**行の前後に空白を入れない** —— age は空白を削らずに読むため、
   その行は不正になる。行末の CRLF はよい）。
   占位のまま・不正な行が 1 つでもあると、CronJob は何も書かずに失敗する。
3. **ConfigMap にする。** どちらか:

   ```bash
   # 起動スクリプトと一緒に（与えたときだけ作り直す。与えない再実行では触らない）
   BACKUP_AGE_RECIPIENTS_FILE=<受取人ファイル> bash scripts/k8s-local-up.sh --live

   # 単独で
   kubectl -n platform-infra create configmap platform-backup-age-recipients \
     --from-file=recipients.txt=<受取人ファイル> --dry-run=client -o yaml | kubectl apply -f -
   ```

4. **保管先に目印を置く。** PowerShell で（C: と E: の両方）:

   ```powershell
   New-Item -ItemType Directory -Force C:\platform-infra-backups | Out-Null
   New-Item -ItemType File -Force C:\platform-infra-backups\.platform-backup-target | Out-Null
   New-Item -ItemType Directory -Force E:\platform-infra-backups | Out-Null
   New-Item -ItemType File -Force E:\platform-infra-backups\.platform-backup-target | Out-Null
   ```

   🔴 **目印の無い保管先には書かない。** ドライブが外れているとクラスタはディストリの中に空のディレクトリを
   作る（そこはクラスタの外ではない）ため、目印で見分けている。
5. **CronJob を当てる**（通常は起動スクリプトの永続化の既定で入っている）。単独で当てるなら、先にイメージを作ってから:

   ```bash
   # イメージ（pg_dump と age を同梱。レジストリには無く、クラスタのコンテナランタイムに直接置く）
   bash scripts/k8s-local-images.sh --live          # 全イメージ。起動スクリプトの [2/7] と同じもの
   kubectl apply -k deploy/local/platform-backup/postgres
   kubectl apply -k deploy/local/platform-backup/vault      # Vault を永続化しているときだけ
   ```

   CronJob のイメージは `k3d-local/platform-backup:pg<PG の版>-age<age の版>` で、`imagePullPolicy: IfNotPresent`
   （pull しない）。**age は実行時に取りに行かない** —— イメージに版とチェックサムを固定して入れてあり、日次の回は
   インターネットへの到達に依存しない。イメージだけを作り直すなら
   `nerdctl --namespace k8s.io build -t k3d-local/platform-backup:<タグ> deploy/local/platform-backup/image`
   （タグは `scripts/k8s-local-images.sh` の `LOCAL_ONLY_IMAGES` の値）。

6. **初回を今すぐ走らせて確かめる**（翌日の 12:00 を待たない）:

   ```bash
   kubectl -n platform-infra create job --from=cronjob/platform-backup-postgres platform-backup-postgres-manual-1
   kubectl -n platform-infra create job --from=cronjob/platform-backup-vault platform-backup-vault-manual-1
   kubectl -n platform-infra wait --for=condition=complete job/platform-backup-postgres-manual-1 --timeout=15m
   kubectl -n platform-infra logs job/platform-backup-postgres-manual-1
   ```

   ログの末尾が `完了（保管先 2 か所）` であり、C: と E: の `postgres\` に同じ名前の回が並べば成功。
   手動の Job は確かめたら `kubectl -n platform-infra delete job <名前>` で消してよい（保管先の回は消えない）。

## 2. 日々の確認

1. 直近の Job が成功しているか:

   ```bash
   kubectl -n platform-infra get jobs -l app=platform-backup --sort-by=.metadata.creationTimestamp
   ```

2. 失敗していればログを読む（`kubectl -n platform-infra logs job/<名前>`）。**ログに秘密の値は出ない**
   （パスワードは環境変数で渡し、引数にもログにも載せない）。
3. C: と E: の両方に、直近の回が並んでいるか（エクスプローラで `C:\platform-infra-backups\postgres\` の最新の名前を見る）。

## 3. リストア試験（四半期・切替前）

使い捨ての Postgres（コンテナ。**ネットワーク無し**・ポートを開けない）へ戻し、戻した DB の全テーブルの行数を
稼働 DB と突き合わせる。**稼働 DB へは読み取り専用のトランザクションで数えるだけで、書き込まない。**

1. 試す回を選ぶ（例: `C:\platform-infra-backups\postgres\2026-09-27T030000Z`。Git Bash では `/c/platform-infra-backups/...`）。
2. 台帳テーブルの一覧を用意する（任意だが推奨）。AST 側の移行仕様書の全数表で保持区分が台帳のテーブルを
   1 行 1 つ `<DB 名>|<スキーマ>.<テーブル>` の形で書く（例 `audit_svc|public.audit_log`）。一覧に載せたテーブルは
   「戻した側が稼働側より多い（＝稼働側の台帳が減った）」を失敗として扱う。
3. 走らせる:

   ```bash
   bash scripts/backup-restore-drill.sh \
     --run-dir /c/platform-infra-backups/postgres/2026-09-27T030000Z \
     --identity <秘密鍵のファイル> \
     --live \
     --ledger-tables <台帳テーブルの一覧>
   ```

   - 既定で AST の 7 DB を試す。他の DB は `--db <名前>` を並べる（並べたものだけを試す）。
   - `--live` は `kubectl -n platform-infra exec deploy/postgres` で稼働側の行数を取る。クラスタへ触れたくないときは
     稼働側の行数を TSV（`<DB>|<スキーマ>.<テーブル>|<行数>`）で用意して `--live-counts <ファイル>` を渡す。
   - **切替前**（書き込みを止めてから取った回）は `--exact` を付ける（1 行でも差があれば失敗）。
4. 結果を読む:

   | 行の頭 | 意味 |
   | --- | --- |
   | `OK` | 行数が一致 |
   | `INFO … 増えた` | バックアップの後に稼働側で増えた分（通常） |
   | `INFO … 状態テーブルの削除` | 台帳でないテーブルが稼働側で減った（通常） |
   | `FAIL … 戻した側に無い` | 戻した DB にテーブルが無い（ダンプの欠け・リストアの失敗） |
   | `FAIL … 追記専用の台帳が減っている` | 台帳の行が稼働側で消えている（**バックアップではなく稼働側の問題を疑う**） |

   終了コード 0 が合格。使い捨てのコンテナは成否によらず消える。
5. Vault の回も復号できることを確かめる（中身は展開しない。**一覧を見るだけ**）:

   ```bash
   age -d -i <秘密鍵のファイル> /c/platform-infra-backups/vault/<回>/vault-data.tar.gz.age | tar -tzf - | head
   ```

   Vault の実際の戻し（Vault を止めて `/vault/data` を置き換える）は、失ったときにだけ行う。手順は §5。

## 4. 切替前の回を長期保持にする

切替の直前に書き込みを止め、手動で 1 回取る（§1 の 6 と同じコマンド）。成功したら C: と E: の両方で、その回の
ディレクトリ名の末尾に `-keep` を付ける（例 `2026-10-03T010000Z` → `2026-10-03T010000Z-keep`）。
`-keep` の回は 7 年残る。§3 を `--exact` で走らせてから切替に進む。

## 5. 失ったときの戻し方（概略）

- **Postgres**: 稼働の `postgres` を止めずに DB を戻すなら、対象 DB を作り直してから
  `age -d -i <秘密鍵> pg-<DB>.dump.age | kubectl -n platform-infra exec -i deploy/postgres -- pg_restore -U postgres -d <DB>`。
  ロールが無いときは先に `pg-globals.sql.age` を `psql` へ流す。**まず §3 の方法で使い捨ての Postgres に戻して中身を
  確かめてから**稼働側へ戻す。
- **Vault**: Vault の Deployment を 0 にし、PVC `vault-data` の中身を復号した写しで置き換え、1 に戻す（Pod 内の
  ラッパーが写しの中の鍵で unseal する）。🔴 復号した写しは作業が終わったら消す。

## 6. イメージの版を上げる（age・ベース）

イメージ（`deploy/local/platform-backup/image/Dockerfile`）は、ベースを digest で固定し、age を**版・チェックサム・署名**の
3 つで固定している。Alpine の安定版ブランチは各パッケージの最新のリリースしか置かないため、上流が age の `-rN` を上げると
取得が失敗し、CI の `build-local (platform-backup)` が赤くなる。**赤くなるのが正しい**（黙って別の版を入れない）。
次の手順で上げる。稼働クラスタへは、リポジトリに入ってから §1 の 5 で当てる。

1. **ベースの digest を引く**（稼働クラスタへ pull しない。レジストリの API を読むだけ）。`postgres:<PG の版>-alpine<Alpine の版>`
   の image index の digest を、匿名トークンで `registry-1.docker.io/v2/library/postgres/manifests/<タグ>` へ HEAD を撃ち、
   応答ヘッダ `docker-content-digest` から取る。PG のメジャー版は本体（`deploy/local/infra/postgres.yaml`）と揃える。
2. **age の版と sha256 を引く。** そのベースの Alpine のブランチ（例 `v3.24`）の
   `dl-cdn.alpinelinux.org/alpine/<ブランチ>/community/<x86_64|aarch64>/APKINDEX.tar.gz` の `P:age` の `V:` が版。
   同じ場所の `age-<版>.apk` を取り、`sha256sum` で両アーキテクチャのチェックサムを取る。
3. **4 か所を同じ値へ上げる。** Dockerfile の `FROM`（タグと digest）と `ARG AGE_VERSION` / `AGE_APK_SHA256_*` / `ALPINE_BRANCH`（ベースの Alpine を上げたとき）、
   `scripts/k8s-local-images.sh` の `LOCAL_ONLY_IMAGES` のタグ（`platform-backup:pg<PG の版>-age<age の版>`）、
   2 つの CronJob の `image`。`node scripts/platform-backup.test.js` が食い違いを落とす。
4. PR の CI で `build-local (platform-backup)` が緑になることを確かめる（ビルドし、ネットワーク無しで
   `age --version` と `pg_dump --version` を実行する）。

## 確認（この手順が成功したと言える条件）

- §1: 手動の Job のログ末尾が `完了（保管先 2 か所）` で、C: と E: に同じ名前の回が並ぶ。
- §2: 直近の Job がすべて `Complete`。
- §3: `backup-restore-drill.sh` が終了コード 0 で、要約の「失敗」が 0。
- §6: `node scripts/platform-backup.test.js` が通り、CI の `build-local (platform-backup)` が緑。当てたあとの手動の Job が §1 と同じく完了する。

## 失敗したときの分岐

| 症状（ログ） | 原因の候補 | 次の手 |
| --- | --- | --- |
| `age の受取人ファイルがありません` | ConfigMap `platform-backup-age-recipients` が無い | §1 の 2〜3 |
| `… 行目が age の公開鍵（age1...）ではありません` / `公開鍵が 1 つもありません` | 占位のまま・写し間違い | 受取人ファイルを直して §1 の 3 |
| `受取人ファイル … 行目の前後に空白があります` | 公開鍵の行の前後や `#` 行の頭に空白がある | 空白を消して §1 の 3 |
| Pod が `ErrImageNeverPull` / `ErrImagePull` / `ImagePullBackOff`（イメージ `k3d-local/platform-backup:…`） | イメージを作っていない・タグを上げたのに作り直していない・起動スクリプトのビルドが失敗した（`WARN: k3d-local/platform-backup:… のビルドに失敗しました` が出る。起動は止めない） | WARN が出ていれば §6（age の版が Alpine で上がった可能性が高い）。出ていなければ §1 の 5 の手順でイメージを作り、手動の Job を走らせ直す（レジストリからは取れない） |
| `age がありません（イメージが k3d-local/platform-backup ではない可能性があります…）` | CronJob が age を持たない別のイメージを指している（古いマニフェストの当て直し等） | `kubectl -n platform-infra get cronjob platform-backup-postgres -o jsonpath='{..image}'` で確かめ、§1 の 5 で当て直す |
| `保管先に目印 .platform-backup-target がありません` | ドライブが外れている・目印を置いていない | ドライブを確かめて §1 の 4。**もう片方には書けている** |
| `DB の一覧を取れません` | Postgres が落ちている・Secret `postgres` のパスワードと DB が食い違う | `kubectl -n platform-infra get pods`、Secret の供給（起動スクリプト・ESO）を確かめる |
| `一部の成果物が欠けています`（回が `-partial`） | 特定の DB の `pg_dump` が失敗 | ログの `pg_dump が失敗しました: <DB>` を見る |
| `Vault のデータが変わり続けるため…` | 写している間に Vault へ書き込みが続いた | 書き込みの少ない時間に手動の Job を走らせ直す |
| `消せませんでした（想定外の中身があります）` | 回のディレクトリに手でファイルやフォルダを置いた | 置いたものを退けてから次の回を待つ |
| Job が作られない | PC 停止で後追いの締切（22:00 JST）を過ぎた | 翌日の回を待つか、手動の Job を走らせる |

## 記録

- リストア試験（§3）の実施日・試した回・結果（要約の行）・実施者を、AST 側の切替の issue へコメントで残す
  （四半期ごと・切替前）。
- 日々の確認は記録しない（Job の履歴が失敗 7 件・成功 3 件まで残る）。

## 限界（この手順で担保できないこと）

- **オフサイトではない。** C: と E: は同じ筐体にある。本機の喪失・盗難・ランサムウェアには耐えない。
- **Vault の写しは稼働中の写しである。** 写す前後でファイル一覧（名前・サイズ・更新時刻）を比べて変化があればやり直すが、
  秒未満の間に同じサイズで書き換わったファイルは見分けられない。ローカルの Vault は書き込みが稀なので実害は小さい。
- **行数の一致は中身の一致ではない。** リストア試験は件数だけを突き合わせる（台帳の欠損を見つけるための最低限）。
- 秘密鍵を失えば、すべての回が復号できない。予備の鍵を受取人に並べて別の場所に保管すること。
- 取りこぼしの後追いは 22:00 JST まで。PC を毎日 12:00〜22:00 に動かしていないと、その日の回は取れない。
