# Wiki.js の初期セットアップ（FR-13 / UC-07 / SC-04 / ADR-0011・IADR-0327・#1108）

> 起点: [IADR-0327](../../../.ai-context/adr/IADR-0327_wikijs-setup-bootstrap.md) /
> 作業仕様書 [`.ai-context/specs/20260831_issue-1108_wikijs-setup-bootstrap.md`](../../../.ai-context/specs/20260831_issue-1108_wikijs-setup-bootstrap.md)

`bootstrap.sh` は **Wiki.js の初期セットアップ・同期用 API キー・本文 locale** を入れる冪等な
runtime bootstrap である。`scripts/k8s-local-up.sh` が**既定で**呼ぶ（opt-in ではない）。

```sh
bash deploy/local/wikijs-setup/bootstrap.sh     # 単独でも何度でも実行できる
```

## なぜ要るか —— 「Running」と「使える」は別である

Wiki.js 2.x は初期セットアップが済むまで本体のルータ（`/graphql` を含む）を載せない。
その間 `server/setup.js` の catch-all（`app.get('*')`）が **`/healthz` を含むすべての URL に 200 を返す**。

つまり **`readinessProbe` は通り、Pod は `2/2 Running` のまま、Wiki 同期だけが全件失敗する。**
失敗はエラーキューに移るだけで、画面にも SC-10 にも出ない。実際、稼働 dev クラスタでは
`DocumentUpdated` / `DocumentDeleted` が **1 件も成立しないまま数日放置されていた**（#1108）。

セットアップ状態は Wiki.js の **DB（`settings` テーブル）** に載る。DB は `platform-infra` の
共有 Postgres（`wikijs`）であり、`PERSIST=1` を付けずに立てたクラスタでは **`emptyDir`** である。
**postgres Pod を作り直すと `wikijs` DB ごと消え、Wiki.js は setup モードへ戻る。**
manifest だけでは復元できない runtime 状態なので、Vault の bootstrap や realm import と同じく
「冪等な再適用」で面倒を見る。

## 何をするか（すべて冪等）

| # | 段 | 中身 |
| --- | --- | --- |
| 0 | 前提 | `wiki-js` Deployment の存在と Ready を確かめる（無ければ何もしない） |
| 1 | 管理者資格情報 | Secret `wikijs-admin`（`email` / `password`）。**既定パスワードを置かず、無ければ乱数を生成**して Secret へ入れる |
| 2 | finalize | `/graphql` が 404（= setup モード）のときだけ `POST /finalize`。`siteUrl` は `WIKIJS_SITE_URL`、telemetry は false 固定 |
| 3 | 本文 locale | WikiService が push に使う locale（実装から走査）を `locales` に入れる。§落とし穴 参照 |
| 4 | 管理者ログイン | GraphQL `authentication.login`（strategy=`local`） |
| 5 | API 有効化 | `authentication.setApiState(enabled:true)` |
| 6 | 外部取得の停止 | `localization.updateLocale(autoUpdate:false)`。既定では `graph.requarks.io` を毎起動叩く |
| 7 | API キー | 既存キーが有効なら**再発行しない**。無ければ発行し、Secret `wikijs-sync.apiKey`（＋ Vault が居れば `secret/msp/wikijs-sync`）へ書き、`wiki-service` を再起動する |
| 8 | **Keycloak OIDC ストラテジ**（**opt-in・既定オフ**） | `WIKIJS_OIDC=1` のときだけ。`authentication` の oidc 行と `settings.host` を UPSERT で突き合わせる。**変わったときだけ** `wiki-js` を再起動する。IADR-0334・#1127 |

**HTTP はすべて wiki-js コンテナ内の loopback（`http://127.0.0.1:3000`）へ出す。**
エッジ（Traefik / Istio Ingress Gateway）にも port-forward にも依存せず、`PeerAuthentication` が
STRICT でも成立する。

## 🔴 落とし穴 —— セットアップを終えただけでは同期は成立しない

`POST /finalize` が入れる locale は **`en` ただ 1 つ**である（`setup.js` は `locales` を
`code != 'x'` で全削除してから `en` を 1 行入れる）。一方 WikiService は本文を **`ja`** で push する
（`WikiJsGraphQlClient.Locale`）。この不一致があると `pages.create` が外部キー制約
`pages_localecode_foreign` に違反して落ちる —— **しかも Wiki.js は GraphQL 200 を返すため、
失敗は WikiService のエラーキューにしか残らない。**

段 3 はこれを埋める。**locale の値はここに書かず、WikiService の実装から走査して得る**
（2 か所に持つと、次に片方だけ変わったとき静かに割れる）。

## 検知（fail-closed の門は別に在る）

本 script は **best-effort** であり、失敗しても `k8s-local-up.sh` を止めない。
**落とす役は `scripts/check-stack-ready.js` の G7** が持つ。G7 は 3 つを見る:

1. `/graphql` が 404 でない（setup モードでない）
2. `wikijs-sync.apiKey` が空でない
3. WikiService が使う locale が Wiki.js に入っている（`isInstalled`）

```sh
node scripts/check-stack-ready.js          # 全ゲート
node scripts/check-stack-ready.js --self-test
```

## 環境変数（すべて任意）

| 変数 | 既定 | 用途 |
| --- | --- | --- |
| `WIKIJS_ADMIN_EMAIL` | `admin@example.com` | 管理者のメールアドレス（＝ログイン ID） |
| `WIKIJS_ADMIN_PASSWORD` | **無し（乱数生成）** | 指定すると既存 Secret より優先する。英数と `_.@:+=-` のみ |
| `WIKIJS_SITE_URL` | `https://wiki.localhost:50000` | `settings.host`。**OIDC の callback を組む値**なので経路と一致させる。🔴 段 2（finalize）は**初回しか通らない**ので、既にセットアップ済みの Wiki.js でこの値を動かせるのは**段 8（`WIKIJS_OIDC=1`）だけ**である |
| `WIKIJS_CONTENT_LOCALE` | 実装から走査 | 走査できないときの明示指定 |
| `WIKIJS_API_KEY_NAME` / `WIKIJS_API_KEY_TTL` | `wiki-service-sync` / `1y` | 発行する API キー |
| `WIKIJS_OIDC` | **無し（オフ）** | `1` で段 8（Keycloak OIDC ストラテジの投入）を有効にする |
| `WIKIJS_OIDC_CLIENT_SECRET` | Secret `wikijs-oidc` の `client-secret` | 段 8 が使う client secret。**どちらも空なら段 8 は既存設定に触らずに終わる** |
| `WIKIJS_OIDC_BROWSER_ISSUER` / `WIKIJS_OIDC_SERVER_ISSUER` | `https://keycloak.localhost/realms/platform` / `http://keycloak:8080/realms/platform` | **揃えない**（ブラウザが開く 3 つ / pod が叩く 2 つ）。理由は [`../wiki-oidc/README.md`](../wiki-oidc/README.md) |
| `WIKIJS_OIDC_CLIENT_ID` / `WIKIJS_OIDC_DISPLAY_NAME` / `WIKIJS_OIDC_AUTOENROLL_GROUP` | `wiki-js` / `Keycloak` / `Guests` | realm の client、ボタン文言、未マッピング利用者が落ちる最小権限の床 |

管理者パスワードの取り出し（**値をリポジトリへ書かない**）:

```sh
kubectl -n microservices-platform get secret wikijs-admin -o jsonpath='{.data.password}' | base64 -d
```

## 復旧・つまずき

- **DB を作り直した / クラスタを立て直した** → もう一度 `bash deploy/local/wikijs-setup/bootstrap.sh`。
  `PERSIST=1` で立てておけば `wikijs` DB は PVC に載る（それでも**新規 DB は必ず setup モードから始まる**ので、
  本 script は要る）。
- **「管理者ログインに失敗した」と出る** → Secret `wikijs-admin` のパスワードと Wiki.js の DB が
  食い違っている（人が管理 UI でセットアップを済ませた等）。DB 側の値が正なので、
  `WIKIJS_ADMIN_PASSWORD=<実際の値>` を渡して再実行するか、Wiki.js の DB を作り直して本 script を走らせる。
- **Keycloak SSO（OIDC ストラテジ）は段 8 が入れる**（#1127・IADR-0334。以前は射程外で、
  手作業の SQL が正本だった）。**既定オフ**なので、要るときは `WIKIJS_OIDC=1` を付ける:

  ```sh
  WIKIJS_OIDC=1 bash deploy/local/wikijs-setup/bootstrap.sh
  ```

  設定の中身と、自動化が効かないときの手当ては [`../wiki-oidc/README.md`](../wiki-oidc/README.md) に在る。
