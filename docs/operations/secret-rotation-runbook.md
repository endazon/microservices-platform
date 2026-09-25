---
title: 運用 Runbook — 秘密情報のローテーション（保管先に置いた資格情報・API キーを新しい値へ回す）
type: runbook
status: draft
author: claude
created: 2026-09-25
updated: 2026-09-25
---
<!-- trace:
ids: [NFR-18, SC-22]
adrs: [ADR-0005, ADR-0023, ADR-0095]
iadrs: [IADR-0096, IADR-0097, IADR-0098, IADR-0099, IADR-0327, IADR-0369, IADR-0433, IADR-0453, IADR-0456, IADR-0457]
specs: [20260925_458_secret-rotation-runbook]
issues: [#458, #1411, #1477]
-->

# 運用 Runbook: 秘密情報のローテーション

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 対象は経路B（ローカル k8s。`VAULT=1 ESO=1` で立ち上げ、保管先 Vault から External Secrets Operator が
> Secret へ同期する構成）である。**本番の供給経路は未配備**であり、本書は本番の手順ではない。
>
> **値そのものは本書にもリポジトリのどこにも置かない。** 手順は最後まで値を画面へ出さない。
>
> 🔴 **本書の手順は稼働環境で一度も実行していない（リハーサル未実施）。** 末尾「リハーサル記録」を参照。

## 一目でわかる結論

保管先に入っている秘密は **3 つに分かれ、回し方がまったく違う。** 分類の単一情報源は
`deploy/bootstrap/sc22-secret-items.json` である（`items[]` / `excluded[]` / `deferred[]`）。

| 分類 | 何か | 回せるか | 手順 |
| --- | --- | --- | --- |
| `items[]`（6 項目） | 外部の発行元がある値 —— 外部 LLM の API キー・メール送信のアプリパスワード・Wiki.js の API キー・取引ユニットの外部 API キー / Discord / 証券会社ログイン / OpenD の RSA 鍵 | ✅ **回せる** | [手順 A](#手順-a-items画面から回す)（製品の画面から） |
| `excluded[]`（7 項目） | データストアの資格情報 —— `postgres` / `postgres-app` / `rabbitmq` / `rabbitmq-app` / `keycloak-admin` / `minio-credentials` / `wikijs-db` | 🟡 **ストア側と同時なら回せる** | [手順 B](#手順-b-excludedストア側と同時に回す)（コンソール。未実測） |
| `deferred[]`（18 項目） | 認証基盤（Keycloak）のクライアントシークレット —— OIDC クライアント 9・サービス間 9 | 🔴 **いまは恒久的には回せない** | [手順 C](#手順-c-deferredいまは回せない理由と回すための前提) |

🔴 **どの分類でも、`scripts/k8s-local-up.sh` の再実行は回した値を元へ戻し得る。** 戻す経路は 3 つある
（[回した値を元へ戻す経路](#回した値を元へ戻す経路)）。**回した後の運用まで含めて 1 つの手順である。**

## この手順を実行する条件（いつ走らせるか）

**周期は計画に定められていない。** 本書も周期を定めない。次のいずれかで実行する。

- **漏洩の疑い**（ログ・画面共有・リポジトリ・チャットへの露出、端末の紛失）。このときは新旧の重なりを待たずに旧を失効させてよい。
- **その値を知っている担当者の離任。**
- **発行元が失効・再発行を求めた**（外部サービスの通知、期限切れ —— Wiki.js の API キーは発行時に有効期限を持つ）。
- **開発用の既定値のまま運用している値を、実運用の値へ差し替える**（最初の 1 回もローテーションと同じ手順で行う）。

**実行してはいけない場合**:

- 環境を新しく立ち上げるとき。それは初期投入であり、`scripts/k8s-local-up.sh` の通常経路が担う。
- 画面が使えないときに `items[]` の 1 項目を急ぎ差し替えたいだけのとき。それは
  [`secret-item-console-injection-runbook.md`](secret-item-console-injection-runbook.md)（退避手段）である。
- `deferred[]` の項目を単独で回すとき（[手順 C](#手順-c-deferredいまは回せない理由と回すための前提)）。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | 手順 A: 製品の画面（`/admin/secrets`）を開けるロール（運用者・システム管理者）。手順 B: 対象クラスタへの `kubectl exec`（`platform-infra`）と、対象名前空間の Secret / ExternalSecret / Deployment への読み書き |
| 必要なツール | 手順 A: ブラウザ。手順 B: `kubectl` のみ（**ホストに `vault` CLI は不要**。すべて Vault Pod 内で実行する） |
| 前提の状態 | Vault と External Secrets Operator が稼働している。Vault は既定で永続化されている（file ストレージ＋PVC） |
| 所要時間の目安 | 手順 A: 1 項目 5〜10 分（発行元での操作を除く）。手順 B: 1 ストア 15〜30 分（消費側の再起動を含む。**その間サービスが断続的に止まる**） |

## 共通の原則

1. **新旧を重ねてから旧を消す。** 発行元で新しい値を発行 → 保管先へ投入 → 消費側が新しい値で動くことを確かめる → **最後に**旧を失効させる。
   逆順にすると、確かめる前に動かなくなる。**発行元が再発行と同時に旧を失効させる種別**（Discord の bot token など）は重ねられない —— 投入までの間は止まると見込む。
2. **値を画面にも引数にも出さない。** `read -rs` で読み込み、コマンドには標準入力で渡す（`キー名=-`）。シェル履歴・`ps`・スクロールバック・画面共有に残さない。
3. **KV 全体を置き換えない。** コンソールでは `vault kv patch -method=patch` を使い、`vault kv put` を使わない（同居するプロパティが消える）。
4. **確かめるのは長さだけ。** 値は表示しない。空の値でも同期は成功するので、「同期が Ready」だけを成功としない。
5. **回した事実を残す。** 画面から書いた更新は監査ログに残る。コンソールで行った操作は残らないので、[記録](#記録)に従って書く。

## 回した値を元へ戻す経路

`scripts/k8s-local-up.sh` を再実行すると、次の 3 つが値を書き直す。

| 経路 | 何をするか | 影響する分類 |
| --- | --- | --- |
| 手動の Secret 作成（`apply_secret`） | env が無ければ**開発用既定値**で Secret を作る。`postgres` / `rabbitmq` / `keycloak-admin` は `ESO=1` でも作る（同期は `Merge` で上書きするだけ） | `excluded[]` |
| 保管先の再投入（`deploy/local/vault/eso/bootstrap.sh`） | 画面が書く KV（`items[]` のうち種を入れる 4 つ）は**無いときだけ**作り、在れば env が空でないプロパティだけ差し替える。**それ以外の 24 KV は毎回、env か開発用既定値で全置換する** | `excluded[]` と `deferred[]` |
| 認証基盤の宣言の追随（`deploy/local/keycloak-setup/reconcile-realm.sh`） | realm JSON を正として稼働中の realm へ差分を当てる。**クライアントの `secret` も含む** | `deferred[]` |

- **`items[]` は戻らない**（画面で入れた値は再投入で消えない）。
- **`excluded[]` は env で新しい値を渡し続ける限り戻らない。** env 名は `deploy/local/README.md` の「機密情報」表にある
  （`PG_PASSWORD` / `APP_DB_PASSWORD` / `WIKIJS_DB_PASSWORD` / `RABBITMQ_USER` / `RABBITMQ_PASSWORD` / `KEYCLOAK_ADMIN_PASSWORD` / `MINIO_ACCESS_KEY` / `MINIO_SECRET_KEY`）。
  🔴 **渡し忘れると、保管先と Secret だけが既定値へ戻り、ストア側は新しい値のまま残る —— 認証が壊れる。**
- **`deferred[]` は env を渡しても戻る**（3 つ目の経路が realm JSON の値へ当て直す）。[手順 C](#手順-c-deferredいまは回せない理由と回すための前提)。

## 手順 A: `items[]`（画面から回す）

画面から書いた更新は**監査ログに残り**、書き込みが成立すると境界層が同期先の ExternalSecret へ即時同期を依頼し、
Stakater Reloader が注釈を持つ消費側（外部 LLM の境界サービス・Wiki 同期サービス・メールの近接 MTA。取引ユニットの消費側は取引ユニットのチャート）を作り直す。

1. **発行元で新しい値を発行する。** 旧はまだ失効させない（原則 1）。
2. **画面（秘密情報・接続設定の管理）で、その項目のプロパティを 1 つずつ書く。** 画面の仕様は [秘密情報・接続設定の管理](../screens/SC-22_secret-item-management.md)。
   画面が「即時同期を依頼しました」と出すことを確かめる。「依頼できませんでした」と出たら
   [`secret-item-console-injection-runbook.md`](secret-item-console-injection-runbook.md) の手順 4（同期を促す）を手で行う。
3. **反映を確かめる**（[確認](#確認この手順が成功したと言える条件)）。消費側が作り直され、機能が新しい値で動いていること。
4. **発行元で旧の値を失効させる。**
5. **記録する**（画面の監査ログに残るので、発行元での失効の日時だけを[記録](#記録)に書く）。

項目ごとの注意:

| 項目 | 発行元 | 注意 |
| --- | --- | --- |
| `llm-provider-credentials`（`anthropic-api-key` / `openai-api-key`） | 各 LLM プロバイダの管理画面 | 漏洩が即座に金銭へ変わる。漏洩の疑いなら 4 を先に行ってよい（その間は外部 LLM を使う機能が縮退応答になる） |
| `keycloak-smtp`（`from` / `user` / `password`） | 組織のメールテナント | 🔴 `host` / `port` / `starttls` は構成であり画面では書けない（Git 経由）。**認証基盤は作り直さない**（読むのは近接 MTA）。送信元アドレスを変えるときは [`keycloak-smtp-relay-setup-runbook.md`](keycloak-smtp-relay-setup-runbook.md) も見る |
| `wikijs-sync`（`apiKey`） | Wiki.js の管理画面（Administration → API Access） | 新しいキーを発行 → 画面で書く → 同期が通ることを確かめる → **旧キーを Wiki.js 側で Revoke する**。キーは Wiki.js の管理 GraphQL 全体に及ぶので、漏洩の疑いなら即時 Revoke する |
| `ast-app-secrets`（外部 API キー・Discord） | 各外部サービス | 🔴 **Discord の bot token は再発行した瞬間に旧が失効する**（重ねられない）。同じ KV の `*-auth-client-*` は画面では書けない（`deferred[]` と同じ扱い） |
| `ast-moomoo`（`login-account` / `login-pwd-md5`） | 証券会社 | パスワードは画面へ平文で入れ、境界層が MD5 へ変換して書く。証券会社側で変えてから画面で書く。ログインのやり直し（検証コード）が要ることがある —— 取引ユニットの手順に従う |
| `ast-moomoo-rsa`（`opend_rsa.pem`） | 画面の「生成」 | 🔴 **生成し直すと、OpenD に登録済みの鍵との対応が失効する。** 取引ユニットの手順で OpenD 側の登録を合わせてから行う |

## 手順 B: `excluded[]`（ストア側と同時に回す）

`excluded[]` は画面の対象外である。**稼働中のデータストアが既存の値で初期化済み**であり、保管先だけ書き換えると
同期が誤った資格情報を配って認証が壊れる。**ストア側の値を先に変え、同じ作業の中で保管先・Secret・消費側を合わせる。**

🔴 **本節は未実測である。** ストアごとの挙動（特に MinIO のルート資格情報の変更）は、最初のリハーサルで確かめて本書を直すこと。

### B-0. 共通の部品

値を読み込む（原則 2）:

```sh
read -rs -p "new value: " NEW_VALUE && echo
```

保管先のプロパティを 1 つ書く（`<path>` と `<property>` を置き換える）:

```sh
kubectl -n platform-infra exec -i deploy/vault -- sh -c '
  export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"
  vault kv patch -method=patch secret/<path> <property>=-
' <<EOF
$NEW_VALUE
EOF
```

同期を促す（同期の既定間隔は 1 時間）:

```sh
kubectl -n <namespace> annotate externalsecret <name> force-sync="$(date +%s)" --overwrite
```

書き終えたら `unset NEW_VALUE`。**以後 `scripts/k8s-local-up.sh` を再実行するときは、表の env に新しい値を必ず渡す**（[回した値を元へ戻す経路](#回した値を元へ戻す経路)）。

### B-1. ストアごとの手順

| 対象（保管先のパス） | ストア側で先に行うこと | 保管先に書くもの | 同期を促す ExternalSecret | 作り直す消費側 | 以後の env |
| --- | --- | --- | --- | --- | --- |
| DB 管理者（`msp/postgres`） | `kubectl -n platform-infra exec -it deploy/postgres -- psql -U postgres -c '\password postgres'`（対話で 2 回入力。値は引数にもログにも出ない） | `msp/postgres` の `password` | `platform-infra/postgres` | 無し（DB の初期化時にしか読まれない） | `PG_PASSWORD` |
| アプリの DB 利用者 `kp`（`msp/postgres-app` と `msp/wikijs-db`） | `kubectl -n platform-infra exec -it deploy/postgres -- psql -U postgres -c '\password kp'` | 🔴 **`msp/postgres-app` と `msp/wikijs-db` の両方の `password`**（同じ DB 利用者 `kp` を共有している） | `microservices-platform/postgres-app` と `microservices-platform/wikijs-db` | `microservices-platform` の Deployment すべて（DB を使う各サービスと `wiki-js`） | `APP_DB_PASSWORD` と `WIKIJS_DB_PASSWORD`（**同値**） |
| ブローカ（`msp/rabbitmq` と `msp/rabbitmq-app`） | 無し（ブローカは永続化しておらず、再起動時に Secret の値で利用者を作り直す） | 🔴 **`msp/rabbitmq` の `password` と `msp/rabbitmq-app` の `password` を同値で** | `platform-infra/rabbitmq` と `microservices-platform/rabbitmq-app` | `platform-infra` の `rabbitmq` を先に、次に `microservices-platform` の Deployment すべて | `RABBITMQ_PASSWORD`（利用者名も変えるなら `RABBITMQ_USER` と chart の `global.messaging.user`） |
| 認証基盤の管理者（`msp/keycloak-admin`） | 認証基盤の master realm の管理画面で `admin` のパスワードを変える（環境変数の管理者は初回起動時にしか使われず、以後は認証基盤の DB が持つ） | `msp/keycloak-admin` の `password` | `platform-infra/keycloak-admin` | 無し。確かめ方: `bash deploy/local/keycloak-setup/reconcile-realm.sh --check` が**認証エラーで落ちない**こと（宣言の追随 Job がこの Secret で管理 API へログインする） | `KEYCLOAK_ADMIN_PASSWORD` |
| オブジェクトストレージのルート（`msp/minio-credentials`） | 無し（ルート資格情報は起動時の環境変数で決まる）。🔴 **永続化済みのデータを持つ MinIO がルート資格情報の変更を受け入れることは未実測** | `msp/minio-credentials` の `accessKey` / `secretKey` | `microservices-platform/minio-credentials` | `minio` を先に、次に `microservices-platform` の Deployment すべて（各サービスが同じ資格情報で接続する） | `MINIO_ACCESS_KEY` / `MINIO_SECRET_KEY` |

作り直しのコマンド:

```sh
kubectl -n platform-infra rollout restart deploy/rabbitmq          # ブローカのときだけ、先に
kubectl -n platform-infra rollout status  deploy/rabbitmq --timeout=180s
kubectl -n microservices-platform rollout restart deploy           # 名前空間の Deployment をすべて
kubectl -n microservices-platform wait --for=condition=Available deploy --all --timeout=300s
```

🔴 **ブローカを回してはいけない場合**: 取引ユニットを同じクラスタに配備しているとき。取引ユニットのチャートは
ブローカの接続文字列に開発用の利用者名とパスワードを**直書きしている**（取引ユニットの `values.yaml` の `rabbitmqConnectionString`）。
回すと取引ユニットの Worker がブローカへ接続できなくなる。**取引ユニット側が接続文字列を Secret から組み立てるようになるまで回さない。**

🔴 **ブローカの再起動で、処理中のメッセージは失われる**（永続化していない）。各サービスの消費は冪等なので再配信で重複しても安全だが、失われたものは戻らない。業務の少ない時間に行う。

**切り替えの間はサービスが断続的に止まる。** ストア側を変えた瞬間から、既存の接続は保たれるが新しい接続は旧い値で失敗する。
消費側の作り直しが終わるまでを 1 つの作業として続けて行う。

## 手順 C: `deferred[]`（いまは回せない理由と回すための前提）

**認証基盤のクライアントシークレット 18 項目**（OIDC クライアント: 境界層・利用者管理・MinIO・Grafana・Vault・Headlamp・Wiki.js・パスワード再設定の門・合成監視／
サービス間: 各サービスのサービスアカウント 9 本）は、**経路B では恒久的に回せない。**

- 値は保管先と認証基盤の**両方**に在り、**両方が同値でなければ**トークン端点が `invalid_client` を返し続ける。
- 認証基盤側の値の**正は realm JSON**（`deploy/keycloak/microservices-platform-realm.json`）であり、
  `scripts/k8s-local-up.sh` のたびに宣言の追随がその値へ当て直す（[回した値を元へ戻す経路](#回した値を元へ戻す経路)の 3 つ目）。
- 保管先側も `bootstrap.sh` が開発用既定値で全置換する（同 2 つ目）。
- **その開発用既定値はリポジトリに在る。** したがって経路B のこれらの値は、**そもそも秘密として機能していない**（開発専用。
  `docs/security/security.md`「開発専用の平文認証情報」の扱い）。

**一時的に回しても次の起動で戻る。** 手作業で両側を書き換えることは可能だが、それは本書の手順にしない。

**回せるようにするための前提**（いずれも未着手。設計の判断が要る）:

1. realm JSON からクライアントの `secret` を外し、宣言の追随が secret を当て直さないようにする。
2. 認証基盤の管理 API への書き込みと保管先への書き込みを**対で**行う経路を作る（画面でも、コンソールの手順でもよい）。
3. `bootstrap.sh` がこれらを「無いときだけ作る」ようにする（`items[]` と同じ扱い）。

分類を動かすときは、分類の単一情報源（`deploy/bootstrap/sc22-secret-items.json`）の注記に従う。

## 本書が扱わない秘密

| 対象 | 理由 |
| --- | --- |
| 保管先（Vault）自身の root トークン・unseal 鍵 | 経路B の開発専用の既知値であり、本番の Vault 運用（unseal・監査・HA）は未配備 |
| 取引ユニットの DB 利用者 `ai` | 保管先に無い（DB の初期化スクリプトに直書き）。取引ユニットの管轄 |
| データソースの接続資格情報 | 保管先に無い。データソース管理画面の更新で差し替える（削除→再登録は ID と履歴を切るので行わない） |
| Wiki.js の管理者パスワード | 保管先に無い（起動時に乱数で生成）。Wiki.js の管理画面で変える |
| メッシュのサービス間証明書・エッジの TLS 証明書 | 自動で更新される（istiod / cert-manager） |
| 利用者のパスワード・OTP | 利用者本人の操作であり、運用のローテーションではない |

## 確認（この手順が成功したと言える条件）

**🔴 値を端末へ出さない。長さだけを見る。** コマンドは
[`secret-item-console-injection-runbook.md`](secret-item-console-injection-runbook.md) の「確認」節と同じ（ExternalSecret の Ready・Secret の当該キーの長さ・キー一覧）。

成功と言える条件:

- 同期先の ExternalSecret が `Ready=True`。
- 同期先 Secret の当該キーの長さが、**投入した値の長さと一致する**（0 でないことだけでは不足）。
- 同期先 Secret のキー一覧が投入前と同じ（数も名前も減っていない）。
- 消費側が `Ready` になり、機能が新しい値で動いている（外部 LLM を使う回答が縮退しない・Wiki 同期が通る・パスワード再設定メールが届く・DB / ブローカに接続できる、など回した値を実際に使う操作で確かめる）。
- **旧の値を発行元・ストア側で失効させた後も**、上が保たれている。
- 手順 B のときは、**次の `scripts/k8s-local-up.sh` を env つきで再実行しても**上が保たれている（env の渡し忘れが無い）。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| 消費側が新しい値で動かない | 消費側が古い環境変数を持ったまま／同期がまだ | 同期を促し、消費側を作り直す（B-0・B-1） |
| 回した直後から認証が壊れた（DB / ブローカ / 管理 API） | ストア側と保管先の値が食い違っている（片側だけ変えた・同値にすべき 2 つの片方を忘れた） | 同値にすべき組（`kp` の 2 つ・ブローカの 2 つ）を見直す。どちらが正か分からなければ、ストア側を正として保管先を合わせる |
| 次の起動の後に認証が壊れた | env を渡さずに `scripts/k8s-local-up.sh` を再実行し、保管先と Secret が既定値へ戻った | env に新しい値を渡して再実行する。値を控えていなければ、手順 B をやり直して新しい値を作る |
| 新しい値そのものが誤っていた（発行元で失効させる前） | 投入の誤り | **保管先の直前の版へ戻す**: Vault Pod 内で `vault kv metadata get secret/<path>` で版を見て `vault kv rollback -version=<直前の版> secret/<path>`。その後に同期を促し、消費側を作り直す |
| MinIO がルート資格情報の変更後に起動しない・データが読めない | 未実測の挙動（手順 B の注記） | 保管先を直前の版へ戻し（上）、同期と `minio` の作り直しで旧い値へ戻す。結果を本書へ書き戻す |
| `deferred[]` を回したら `invalid_client` | 手順 C のとおり、片側だけ・次の起動で戻る | 認証基盤側・保管先側とも realm JSON の値へ戻す（`scripts/k8s-local-up.sh` の再実行で両側とも既定値へ戻る） |

## 記録

- **手順 A**: 画面から書いた更新は監査ログに残る（誰が・いつ・どの項目のどのプロパティを）。**発行元で旧を失効させた日時**だけを、下の「実施記録」に追記する。
- **手順 B**: コンソールの操作は監査ログに乗らない。**実施日時・実施者・対象・渡した env の名前**を、#458 へのコメントとして残す（暫定。退避手段の使用記録と同じく、計画側に記録手段の設計が残っている）。
- **値・値の長さ・値の一部は書かない。**

### リハーサル記録

新しい環境（稼働中の利用者の環境でないもの）で本書を通しで実行し、結果をここへ追記する。**手順と食い違ったら本書を直す。**

| 実施日 | 実施者 | 環境 | 手順（A / B の対象） | 結果 | 本書との食い違いと直した箇所 |
| --- | --- | --- | --- | --- | --- |
| （未実施） | — | — | — | — | 2026-09-25 時点で一度も実施していない。稼働中のクラスタは利用者の検証環境であり、そこで回さない |

### 実施記録

| 実施日 | 実施者 | 対象 | 旧の失効日時 | 備考 |
| --- | --- | --- | --- | --- |
| （まだ無い） | — | — | — | — |

## 限界（この手順で担保できないこと）

- 🔴 **リハーサル未実施。** 本書は手順を定めただけであり、「この手順で回せる」ことは最初のリハーサルではじめて確かめられる。
- 🔴 **`deferred[]` の 18 項目は回せない**（手順 C）。「集中管理・ローテーション」の要件に対し、経路B が満たすのは `items[]` と `excluded[]` だけである。
- **周期の統制は無い。** 周期を定めていないので、回し忘れを知らせる仕組みも無い。
- **旧の値の失効は発行元の操作であり、機械で確かめていない。** 失効させたかどうかは実施記録に人が書く。
- **手順 B は監査にならない。** 記録は人が書く前提であり、書かなければ残らない。
- **本番の手順ではない。** 本番の秘密の供給経路（保管先の配備・unseal・監査）が決まったら、本書を本番向けに書き直すか、別の Runbook を起こす。
