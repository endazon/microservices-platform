---
title: 運用 Runbook — platform-infra の暗号化バックアップ（準備・日々の確認・リストア試験）
type: runbook
status: draft
author: claude
created: 2026-09-26
updated: 2026-10-20
---
<!-- trace:
ids: [NFR-21, NFR-05, NFR-18]
adrs: [ADR-0132, ADR-0002, ADR-0008]
iadrs: [IADR-0548, IADR-0525, IADR-0096, IADR-0066, IADR-0081, IADR-0369, IADR-0457, IADR-0471, IADR-0489, IADR-0568]
specs: [20261016_1868_openbao-raft-snapshot-backup, 20261009_1840_secret-store-openbao, 20260926_issue-1560_platform-infra-encrypted-backup, 20260926_issue-1564_platform-backup-image, 20260928_issue-1689_backup-image-build-warn-cause, 20260929_issue-1699_backup-cronjob-suspend-until-ready, 20261001_issue-1709_backup-suspended-status, 20261001_1709_backup-image-build-credential-helper, 20261020_2005_age-apkindex-watch]
issues: [#1840, #1868, #1709, #1560, #1564, #1689, #1699, #2005, AST#346]
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
| 秘匿管理（OpenBao）の raft の**整合したスナップショット**と init のファイル（unseal 鍵） | `platform-backup-vault` | 毎日 12:15 | `vault-raft.snap.age`・`vault-init.age`・`SHA256SUMS` |

- 保管先は 2 か所: `C:\platform-infra-backups\` と `E:\platform-infra-backups\`（WSL のディストリからは
  `/mnt/c/...`・`/mnt/e/...`）。その下に `postgres\<回>\` と `vault\<回>\` が並ぶ。回の名前は UTC の時刻
  `YYYY-MM-DDTHHMMSSZ`（例 `2026-09-27T030000Z` は JST 12:00 の回）。
- 時刻は米国市場の時間帯（JST 22:30〜05:00）を避けている。PC を止めていて取りこぼした回は、22:00 JST までに
  クラスタが戻れば後追いで走り、それを過ぎたら翌日の回を待つ。
- **すべての成果物は age の公開鍵で暗号化されている。** 平文はディスクへ書かれない（生成器から age へパイプで渡す）。
  🔴 Vault の回の `vault-init.age` は unseal 鍵と初期 root トークンの平文ファイルである（Pod 内で自動 unseal するため）。
  **復号したスナップショットと init のファイルを合わせると、Vault の中身をすべて読める**。復号は必要なときだけ、使い捨ての場所で行う。
- Vault の回は、秘匿管理が自分で一貫した時点を書き出す **raft のスナップショット**（`GET /v1/sys/storage/raft/snapshot`）を取り、
  中の `SHA256SUMS` で `meta.json`・`state.bin` を検算してから暗号化する。空・途中で切れた・検算が合わないスナップショットは置かない
  （その日の回を書かずに Job が失敗する）。
- Vault の回の Job は**専用の ServiceAccount `platform-backup-vault` で秘匿管理へログイン**する（k8s auth）。持つ権限はスナップショットを
  取ることだけで、トークンは 1 回きり・5 分。root トークンは Job へ渡さない。認証の設定（role とポリシー）は起動スクリプトが毎回入れる（§1 の 5）。
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
| 必要な権限 | クラスタへの `kubectl`（`platform-infra` の ConfigMap の作成・Job の閲覧、リストア試験の `--live` では `deploy/postgres` への exec、Vault の認証の投入と戻しでは `deploy/vault` への exec と ClusterRoleBinding の適用） |
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
   bash deploy/local/platform-backup/vault/bootstrap.sh      # 同じ。Vault の回の認証（k8s auth の role とポリシー）
   ```

   Vault の回の認証は、起動スクリプトが永続化した秘匿管理を当てた後に毎回入れる（`==> platform-backup(vault): …` の 4 行が出る）。
   単独で当てたときは上の `bootstrap.sh` を走らせる（冪等。秘匿管理が unseal 済みであること）。入るもの: Vault の SA への TokenReview の権限、
   kubernetes 認証、ポリシー `platform-backup-raft-snapshot`（`sys/storage/raft/snapshot` の read だけ）、role `platform-backup-vault`
   （SA `platform-infra/platform-backup-vault` にだけ束縛）。確かめるには:

   ```bash
   kubectl -n platform-infra exec deploy/vault -- sh -c 'VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID" vault policy read platform-backup-raft-snapshot'
   kubectl -n platform-infra exec deploy/vault -- sh -c 'VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID" vault read auth/kubernetes/role/platform-backup-vault' \
     | grep -E 'bound_service_account_(names|namespaces)|token_(policies|no_default_policy|num_uses|ttl)'
   ```

   CronJob のイメージは `k3d-local/platform-backup:pg<PG の版>-age<age の版>` で、`imagePullPolicy: IfNotPresent`
   （pull しない）。**age は実行時に取りに行かない** —— イメージに版とチェックサムを固定して入れてあり、日次の回は
   インターネットへの到達に依存しない。イメージだけを作り直すなら（タグは `scripts/k8s-local-images.sh` の `LOCAL_ONLY_IMAGES` の値）:

   ```bash
   # Rancher Desktop（containerd）: k3s が参照する名前空間 k8s.io へ直接ビルドする（取り込みは要らない）
   nerdctl --namespace k8s.io build -t k3d-local/platform-backup:<タグ> deploy/local/platform-backup/image
   # k3d: ビルドしてからクラスタへ取り込む
   docker build -t k3d-local/platform-backup:<タグ> deploy/local/platform-backup/image
   k3d image import k3d-local/platform-backup:<タグ> -c msp-ast-dev
   ```

   ベースは Docker Hub の公式イメージを**プルスルーミラー `mirror.gcr.io/library` から** digest で固定して取る。
   `docker.io` は匿名の取得にも 401 の認証チャレンジを返し、その応答でビルダーが資格情報ヘルパー
   （`~/.docker/config.json` の `credsStore` / `credHelpers`）を呼ぶ。ヘルパーが壊れている機械ではそこで落ちるため、
   チャレンジを返さないミラーにしてある（digest が同じなので中身は同じ）。取得元は `--build-arg BASE_REGISTRY=<取得元>` で替えられるが、
   `docker.io/library` へ戻すとヘルパーが動く機械でしか通らない。

6. **初回を今すぐ走らせて確かめる**（翌日の 12:00 を待たない）:

   ```bash
   kubectl -n platform-infra create job --from=cronjob/platform-backup-postgres platform-backup-postgres-manual-1
   kubectl -n platform-infra create job --from=cronjob/platform-backup-vault platform-backup-vault-manual-1
   kubectl -n platform-infra wait --for=condition=complete job/platform-backup-postgres-manual-1 --timeout=15m
   kubectl -n platform-infra logs job/platform-backup-postgres-manual-1
   ```

   ログの末尾が `完了（保管先 2 か所）` であり、C: と E: の `postgres\` に同じ名前の回が並べば成功。
   Vault の回は `kubectl -n platform-infra wait --for=condition=complete job/platform-backup-vault-manual-1 --timeout=15m` の後に
   ログを読み、`raft snapshot: ok（<バイト数> バイト・整合を検査済み）`・`init（unseal 鍵）: ok`・`完了（保管先 2 か所）` が出て、
   `vault\<回>\` に `vault-raft.snap.age` と `vault-init.age` が並べば成功。
   手動の Job は確かめたら `kubectl -n platform-infra delete job <名前>` で消してよい（保管先の回は消えない）。

## 2. 日々の確認

> 🔴 **現状（2026-10-02）: 稼働 PoC では 2 本とも停止（`SUSPEND` が `True`）で、バックアップは 1 本も取れていない。**
> age の受取人（利用者が作る）とバックアップのイメージが揃っていない。イメージのビルドは資格情報ヘルパーの失敗で
> 通っていなかったが、ベースの取得元をチャレンジを返さないミラーへ替え、ヘルパーを呼ばずに作れるようにした（§1 の 5）。
> 再開は次の順で行い、再開を確かめたらこの注記を消す。
>
> 1. **受取人を作る（利用者）。** 運用者の端末で鍵の組を作り、公開鍵だけを受取人ファイルへ書く（§1 の 1〜2。秘密鍵はクラスタ・リポジトリに置かない）:
>
>    ```bash
>    age-keygen -o <秘密鍵のファイル>        # 表示される Public key: age1... の 1 行だけを控える
>    cp deploy/local/platform-backup/age-recipients.example.txt <受取人ファイル>
>    # <受取人ファイル> の REPLACE_WITH_AGE_PUBLIC_KEY の行を、控えた age1... の行に置き換える（前後に空白を入れない）
>    ```
>
>    保管先の目印も置く（§1 の 4）。
> 2. **イメージを作り直す。** この手順を含むリビジョンへ作業ツリーを進めてから、§1 の 5 のコマンドで作る
>    （3 の起動スクリプトの `[2/7]` でも作られる）。在ることを確かめる:
>
>    ```bash
>    nerdctl --namespace k8s.io image inspect k3d-local/platform-backup:<タグ> >/dev/null && echo ok   # Rancher Desktop
>    ```
>
>    ビルドが資格情報で落ちたら、ログの `load metadata` の行が `mirror.gcr.io/library/postgres` を指しているかを先に見る（「失敗したときの分岐」）。
> 3. **起動スクリプトを再実行して有効化する。** 受取人の ConfigMap を作り直し、前提が揃っていれば CronJob の停止が外れる:
>
>    ```bash
>    BACKUP_AGE_RECIPIENTS_FILE=<受取人ファイル> bash scripts/k8s-local-up.sh --live
>    kubectl -n platform-infra get cronjob -l app=platform-backup      # SUSPEND が False
>    ```
>
>    最後に `欠けている前提: …` の WARN が出たら、その前提を揃えて再実行する。
> 4. **手動 Job を 1 回走らせ、リストア試験を行う。** §1 の 6 のコマンドで Job を走らせ、ログの末尾が `完了（保管先 2 か所）` であることを確かめてから、
>    できた回で「3. リストア試験」を行う。

0. CronJob が有効か（`SUSPEND` が `False`）:

   ```bash
   kubectl -n platform-infra get cronjob -l app=platform-backup
   ```

   起動スクリプトは、**前提（バックアップのイメージと age の受取人）が揃うまで CronJob を停止（`suspend: true`）で置く。**
   揃わないまま有効にしておくと、毎日の回が失敗した Pod を残すためである。停止で置いたときは、起動スクリプトの最後に
   `WARN: バックアップの CronJob … を停止（suspend）で置きました。欠けている前提: …` が出る。
   前提を揃えて起動スクリプトを再実行すると有効に戻る（手で `suspend` を外さない。次の再実行で、揃っていなければ停止へ戻る）。
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
5. **Vault の回を、使い捨ての OpenBao（コンテナ。ネットワーク無し）へ戻して開けることを確かめる。** 稼働中の秘匿管理には触れない。
   リポジトリの作業ツリーの根で、`RUN` に試す回（例 `/c/platform-infra-backups/vault/2026-10-16T031500Z`）、`KEY` に秘密鍵のファイルを入れて:

   ```bash
   BAO_IMAGE="$(sed -n 's/^ *image: \(openbao\/openbao:.*\)$/\1/p' deploy/local/vault/vault-dev.yaml)"
   W="$(mktemp -d)"
   (cd "$RUN" && sha256sum -c SHA256SUMS)                                   # 保管先の写しの検証
   age -d -i "$KEY" -o "$W/raft.snap" "$RUN/vault-raft.snap.age"
   age -d -i "$KEY" -o "$W/init" "$RUN/vault-init.age"
   mkdir "$W/x" && tar -xzf "$W/raft.snap" -C "$W/x" && (cd "$W/x" && sha256sum -c SHA256SUMS)   # meta.json: OK / state.bin: OK
   chmod 755 "$W" && chmod 644 "$W/raft.snap" "$W/init"                    # コンテナの利用者（openbao）が読めるように
   docker run -d --name bao-drill --network none -v "$W:/drill:ro" --entrypoint sh "$BAO_IMAGE" -c '
     mkdir -p /tmp/raft && printf "%s\n" "storage \"raft\" {" "  path = \"/tmp/raft\"" "  node_id = \"drill\"" "}" \
       "listener \"tcp\" {" "  address = \"127.0.0.1:8200\"" "  tls_disable = true" "}" \
       "api_addr = \"http://127.0.0.1:8200\"" "cluster_addr = \"http://127.0.0.1:8201\"" > /tmp/drill.hcl &&
     exec bao server -config=/tmp/drill.hcl'
   sleep 5
   docker exec bao-drill sh -c '
     export BAO_ADDR=http://127.0.0.1:8200
     bao operator init -key-shares=1 -key-threshold=1 > /tmp/new || exit 1
     printf "%s" "$(sed -n "s/^Unseal Key 1: //p" /tmp/new)" | bao write sys/unseal key=- >/dev/null || exit 1
     sleep 3
     BAO_TOKEN="$(sed -n "s/^Initial Root Token: //p" /tmp/new)" bao operator raft snapshot restore -force /drill/raft.snap || exit 1
     sleep 3
     printf "%s" "$(sed -n "s/^Unseal Key 1: //p" /drill/init)" | bao write sys/unseal key=- >/dev/null || exit 1
     sleep 3
     BAO_TOKEN="$(sed -n "s/^Initial Root Token: //p" /drill/init)" bao kv list secret/msp'
   echo "drill: $?"                                                          # 0 で、KV の名前の一覧が出れば合格
   docker rm -f bao-drill
   rm -rf "$W"                                                               # 🔴 復号したものを残さない
   ```

   - 使い捨ての OpenBao は新しい鍵で init してから、写しを `-force` で戻す（鍵が違うので sealed になる）。そこへ写しの init のファイルの
     unseal 鍵を与えて開く —— §5 の「失ったときの戻し」と同じ道筋を、稼働側に触れずに通している。
   - 値は表示しない（`kv list` は名前だけ）。数を稼働側と比べるなら `| wc -l` を付ける。
   - **差し替え（OpenBao への移行）の前に取った回**は `vault-data.tar.gz.age`（旧 Vault の file ストレージの tar）で、この手順の対象外である。
     復号できることだけを確かめる: `age -d -i "$KEY" "$RUN/vault-data.tar.gz.age" | tar -tzf - | head`。

   Vault の実際の戻しは、失ったときにだけ行う。手順は §5。

## 4. 切替前の回を長期保持にする

切替の直前に書き込みを止め、手動で 1 回取る（§1 の 6 と同じコマンド）。成功したら C: と E: の両方で、その回の
ディレクトリ名の末尾に `-keep` を付ける（例 `2026-10-03T010000Z` → `2026-10-03T010000Z-keep`）。
`-keep` の回は 7 年残る。§3 を `--exact` で走らせてから切替に進む。

## 5. 失ったときの戻し方（概略）

- **Postgres**: 稼働の `postgres` を止めずに DB を戻すなら、対象 DB を作り直してから
  `age -d -i <秘密鍵> pg-<DB>.dump.age | kubectl -n platform-infra exec -i deploy/postgres -- pg_restore -U postgres -d <DB>`。
  ロールが無いときは先に `pg-globals.sql.age` を `psql` へ流す。**まず §3 の方法で使い捨ての Postgres に戻して中身を
  確かめてから**稼働側へ戻す。
- **Vault（OpenBao。`vault-raft.snap.age` の回）**: **まず §3 の 5 で使い捨ての OpenBao へ戻して開けることを確かめてから**稼働側へ戻す。
  戻すと、スナップショットを取った後に入れた値（秘密情報の投入画面での書き込み等）は失われる。root で行う（運用者の操作。Job の role では戻せない）。
  - **秘匿管理は動いていて、中身だけを戻すとき**（誤って消した・壊した）:

    ```bash
    age -d -i <秘密鍵のファイル> <回>/vault-raft.snap.age | kubectl -n platform-infra exec -i deploy/vault -- sh -c \
      'umask 077; f="$(mktemp)"; cat > "$f" && VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID" vault operator raft snapshot restore "$f"; rc=$?; rm -f "$f"; exit $rc'
    kubectl -n platform-infra exec deploy/vault -- vault status | grep Sealed     # false
    ```

    `Sealed` が `true` のまま（collector に届かない audit device があると、戻した後の立ち上げで封印される）なら
    `kubectl -n platform-infra rollout restart deploy/vault` で Pod を作り直す（ラッパーが unseal する）。
  - **PVC ごと失ったとき**（新しい空の PVC で立て直した後）: ラッパーは新しい鍵で init して立ち上がる。そこへ写しを `-force` で戻し、
    init のファイルを写しの方へ差し替えて Pod を作り直す:

    ```bash
    kubectl -n platform-infra rollout status deploy/vault --timeout=180s            # 新しい鍵で init・unseal 済み
    age -d -i <秘密鍵のファイル> <回>/vault-raft.snap.age | kubectl -n platform-infra exec -i deploy/vault -- sh -c \
      'umask 077; f="$(mktemp)"; cat > "$f" && VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID" vault operator raft snapshot restore -force "$f"; rc=$?; rm -f "$f"; exit $rc'
    # 鍵が違うので、ここで sealed になる（正しい）。init のファイルを写しの方へ差し替える:
    age -d -i <秘密鍵のファイル> <回>/vault-init.age | kubectl -n platform-infra exec -i deploy/vault -- sh -c \
      'umask 077; cat > /vault/data/.local-dev-init.restore && mv /vault/data/.local-dev-init.restore /vault/data/.local-dev-init'
    kubectl -n platform-infra rollout restart deploy/vault
    kubectl -n platform-infra rollout status deploy/vault --timeout=180s
    kubectl -n platform-infra logs deploy/vault | grep vault-entrypoint          # initialized (reusing …)・unsealed・fixed root token present・ready
    ```

    `-force` を付けずに別の鍵の秘匿管理へ戻すと `could not verify hash file` で拒まれる（正しい）。戻した後は k8s auth の role
    （ESO・バックアップ等）もスナップショットの時点のものになる。起動スクリプトを再実行すると bootstrap が最新の設定を入れ直す。
- **Vault（差し替えの前に取った `vault-data.tar.gz.age` の回）**: Vault の Deployment を 0 にし、PVC `vault-data` の中身を復号した写しで
  置き換え、1 に戻す。🔴 **これは旧 Vault の file ストレージの写しである**（`raft/` を含まない）。OpenBao はそれを開けない
  （Pod 内のラッパーは起動を拒んで止まる。データは守られる）。戻した後に [移行の手順書](secret-store-openbao-migration-runbook.md)の
  手順 3〜5 で raft へ移す。🔴 復号した写しは作業が終わったら消す。

## 6. イメージの版を上げる（age・ベース）

イメージ（`deploy/local/platform-backup/image/Dockerfile`）は、ベースを digest で固定し、age を**版・チェックサム・署名**の
3 つで固定している。Alpine の安定版ブランチは各パッケージの最新のリリースしか置かないため、上流が age の `-rN` を上げると
取得が失敗し、CI の `build-local (platform-backup)` が赤くなる。**赤くなるのが正しい**（黙って別の版を入れない）。
次の手順で上げる。稼働クラスタへは、リポジトリに入ってから §1 の 5 で当てる。
上流の作り直しは日次の監視（`age-apkindex-watch.yml`）が検知し、ラベル `age-apkindex-drift` の issue を立てる（本文に固定の版と APKINDEX の版が並ぶ）。**版は自動では上がらない**ので、その issue から次の手順へ進む。
起動スクリプトの WARN から来たときは、その「原因:」の行が**版の解決の失敗**であることを先に確かめる（資格情報ヘルパーや到達の失敗は版を上げても直らない。「失敗したときの分岐」を見る）。

1. **ベースの digest を引く**（稼働クラスタへ pull しない。レジストリの API を読むだけ）。`postgres:<PG の版>-alpine<Alpine の版>`
   の image index の digest を、匿名トークンで `registry-1.docker.io/v2/library/postgres/manifests/<タグ>` へ HEAD を撃ち、
   応答ヘッダ `docker-content-digest` から取る。PG のメジャー版は本体（`deploy/local/infra/postgres.yaml`）と揃える。
   続けて、ビルドが取りに行くミラーがその digest を**トークン無しで 200 で返す**ことを確かめる（401 なら資格情報ヘルパーが呼ばれる）:

   ```bash
   curl -sS -o /dev/null -w '%{http_code}\n' \
     -H 'Accept: application/vnd.oci.image.index.v1+json' \
     https://mirror.gcr.io/v2/library/postgres/manifests/<digest>          # 200 であること
   ```
2. **age の版と sha256 を引く。** そのベースの Alpine のブランチ（例 `v3.24`）の
   `dl-cdn.alpinelinux.org/alpine/<ブランチ>/community/<x86_64|aarch64>/APKINDEX.tar.gz` の `P:age` の `V:` が版。
   同じ場所の `age-<版>.apk` を取り、`sha256sum` で両アーキテクチャのチェックサムを取る。
3. **4 か所を同じ値へ上げる。** Dockerfile の `FROM`（タグと digest。取得元の `BASE_REGISTRY` は替えない）と `ARG AGE_VERSION` / `AGE_APK_SHA256_*` / `ALPINE_BRANCH`（ベースの Alpine を上げたとき）、
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
| 起動スクリプトの最後に `欠けている前提: image` / `recipients`（CronJob の `SUSPEND` が `True`） | `image` … バックアップのイメージがランタイムに無い（ビルドの失敗は `[2/7]` の WARN に原因が出る）。`recipients` … ConfigMap `platform-backup-age-recipients` が無い・占位のまま・不正な行がある（判定は CronJob の中の検査と同じ規則） | 欠けている方を §1 で揃え、起動スクリプトを再実行する（有効に戻る） |
| `age の受取人ファイルがありません` | ConfigMap `platform-backup-age-recipients` が無い | §1 の 2〜3 |
| `… 行目が age の公開鍵（age1...）ではありません` / `公開鍵が 1 つもありません` | 占位のまま・写し間違い | 受取人ファイルを直して §1 の 3 |
| `受取人ファイル … 行目の前後に空白があります` | 公開鍵の行の前後や `#` 行の頭に空白がある | 空白を消して §1 の 3 |
| Pod が `ErrImageNeverPull` / `ErrImagePull` / `ImagePullBackOff`（イメージ `k3d-local/platform-backup:…`） | イメージを作っていない・タグを上げたのに作り直していない・起動スクリプトのビルドが失敗した（`WARN: k3d-local/platform-backup:… のビルドに失敗しました` が出る。起動は止めない） | WARN の「原因:」の行で分かれる。**版の解決に失敗** → §6。**資格情報ヘルパーの失敗か、レジストリ・ミラーへの認証・到達の失敗**（ログに `error getting credentials` 等）→ 版を上げても直らない。まずビルドのログの `load metadata` の行が `mirror.gcr.io/library/postgres` を指しているかを見る（`docker.io` 等を指していれば Dockerfile の `BASE_REGISTRY` の既定が替わっている。匿名の取得に 401 を返すレジストリは資格情報ヘルパーを呼ぶので既定へ戻す）。次に資格情報ヘルパー（`~/.docker/config.json` の `credsStore` / `credHelpers`）を確かめてランタイムを再起動する・プロキシと DNS で `mirror.gcr.io` と `dl-cdn.alpinelinux.org` へ届くか確かめる、のあと §1 の 5 で作り直す。**判別できません** → 起動スクリプトの出力にあるビルドのログを読んで原因を確かめる（sha256 の不一致は版を上げて上書きしない）。WARN が出ていなければ §1 の 5 の手順でイメージを作り、手動の Job を走らせ直す（レジストリからは取れない） |
| `age がありません（イメージが k3d-local/platform-backup ではない可能性があります…）` | CronJob が age を持たない別のイメージを指している（古いマニフェストの当て直し等） | `kubectl -n platform-infra get cronjob platform-backup-postgres -o jsonpath='{..image}'` で確かめ、§1 の 5 で当て直す |
| `保管先に目印 .platform-backup-target がありません` | ドライブが外れている・目印を置いていない | ドライブを確かめて §1 の 4。**もう片方には書けている** |
| `DB の一覧を取れません` | Postgres が落ちている・Secret `postgres` のパスワードと DB が食い違う | `kubectl -n platform-infra get pods`、Secret の供給（起動スクリプト・ESO）を確かめる |
| `一部の成果物が欠けています`（回が `-partial`） | 特定の DB の `pg_dump` が失敗 | ログの `pg_dump が失敗しました: <DB>` を見る |
| `秘匿管理へ接続できません` / `秘匿管理から応答がありません` | 秘匿管理（`deploy/vault`）が落ちている・起動中 | `kubectl -n platform-infra get pods -l app=vault`。Ready になってから手動の Job を走らせ直す |
| `秘匿管理へログインできません（HTTP 403 …）` | Vault の回の認証（role・ポリシー）が入っていない・PVC を作り直して消えた・role が別の SA に束縛されている | §1 の 5 の `bootstrap.sh` を走らせ（または起動スクリプトを再実行し）、手動の Job を走らせ直す |
| `秘匿管理へログインできません（HTTP 503 …）` / `スナップショットを取れません（HTTP 503 …）` | 秘匿管理が sealed | `kubectl -n platform-infra rollout restart deploy/vault`（ラッパーが unseal する）の後に走らせ直す |
| `スナップショットを取れません（HTTP 403 …）` | role のポリシーが `sys/storage/raft/snapshot` の read を持たない | §1 の 5 の確かめ方でポリシーを見て、`bootstrap.sh` を走らせ直す |
| `スナップショットが空です` / `gzip として壊れています` / `… SHA256SUMS と一致しません` / `… がありません（raft のスナップショットではない）` | 取得の途中で切れた・秘匿管理が raft でない（旧 Vault のまま） | 秘匿管理のログと `vault status` の `Storage Type` を見る。raft でなければ移行の手順書へ。そうでなければ走らせ直す。**この回は書かれていない**（壊れた写しは置かない） |
| `ServiceAccount のトークンがありません` | CronJob のマニフェストが古い（projected token の無い版） | `kubectl apply -k deploy/local/platform-backup/vault` で当て直す |
| `init のファイルがありません` / `init のファイルに unseal 鍵の行がありません`（回が `-partial`） | PVC の `.local-dev-init` が無い・壊れている | 秘匿管理のラッパーのログ（`vault-entrypoint`）を見る。**この回は同じ秘匿管理へは戻せるが、失ったときには開けない** |
| `消せませんでした（想定外の中身があります）` | 回のディレクトリに手でファイルやフォルダを置いた | 置いたものを退けてから次の回を待つ |
| Job が作られない | PC 停止で後追いの締切（22:00 JST）を過ぎた | 翌日の回を待つか、手動の Job を走らせる |

## 記録

- リストア試験（§3）の実施日・試した回・結果（要約の行）・実施者を、AST 側の切替の issue へコメントで残す
  （四半期ごと・切替前）。
- 日々の確認は記録しない（Job の履歴が失敗 7 件・成功 3 件まで残る）。

## 限界（この手順で担保できないこと）

- **オフサイトではない。** C: と E: は同じ筐体にある。本機の喪失・盗難・ランサムウェアには耐えない。
- **Vault の回は、秘匿管理が動いていて unseal 済みで、認証（§1 の 5）が入っているときだけ取れる。** どれかが欠けた日は回が無い
  （Job の失敗として見える）。
- **Vault の回は raft だけを写す。** 移行の後に片付けていない旧 Vault の file ストレージ（`/vault/data` 直下の `core/` 等）は写さない
  （移行前の写しは、移行の手順書の手順 1 で取った回である）。
- Vault の回のスナップショットは、作業場所（メモリ・256 Mi）に収まる大きさまで。超えると Job が失敗する（PoC の実測は数十 KB）。
- **行数の一致は中身の一致ではない。** リストア試験は件数だけを突き合わせる（台帳の欠損を見つけるための最低限）。
- 秘密鍵を失えば、すべての回が復号できない。予備の鍵を受取人に並べて別の場所に保管すること。
- 取りこぼしの後追いは 22:00 JST まで。PC を毎日 12:00〜22:00 に動かしていないと、その日の回は取れない。
