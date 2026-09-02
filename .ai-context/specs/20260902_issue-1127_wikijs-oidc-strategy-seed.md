---
title: 作業仕様書 — 経路B Wiki.js の Keycloak OIDC ストラテジを冪等な runtime seed で自動化する（#1127）
type: spec
status: done
related_ids:
  - NFR-09
  - FR-13
  - UC-07
  - SC-04
  - ADR-0011
  - ADR-0026
  - IADR-0020
  - IADR-0095
  - IADR-0098
  - IADR-0103
  - IADR-0327
  - IADR-0328
  - IADR-0332
  - IADR-0334
author: Claude（実装）
created: 2026-09-02
updated: 2026-09-02
---

# 作業仕様書: Wiki.js の OIDC ストラテジを冪等な runtime seed で自動化する（#1127）

## 1. 背景と課題

issue #1127（#397 の再起票）。#780 の最終段で「ブラウザ OIDC を持つ 7 クライアントすべてのログインが
成立する」ことを稼働クラスタで実測したが、**Wiki.js の分だけは人が手で SQL を流して成立させていた**
（`deploy/local/wiki-oidc/README.md`「DB seed で入れる」）。

Wiki.js の OIDC ストラテジは Wiki.js の DB（`authentication` テーブル）保持で、manifest にも Helm values
にも無い。したがって `scripts/k8s-local-up.sh` でスタックを起こしただけでは Wiki.js の OIDC ログインは
**存在しない**。稼働中のクラスタでも `authentication` が 0 行になった実測がある（#1127 背景）。

つまり **#780 の「7/7 成立」は Wiki.js については再現手順が人手に依存している。**

## 2. 母集合（自分で走査して引いた）

### 2-a. OIDC ストラテジの seed が「どこで・どう」書かれているか

`git grep -ln "strategyKey\|activeStrategies\|7c1f6f2e"` と `git grep -ln "wiki-oidc"` の和。

| ファイル | 種別 | 本 issue での扱い |
| --- | --- | --- |
| `deploy/local/wiki-oidc/README.md` | live 手順書 | **手順の正本。自動化前提へ書き換える**（手作業の全文は「自動化が効かないときの手当て」へ降ろす） |
| `deploy/local/wikijs-setup/README.md` | live 手順書 | 相乗り先の README。新しい段（8）を追記 |
| `deploy/local/README.md` | live 手順書 | 331 行の Site URL 注記から本文へリンク。自動化の env を追記 |
| `deploy/local/edge/README.md` | live 手順書 | wiki-oidc/README への参照 2 箇所。**記述は「OIDC は wiki-oidc/README」のみで自動化の有無に触れていない**ので追随不要 |
| `docs/operations/local-sso-recovery-runbook.md` | live runbook | **STEP 3「wikijs DB 再作成時のみ・手動」を自動化前提へ更新**。揮発マトリクスの「復旧」欄も直す |
| `.ai-context/adr/IADR-0095` `IADR-0197` `IADR-0327` `IADR-0328` | 凍結記録 | **書き換えない** |
| `.ai-context/specs/*`（8 件） | 凍結記録 | **書き換えない** |
| `.ai-context/adr/README.md` | live 索引 | 新 IADR の行を足す |
| `CHANGELOG.md` | 生成物 | **手で書かない**（CI が生成する） |

除外理由: `.ai-context/` の確定済み記録は `traceability.repo.md`「Superseded / Deprecated」節の凍結規約に
より本文を書き換えない。`deploy/local/edge/README.md` は「OIDC の設定は wiki-oidc/README を見よ」という
リンクだけで、手動/自動の別を主張していないため誤りにならない。

### 2-b. secret の供給経路（IADR-0098 / IADR-0332 と同型にする先）

`deploy/local/vault/eso/externalsecret-*.yaml` は 18 本。OIDC client secret 群は
`minio-oidc` / `bff-oidc` / `identity-admin-oidc` / `grafana-oidc` / `vault-oidc` / `headlamp-oidc` の 6 本で、
**`wiki-js` の分だけ無い**（realm には client が在るのに供給経路が無い）。3 点セットで足りる:

1. `deploy/local/vault/eso/bootstrap.sh` の seed（`vault kv put secret/msp/<name>`）
2. `deploy/local/vault/eso/externalsecret-<name>.yaml`
3. `scripts/k8s-local-up.sh` の apply（ESO=1）＋ `ESO != 1` の手動 `apply_secret`

### 2-c. realm 側

`deploy/keycloak/microservices-platform-realm.json` の `wiki-js` client は **すでに揃っている**
（`secret`・4 つの `redirectUris`・`webOrigins`・`wikijs-realm-roles` の groups mapper、realm ロール
`Administrators`）。**realm JSON は 1 バイトも変えない** —— #1115（バックチャネルログアウト）が同じ
ファイルを並行編集中なので、触らないことが衝突回避そのものになる。

## 3. 着手前の実測（稼働 k3s・生出力は PR 本文へ）

`kubectl` は `C:/Program Files/Rancher Desktop/resources/resources/win32/bin/kubectl`。

- `authentication` は **2 行**: `local`（`order` 0）と `7c1f6f2e-…-000000000001`（`strategyKey='oidc'`,
  `displayName='Keycloak'`, `order` 1）。**後者が #780 で人が手で入れた行**である。
- `config` の 14 プロパティは IADR-0328 の分離どおり（browser 3 つ＝`https://keycloak.localhost`、
  server 2 つ＝`http://keycloak:8080`）。`clientSecret` は長さ 28 ＝ realm の
  `wiki-js-dev-secret-change-me` と同値。
- `settings.host` は `{"v":"https://wiki.localhost:50000"}`。
- `groups` は `1 Administrators` / `2 Guests`（ともに `isSystem`）。
- `users.providerKey` は `local` が 2 件、oidc の key が **3 件**。

### 🔴 実測で見つかった、手順書そのものの欠陥

`deploy/local/wiki-oidc/README.md` の `DELETE FROM authentication WHERE "strategyKey"='oidc'` は、
**誰か 1 人でも OIDC でログインした後は必ず落ちる**。`users.providerKey` が
`authentication.key` を参照する外部キー `users_providerkey_foreign` があるためである。
ROLLBACK 付きで実測した（陰性対照＝誰も参照していない架空の strategyKey の DELETE は成功する）:

```
--- 陰性対照: 誰も参照していない架空の strategyKey を DELETE（成功するはず） ---
BEGIN / DELETE 0 / ROLLBACK
--- 陽性: README の DELETE FROM authentication WHERE strategyKey=oidc（users が参照中） ---
ERROR:  update or delete on table "authentication" violates foreign key constraint
        "users_providerkey_foreign" on table "users"
DETAIL:  Key (key)=(7c1f6f2e-9d3a-4b5c-8e10-000000000001) is still referenced from table "users".
```

つまり **現行の「冪等」手順は初回しか冪等でない。** 自動化は DELETE→INSERT ではなく
**行を保存したままの UPSERT** でなければならない。

## 4. 決定（詳細は IADR-0334）

### 決定 1: 新しい入口を増やさず `deploy/local/wikijs-setup/bootstrap.sh` の段 8 として置く

IADR-0327 が入れた冪等 bootstrap が、すでに（a）wiki-js の Ready 待ち、（b）コンテナ内 loopback への
GraphQL、（c）`platform-infra/postgres` への psql、（d）Secret 読み書き、（e）best-effort の作法を
すべて持っている。**同じ runtime 状態（Wiki.js の DB にしか無い設定）を面倒みる仕事**なので、
`deploy/local/wiki-oidc/` に別の入口を作らない。

### 決定 2: 適用は GraphQL `authentication.updateStrategies` ではなく DB の UPSERT で行う

`updateStrategies` は魅力的（`WIKI.auth.activateStrategies()` を呼ぶので再起動が要らない）だが、
**渡さなかったストラテジを全部削除する**。稼働イメージの
`/wiki/server/graph/resolvers/authentication.js:232` を直接読んで確認した:

```js
for (const str of _.differenceBy(previousStrategies, args.strategies, 'key')) {
  const hasUsers = await WIKI.models.users.query().count(...).where({ providerKey: str.key }).first()
  if (_.toSafeInteger(hasUsers.total) > 0) { throw new Error(`Cannot delete ...`) }
  else { await WIKI.models.authentication.query().delete().where('key', str.key) }
}
```

したがって oidc だけを渡すと `local` を消しにいき、トランザクションが無いので
**oidc を patch した後に例外**になる。安全に使うには全ストラテジを読み戻して往復させる必要があるが、
読み（`{...propDef, value}`）と書き（`{v: value}`）で **config の形が違う**（同ファイル 59–70 行 と
208–211 行）ため、往復の実装ミスが `local` の設定を静かに消す。**「潰さない」を最優先**して、
oidc の 1 行だけに触る UPSERT を採る。psql 経路は同じ bootstrap の段 3（locale 投入）で実証済み。

### 決定 3: 既存行があればその `key` を再利用する（新しい行を作らない）

`strategyKey='oidc'` の行が在ればその `key` を使い、無ければ README と同じ正準 key
`7c1f6f2e-9d3a-4b5c-8e10-000000000001` を使う。これで（a）`users.providerKey` の外部キーが切れず、
（b）管理 UI から人が作った乱数 key の残骸に**二重行を作らない**。

### 決定 4: 変わったときだけ wiki-js を再起動する

Wiki.js は起動時にストラテジを読む。DB だけ書いても反映されないので rollout が要るが、
**SQL の `ON CONFLICT ... DO UPDATE ... WHERE <差分あり>` ＋ `RETURNING` の件数**で
「実際に変わったか」を返させ、0 件なら再起動しない。これが「2 回目は no-op」の実体である。

### 決定 5: 既定オフの opt-in（`WIKIJS_OIDC=1`）

#1127 の期待どおり。endpoint はエッジ host（`https://keycloak.localhost`）を前提にするので、
`LOCALEDGE` 抜きで既定 ON にすると「押せるが 502 になるボタン」を作ってしまう。
既定オフなら `local` ログインだけが残り、fail-safe。

### 決定 6: client secret は Vault/ESO 経路（`secret/msp/wikijs-oidc`）で供給し、無ければ何もしない

優先順は **env `WIKIJS_OIDC_CLIENT_SECRET` ＞ Secret `microservices-platform/wikijs-oidc`
（key `client-secret`）＞ 断念**。取れなかったときは **既存行に触らない**（空の clientSecret で
上書きして、動いていたログインを壊さない）。リポジトリに載るのは realm と同じ dev プレースホルダ
`wiki-js-dev-secret-change-me` だけで、実値は載らない。

### 決定 7: `settings.host`（Site URL）も同じ段で突き合わせる

Wiki.js はコールバックを `{Site URL}/login/{key}/callback` で組むので、Site URL がズレると
realm に登録があっても `invalid_redirect_uri` になる。**`/finalize` は初回しか通らない**ので、
`WIKIJS_SITE_URL` を変えて bootstrap を再実行しても現状は `settings.host` が動かない
（`wikijs-setup/README.md` の「`WIKIJS_SITE_URL` を渡して再実行すればよい」は**誤り**）。
段 8 で `settings.host` を突き合わせ、この記述も直す。

## 5. 変更するファイル（宣言する作業領域）

- `deploy/local/wikijs-setup/bootstrap.sh` — 段 8 を追加
- `deploy/local/vault/eso/externalsecret-wikijs-oidc.yaml` — 新規
- `deploy/local/vault/eso/bootstrap.sh` — `secret/msp/wikijs-oidc` の seed
- `scripts/k8s-local-up.sh` — `WIKIJS_OIDC=1` の secret 供給（ESO / 手動の両経路）と案内
- `scripts/k8s-local-up.test.js` — 不変条件テスト
- `deploy/local/wiki-oidc/README.md` / `deploy/local/wikijs-setup/README.md` / `deploy/local/README.md`
- `docs/operations/local-sso-recovery-runbook.md`
- `.ai-context/adr/IADR-0334_*.md` ＋ `.ai-context/adr/README.md`

**`deploy/keycloak/microservices-platform-realm.json` は触らない**（#1115 と非重複）。

## 6. 受け入れ基準

1. `WIKIJS_OIDC=1` を立てて bootstrap を走らせると、Wiki.js の DB を作り直した後でも**手で SQL を
   流さずに** OIDC ログインが復旧する。
2. **冪等**: 2 回目の実行が差分なし（`変更なし` を報告し、wiki-js を再起動しない）。
3. **潰さない**: `local` ストラテジ、`users` の行、発行済みの `wikijs-sync` API キーが不変。
4. client secret がリポジトリにもログにも平文で残らない（載るのは realm と同じ dev プレースホルダのみ）。
5. `settings.host` が経路（edge / port-forward）に一致する。
6. fail-safe: 未マッピング利用者は `Guests`（最小権限）へ auto-enroll。`WIKIJS_OIDC` 未設定なら
   `authentication` に一切触らない。
7. `/login/<key>` がエッジ経由で 302（Keycloak の認可端点）を返し、`activeStrategies` に
   Keycloak が出る。
8. 手順書 4 件が自動化前提へ追随し、`scripts/k8s-local-up.test.js` が不変条件を持つ。
9. `/verify` 相当（`check-adr-numbering` / `check-doc-links` / `check-trace-blocks` /
   `check-doc-updated` / `check-default-credentials` / `scripts.test.js` / `k8s-local-up.test.js`）が緑。

## 7. 実測計画（陽性・陰性を対で）

- **陽性対照（作れること）**: 使い捨ての DB `wikijs_oidcprobe` に同じ DDL を作り、seed SQL を
  2 回流して「1 回目＝作成 1 件／2 回目＝0 件」を見る。終わったら DROP する。
- **陰性対照（潰さないこと）**: 稼働 DB では、`displayName` をわざと壊してから bootstrap を走らせ、
  「直る＋再起動する」→ もう一度走らせて「0 件・再起動しない」を見る。前後で `local` 行・`users`・
  `wikijs-sync` の apiKey 長が不変であることを対で示す。
- **陰性対照（既定オフ）**: `WIKIJS_OIDC` を与えずに bootstrap を走らせ、`authentication` に触らない
  ことを見る。
- **ログイン**: `curl --cacert <local-edge-root-ca> --resolve wiki.localhost:50000:127.0.0.1` で
  `/login/<key>` が 302 を返し、`Location` が `https://keycloak.localhost/realms/platform/...` を指すこと。

**他ユニットの Pod を再起動しない。`scripts/k8s-local-up.sh` を丸ごと再実行しない**
（同じクラスタで #1110 / #1115 / #1118 / #1103 / #1126 / #1098 が並行実測中）。

## 8. 未決事項 / 積み残し

- `scripts/verify-oidc-edge-flow.sh` に Wiki.js の段は無い（同スクリプトは SPA→BFF の経路専用）。
  ツール側 7 クライアントの段を足すかは本 issue の射程外とし、必要なら別 issue で起票する。
