---
title: 運用 Runbook — 対になる秘密のローテーション（相手と保管先を対で書く）
type: runbook
status: draft
author: claude
created: 2026-09-28
updated: 2026-10-10
---
<!-- trace:
ids: [SC-22, NFR-18, SC-12, FR-16]
adrs: [ADR-0132, ADR-0124, ADR-0095, ADR-0005, ADR-0023, ADR-0123]
iadrs: [IADR-0525, IADR-0518, IADR-0517, IADR-0516, IADR-0492, IADR-0485, IADR-0369, IADR-0433, IADR-0453, IADR-0456, IADR-0092, IADR-0133, IADR-0524]
specs: [20261010_1877_paired-rotation-windows-portable, 20261009_1840_secret-store-openbao, 20261009_1834_realm-import-secret, 20261009_1830_dev-secret-guard, 20261009_1818_sc12-idp-drift-detection, 20261009_1817_sc12-provisioning-wiring, 20260928_issue-1682_paired-secrets-outside-sc22, 20260925_458_secret-rotation-runbook, 20261009_1859_keycloak-26-upgrade]
issues: [#1877, #1840, #1834, #1830, #1818, #1817, #1696, #1682, #458, #1411, #1859, planning#700, AST#1078]
-->

# 運用 Runbook: 対になる秘密のローテーション

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 秘密情報のローテーション全体の入口は [`secret-rotation-runbook.md`](secret-rotation-runbook.md) であり、本書はそのうち
> **製品の画面では回せない「対になる秘密」**の手順を持つ。対象は経路B（ローカル k8s。`VAULT=1 ESO=1`）である。**本番の供給経路は未配備**であり、本書は本番の手順ではない。
>
> **値そのものは本書にもリポジトリのどこにも置かない。** 手順は最後まで値を画面へ出さない。
>
> 🔴 **本書の手順を通しで実行したことはまだ無い（リハーサル未実施）。** 手順 1 の 1-1〜1-3 だけは 2026-10-10 に Windows（Git Bash）で実行し、回すところまで通った。末尾「リハーサル記録」を参照。

## 対になる秘密とは

**相手（認証基盤の realm・データストア）と保管先（Vault）の両方に同じ値が在り、両方を同時に変えなければ認証が壊れる秘密**である。
片側だけ書くと、認証基盤ならトークン端点が `invalid_client` を返し続け、データストアなら接続が認証エラーになる。
**画面の 1 欄では変えられない**ので、秘密情報・接続設定の管理画面の対象外であり、計画がそう裁定している（trace ブロック参照）。

- **Git には置かない。** 値の置き場所は保管先のままである。
- **境界は性質であり、項目名ではない。** 新しい秘密が同じ性質を持つなら、同じ扱いにする。
- 分類の単一情報源は `deploy/bootstrap/sc22-secret-items.json` である（`deferred[]`・`excluded[]`・`items[].notWritable` の `*-auth-client-*`）。

| 群 | 何か | 件数 | 本書の節 |
| --- | --- | ---: | --- |
| 群 1 | 認証基盤のクライアントシークレット —— OIDC クライアント 8（境界層・利用者管理・Grafana・Vault・Headlamp・Wiki.js・パスワード再設定の門・合成監視）とサービス間 9（`deferred[]`）、取引ユニットの `*-auth-client-*` の 5 組（`ai-stock-trading/app-secrets` の `notWritable`） | 17 ＋ 5 | [手順 1](#手順-1-群-1認証基盤のクライアントシークレット) |
| 群 2 | データストアの資格情報 —— `postgres` / `postgres-app` / `rabbitmq` / `rabbitmq-app` / `keycloak-admin` / `object-storage-credentials` / `wikijs-db`（`excluded[]`） | 7 | [手順 2](#手順-2-群-2データストアの資格情報) |

> **同じ性質だが保管先に無いもの**: 認証基盤の `abac-seeder`（開発用の投入器だけが使う）と `argocd`（`argocd-secret` を起動器が直接書く）。
> 保管先に置き場が無いので本書の対象外だが、回すときは同じ順序（相手を変えてから消費側を作り直す）で行う。

### 回した値が戻らないこと（前提）

回した値を元へ戻す経路は、次のとおり塞いである。**塞がれていない経路が 1 つあり、それは手順の中で扱う。**

| 経路 | 今の扱い |
| --- | --- |
| 認証基盤の realm の宣言（`deploy/keycloak/microservices-platform-realm.json`） | 宣言の client の `secret` は**開発用の値**だけであり、**client を新しく作るときにだけ**使う。宣言の追随（`deploy/local/keycloak-setup/reconcile-realm.sh`）は既存の client の `secret` を比べず・書かない。起動時の import は realm が在れば飛ばす。空の状態からの import では、管理用の 3 クライアント（`identity-admin`・`reset-gate`・`mcp-client-admin`）だけは起動器に与えた env の値で作る（与えなければ開発用の値） |
| 保管先の種（`deploy/local/vault/eso/bootstrap.sh`） | 対になる秘密の KV は**無いときだけ作る**。在れば env を渡しても触らない |
| 🔴 手動の Secret 作成（`scripts/k8s-local-up.sh` の `apply_secret`） | **塞いでいない。** `postgres` / `rabbitmq` / `keycloak-admin` / `reset-gate-oidc` は `ESO=1` でも env か開発用既定値で Secret を作る。保管先の値は戻らないが、**次の同期までの間 Secret が既定値になる。** 起動の後に同期を促す（[起動の後](#起動の後に同期を促す)） |

## この手順を実行する条件（いつ走らせるか）

**周期は計画に定められていない。** 本書も周期を定めない。次のいずれかで実行する。

- **漏洩の疑い**（ログ・画面共有・リポジトリ・チャットへの露出、端末の紛失）。
- **その値を知っている担当者の離任。**
- **開発用の既定値のまま運用している値を、実運用の値へ差し替える**（最初の 1 回もこの手順で行う。開発用の値はリポジトリに在り、秘密として機能していない）。

**実行してはいけない場合**:

- 環境を新しく立ち上げるとき。それは初期投入であり、`scripts/k8s-local-up.sh` の通常経路が担う。
- **片側だけを書くとき。** 保管先だけ・相手だけの書き込みは、本書の手順の途中で止まった状態と同じである（[途中で止まったとき](#途中で止まったとき)）。
- 取引ユニットのブローカの接続文字列を Secret から組み立てていない間の `rabbitmq` / `rabbitmq-app`（[`secret-rotation-runbook.md`](secret-rotation-runbook.md) 手順 B の注意）。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | 対象クラスタの `kubectl exec`（`platform-infra` の Vault）・`port-forward`（Keycloak）と、対象名前空間の Secret / ExternalSecret / Deployment への読み書き。認証基盤の master realm の管理者（Secret `platform-infra/keycloak-admin`） |
| 必要なツール | `bash`・`kubectl`・`curl`・`jq`・`openssl`（新しい値の生成）。**ホストに `vault` CLI は不要**（Vault Pod 内で実行する）。Linux・macOS・WSL・Windows の Git Bash のどれでも同じ部品で動く（Git Bash の注意は [Windows（Git Bash）での注意](#windowsgit-bashでの注意)） |
| 前提の状態 | Vault と External Secrets Operator が稼働し、Vault は永続化されている（raft ストレージ＋PVC） |
| 所要時間の目安 | 群 1: 1 クライアント 10〜15 分。群 2: 1 ストア 15〜30 分。🔴 **どちらも、相手を書いてから消費側の作り直しが終わるまでの間、その資格情報を使う処理が失敗する**（重ねられない） |

## 共通の原則

1. **重ねられない。** 認証基盤の client の `secret` は 1 つ、データストアの利用者のパスワードも 1 つである。**相手を書いた瞬間に旧は使えなくなる。**
   相手を書いてから消費側の作り直しまでを、途切れずに 1 つの作業として続ける。業務の少ない時間に行う。
2. **書く順序は「控える → 保管先 → 相手 → 同期 → 作り直し」。** 保管先を先に書いても、同期を促すまで消費側は旧の値を持ち続けるので何も壊れない。
   相手を書いた時点から壊れ始め、同期と作り直しで直る。**途中で止まったときの戻す向きは、どこまで進んだかで決まる**（[途中で止まったとき](#途中で止まったとき)）。
3. **値を画面にも引数にも出さない。** 新しい値は変数に作り、コマンドへは標準入力で渡す。シェル履歴・`ps`・スクロールバックに残さない。
4. **保管先は KV 全体を置き換えない。** `vault kv patch -method=patch` を使い、`vault kv put` を使わない（`ai-stock-trading/app-secrets` は同じ KV に画面から書く値が同居している）。
5. **確かめるのは長さだけ。** 値は表示しない。
6. **回した事実を残す**（[記録](#記録)）。コンソールの操作は画面の監査ログに乗らない。

## 共通の部品

以下はすべて **bash** で実行する（`read -s` は bash の機能）。**1 つの端末で上から順に実行し、終えるまで端末を閉じない**（部品が作る変数と一時ディレクトリを後の段が使う）。
部品は Linux・macOS・WSL・Windows の Git Bash で同じ形のまま動くように書いてある（Git Bash で壊れた形と直し方は [Windows（Git Bash）での注意](#windowsgit-bashでの注意)）。

**0-0. 作業の準備**（最初に 1 回。一時ディレクトリ・片付け・パスの渡し方を用意する）:

```bash
umask 077
WORK="$(mktemp -d /tmp/rot.XXXXXX)"   # 値を含むファイルはここにだけ置き、使い終えたらすぐ消す
trap 'rm -rf -- "${WORK:?}"' EXIT     # シェルが正常に終わったときの保険。片付けの本体は 0-z
case "$(uname -s)" in
  MINGW*|MSYS*) np() { cygpath -m "$1"; } ;;   # Git Bash: ネイティブの jq・curl へは C:/ 形で渡す
  *)            np() { printf '%s' "$1"; } ;;
esac
```

🔴 **trap は保険であり、片付けを任せない。** 次の場合は `$WORK` が残る（1-3 の 2〜5 の間なら、新しい値を含む `client-new.json` が残る）:

- 対話シェルで Ctrl-C を押しても、EXIT の trap は発火しない（シェルは終わらない）。
- Git Bash のウィンドウを強制的に閉じた・`kill -9` で止まったときは trap が走らない。`/tmp` は Windows では `%TEMP%` の下にあり、そこに残る。
- 0-0 を 2 回実行すると、trap は最後の `$WORK` しか消さない（前の `$WORK` は残る）。

**途中で止めたら、やり直す前でも終える前でも必ず 0-z を実行する。** 0-z は前の試行の `/tmp/rot.*` もまとめて消す（同じ利用者で 2 つの手順を同時に回さない）。
端末ごと失ったときは、新しい Git Bash で 0-z の `rm` の行だけを実行する。

部品の書き方の約束（以下の部品はすべてこれに従っている。部品を書き換えるときも守る）:

- **ネイティブのコマンド（`jq`・`curl`）へファイルのパスを引数で渡すときは `"$(np "$WORK/…")"` で渡す。** リダイレクト（`>`・`<`）は bash が開くので `np` は要らない。
- **値を 1 つ取り出す jq は `-j` を使う**（行末に何も足さない）。複数行を取り出すときは `jq -r … | tr -d '\r'` にする。
- **プロセス置換（`<(…)`）を使わない。** 一時ファイルに書いて渡す。
- **大きな JSON はパイプせず、ファイルへ書いてから jq に読ませる。**
- **HTTP の状態コードは `curl -o … -w '%{http_code}'` で変数に受けて比べる**（`| grep -q` で判定しない）。

**0-a. 新しい値を作る**（表示しない）:

```bash
NEW_VALUE="$(openssl rand -hex 32)"
```

**0-b. 保管先の直前の版を控える**（metadata だけ。値は出ない。`current_version` の番号を手元に書き留める）:

```bash
kubectl -n platform-infra exec deploy/vault -- sh -c '
  export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"
  vault kv metadata get secret/<path>
' | grep -E 'current_version|^Key|^---'
```

🔴 **前の試行で `keycloak ok`（1-3 の 5）が出ていないなら、控えるのは最初の試行の前の版である。** 途中で止めてやり直すとき、0-c まで進んだ試行のたびに版が 1 つ進む（2026-10-10 の実行では中断した試行で 1→6 まで進んだ）。
やり直しのたびに控え直すと、相手と一致しない版を控えることになる。
前の試行で `keycloak ok` まで出ていたなら、やり直さない。保管先と相手は一致しているので、[途中で止まったとき](#途中で止まったとき)の 3 段目どおり前へ進める。

**0-c. 保管先のプロパティを 1 つ書く**（`printf '%s'` で渡す。ヒアドキュメントや `echo` は末尾の改行ごと入り得る）:

```bash
printf '%s' "$NEW_VALUE" | kubectl -n platform-infra exec -i deploy/vault -- sh -c '
  export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"
  vault kv patch -method=patch secret/<path> <property>=-
'
```

**0-d. 同期を促す**（既定の同期間隔は 1 時間）:

```sh
kubectl -n <namespace> annotate externalsecret <name> force-sync="$(date +%s)" --overwrite
```

**0-e. Secret を読む消費側を引く**（名前を書き写さずに引く。`env`・`envFrom`・ボリュームのどれで読んでいても当たる。全名前空間の JSON は大きいので、ファイルへ書いてから読む）:

```bash
kubectl get deploy -A -o json > "$WORK/deploy.json"
jq -r --arg s "<secret>" '.items[]
  | select([.spec.template.spec.containers[]? | (.env[]?.valueFrom.secretKeyRef.name), (.envFrom[]?.secretRef.name)]
           + [.spec.template.spec.volumes[]?.secret.secretName] | index($s))
  | "\(.metadata.namespace)/\(.metadata.name)"' "$(np "$WORK/deploy.json")" | tr -d '\r'
rm -f "$WORK/deploy.json"
```

**0-z. 片付ける**（手順の最後と、途中で止めたとき。前の試行の一時ディレクトリもまとめて消す）:

```bash
unset NEW_VALUE; rm -rf -- /tmp/rot.*; trap - EXIT
```

### Windows（Git Bash）での注意

2026-10-10 に Windows の Git Bash（ネイティブの `jq` 1.8.2・`curl`・`kubectl`）で手順 1 の 1-1〜1-3 を実行したとき、部品の旧い形が次の 5 点で壊れた。
上の部品は 5 点とも避ける形に直してある。**部品を書き換えたり手で打ち直したりするときは、旧い形へ戻さない。**

| 症状 | 原因 | 部品での避け方 |
| --- | --- | --- |
| 1-1 のトークン要求が `invalid_user_credentials` になる。1-2 の `client ok` が出ても、その後の要求が通らない | Windows の jq は `-r` の出力の行末を CRLF にする。フォームの末尾や `CID` に `\r` が付く | 値 1 つは `jq -j`。複数行は `jq -r … \| tr -d '\r'`（Windows 版の jq なら `-b` でも LF のまま出せる） |
| 1-3 で jq が `/dev/fd/63` を開けないと言って止まる | プロセス置換 `<(…)` が渡す `/dev/fd/N` を、ネイティブの jq は開けない | 一時ファイル（0-0 の `$WORK`）に書いて、パスで渡す |
| 書いたはずのファイルが無い・別のファイルを読む | MSYS の `/tmp/…` をネイティブの `curl`・`jq` へ渡すと、引数の形によっては変換されず `C:\tmp\…` を読み書きする。`MSYS_NO_PATHCONV=1` の下では変換が起きない | 0-0 の `np`（`uname` が `MINGW*`・`MSYS*` のとき `cygpath -m` で `C:/…` 形にする）で渡す |
| `kubectl get deploy -A -o json \| jq …` が返ってこないことがある | 大きな JSON のパイプが時々止まる | ファイルへ書いてから jq に読ませる（0-e） |
| `204` が返っているのに失敗と判定される | `set -o pipefail` の下で `curl … \| grep -q 204` は、grep が先に終わると curl が SIGPIPE で落ち、パイプ全体が偽になる | 状態コードを変数に受けて比べる（1-3 の 5） |

- `umask 077` は Windows のファイルの権限（ACL）には効かない。`$WORK` は利用者ごとの一時ディレクトリ（`%TEMP%` の下）に作られ、値を含むファイルは使った直後に消す。
- 1-1 の `port-forward` を `&` で裏へ回し `kill "$PF_PID"` で止める形は、Git Bash でもそのまま動く。

## 手順 1: 群 1（認証基盤のクライアントシークレット）

### 1-0. 対応表

| client | 保管先のパスとプロパティ | 同期する ExternalSecret | 値を読むもの |
| --- | --- | --- | --- |
| `bff` | `msp/bff-oidc` の `client-secret` | `microservices-platform/bff-oidc` | 0-e で引く |
| `identity-admin` | `msp/identity-admin-oidc` の `client-secret` | `microservices-platform/identity-admin-oidc` | 0-e で引く |
| `mcp-client-admin` | `msp/mcp-client-admin-oidc` の `client-secret` | `microservices-platform/mcp-client-admin-oidc` | 0-e で引く（`mcp-service`）。🔴 **漏えいを疑うときは [管理用の資格情報が漏れたとき](#管理用の資格情報が漏れたときmcp-client-admin) を先に読む** |
| `retrieval-service` / `ingestion-service` / `aianalysis-service` / `graph-service` / `conversion-service` / `wiki-service` / `datasource-service` / `mcp-server` / `document-service` | `msp/<client>-token` の `client-secret` | `microservices-platform/<client>-token` | 0-e で引く |
| `synthetic-monitor` | `msp/synthetic-monitor-oidc` の `client-secret` | `microservices-platform/synthetic-monitor-oidc` | 0-e で引く（`SYNTHETIC=1` のときだけ在る） |
| `reset-gate` | `msp/reset-gate-oidc` の `client-secret` | `platform-infra/reset-gate-oidc` | 0-e で引く |
| `grafana` / `headlamp` | `msp/<client>-oidc` の `client-secret` | `platform-infra/<client>-oidc` | 0-e で引く（`OBSERVABILITY=1` / `HEADLAMP=1` のときだけ在る） |
| `vault` | `msp/vault-oidc` の `client-secret` | `platform-infra/vault-oidc` | **Pod ではなく** `deploy/local/vault/oidc/bootstrap.sh` が Vault の OIDC 認証設定へ書く。同期の後に同スクリプトを再実行する |
| `wiki-js` | `msp/wikijs-oidc` の `client-secret` | `microservices-platform/wikijs-oidc` | **Pod ではなく** `deploy/local/wikijs-setup/bootstrap.sh` が Wiki.js の DB へ書く。同期の後に同スクリプトを再実行する |
| `ai-stock-trading-svc` / `-kb-writer` / `-kb-reader` / `-llm-caller` / `-owner` | `ai-stock-trading/app-secrets` の `service-` / `kb-` / `kb-reader-` / `llm-` / `discord-owner-auth-client-secret` | `ai-stock-trading/ast-secrets` | 0-e で引く（取引ユニットの消費側） |

### 1-1. 管理者のトークンを取る

**0-0** を済ませてから行う。

```bash
kubectl -n platform-infra port-forward deploy/keycloak 18080:8080 >/dev/null 2>&1 &
PF_PID=$!
KC=http://127.0.0.1:18080
kc_token() {   # 管理者のトークンを取り直す（何度でも呼べる）
  local u; u="$(kubectl -n platform-infra get secret keycloak-admin -o jsonpath='{.data.username}' | base64 -d)"
  kubectl -n platform-infra get secret keycloak-admin -o jsonpath='{.data.password}' | base64 -d \
    | jq -Rj --arg u "$u" '"grant_type=password&client_id=admin-cli&username=\($u|@uri)&password=\(.|@uri)"' \
    | curl -sf -X POST "$KC/realms/master/protocol/openid-connect/token" \
        -H 'Content-Type: application/x-www-form-urlencoded' --data-binary @- | jq -j .access_token
}
TOKEN="$(kc_token)"
[ -n "$TOKEN" ] && [ "$TOKEN" != null ] && echo "token ok"
```

🔴 **master realm の管理者のトークンは短命である**（既定で 60 秒）。1-3 の 5 の `PUT` の前と、[途中で止まったとき](#途中で止まったとき)の確かめ方の前に、
上の最後の 2 行（`TOKEN="$(kc_token)"` と確認）で**取り直す**。期限切れのトークンでは管理 API が `401` を返す。

フォームは `jq -j` で組み立てる（行末に何も足さない）。`-r` にすると Windows の jq ではフォームの末尾に `\r` が付き、`invalid_user_credentials` になる。

🔴 **Keycloak の Pod で `kcadm.sh` を exec しない**（別 JVM が本体を OOMKilled にする）。管理操作は管理 API で行う。

### 1-2. 対象の client を引く

```bash
CLIENT=<client>   # 例: bff
CID="$(curl -sf -H "Authorization: Bearer $TOKEN" "$KC/admin/realms/platform/clients?clientId=$CLIENT" | jq -j '.[0].id')"
[ -n "$CID" ] && [ "$CID" != null ] && echo "client ok"
```

### 1-3. 回す

**新しい client の表現を組み立てて確かめるまで、保管先にも認証基盤にも書かない。** 組み立てで止まっても、何も変わっていない状態で終えられる。

1. **0-a** で新しい値を作る。
2. **新しい client の表現を組み立てて確かめる**（client の表現を読み、`secret` だけを差し替える。値はファイルで渡し、引数にも画面にも出さない。まだどこにも書かない）:

   ```bash
   curl -sf -H "Authorization: Bearer $TOKEN" -o "$(np "$WORK/client.json")" "$KC/admin/realms/platform/clients/$CID"
   printf '%s' "$NEW_VALUE" > "$WORK/secret"
   jq --rawfile s "$(np "$WORK/secret")" '.secret = $s' "$(np "$WORK/client.json")" > "$WORK/client-new.json"
   jq -e --arg cid "$CID" --arg c "$CLIENT" --rawfile s "$(np "$WORK/secret")" \
     '.id == $cid and .clientId == $c and .secret == $s' "$(np "$WORK/client-new.json")" >/dev/null \
     && echo "body ok"
   rm -f "$WORK/secret" "$WORK/client.json"
   ```

   `body ok` は、組み立てた表現の `secret` が新しい値と**等しい**ことを値を出さずに確かめる（長さでは見ない。差し替えが空振りして旧の値が残っても、旧新とも 64 文字なら長さは一致する）。
   `body ok` が出なければ先へ進まない。まだ何も書いていないので、**0-z** で終えてよい（0-0・1-1 からやり直す）。
3. **0-b** で保管先の直前の版を控える（`<path>` は 1-0 の表）。
4. **0-c** で保管先へ書く。この時点では消費側は旧の値を持っているので、何も壊れない。
5. **認証基盤の client の `secret` を同じ値にする**（2 で確かめた表現を `PUT` で戻す。直前にトークンを取り直し、`token ok` を確かめてから `PUT` する）:

   ```bash
   TOKEN="$(kc_token)"; [ -n "$TOKEN" ] && [ "$TOKEN" != null ] && echo "token ok"
   CODE="$(curl -s -o /dev/null -w '%{http_code}' -X PUT -H "Authorization: Bearer $TOKEN" \
     -H 'Content-Type: application/json' --data-binary @"$(np "$WORK/client-new.json")" \
     "$KC/admin/realms/platform/clients/$CID")"
   [ "$CODE" = 204 ] && rm -f "$WORK/client-new.json"
   [ "$CODE" = 204 ] && echo "keycloak ok" || echo "keycloak NG: $CODE"
   ```

   `keycloak ok` が出れば書けている。🔴 **ここから、旧の値を持つ消費側は `invalid_client` になる。6・7 を続けて行う。**
   `keycloak NG: 401`（・`403`）は認証で断られたので **`PUT` は適用されていない**（成否不明ではなく未実行）。トークンを取り直して 5 だけをやり直す（`client-new.json` は残してある）。
   それ以外の `keycloak NG`（`000`・`5xx` など）は [途中で止まったとき](#途中で止まったとき)の「相手の書き込みが失敗した・成否が分からない」へ進む。
6. **0-d** で同期を促す（1-0 の ExternalSecret）。
7. 消費側を作り直す（**0-e** で引いた Deployment を `kubectl -n <ns> rollout restart deploy/<name>`、`rollout status` で待つ）。
   `vault` / `wiki-js` は 1-0 の表のスクリプトを再実行する。
8. **確かめる**（[確認](#確認この手順が成功したと言える条件)）。
9. 終えたら `unset TOKEN CODE; kill "$PF_PID"` と **0-z**。**[記録](#記録)する。**

### 管理用の資格情報が漏れたとき（`mcp-client-admin`）

MCP クライアント登録管理の後段が、無人のクライアントとそのサービスアカウントの属性を認証基盤へ書くための機密クライアントである。
**群 1 の中でいちばん影響範囲が広い**ので、回す前に影響範囲を確かめる。

**影響範囲**（認証基盤のレルム管理のロール「クライアントの管理」「利用者の管理」の 2 つが届く範囲。現行の認証基盤の版では絞れない）:

| 届くもの | 漏えいした secret でできること |
| --- | --- |
| レルムの**全クライアント** | 作成・変更・削除。**全クライアントの secret の読み取り**（他のサービス・道具へのなりすまし）。削除による認証の停止 |
| レルムの**全利用者**（人とサービスアカウント） | 属性とロールの書き換え（例: 機密区分を上げる・管理者ロールを付ける）。**部分集合の規則と利用者アカウント管理の画面を経ずに書ける** |
| 🔴 レルムの設定（**間接**） | 読める secret にレルムの管理を持つ申請の門（`reset-gate`）と利用者の管理を持つ反映先（`identity-admin`）が含まれる。それを使えば認証フロー・送信設定・総当たり対策まで書ける。**レルムの全権の漏えいとして扱う** |
| 直接は届かないもの | 他のレルム（master を含む） |

🔴 **dev 以外のクラスタでは、起動の直後に dev の値が残っていないかを確かめ、残っていれば回す**（漏えいが無くても）。`identity-admin`・`reset-gate` も同じである。

- 起動器（手動の Secret 作成・保管先の初期投入・レルムの後追い）は、kube context が dev の許可集合（`k3d-*`・`kind-*`・`rancher-desktop`・`docker-desktop`）に
  無いと、この 3 つを dev の値（リポジトリに公開されている）で作ろうとした時点で名指して止まる。そのときは `IDENTITY_ADMIN_CLIENT_SECRET`・
  `RESET_GATE_CLIENT_SECRET`・`MCP_CLIENT_ADMIN_CLIENT_SECRET` に dev 以外の値（0-a の作り方）を与えて再実行する。認証基盤の側が dev の値のままなら、
  その後に下の確かめ方で名指されるので、1-3 の手順で対に回す。dev のクラスタだと分かっているときだけ `ALLOW_DEV_CLIENT_SECRETS=1` で通せる（警告を出す）。
  止まったときのメッセージにも、下の確かめ方が出る。
- Keycloak 本体の realm の初回 import（空の状態からの起動・realm を消した後の再起動）は、起動器が作る取り込み元（Secret `keycloak-realm-import`）を読む。
  起動器はこの取り込み元を作るとき、env を与えたクライアントの secret を宣言の dev の値から env の値へ差し替える（値は出力しない）。
  よって env を与えて新しいクラスタを起動すれば、認証基盤の側も最初からその値になり、消費側の Secret と食い違わない。
  ただし保管先（Vault）を使う起動（`ESO=1`）で保管先に既に値が在るときは、保管先は書き換えない（無いときだけ書く）。
  **このときは env に保管先に在る値と同じ値を与える**（違う値を与えると、認証基盤は env の値・消費側は保管先の値になり食い違う）。
  値に `${` を含めない（Keycloak の取り込みが置き換えるので、起動器が止める）。
- それでも dev の値が残る場合がある: 認証基盤が既に在るクラスタ（import は飛ばされる）・`ALLOW_DEV_CLIENT_SECRETS=1` で通したとき・保管先（Vault）の値を
  回した後に env を与えずに空の状態から起動したとき（認証基盤は宣言の値、保管先は回した値になり食い違う）。だから起動の直後に下の確かめ方を回す。

**確かめ方**（読むだけ・値を出さない）:

```bash
bash deploy/local/keycloak-setup/reconcile-realm.sh --check-dev-secrets
```

稼働の secret が dev の値のままのクライアントを `dev-secret <client>` と名指して非 0 で終える。名指されたものを 1-3 の手順で回し、もう一度実行して
0 で終わる（3 つとも `ok`）ことを確かめる。稼働に無いクライアントは `absent` と出る（作られるときは上の守りが掛かる）。

**手順**（通常の 1-3 に次を足す）:

1. **先に回す**（1-0 の行・1-3 の手順そのまま）。回した時点で旧い secret ではトークンが出なくなる（既に出ているトークンは有効期限まで残る）。
2. **全クライアントの secret が読まれた前提に立つ。** 1-0 の表の**ほかの行もすべて**回す（群 1 の全件）。読まれたかどうかは、監査の取り込みが無い間は判定できない。
3. **書き換えの痕跡を探す。** 認証基盤の管理イベント（このクライアントのサービスアカウントが主体のもの**と**、`reset-gate`・`identity-admin` のサービスアカウントが主体のもの）を、漏えいの疑いの期間について引く。利用者の属性・ロールの変更、クライアントの作成・削除、レルムの設定の変更（認証フロー・送信設定・総当たり対策）があれば、登録簿と宣言に照らして戻す（レルムの設定は後追いの check モードで宣言との差分を見る）。
4. MCP クライアント登録管理の画面の無人の行と、認証基盤の入口の印つきのクライアントを突き合わせる。［2026-10-09］MCP サーバーの定期の照合が 1 分ごとに突き合わせる（[運用仕様書](operations.md)「MCP クライアント登録簿と認証基盤の照合」）。回した後の照合で警報 `McpClientIdpDrift` が鳴っていないこと・MCP サーバーの Warning ログに `kind=attributes_differ` / `kind=orphan` が無いことを確かめる。**照合が見るのは登録簿と認証基盤の一致だけ**であり、登録簿の値そのものの正しさ（漏えい中に画面から書かれた値か）は見ないので、無人の行の属性は登録者に確かめる。
5. [記録](#記録)する。

🔴 **残余**: このクライアントの権限は「入口の印があるクライアントとそのサービスアカウント」へ絞れていない。［2026-10-09］認証基盤は 26.7.4 へ上がり、絞るのに要る細粒度の管理権限の新しい版（26.2 以降）は使える版になったが、絞る作業はまだ行っていない。

## 手順 2: 群 2（データストアの資格情報）

**ストアごとのコマンド（ストア側で何をするか・同期先・作り直す消費側）は [`secret-rotation-runbook.md`](secret-rotation-runbook.md) 手順 B-1 の表が正である。**
本書が持つのは順序と、途中で止まったときの戻し方である。

1. **0-0** で準備し、**0-a** で新しい値を作る（ストア側の対話入力で決める場合は、その値を `read -rs NEW_VALUE` で読む）。
2. **0-b** で保管先の直前の版を控える。**同値にすべき 2 つの KV**（`postgres-app` と `wikijs-db`、`rabbitmq` と `rabbitmq-app`）は**両方**控える。
3. **0-c** で保管先へ書く（同値の組は両方）。まだ何も壊れない。
4. **ストア側の値を同じ値にする**（B-1 の「ストア側で先に行うこと」）。🔴 **ここから、旧の値で新しく接続する処理が失敗する。**
5. **0-d** で同期を促し、B-1 の順で消費側を作り直す。
6. **確かめる。** 終えたら **0-z**。**[記録](#記録)する。**

## 起動の後に同期を促す

`scripts/k8s-local-up.sh` を再実行すると、手動の Secret 作成が `postgres` / `rabbitmq` / `keycloak-admin` / `reset-gate-oidc` の Secret を
**env か開発用既定値で**書き直す（保管先の値は戻らない）。env に新しい値を渡さなかったときは、起動の後に 4 つの同期を促す:

```sh
for es in postgres rabbitmq keycloak-admin reset-gate-oidc; do
  kubectl -n platform-infra annotate externalsecret "$es" force-sync="$(date +%s)" --overwrite
done
```

同期の後、`keycloak-admin` を読む宣言の追随（`bash deploy/local/keycloak-setup/reconcile-realm.sh --check`）が認証エラーで落ちないことを確かめる。
🔴 **`keycloak-admin` を回した直後の起動では、起動器が呼ぶ追随の Job が認証エラーの WARN で飛ぶことがある**（呼び出しは best-effort で、起動は止まらない）。
`--check` で確かめるだけで終えず、同期の後に**本実行（`bash deploy/local/keycloak-setup/reconcile-realm.sh`。apply）でやり直し**、その後に `--check` が差分 0 件になることを確かめる。

## 確認（この手順が成功したと言える条件）

**🔴 値を端末へ出さない。長さだけを見る。**

```sh
kubectl -n <namespace> get externalsecret <name> -o jsonpath='{.status.conditions[?(@.type=="Ready")].status}{"\n"}'
kubectl -n <namespace> get secret <name> -o jsonpath='{.data.<key>}' | base64 -d | wc -c
```

成功と言える条件:

- 同期先の ExternalSecret が `Ready=True`。
- 同期先 Secret の当該キーの長さが、**新しい値の長さと一致する**（`openssl rand -hex 32` なら 64）。
- 消費側が `Ready` になり、回した値を実際に使う操作が通る（群 1: ログイン・サービス間の呼び出し・管理 API への反映。群 2: DB / ブローカ / オブジェクトストレージへの接続）。
- **次の `scripts/k8s-local-up.sh` の後も**、上が保たれている（[起動の後に同期を促す](#起動の後に同期を促す)を含めて）。
- 宣言の追随の `--check` が差分 0 件のまま（client の `secret` は比べないので、回したことは差分にならない）。

## 途中で止まったとき

**どこまで進んだかで、戻す向きが決まる。** 迷ったら「相手を書いたか」だけを確かめる。

| 止まった段 | 状態 | 戻し方 |
| --- | --- | --- |
| 保管先へ書く前（群 1 の 1〜3、群 2 の 1・2） | 何も変わっていない | **0-z** で終える |
| 保管先へ書いた後・相手を書く前（群 1 の 4、群 2 の 3） | 保管先だけ新しい。消費側は旧の値を持ち、動いている | **保管先を控えた版へ戻す**: Vault Pod 内で `vault kv rollback -version=<控えた版> secret/<path>`（同値の組は両方）。**同期を促さない**（促すと消費側が新しい値を受け取って壊れる。促してしまったなら、戻した後にもう一度促す） |
| 相手を書いた後・作り直しの前（群 1 の 5 の後、群 2 の 4 の後） | 保管先と相手は新しい値で一致。消費側だけ旧い | **戻さない。前へ進める**（同期を促し、作り直す）。新しい値を失っていても、保管先に在る |
| 相手の書き込みが失敗した・成否が分からない | 保管先は新しい。相手は不明 | 群 1: 下の[成否の確かめ方](#成否の確かめ方群-1)を実行する。`一致` なら書けている → 前へ進める。**`不一致` のときだけ**保管先を控えた版へ戻す（上の 2 段目）。`判定不能` なら**何も戻さない**（トークンを取り直して確かめ方からやり直す）。群 2: 新しい値でストアへ接続を試し、通れば前へ進める。通らなければ保管先を戻す |
| 作り直しの後に動かない | 値は一致しているはず | 同期が `Ready` か・Secret の長さが一致するかを見る。一致しているのに動かなければ、相手を旧の値へ戻す: 保管先の控えた版の値を変数へ読み（`vault kv get -version=<控えた版> -field=<property> secret/<path>` を変数に受ける。表示しない）、群 1 はその値を `NEW_VALUE` に入れて 1-3 の 2 と 5、群 2 はストア側の手順でその値を書き、保管先を `rollback` し、同期と作り直しをやり直す |

### 成否の確かめ方（群 1）

認証基盤の client の `secret` が新しい値と同じかを、値を出さずに比べる。**3 つの結果を区別する**（長さでは比べない。旧の値も同じ長さのとき区別できない）。

```bash
TOKEN="$(kc_token)"
printf '%s' "$NEW_VALUE" > "$WORK/secret"
CODE="$(curl -s -o "$(np "$WORK/client-now.json")" -w '%{http_code}' \
  -H "Authorization: Bearer $TOKEN" "$KC/admin/realms/platform/clients/$CID")"
if [ -z "$TOKEN" ] || [ "$TOKEN" = null ] || [ "$CODE" != 200 ]; then
  echo "判定不能: HTTP $CODE"
else
  RC=0
  jq -e --rawfile s "$(np "$WORK/secret")" \
    'if has("secret") then .secret == $s else error("secret が無い") end' "$(np "$WORK/client-now.json")" >/dev/null 2>&1 || RC=$?
  case "$RC" in 0) echo "一致" ;; 1) echo "不一致" ;; *) echo "判定不能: jq $RC" ;; esac
fi
rm -f "$WORK/secret" "$WORK/client-now.json"
```

| 結果 | 意味 | 次に行うこと |
| --- | --- | --- |
| `一致` | 相手は新しい値になっている | 前へ進める（1-3 の 6） |
| `不一致` | 相手は新しい値になっていない | 保管先を控えた版へ戻す（上の表の 2 段目） |
| `判定不能` | 読めなかった（トークンの期限切れの `401`・port-forward が落ちた `000` など） | **何も戻さない。** 判定不能のまま戻すと、`PUT` が実は通っていたとき「保管先が旧・相手が新」になる。1-1 の port-forward が生きているかを確かめ、トークンを取り直してやり直す |

端末を閉じて変数を失っていたら、0-0・1-1・1-2 をやり直し、保管先の新しい版の値を変数へ読み（`vault kv get -field=<property> secret/<path>` を変数に受ける。表示しない）、`NEW_VALUE` に入れてから実行する。

🔴 **保管先の版の履歴は、戻すための唯一の控えである。** 0-b を飛ばさない。`vault kv metadata delete` や `destroy` をしない。

## 記録

- **記録先は #458 へのコメントただ 1 か所**である（退避手段〔画面を使わないコンソール投入〕の使用記録と同じ置き場）。
  **実施日時（タイムゾーンつき）・実施者・対象（client 名またはストア名と保管先のパス）・どの段まで進んだか・戻したならその版の番号**を書く。
- **値・値の長さ・値の一部は書かない。**
- この記録先は、保管先の監査を可観測性基盤へ取り込めるまでの暫定である。取り込めたら本節を差し替える（日付つきで追記する）。

### リハーサル記録

新しい環境（稼働中の利用者の環境でないもの）で本書を通しで実行し、結果をここへ追記する。**手順と食い違ったら本書を直す。**

| 実施日 | 実施者 | 環境 | 対象 | 結果 | 本書との食い違いと直した箇所 |
| --- | --- | --- | --- | --- | --- |
| （未実施） | — | — | — | — | 2026-09-28 時点で一度も実施していない。稼働中のクラスタは利用者の検証環境であり、そこで回さない |
| 2026-10-10 | 利用者 | Windows の Git Bash（ネイティブの `jq` 1.8.2）から経路 B のクラスタへ | 手順 1 の 1-1〜1-3（群 1 の 1 クライアント） | 回すところまで成功した。値は一度も表示していない。中断した試行で保管先の版が 1→6 まで進んだ | 部品の旧い形が Git Bash で 5 点壊れたので、部品を移植できる形へ直した（[Windows（Git Bash）での注意](#windowsgit-bashでの注意)）。1-3 を「新しい client の表現を組み立てて確かめてから保管先へ書く」順へ並べ替えた。0-b に「控えるのは最初の試行の前の版」を足した。直した後の部品は Windows では未実行（Linux の bash で論理だけ確かめた） |

## 限界（この手順で担保できないこと）

- 🔴 **通しのリハーサルは未実施。** 手順 1 の 1-1〜1-3（管理 API の要求の形〔client の表現を読み `secret` を差し替えて `PUT`〕を含む）は 2026-10-10 に Windows（Git Bash）で通った。手順 2 と、途中で止まったときの戻し方は確かめていない。直した後の部品は Windows では未実行である。
- 🔴 **重ねられない。** 相手を書いてから作り直しが終わるまで、その資格情報を使う処理は失敗する。
- 🔴 **手動の Secret 作成は塞いでいない**（[前提](#回した値が戻らないこと前提)）。起動の後に同期を促すのは人の手順である。
- **本番の手順ではない。** 本番の保管先（unseal・監査・HA）と認証基盤の運用が決まったら、本書を本番向けに書き直す。
- **記録は人が書く。** 書かなければ残らない（保管先の監査の取り込みが済むまで）。
