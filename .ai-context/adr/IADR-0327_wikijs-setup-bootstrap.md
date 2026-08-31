---
title: IADR-0327 Wiki.js の初期化は「セットアップ API を冪等に叩く runtime bootstrap」で入れ、検知は check-stack-ready の門に置く
type: impl-adr
status: Accepted
related_ids:
  - FR-06
  - FR-13
  - FR-19
  - UC-07
  - SC-04
  - ADR-0011
  - ADR-0046
  - IADR-0020
  - IADR-0021
  - IADR-0095
  - IADR-0097
  - IADR-0248
author: claude
created: 2026-08-31
updated: 2026-08-31
---

# IADR-0327: Wiki.js の初期化を冪等な runtime bootstrap で入れる（#1108）

- 状態: Accepted
- 日付: 2026-08-31
- 決定者: claude（実装）

## コンテキストと課題

稼働 dev クラスタ（Rancher Desktop 内蔵 k3s v1.35.4+k3s1）で、**Wiki 同期が 1 件も成立していなかった。**
`DocumentUpdated` / `DocumentDeleted` は全件エラーキューへ落ち、画面にも `SC-10` にも何も出ていない。

原因は Wiki.js が **初期セットアップ未完了のまま動いていた**ことである。Wiki.js 2.x は
セットアップが済むまで本体のルータ（`/graphql` を含む）を載せず、その間 `server/setup.js` の
catch-all（`app.get('*')`）が **`/healthz` を含むすべての URL に 200 を返す**。

🔴 **したがって readinessProbe は通り、Pod は `2/2 Running` を保ったまま「使えない」。**
「動いている」と「使える」が乖離しており、乖離自体を誰も測っていなかった。

### 実測（2026-08-31・着手前）

| 測ったこと | 出力 |
| --- | --- |
| `POST http://127.0.0.1:3000/graphql`（wiki-js pod 内 loopback） | `http=404` |
| `GET /healthz` | `http=200` ← setup モードの catch-all |
| wiki-js ログ | `DB Configuration is empty or incomplete. Switching to Setup mode...` |
| `wikijs` DB の `settings`/`users`/`pages`/`authentication` | すべて 0 行（スキーマだけ在る） |
| wiki-js の DB 参照 | `DB_HOST=postgres` / `DB_NAME=wikijs`（**platform-infra の共有 Postgres**） |
| wiki-js の PVC | `wiki-js-data` → `/wiki/data` のみ（**DB はここに載っていない**） |
| postgres Deployment の volume | `{"emptyDir":{},"name":"data"}` ← PVC ではない |
| `wikijs-sync.apiKey` | 長さ 0（fail-safe の空既定のまま） |

**Wiki.js のセットアップ状態は DB（`settings`）に載り、その DB は既定（`PERSIST` 未設定）で
コンテナ層（emptyDir）に載っている。** postgres Pod を作り直すたびに丸ごと消え、
Wiki.js は setup モードへ戻る —— **manifest だけでは復元できない runtime 状態**である。

### 先例（#397）

`#397`「Wiki.js OIDC ストラテジの DB seed 自動化」は **`duplicate` として close されており、
実装は入っていない**（2026-08-02・#454 の全面再実装へ畳まれた）。
`deploy/local/wiki-oidc/README.md` には手動 SQL 手順が残ったままである。
つまり #397 は「採った方式」ではなく **「自動化すると決めたのに入らなかった前例」** であり、
本件は同じ穴（runtime 設定が manifest に無く、DB 再作成で黙って消える）の再来である。

## 決定

### 決定 1 — 方式は「セットアップ API を冪等に叩く」。DB スナップショットは採らない

`deploy/local/wikijs-setup/bootstrap.sh` が `POST /finalize` でセットアップを完了させる。

初期化済み DB スナップショットを流し込む案（#1108 の選択肢 2）は採らない。
chart の `wikijs.tag` は `"2.5"`（**浮動 minor**）であり、スナップショットは版に固定される。
版が上がったときスキーマが合わず、しかも**その不一致は起動時にしか現れない**。

### 決定 2 — `k8s-local-up.sh` から**既定で**呼ぶ（opt-in にしない）

🔴 **既定の経路が「Running なのに使えない Wiki.js」を残すことが #1108 そのものである。**
`ABACSEED` / `SEARCHSEED` が opt-in なのは**文書やポリシーを作る副作用**があるからで、
こちらは配備の初期化であって副作用ではない。

ただし **best-effort**（`|| echo WARN`）とし、失敗しても `up` は止めない。
**fail-closed の役は検知側（決定 5）が持つ。**

### 決定 3 — HTTP は wiki-js コンテナ内の loopback へ出す

`kubectl exec deploy/wiki-js -c wiki-js -- curl http://127.0.0.1:3000/...`。

エッジは 2 通りあり（Traefik / Istio Ingress Gateway・IADR-0317）、`PeerAuthentication` が STRICT の
ときメッシュ外からの平文は Envoy に落とされる（#1072 / #1109）。loopback ならその全部と無関係に
**「Wiki.js 自身が何を返すか」だけ**を測れる。port-forward も使わない。

### 決定 4 — 管理者パスワードに **dev 既定文字列を置かない**（無ければ乱数を生成する）

他の dev 資格情報は `*-dev-secret-change-me` を既定に持つが、ここは**エッジに露出する実ログイン口**
（`wiki.localhost:50000`）である。既定値を置くと「変えなければ誰でも入れる管理者」がリポジトリに載る。

優先順は **env（`WIKIJS_ADMIN_PASSWORD`）＞ 既存 Secret `wikijs-admin` ＞ 乱数生成**。
生成した値は Secret にだけ入り、標準出力へは出さない。**finalize より先に保存する**
（途中で落ちても資格情報が迷子にならないため）。

### 決定 5 — 検知は `check-stack-ready.js` の **G7** に置く（検査器を足す判断）

`.claude/rules` の「同型の事故が 2 回起きたら検査器を足す」に照らして数えた。
同型＝**「manifest に無い runtime 状態が黙って欠落したまま、Pod は Ready を返す」**。

| # | issue | 事故 |
| --- | --- | --- |
| 1 | #397（2026-07・close: duplicate） | Wiki.js の OIDC ストラテジが DB 保持で、DB 再作成のたびに消える。**自動化すると決めたが入らなかった** |
| 2 | #1088（2026-08-30） | Keycloak realm が古い ConfigMap のまま焼き付き、**検知手段が無い** |
| 3 | #1108（本件） | Wiki.js が setup モードのまま `2/2 Running`。`/healthz` が setup ページに 200 を返す |

**3 件目である。よって足す。** G7 は 3 つを fail-closed で見る:

1. `/graphql` が **404 でない**（setup モードでない）
2. `wikijs-sync.apiKey` が **空でない**
3. **WikiService が push に使う locale が Wiki.js に入っている**（決定 6）

`wiki-js` の Deployment が無い構成（`wikijs.enabled=false`）は notice で飛ばす（G5 と同じ作法）。

### 決定 6 — **セットアップを終えただけでは同期は成立しない。** 本文 locale も入れる

🔴 **本 issue の作業中に実測で見つかった、issue 本文に書かれていなかった 2 段目の欠陥である。**

`POST /finalize` が入れる locale は **`en` ただ 1 つ**（`setup.js` は `locales` を `code != 'x'` で
全削除してから `en` を 1 行だけ入れる）。一方 WikiService は本文を **`ja`** で push する
（`WikiJsGraphQlClient.Locale`・IADR-0021）。この不一致で `pages.create` が
外部キー制約 `pages_localecode_foreign` に違反して落ちる。

```
WikiJsSyncException: Wiki.js pages.create failed for 'doc/…' (code=1):
  insert or update on table "pages" violates foreign key constraint "pages_localecode_foreign"
```

🔴 **Wiki.js は GraphQL 200 を返す。** 失敗は WikiService のエラーキューにしか残らないため、
setup モードを直しただけでは「404 が消えて別の理由で全件落ちる」状態になる。

bootstrap は `wikijs` DB へ locale 行を冪等に入れる（`ON CONFLICT DO NOTHING`）。
Wiki.js の `downloadLocale` は `graph.requarks.io` からのダウンロードであり、閉域前提と両立しないので使わない。

**locale の値は bootstrap にも検査器にも書き写さず、`WikiJsGraphQlClient.cs` から走査して得る。**
2 か所に持つと、次に片方だけ変わったとき静かに割れる（IADR-0141 の規則 9・10 と同じ向き）。

### 決定 7 — API キーは Wiki.js に発行させ、既存の供給経路（`wikijs-sync`）へ書き戻す

新しい Secret も新しい ExternalSecret も作らない（#458 と「同じパターンを 2 つ作らない」）。
Vault が居れば `secret/msp/wikijs-sync` にも書く —— ESO（`creationPolicy: Owner`）が復旧したときに
**空文字で上書きされて静かに壊れる**のを防ぐため。消費側（`WikiJs__ApiKey` の `secretKeyRef`）は無改変。

env は Pod 起動時にしか読まれないので、キーを更新したら `wiki-service` を rollout restart する。

🔴 併せて `k8s-local-up.sh` の `apply_secret … "apiKey=${WIKIJS_SYNC_APIKEY:-}"` を
**既存値へフォールバック**させた。そのままだと **up を再実行するたびに apiKey が空へ戻り**、
次に wiki-service の Pod が作り直された瞬間に #1108 が再発する（原因と結果が時間的に離れる）。

### 決定 8 — 外部への定期取得（telemetry / locale 自動更新）を切る

`finalize` は `lang.autoUpdate: true` を焼き込み、Wiki.js は起動のたびに `graph.requarks.io` を叩く
（実測: `Syncing locales with Graph endpoint: [ COMPLETED ]`）。telemetry は finalize の引数で false に
できるが、**locale 自動更新は別経路**なので `localization.updateLocale(autoUpdate:false)` で落とす。

## #1088（`PERSIST=1` で立っていない）と同じ PR で解くべきか —— 解かない

- 稼働クラスタが `PERSIST=1` で立っていないのは事実で、`postgres` の emptyDir が Wiki.js の DB を消している。
  **しかし永続化オーバーレイ（`deploy/local/infra-persistence/`）は既に実装済み**であり、
  #1088 の射程は **realm import の戦略（`IGNORE_EXISTING`）と乖離検知**である。
- **永続化しても本件は解けない。** 新規クラスタの `wikijs` DB は必ず空であり、
  **空の DB は必ず setup モードになる。** seed が要ることは永続化と独立している。
- 逆に **seed だけでも本件は解ける** —— 冪等な bootstrap を `up` の経路へ置けば、DB が消えても再実行で復旧する。
- **互いを無意味にしないので、別 PR とする。** 依存は「#1088 が入ると本 bootstrap の再実行頻度が下がる」だけである。

## 結果

- 既定の `k8s-local-up.sh` で Wiki.js が使える状態になる。再実行は冪等（finalize もキー発行も飛ぶ）。
- 「Running なのに使えない」が **G7 で落ちる**（陽性対照つき。`--self-test` 9 件を追加）。
- **管理者の既定パスワードがリポジトリに載らない。** API キーもリポジトリに現れない。
- 代償: `k8s-local-up.sh` の既定経路に 1 段増える（wiki-js の Ready 待ち ＋ 数回の `kubectl exec`）。
  Wiki.js が居ない構成では即座に no-op で返る。

## 却下した案

| 案 | 却下理由 |
| --- | --- |
| 初期化済み DB スナップショットの流し込み | Wiki.js の版に固定される（`tag: "2.5"` は浮動 minor）。不一致は起動時にしか現れない |
| 手順書に留め、自動化しない | #397 がその形で 1 か月放置され、**誰も気づかないまま同期が全滅した**のが本件である |
| readinessProbe を `/graphql` の実疎通にする | 認証が要る（API キー）ため probe に置けない。かつ**未設定の Pod が永久に Ready にならず**、初期化そのものができなくなる（鶏と卵） |
| `WIKISETUP=1` の opt-in にする | 既定が壊れたままになる。#1108 の再来を招く |
| 外部の `downloadLocale` で `ja` を入れる | `graph.requarks.io` への外向き通信に依存する（閉域前提と両立しない・CI で不安定） |
| 新しい Secret / ExternalSecret を作って API キーを供給する | `wikijs-sync` の供給経路が既に在る。同じパターンを 2 つ作らない（#458） |

## 申し送り

- **OIDC ストラテジの seed（#397 の残件）は射程外**である。本 bootstrap が入れる `settings.host`
  （Site URL）だけが接点で、ストラテジ自体は `deploy/local/wiki-oidc/README.md` の手順のまま。
  本 bootstrap の上に段を足せば自動化できる形にはなった。
- 実測中に **`wiki-service.u0e-shared-document-updated` が消費者 0 のまま溜まる**ことに気づいた
  （`consumers=0`・本件の対象外）。Wolverine の宛先宣言まわりの別件として扱うこと。
