---
title: 運用 Runbook — 対になる秘密のローテーション（相手と保管先を対で書く）
type: runbook
status: draft
author: claude
created: 2026-09-28
updated: 2026-10-09
---
<!-- trace:
ids: [SC-22, NFR-18, SC-12, FR-16]
adrs: [ADR-0124, ADR-0095, ADR-0005, ADR-0023, ADR-0123]
iadrs: [IADR-0516, IADR-0492, IADR-0485, IADR-0369, IADR-0433, IADR-0453, IADR-0456, IADR-0092, IADR-0133]
specs: [20261009_1818_sc12-idp-drift-detection, 20261009_1817_sc12-provisioning-wiring, 20260928_issue-1682_paired-secrets-outside-sc22, 20260925_458_secret-rotation-runbook]
issues: [#1818, #1817, #1696, #1682, #458, #1411, planning#700, AST#1078]
-->

# 運用 Runbook: 対になる秘密のローテーション

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 秘密情報のローテーション全体の入口は [`secret-rotation-runbook.md`](secret-rotation-runbook.md) であり、本書はそのうち
> **製品の画面では回せない「対になる秘密」**の手順を持つ。対象は経路B（ローカル k8s。`VAULT=1 ESO=1`）である。**本番の供給経路は未配備**であり、本書は本番の手順ではない。
>
> **値そのものは本書にもリポジトリのどこにも置かない。** 手順は最後まで値を画面へ出さない。
>
> 🔴 **本書の手順は稼働環境で一度も実行していない（リハーサル未実施）。** 末尾「リハーサル記録」を参照。

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
| 認証基盤の realm の宣言（`deploy/keycloak/microservices-platform-realm.json`） | 宣言の client の `secret` は**開発用の値**だけであり、**client を新しく作るときにだけ**使う。宣言の追随（`deploy/local/keycloak-setup/reconcile-realm.sh`）は既存の client の `secret` を比べず・書かない。起動時の import は realm が在れば飛ばす |
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
| 必要なツール | `kubectl`・`curl`・`jq`・`openssl`（新しい値の生成）。**ホストに `vault` CLI は不要**（Vault Pod 内で実行する） |
| 前提の状態 | Vault と External Secrets Operator が稼働し、Vault は永続化されている（file ストレージ＋PVC） |
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

以下はすべて **bash** で実行する（`read -s` は bash の機能）。

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

**0-e. Secret を読む消費側を引く**（名前を書き写さずに引く。`env`・`envFrom`・ボリュームのどれで読んでいても当たる）:

```bash
kubectl get deploy -A -o json | jq -r --arg s "<secret>" '.items[]
  | select([.spec.template.spec.containers[]? | (.env[]?.valueFrom.secretKeyRef.name), (.envFrom[]?.secretRef.name)]
           + [.spec.template.spec.volumes[]?.secret.secretName] | index($s))
  | "\(.metadata.namespace)/\(.metadata.name)"'
```

書き終えたら `unset NEW_VALUE`。

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

```bash
kubectl -n platform-infra port-forward deploy/keycloak 18080:8080 >/dev/null 2>&1 &
PF_PID=$!
KC=http://127.0.0.1:18080
KC_ADMIN_USER="$(kubectl -n platform-infra get secret keycloak-admin -o jsonpath='{.data.username}' | base64 -d)"
TOKEN="$(kubectl -n platform-infra get secret keycloak-admin -o jsonpath='{.data.password}' | base64 -d \
  | jq -Rr --arg u "$KC_ADMIN_USER" '"grant_type=password&client_id=admin-cli&username=\($u|@uri)&password=\(.|@uri)"' \
  | curl -sf -X POST "$KC/realms/master/protocol/openid-connect/token" \
      -H 'Content-Type: application/x-www-form-urlencoded' --data-binary @- | jq -r .access_token)"
[ -n "$TOKEN" ] && [ "$TOKEN" != null ] && echo "token ok"
```

🔴 **Keycloak の Pod で `kcadm.sh` を exec しない**（別 JVM が本体を OOMKilled にする）。管理操作は管理 API で行う。

### 1-2. 対象の client を引く

```bash
CLIENT=<client>   # 例: bff
CID="$(curl -sf -H "Authorization: Bearer $TOKEN" "$KC/admin/realms/platform/clients?clientId=$CLIENT" | jq -r '.[0].id')"
[ -n "$CID" ] && [ "$CID" != null ] && echo "client ok"
```

### 1-3. 回す

1. **0-a** で新しい値を作る。
2. **0-b** で保管先の直前の版を控える（`<path>` は 1-0 の表）。
3. **0-c** で保管先へ書く。この時点では消費側は旧の値を持っているので、何も壊れない。
4. **認証基盤の client の `secret` を同じ値にする**（client の表現を読み、`secret` だけを差し替えて戻す。値は標準入力で渡す）:

   ```bash
   curl -sf -H "Authorization: Bearer $TOKEN" "$KC/admin/realms/platform/clients/$CID" \
     | jq --rawfile s <(printf '%s' "$NEW_VALUE") '.secret = $s' \
     | curl -sf -o /dev/null -w '%{http_code}\n' -X PUT -H "Authorization: Bearer $TOKEN" \
         -H 'Content-Type: application/json' --data-binary @- "$KC/admin/realms/platform/clients/$CID"
   ```

   `204` が出れば書けている。🔴 **ここから、旧の値を持つ消費側は `invalid_client` になる。5・6 を続けて行う。**
5. **0-d** で同期を促す（1-0 の ExternalSecret）。
6. 消費側を作り直す（**0-e** で引いた Deployment を `kubectl -n <ns> rollout restart deploy/<name>`、`rollout status` で待つ）。
   `vault` / `wiki-js` は 1-0 の表のスクリプトを再実行する。
7. **確かめる**（[確認](#確認この手順が成功したと言える条件)）。
8. 終えたら `unset NEW_VALUE TOKEN; kill "$PF_PID"`。**[記録](#記録)する。**

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

🔴 **dev 以外のクラスタでは、起動の直後に回す**（漏えいが無くても）。起動器は env が無ければ dev の値（リポジトリに公開されている）で
保管先と Secret を作り、レルムの後追いも無い client を dev の値で作る。`identity-admin`・`reset-gate` も同じである。
dev 以外の文脈で dev の値を拒む機械の守りは起動器に無い（後続の作業で入れる）。

**手順**（通常の 1-3 に次を足す）:

1. **先に回す**（1-0 の行・1-3 の手順そのまま）。回した時点で旧い secret ではトークンが出なくなる（既に出ているトークンは有効期限まで残る）。
2. **全クライアントの secret が読まれた前提に立つ。** 1-0 の表の**ほかの行もすべて**回す（群 1 の全件）。読まれたかどうかは、監査の取り込みが無い間は判定できない。
3. **書き換えの痕跡を探す。** 認証基盤の管理イベント（このクライアントのサービスアカウントが主体のもの**と**、`reset-gate`・`identity-admin` のサービスアカウントが主体のもの）を、漏えいの疑いの期間について引く。利用者の属性・ロールの変更、クライアントの作成・削除、レルムの設定の変更（認証フロー・送信設定・総当たり対策）があれば、登録簿と宣言に照らして戻す（レルムの設定は後追いの check モードで宣言との差分を見る）。
4. MCP クライアント登録管理の画面の無人の行と、認証基盤の入口の印つきのクライアントを突き合わせる。［2026-10-09］MCP サーバーの定期の照合が 1 分ごとに突き合わせる（[運用仕様書](operations.md)「MCP クライアント登録簿と認証基盤の照合」）。回した後の照合で警報 `McpClientIdpDrift` が鳴っていないこと・MCP サーバーの Warning ログに `kind=attributes_differ` / `kind=orphan` が無いことを確かめる。**照合が見るのは登録簿と認証基盤の一致だけ**であり、登録簿の値そのものの正しさ（漏えい中に画面から書かれた値か）は見ないので、無人の行の属性は登録者に確かめる。
5. [記録](#記録)する。

🔴 **残余**: 認証基盤の現行の版（24.0）では、このクライアントの権限を「入口の印があるクライアントとそのサービスアカウント」へ絞れない（細粒度の管理権限の新しい版は 26.2 以降）。版を上げた後に絞る。

## 手順 2: 群 2（データストアの資格情報）

**ストアごとのコマンド（ストア側で何をするか・同期先・作り直す消費側）は [`secret-rotation-runbook.md`](secret-rotation-runbook.md) 手順 B-1 の表が正である。**
本書が持つのは順序と、途中で止まったときの戻し方である。

1. **0-a** で新しい値を作る（ストア側の対話入力で決める場合は、その値を `read -rs NEW_VALUE` で読む）。
2. **0-b** で保管先の直前の版を控える。**同値にすべき 2 つの KV**（`postgres-app` と `wikijs-db`、`rabbitmq` と `rabbitmq-app`）は**両方**控える。
3. **0-c** で保管先へ書く（同値の組は両方）。まだ何も壊れない。
4. **ストア側の値を同じ値にする**（B-1 の「ストア側で先に行うこと」）。🔴 **ここから、旧の値で新しく接続する処理が失敗する。**
5. **0-d** で同期を促し、B-1 の順で消費側を作り直す。
6. **確かめる。** 終えたら `unset NEW_VALUE`。**[記録](#記録)する。**

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
| 保管先へ書く前（0-a・0-b） | 何も変わっていない | `unset NEW_VALUE` で終える |
| 保管先へ書いた後・相手を書く前（群 1 の 3、群 2 の 3） | 保管先だけ新しい。消費側は旧の値を持ち、動いている | **保管先を控えた版へ戻す**: Vault Pod 内で `vault kv rollback -version=<控えた版> secret/<path>`（同値の組は両方）。**同期を促さない**（促すと消費側が新しい値を受け取って壊れる。促してしまったなら、戻した後にもう一度促す） |
| 相手を書いた後・作り直しの前（群 1 の 4 の後、群 2 の 4 の後） | 保管先と相手は新しい値で一致。消費側だけ旧い | **戻さない。前へ進める**（同期を促し、作り直す）。新しい値を失っていても、保管先に在る |
| 相手の書き込みが失敗した・成否が分からない | 保管先は新しい。相手は不明 | 群 1: 1-2 の client を読み直し、`secret` の長さ（`jq -r '.secret | length'`）が新しい値と同じなら書けている → 前へ進める。違えば保管先を控えた版へ戻す（上の 2 段目）。群 2: 新しい値でストアへ接続を試し、通れば前へ進める。通らなければ保管先を戻す |
| 作り直しの後に動かない | 値は一致しているはず | 同期が `Ready` か・Secret の長さが一致するかを見る。一致しているのに動かなければ、相手を旧の値へ戻す: 保管先の控えた版の値を変数へ読み（`vault kv get -version=<控えた版> -field=<property> secret/<path>` を変数に受ける。表示しない）、群 1 は 1-3 の 4、群 2 はストア側の手順でその値を書き、保管先を `rollback` し、同期と作り直しをやり直す |

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

## 限界（この手順で担保できないこと）

- 🔴 **リハーサル未実施。** 管理 API の要求の形（client の表現を読み `secret` を差し替えて `PUT`）も、稼働環境では確かめていない。
- 🔴 **重ねられない。** 相手を書いてから作り直しが終わるまで、その資格情報を使う処理は失敗する。
- 🔴 **手動の Secret 作成は塞いでいない**（[前提](#回した値が戻らないこと前提)）。起動の後に同期を促すのは人の手順である。
- **本番の手順ではない。** 本番の保管先（unseal・監査・HA）と認証基盤の運用が決まったら、本書を本番向けに書き直す。
- **記録は人が書く。** 書かなければ残らない（保管先の監査の取り込みが済むまで）。
