# Keycloak realm の追随（FR-05 / NFR-09 / ADR-0004・ADR-0026・IADR-0369・#1088 / #324）

> 起点: [IADR-0369](../../../.ai-context/adr/IADR-0369_persist-by-default-and-realm-reconcile-job.md) /
> 作業仕様書 [`.ai-context/specs/20260904_issue-1088_persist-by-default-and-realm-reconcile.md`](../../../.ai-context/specs/20260904_issue-1088_persist-by-default-and-realm-reconcile.md)

`reconcile-realm.sh` は **realm JSON（宣言）と稼働 realm の差分を Admin REST API で当てる**冪等な runtime 後追いである。
`scripts/k8s-local-up.sh` が**既定で**（[4/7] の Keycloak の rollout の直後・[6/7] の helm upgrade の前に）呼び、失敗したら up を止める
（IADR-0523 / #1846。サービスが audience `platform-api` を求めるので、realm が先に追随していないと全 API が 401 になる）。

```sh
bash deploy/local/keycloak-setup/reconcile-realm.sh           # 差分を当てる（apply）。単独でも何度でも実行できる
bash deploy/local/keycloak-setup/reconcile-realm.sh --check   # 差分を数えるだけ（書き換えない）。1 件でも在れば exit 1
bash deploy/local/keycloak-setup/reconcile-realm.sh --check-dev-secrets  # サービスアカウントを持つ機密クライアントの secret が dev の値のままなら名指して exit 1（#1830・IADR-0572）
```

## dev 以外のクラスタでの守り（NFR-18 / ADR-0124 決定 1 / [IADR-0517](../../../.ai-context/adr/IADR-0517_dev-client-secret-guard-kube-context-allowlist.md) / #1830）

- **対象**: realm の宣言でサービスアカウントを持つ機密クライアント全部（宣言が `secret` を持つもの。手で並べず宣言から導出する。
  当初の 3 つ `identity-admin`・`reset-gate`・`mcp-client-admin` から広げた。[IADR-0572](../../../.ai-context/adr/IADR-0572_dev-secret-guard-all-service-account-clients.md)）。
- **作らせない**: 無い client は宣言の secret（公知の dev の値）で作られる。対象のクライアントについて、本スクリプトは kube context を `scripts/lib/dev-client-secret-guard.sh`（`k8s-local-up.sh`・
  Vault の種の `bootstrap.sh` と同じ判定器）で判定し、Job の env `DEV_CLIENT_SECRETS_ALLOWED` へ `allow` / `deny` を渡す
  （許可集合 `k3d-*`・`kind-*`・`rancher-desktop`・`docker-desktop`、または `ALLOW_DEV_CLIENT_SECRETS=1`。マニフェストの既定は `deny`）。
  `deny` で対象のどれかを作る計画があれば、その realm には何も書かずに名指して非 0 で終える。
- **検知する**: `--check-dev-secrets` は別名の Job（`keycloak-realm-dev-secret-check`・`RECONCILE_MODE=check-dev-secrets`）で、
  対象の稼働の secret を読み、宣言の値と一致するものを `dev-secret <client>` と名指して非 0。読むだけで書かず、値は出さない。
  Keycloak 本体の初回 import（空 PVC）が作る dev の値はこちらで拾う。回し方は `docs/operations/paired-secret-rotation-runbook.md`。

## なぜ要るか —— `--import-realm` は同名 realm が在ると黙って飛ばす

Keycloak は `start-dev --import-realm` で立つ。**同名 realm が既に在ると import は飛ばされる**（`IGNORE_EXISTING`。
エラーも警告も出ない）。永続化が既定（IADR-0369 決定 1）になり realm が PVC に残るようになった今、
**`deploy/keycloak/microservices-platform-realm.json` を直しても稼働 realm は変わらない**のが通常状態である。
かといって `OVERWRITE_EXISTING` は realm を丸ごと作り直し、永続化で守りたい runtime state（TOTP 資格情報・
追加利用者・セッション）を毎回消す。両立する形が「静的 import（空 PVC のときだけ）＋ 起動後に差分を当てる」である。

## 何をするか

| # | 段 | 中身 |
| --- | --- | --- |
| 0 | 前提 | `platform-infra/keycloak` Deployment の存在を確かめる（無ければ apply は何もしない。`--check` は失敗） |
| 1 | スクリプト本体 | `reconcile-realm.js` を ConfigMap `keycloak-realm-reconcile` にする（毎回上書き＝リポジトリの版が正） |
| 2 | Job | 前回の Job を消し、`realm-reconcile-job.yaml` を apply する（Job は immutable）。`--check` は Job 名と `RECONCILE_MODE` を差し替える |
| 3 | 完了待ち | `Complete` / `Failed` のどちらかが立つまで見る（既定 300 秒。`RECONCILE_JOB_TIMEOUT`） |
| 4 | ログ | Job のログを出す。**値（secret・パスワード）は出さない**（計画は操作の種類と対象と理由だけを出す） |

Job（`node:22-alpine`・`platform-infra`）は **ConfigMap `keycloak-realms`**（起動器 [3/7] が実 realm ファイルから作る＝
単一情報源。AST realm も同梱）を `/import` に、Secret `keycloak-admin`（`username` / `password`）を env に読み、
`http://keycloak:8080` を叩く。**エッジ・TLS・メッシュのどれにも依存しない。**

🔴 **Keycloak pod で `kcadm.sh` を exec しない。** 別 JVM がコンテナのメモリ制限を食い潰し、本体が OOMKilled で
再起動する（2026-09-02 実測）。旧 `reconcile-backchannel-logout.sh` はその経路だったので撤去した。

## 境界 —— 宣言が所有する層と、実行時が所有する層

| 層 | 対象 | 扱い |
| --- | --- | --- |
| 宣言（realm JSON が正） | realm の非コレクション設定 / `requiredActions` / realm・client ロール / グループ / client scopes ＋ mappers / clients（属性・redirect・scope 割当 ＋ mappers）/ **seed 利用者の存在** / サービスアカウント利用者のロールと属性 | 差分があれば当てる。集合欄は宣言が全集合、実体は加算的（余剰は消さない） |
| 宣言（**作成時だけ**） | client の `secret`（開発用の値） | client が無いとき（と realm が無いとき）にだけ宣言の値で作る。**既存の client では比べず・書かず・読まない** |
| 実行時（Keycloak / SC-17 / 本人が正） | 既存の人間の利用者の資格情報・属性・ロール・グループ・`requiredActions`・セッション／**既存の client の `secret`** | **触らない**（例外は次の行） |
| 宣言（**加えるだけ**。#1894 / IADR-0546） | 既存の人の seed 利用者の `account` の `view-profile`・`manage-account`（`SEED_USER_SELF_SERVICE_ROLES`） | 実効の割当に無ければ加える。外さない。他のロールは触らない |
| 宣言（**作るときだけ・差し込めたときだけ**。#2024 / IADR-0570） | 試験アカウント `e2e-*` の資格情報（宣言はプレースホルダ `${E2E_…}`。値は Secret `e2e-accounts` を Job が `envFrom` で読む） | 差し込めない利用者は作らない（`skipped` の行。drift にしない）。作った後は実行時が所有する |
| Keycloak（**自動付与**。#2208 / IADR-0618） | サービスアカウントを持つ client（宣言の `serviceAccountsEnabled: true`）の既定スコープ `service_account`（Keycloak 26 が client の作成・更新のたびに足す。`KEYCLOAK_AUTO_ASSIGNED_CLIENT_SCOPES`） | 稼働側にあっても余剰と見なさない（外さない）。宣言には書かない（`scripts/check-realm-constraints.js` の検査 11）。それ以外の余剰スコープは従来どおり外す |

> **［2026-09-28 / #1682 / ADR-0124 決定 1・IADR-0485］client の `secret` を「作成時だけ」へ移した。**
> client シークレットは認証基盤と Vault を同時に変えないと成立しない「対になる秘密」であり、本番の値を realm の宣言（Git）に置かない。
> 従前は宣言の値（開発用）へ当て直したため、**対で回した値を次の起動で認証基盤側だけ戻していた。** 回し方は
> [対になる秘密のローテーション](../../../docs/operations/paired-secret-rotation-runbook.md)。宣言の値が開発用の形であることは
> `scripts/check-realm-constraints.js` の検査 8 が見る。
| 門（`reset-gate` が正。**条件つき**） | `resetPasswordAllowed` —— ただし除くのは「**宣言 true・稼働 false・`attributes["reset-gate.state"]==="closed"`**」の 1 組だけ | その 1 組のときだけ差分から除く |

> **［2026-09-06 / #1245］`smtpServer` は実行時所有から宣言所有へ移った。** 送出先はクラスタ内の
> 近接 MTA（`deploy/mail-relay/`）へ固定され、秘匿値は relay 側の Secret にある。`RUNTIME_OWNED_REALM_KEYS`
> は**空集合**である。宣言に無いキー（`user` / `password`）は消さない。
> 🔴 `resetPasswordAllowed` を無条件の実行時所有にしてはならない —— 「**宣言 false なのに稼働 true**」
> （閉じたはずの申請が開いている）という**危険な向きの drift** まで見なくなる。

> **［2026-10-15 / #1894 / IADR-0546］アカウントコンソール（SC-16）の構成。** realm import では組み込みの `account-console` に
> スコープが付かず、import で作る利用者は既定ロールを受け取らないので、SC-16 の API が 401 / 403 になっていた。realm JSON に
> `account` と `account-console` を対で宣言し（片方だけだと import が落ちる）、seed 利用者へ `account` の 2 つのロールを付けた。
> 既存の realm には本スクリプトが当てる（`--check` で `account` / `account-console` の差分と seed 利用者 4 人の `user.clientRoles.add` が出る）。

### 既存の realm へ SC-16 の是正を当てる（PoC）

通常は `scripts/k8s-local-up.sh` が毎回本スクリプトを呼ぶので、**最新の develop で up を流せば当たる**。単独で当てるとき:

```sh
bash deploy/local/keycloak-setup/reconcile-realm.sh --check   # 期待: account / account-console の差分と user.clientRoles.add（seed 利用者 4 人）
bash deploy/local/keycloak-setup/reconcile-realm.sh           # 当てる（冪等。2 回目は「一致（差分なし）」）
bash deploy/local/keycloak-setup/reconcile-realm.sh --check   # 期待: realms=<n> drift=0
```

Job を使えないとき（クラスタ外の Keycloak など）の最小の手当ては kcadm.sh で行える。**Keycloak の pod で exec しない**（上の 🔴）。
port-forward した先へ、手元の別コンテナの kcadm.sh から当てる。何度流しても同じ状態になる（写像は名前で有無を見てから作る・割当の PUT は冪等）。
後で本スクリプトを流すと、残りの差分（`account` / `account-console` の説明と `account` の既定スコープ）も宣言へ揃う。

```sh
kubectl -n platform-infra port-forward svc/keycloak 8080:8080 &
KCDIR=$(mktemp -d) && chmod 777 "$KCDIR"   # kcadm の資格情報の置き場（リポジトリの外。コンテナの利用者が書ける）
KCADM="docker run --rm --network host -v $KCDIR:/opt/keycloak/.keycloak --entrypoint /opt/keycloak/bin/kcadm.sh quay.io/keycloak/keycloak:26.7.4"
$KCADM config credentials --server http://localhost:8080 --realm master --user "$KC_ADMIN_USER" --password "$KC_ADMIN_PASSWORD"
AC=$($KCADM get clients -r platform -q clientId=account-console --fields id --format csv --noquotes)
for s in basic profile; do
  SID=$($KCADM get client-scopes -r platform --fields id,name --format csv --noquotes | awk -F, -v n="$s" '$2==n{print $1}')
  $KCADM update "clients/$AC/default-client-scopes/$SID" -r platform
done
$KCADM get "clients/$AC/protocol-mappers/models" -r platform --fields name --format csv --noquotes | grep -qx 'account client roles' || \
  $KCADM create "clients/$AC/protocol-mappers/models" -r platform \
    -s name='account client roles' -s protocol=openid-connect -s protocolMapper=oidc-usermodel-client-role-mapper \
    -s 'config."usermodel.clientRoleMapping.clientId"=account' -s 'config."claim.name"=resource_access.${client_id}.roles' \
    -s 'config."jsonType.label"=String' -s 'config.multivalued=true' -s 'config."access.token.claim"=true' \
    -s 'config."id.token.claim"=false' -s 'config."userinfo.token.claim"=false' -s 'config."introspection.token.claim"=true'
for u in admin poc-user poc-operator developer; do
  $KCADM add-roles -r platform --uusername "$u" --cclientid account --rolename view-profile --rolename manage-account
done
```

確かめ方: ブラウザで `https://<host>/realms/platform/account/` を開いてログインし、個人情報（メールアドレスが入っている）・
アカウントセキュリティ → サインイン・デバイスアクティビティが「何らかの問題が発生しました」にならないこと。開発者ツールで
`/realms/platform/account/?userProfileMetadata=true`・`/supportedLocales`・`/credentials`・`/sessions/devices` が 200 であること
（是正前は 401）。

seed 利用者の宣言（例: `requiredActions`）を変えて既存クラスタへ届けたいときは、破壊経路を使う:
`kubectl -n platform-infra delete pvc keycloak-data && kubectl -n platform-infra rollout restart deploy/keycloak`
（空 PVC へ再 import。実行時状態は失われる）。

## 収束と門

`apply` は「計画 → 適用 → 再計画」を最大 3 周し、最後の計画が 0 件でなければ非 0 で終える。`--check` は計画だけ。
標準出力の最終行 `realms=<n> drift=<m> applied=<k>` を **`scripts/check-stack-ready.js` の G9** が読む（fail-closed）。
up.sh からの呼び出しは **fail-closed**（失敗したら helm へ進まず非 0 で止まる。IADR-0523 / #1846。旧: best-effort の WARN）。稼働の門は引き続き G9 が持つ。

計画器（`plan(desired, live)`）は純粋関数で、`node scripts/keycloak-realm-reconcile.test.js` が
「一致なら 0 件」「差分の種類ごとに 1 件」「実行時層には触れない」「前提が無い操作は deferred として残る」を固定する。
