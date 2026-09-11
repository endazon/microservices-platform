---
title: IADR-0429 SPA の public client を realm から撤去し、BFF の Bearer 受理を「ブラウザが取得し得ないトークン」に絞る
type: impl-adr
status: Accepted
related_ids: [NFR, SC-13, SC-16, ADR-0026, ADR-0031, ADR-0032, IADR-0033, IADR-0197, IADR-0251, IADR-0273, IADR-0420]
author: claude
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - ../specs/20260911_issue-1393_remove-platform-spa-public-client.md
---

# IADR-0429: `platform-spa` の撤去と Bearer 受理の絞り込み（#1393 / #439 残射程 1）

> 実装リポジトリ内の意思決定記録。[IADR-0273](./IADR-0273_bff-session-completion.md) の
> フォローアップ（「`platform-spa` public client・実行時 config の `oidc.clientId` を撤去する」）を
> 実行し、あわせて [IADR-0251](./IADR-0251_bff-session-token-handler.md) 決定 9 の
> **Bearer 受理を一段狭める**。

- 状態: Accepted
- 日付: 2026-09-11
- 決定者: Claude（実装）

## コンテキストと課題

ADR-0032 の移行（#439）で SPA はトークンを扱わなくなった（`oidc-client-ts` は撤去済み・
ESLint が再導入を禁止）。それでも realm には **`platform-spa`**（`publicClient: true` /
`standardFlowEnabled: true` / PKCE）が残っており、**ブラウザから利用者のアクセストークンを
取得できる口**が開いていた。

BFF の側は `BffSmart` 振り分け（IADR-0251 決定 9）により、**`Authorization: Bearer` が在れば
realm が発行した有効な JWT を誰の名義でも受理する**。この 2 つが揃うと、
「ブラウザが public client でトークンを取り、`/bff/*` を Bearer で直接叩く」経路が成立し、
**HttpOnly セッション Cookie と CSRF ヘッダ（IADR-0251 決定 1 の 2 枚の壁）が丸ごと迂回される。**

撤去できなかった理由は「AST（別リポジトリ）の追随待ち」だったが、submodule pin `1da636de` に
`platform-spa` の参照は 0 件であり（#439 の 2026-09-11 監査）、**その理由は消えている。**

## 決定 1: `platform-spa` を realm から削除する（clients 25 → 24 件）

`deploy/keycloak/microservices-platform-realm.json` から client 定義を削除した。
**同型の public client を足し直さない** —— 足せば上の迂回経路が復活する。

## 決定 2: 実行時 config の `oidc.clientId` は**値を替えるのではなく層ごと落とす**

SPA の `oidc.clientId` は **読む製品コードが 1 つも無かった**（#439 の 2026-09-11 監査が
「静的に追っていない」と残した未測定事項を、本作業で追った）。

```console
$ git grep -n "clientId" -- src/platform/frontend/src src/knowledge/frontend/src
（型・既定・合成・テストのみ。消費点 0 件）
$ git grep -n "appConfig().oidc" -- src
src/platform/frontend/src/app/Layout.tsx:145  accountConsoleUrl(appConfig().oidc.authority)   ← 陽性対照
```

死んでいたのは **config.js → 型 → env 既定 → helm values/template → compose の 5 層**である。
`bff` へ書き換える案は捨てた —— **生きていない設定は、次の書き手に「ここを直せば効く」と
誤読させる**。`oidc.authority` は SC-16 のアカウントコンソール導線が読むので残す。

## 決定 3: Bearer 腕を「無人の主体」＋「BFF 自身のクライアント名義」に絞る

`BearerCallerPolicy.IsAcceptedCaller` を新設し、`JwtBearerOptions.OnTokenValidated`（`PostConfigure`）
から掛けた。**どちらの腕にも当たらなければ `ctx.Fail`（→ 401）。**

| 腕 | 条件 | 何を通すか |
| --- | --- | --- |
| A | `MachinePrincipal.IsMachine`（IADR-0420） | サービス間 Bearer（サービスアカウント・`profile` を持たない機械クライアント） |
| B | `azp` が `BffSession:ClientId`（既定 `bff`）と一致 | 非ブラウザの外形確認（`scripts/verify-oidc-edge-flow.sh`） |

🔴 **腕 B が抜け道にならないのは、`bff` が confidential client だからである。**
ブラウザは client_secret を持てないので、この名義のトークンを取得できない。
ADR-0032 が禁じた「SPA がトークンを扱う」形はこの腕では成立しない。

🔴 **クライアント ID の許可リストを構成に持たない。** 見るのは BFF 自身が OIDC で名乗る値
ただ 1 つである —— 一覧に足すだけで通る形にすると、統制が構成ファイルの編集権限まで薄まる
（IADR-0420 が許可リストを退けたのと同じ理由）。

🔴 **狭める条件**（IADR-0251 決定 9 条件 1 をそのまま引き継ぐ）: `verify-oidc-edge-flow.sh` が
Cookie 方式へ移ったら**腕 B を落とす**（機械のみ＝`ServiceCaller` と同じ強さ）。
狭めるのは緩める方向ではないので後から実施できる。

### なぜ realm の撤去だけで足りないか

realm は複数環境で運用され、**同型の public client は再び足され得る**（`platform-spa` は #126 から
8 か月残った）。口だけ閉じると、口が開いた瞬間に迂回が復活する。逆に BFF の門だけだと、
別のリソースサーバ（`/bff/*` 以外）が同じトークンを受けてしまう。**口と受理の両方を閉じる。**

## 決定 4: `verify-oidc-edge-flow.sh` は `bff`（confidential）で認可コードを取る。Cookie 方式化はしない

同スクリプトは CI の門（`integration-stack.yml`）が走らせる**統合スタックの外形確認**であり、
`platform-spa` で認可コードを取っていた。撤去すると認可端点が `invalid client` で落ちる。

- 変更は 3 点だけ: `OIDC_CLIENT_ID` 既定を `bff`、`OIDC_REDIRECT_URI` 既定を
  `${EDGE_URL}/bff/auth/callback`（`bff` に登録済み）、トークン交換に `client_secret` を添える。
- **secret のリテラルをスクリプトへ書かない** —— dev realm の単一情報源（realm JSON）から読む。
  実環境は `OIDC_CLIENT_SECRET` / `BFF_OIDC_CLIENT_SECRET` で上書きする。
- 🔴 **Cookie 方式への書き換えは採らなかった**（IADR-0251 決定 9 条件 1 は**開いたまま**）。
  20 段超・700 行の検証器を書き換えるのは本 issue の射程を超え、**CI の唯一の外形確認を
  同じ PR で作り直すのは、失敗したときに原因が切り分けられなくなる**。

## テストと変異試験（検出力の実測。戻して残渣 0 を確認済み）

| 変異 | 落ちたテスト | 戻し確認 |
| --- | --- | --- |
| `IsAcceptedCaller` を「常に受理」へ（＝門を外す＝#1393 以前の姿） | **陰性対照 11 件**（public client 1 ／他ブラウザ client 4 ／未認証 1 ／空 ClientId 3 ／`azp` 欠落 1 ／配線 1）。**陽性 4 件は緑のまま** | 残渣 0 |

**陽性だけを測ると門を外しても緑になる**ので、陰性対照が本体である。
配線（`OnTokenValidated` が実際に呼ぶこと）も陰性・陽性の対で測る —— 純粋関数だけを測ると
「関数は正しいが誰も呼んでいない」形が緑になる。

## 結果

- 良い影響: **ブラウザが取得し得るトークンで `/bff/*` を叩く経路が構造的に消えた**（口と受理の両方）。
  死んだ設定（`oidc.clientId`）が 5 層から消え、realm の client は 24 件になった。
  `scripts.repo.test.js` に「realm に public client が 0 件」のラチェットを置いた。
- トレードオフ: 腕 B（BFF 自身の client 名義の**利用者**トークン）が移行期の負債として残る。
  `verify-oidc-edge-flow.sh` の Cookie 方式化が終わるまで消せない。
  ローカル port-forward `8081` での OIDC ログインは `bff` の redirect に無いため成立しない
  （**#1393 以前から BFF セッション移行で成立していなかった**もので、本作業は文書を実態へ揃えただけである）。
- フォローアップ: `verify-oidc-edge-flow.sh` の Cookie 方式化（IADR-0251 決定 9 条件 1）。
  IADR-0273 決定 7 の AST 互換 JWT フォールバック（`roles.ts`）の撤去。

## 関連

- 継承: [IADR-0251](./IADR-0251_bff-session-token-handler.md) 決定 9（振り分けスキームと狭める条件）
- 実行: [IADR-0273](./IADR-0273_bff-session-completion.md) §フォローアップ の 2 件（public client・`oidc.clientId`）
- 判定の再利用: [IADR-0420](./IADR-0420_unit-subject-identification-and-project-missing-metric.md)（`MachinePrincipal`）
- 改名の経緯: [IADR-0197](./IADR-0197_realm-rename-and-auth-policy.md) 決定 31（`spa-web` → `platform-spa`）
