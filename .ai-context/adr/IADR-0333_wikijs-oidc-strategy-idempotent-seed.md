---
title: IADR-0333 Wiki.js の OIDC ストラテジを既存 bootstrap の段として冪等に投入し、DELETE→INSERT をやめる
type: impl-adr
status: Accepted
related_ids: [NFR-09, FR-13, UC-07, SC-04, ADR-0011, ADR-0026, IADR-0020, IADR-0095, IADR-0098, IADR-0103, IADR-0327, IADR-0328, IADR-0332]
author: Claude（実装）
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-integration.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
---

# IADR-0333: Wiki.js の OIDC ストラテジを既存 bootstrap の段として冪等に投入し、DELETE→INSERT をやめる

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: Claude（実装）
- 起点: issue #1127（#397 の再起票）。先行する実装ADR:
  [IADR-0095](./IADR-0095_wikijs-keycloak-oidc.md)（経路B の Wiki.js OIDC そのもの）/
  [IADR-0103](./IADR-0103_local-sso-persistence-and-claim-design.md)（realm 側の恒久化と claim 設計）/
  [IADR-0327](./IADR-0327_wikijs-setup-bootstrap.md)（相乗り先の冪等 bootstrap）/
  [IADR-0328](./IADR-0328_tool-oidc-edge-issuer-followthrough.md)（endpoint の分離）。
  供給経路の先例: [IADR-0098](./IADR-0098_vault-eso-secret-supply-pr3.md) /
  [IADR-0332](./IADR-0332_keycloak-smtp-externalsecret-wiring.md)。
- 関連する作業仕様書:
  [`.ai-context/specs/20260902_issue-1127_wikijs-oidc-strategy-seed.md`](../specs/20260902_issue-1127_wikijs-oidc-strategy-seed.md)

## コンテキストと課題

`IADR-0095` が経路B の Wiki.js に Keycloak OIDC を入れ、`IADR-0103` が **Keycloak 側**
（`wiki-js` client の `groups` claim mapper、realm ロール `Administrators`）を `realm.json` へ恒久化した。
**Wiki.js 側だけが恒久化されていない。** OIDC ストラテジは Wiki.js の DB（`authentication` テーブル）
保持で、manifest にも Helm values にも無い。

その結果:

- `scripts/k8s-local-up.sh` でスタックを起こしただけでは **Wiki.js の OIDC ログインは存在しない**
- **稼働中のクラスタでも消える**（#1127 背景。`authentication` が 0 行になった実測がある）
- #780 が「ブラウザ OIDC を持つ 7 クライアントすべてでログインが成立する」と測ったとき、
  **Wiki.js の分だけは人が手で SQL を流して成立させていた**（`deploy/local/wiki-oidc/README.md`）

つまり **#780 の 7/7 は、Wiki.js については再現手順が人手に依存していた。**

### 🔴 実測で分かった、手順書そのものの欠陥

`deploy/local/wiki-oidc/README.md` が「冪等」と称してきた
`DELETE FROM authentication WHERE "strategyKey"='oidc'` → `INSERT` は、
**誰か 1 人でも OIDC でログインした後は必ず落ちる。** `users.providerKey` が
`authentication.key` を参照する外部キー `users_providerkey_foreign` があるためである。
稼働クラスタで ROLLBACK 付きに実測した（陰性対照＝誰も参照していない架空の `strategyKey` の
DELETE は成功する）:

```
--- 陰性対照: 誰も参照していない架空の strategyKey を DELETE ---
BEGIN / DELETE 0 / ROLLBACK
--- 陽性: README の DELETE FROM authentication WHERE strategyKey='oidc' ---
ERROR:  update or delete on table "authentication" violates foreign key constraint
        "users_providerkey_foreign" on table "users"
DETAIL:  Key (key)=(7c1f6f2e-9d3a-4b5c-8e10-000000000001) is still referenced from table "users".
```

**現行の「冪等」手順は初回しか冪等でない。** 「手で流せばいいだけ」ではなく、
**流しても通らない**手順が正本として置かれていた。

## 決定

### 決定 1: 新しい入口を作らず、`deploy/local/wikijs-setup/bootstrap.sh` の **段 8** として置く

`IADR-0327` の bootstrap は、すでに（a）`wiki-js` の Ready 待ち、（b）コンテナ内 loopback への
GraphQL、（c）`platform-infra/postgres` への psql（段 3 の locale 投入）、（d）Secret の読み書き、
（e）best-effort の作法をすべて持っている。**面倒みる対象が同じ**（manifest では復元できない
Wiki.js の DB 上の runtime 状態）なので、`deploy/local/wiki-oidc/` に別の起動口を作らない。

`scripts/k8s-local-up.sh` の呼び出しは 1 行のまま増やさない。

### 決定 2: 適用は GraphQL `authentication.updateStrategies` ではなく **DB の UPSERT** で行う

`updateStrategies` は `WIKI.auth.activateStrategies()` を呼ぶので再起動が要らない。それでも採らない。
稼働イメージの `/wiki/server/graph/resolvers/authentication.js` を直接読んで確かめた（232 行）:

```js
for (const str of _.differenceBy(previousStrategies, args.strategies, 'key')) {
  const hasUsers = await WIKI.models.users.query().count('* as total').where({ providerKey: str.key }).first()
  if (_.toSafeInteger(hasUsers.total) > 0) { throw new Error(`Cannot delete ${str.displayName} as ...`) }
  else { await WIKI.models.authentication.query().delete().where('key', str.key) }
}
```

**渡さなかったストラテジは全部消される。** oidc だけを渡すと `local` を消しにいき、
トランザクションが無いので **oidc を patch した後で例外**になる（中途半端な状態が残る）。
安全に使うには全ストラテジを読み戻して往復させる必要があるが、**読みと書きで config の形が違う**
（読み `{...propDef, value}` ＝ 同ファイル 59–70 行 / 書き `{v: value}` ＝ 208–211 行）。
往復の実装ミスは `local` の設定を静かに消す種類の事故である。

**「潰さない」を最優先**して、oidc の 1 行だけに触る UPSERT を採る。psql 経路は
同じ bootstrap の段 3 で実証済みで、新しい依存を増やさない。

### 決定 3: DELETE→INSERT をやめ、**既存行の `key` を再利用する UPSERT** にする

`strategyKey='oidc'` の行が在ればその `key` を、無ければ README と同じ正準 key
`7c1f6f2e-9d3a-4b5c-8e10-000000000001` を使う。これで:

- `users.providerKey` の外部キーが切れない（上の欠陥がそもそも起きない）
- 管理 UI から人が作った**乱数 key の残骸**に対して二重行を作らない
- `local` ストラテジには 1 バイトも触らない

### 決定 4: **変わったときだけ** `wiki-js` を再起動する

Wiki.js は起動時にストラテジを読むので、DB だけ書いても反映されない。しかし無条件に
`rollout restart` を打つと、`up` の再実行や並行作業のたびに Wiki.js が落ちる
（同じクラスタで別の検証が走っている）。そこで **SQL 側に差分判定を持たせる**:

```sql
ON CONFLICT (key) DO UPDATE SET ...
  WHERE ROW(t."isEnabled", t.config::jsonb, ...) IS DISTINCT FROM ROW(EXCLUDED."isEnabled", ...)
RETURNING 1
```

返った件数が 0 なら再起動しない。**これが「2 回目は no-op」の実体**であり、
宣言ではなく DB が判定する。

### 決定 5: 既定オフの opt-in（`WIKIJS_OIDC=1`）

endpoint は **エッジ host（`https://keycloak.localhost`）を前提にする**（決定 6）。`LOCALEDGE` 抜きの
スタックで既定 ON にすると「押せるが 502 になるボタン」を作る。既定オフなら `local` ログインだけが
残る（fail-safe）。`IADR-0327` の段 0〜7 が既定 ON なのと**扱いが違う**のは、
あちらが「配備の初期化」（無いと同期が全滅する）なのに対し、こちらは**到達経路に依存する追加の
ログイン口**だからである。

### 決定 6: 5 つの URL を揃えない（`IADR-0328` の踏襲）

ブラウザが開く 3 つ（`authorizationURL` / `issuer` / `logoutURL`）は **エッジ host**、
`wiki-js` pod がサーバ側で叩く 2 つ（`tokenURL` / `userInfoURL`）は **in-cluster**（`http://keycloak:8080`）。
揃えるとローカル CA を Wiki.js コンテナへ配る必要が出る。逆に in-cluster へ揃えるとブラウザが
解決できず、`issuer` を in-cluster にすると id_token の `iss` 突合が落ちる。
**役割で分ける**ことがこの設定の要点である。機械検査を `scripts/k8s-local-up.test.js` に置いた。

### 決定 7: client secret は Vault/ESO 経路（`secret/msp/wikijs-oidc`）で供給し、取れなければ**何もしない**

`IADR-0098` が置いた OIDC client secret 群（`minio` / `bff` / `identity-admin` / `grafana` / `vault` /
`headlamp`）と同じ 3 点セット（Vault seed ＋ `externalsecret-wikijs-oidc.yaml` ＋ 起動器の apply）を足す。
`wiki-js` の分だけ**供給経路が無かった**（realm には client が在るのに）。

優先順は **env `WIKIJS_OIDC_CLIENT_SECRET` ＞ Secret `wikijs-oidc`（key `client-secret`）＞ 断念**。
断念のとき **既存行に触らない** —— 空の `clientSecret` で上書きすると、動いていたログインを黙って壊す。
リポジトリに載るのは realm と同じ dev プレースホルダだけで、実値は載らない。

🔴 **この Secret を env で読む Pod は 1 つも無い**（読み手は bootstrap）。`IADR-0332` の
`keycloak-smtp` と同じ性質なので、ESO 後段の rollout 対象には入れない。
ただし **`eso_wait` では待つ** —— 理由は rollout ではなく、`up` の後段で走る段 8 がこの Secret を
読むためである。未同期だと段 8 は「secret を取得できない」で何もせず、
**OIDC ログインが入らないまま `up` が緑で終わる。**

### 決定 8: `settings.host`（Site URL）も同じ段で突き合わせる

Wiki.js はコールバックを `{Site URL}/login/{key}/callback` で組むので、Site URL がズレると
realm に登録があっても `invalid_redirect_uri` になる。**`POST /finalize` は初回しか通らない**ため、
`WIKIJS_SITE_URL` を変えて bootstrap を再実行しても現状は `settings.host` が動かなかった
（`deploy/local/wikijs-setup/README.md` の「`WIKIJS_SITE_URL` を渡して再実行すればよい」は**誤り**
だった）。段 8 が同じトランザクションで突き合わせ、README も直す。

### 決定 9: `realm.json` は 1 バイトも変えない

`wiki-js` client は `secret`・4 つの `redirectUris`・`webOrigins`・`wikijs-realm-roles` mapper・
realm ロール `Administrators` まで揃っている（`IADR-0103`）。**足すものが無い。**
副次的に #1115（バックチャネルログアウト）との編集衝突も起きない。

## 実測（稼働 k3s・`curl -k` は使わずローカル CA を `--cacert` に渡した）

| # | 測ったこと | 結果 |
| --- | --- | --- |
| 1 | 陰性: `WIKIJS_OIDC` 未設定 | `OIDC ストラテジの投入は既定オフ…authentication テーブルには触らない` |
| 2 | 陰性: `WIKIJS_OIDC=1` だが secret 不在 | `WARN: client secret を取得できない…既存の OIDC 設定には触らずに終了する` |
| 3 | 収束: secret 供給後の 1 回目 | `既に一致している（変更なし・wiki-js は再起動しない）`＝**人が #780 で手で入れた状態とバイト等価** |
| 4 | 陽性: `displayName` / `clientSecret` / `mapGroups` / `settings.host` を壊してから実行 | `OIDC ストラテジ / Site URL を 2 件更新した` ＋ 再起動 |
| 5 | 冪等: 直後にもう一度 | `変更なし・wiki-js は再起動しない` |
| 6 | 陽性: **oidc 行の無い使い捨て DB**（＝DB を作り直した直後）に実行 | 行が作られ、`local` は残り、2 回目は `変更なし` |
| 7 | 潰していないこと | `local` 行・`users` の 5 行（うち oidc 3 行）・`wikijs-sync.apiKey`（502 文字）が前後で不変 |
| 8 | ログイン | `Authentication Strategy Keycloak: [ OK ]` / `activeStrategies` に `local` と Keycloak の 2 件 / `/login/<key>` が **302** で `https://keycloak.localhost/realms/platform/protocol/openid-connect/auth?…&redirect_uri=https%3A%2F%2Fwiki.localhost%3A50000%2Flogin%2F<key>%2Fcallback` へ |

**他ユニットの Pod は再起動していない。`scripts/k8s-local-up.sh` は丸ごと再実行していない**
（同じクラスタで複数の検証が並行していたため）。

## 影響

- `deploy/local/wikijs-setup/bootstrap.sh` に段 8 が増える（既定では 1 行 log を出すだけ）。
- `deploy/local/vault/eso/externalsecret-wikijs-oidc.yaml` が増え、`WIKIJS_OIDC=1` のときだけ apply される。
- `deploy/local/wiki-oidc/README.md` は「手作業の正本」から「自動化の説明ページ ＋ 効かないときの手当て」へ
  位置づけが変わる。手作業の SQL は **UPSERT へ直した**（DELETE→INSERT のまま残すと、読んだ人が
  外部キー違反を踏む）。
- `docs/operations/local-sso-recovery-runbook.md` の STEP 3 は「手動」から「`WIKIJS_OIDC=1` で自動」へ。
- `scripts/k8s-local-up.test.js` に不変条件が 6 件増える（うち 5 件は bootstrap 本文への静的検査
  —— stub ハーネスは Wiki.js の loopback に応答しないので段 8 まで到達しない）。

## 却下した案

- **Helm chart / manifest へ寄せる**: Wiki.js は `authentication` を DB にしか持たない。
  init container で SQL を流す形も考えられるが、**Pod の起動経路に DB スキーマ依存を持ち込む**うえ、
  失敗時に Pod が起動しなくなる（fail-safe から遠い）。bootstrap は best-effort でよい。
- **`updateStrategies` の全ストラテジ往復**: 決定 2 のとおり。安全側の実装コストが、
  得られる「再起動不要」に見合わない。
- **client secret を Keycloak Admin API から実行時に取る**（現行 README の手順）: 管理者パスワードと
  Admin REST への到達という**別の前提**を増やす。ほかの 6 クライアントと同じ Vault/ESO 経路に揃える。
- **既定 ON にする**: 決定 5 のとおり。エッジ非有効時に壊れたボタンを作る。

## フォローアップ

1. `scripts/verify-oidc-edge-flow.sh` は SPA → BFF の経路専用で、**ツール側 7 クライアントの段を持たない**。
   Wiki.js を含むツールのログイン導線を検証器へ載せるかは別 issue で判断する（本 IADR の射程外）。
2. `PERSIST=1` を付けない限り `wikijs` DB は `emptyDir` である（`IADR-0327` と同じ前提）。
   本決定は「消えても 1 コマンドで戻る」ようにしただけで、消えないようにはしていない。
