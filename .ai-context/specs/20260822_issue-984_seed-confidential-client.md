---
title: 作業仕様書 — ABAC 投入器を confidential クライアントへ追随させる（#984）
type: spec
status: done
related_ids:
  - FR-05
  - NFR
  - IADR-0133
  - IADR-0252
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs: []
issue: "#984"
---

# 作業仕様書: ABAC 投入器の confidential クライアント追随（#984）

## 起点

- `#984`（自動起票）。develop の `Integration Stack` が **`ABAC ポリシーの投入を確定させる`** で落ちる。

```
[seed-abac-policies] Keycloak のトークン取得に失敗しました（401）。ユーザー admin と client bff を確認してください。
```

- `#983` が `#977` の GraphService 起動クラッシュを直した**直後の run**（`dd5471f4` / `32570351938`）で、
  スタック自体は起きている。**落ちているのは投入だけ**である。

## 🔴 実測

### 原因: `#439` が `bff` クライアントを confidential へ変えた

| ref | `bff` の設定 |
| --- | --- |
| `#439` 以前（本日午前に実測） | `publicClient=true` / `directAccessGrantsEnabled=true` / secret **無し** |
| **`origin/develop`（現在）** | **`publicClient=false`** / `directAccessGrantsEnabled=true` / secret **有り** |

投入器は `grant_type=password` を **`client_id=bff` だけ**で送る。
**confidential クライアントは `client_secret` を要求する**ので Keycloak は 401（`invalid_client`）を返す。

### 代替クライアントは無い

realm の全 9 クライアントを実測した。**`directAccessGrantsEnabled=true` は `bff` ただ 1 つ**である。

| clientId | public | directGrant |
| --- | --- | --- |
| **bff** | false | **true** |
| platform-spa | true | false |
| wiki-js / headlamp / grafana / argocd / minio / vault / ai-stock-trading-kb-writer | false | false |

**したがって `bff` を使い続けるほかなく、secret を送るしかない。**

### secret の出所

`bff` の secret は realm ファイルに平文で入っている（24 文字・`bff-de…`）。
他の OIDC クライアント（minio / grafana / vault / headlamp）と同じ
**「dev 既定 or env 上書き」**の形であり、**Keycloak は realm import の値を受け付ける**。

### 🔴 これは今日 2 回目である

| 回 | 契機 | 壊れ方 |
| --- | --- | --- |
| 1 | `#933` が realm のパスワードを一斉変更 | 直書きの既定が追随せず 401 |
| 2 | **`#439` が `bff` を confidential 化** | **secret を送っていないので 401** |

**どちらも realm の変更に投入器が追随しなかった形**である。1 回目は値の写し取り、2 回目は
**クライアントの種別**という構造の変化で、**値を直せば済む話ではない。**

🔴 **1 回目は `ABACSEED` が best-effort（WARN で通す）ため誰にも見えなかった。
2 回目が即座に見えたのは、`#982` 決定 4 で投入の成否を握り潰さない形にしたからである。**
**着地から 1 時間で新しい破損を捕まえた。**

## 決定

### 決定 1: `client_secret` も realm ファイルから引く

パスワードと同じ構造にする（`#982` / `IADR-0252` と同型）。
`ABAC_SEED_CLIENT_SECRET` があればそれを優先する。

### 🔴 決定 2: 「confidential なのに secret を送っていない」を機械で止める

値を直すだけでは、**次に別のクライアントへ切り替えたとき同じ形で落ちる。**
トークン要求の組み立てを純粋関数へ切り出し、
**realm が `publicClient=false` と言っているクライアントに対しては `client_secret` が載ること**を
テストで固定する。**今日の 2 回目は、この不変条件があれば書いた時点で落ちていた。**

### 決定 3: public クライアントには secret を送らない

送っても無害だが、**「送らない」ことを明示的に試験する**。
`publicClient=true` のクライアントへ切り替えたときに余計な値を載せない。

## 受け入れ基準

1. `node scripts/seed-abac-policies.js` が develop の realm で **401 にならない**（統合スタックで実測）
2. `Integration Stack` が **PASS 18 / FAIL 0** で完走する（`#972` の門が通る）
3. **変異**: realm の `bff` を `publicClient: true` に見せかけると、組み立てから `client_secret` が消える
4. **変異**: secret を直書きに戻すと、直書き禁止のテストが落ちる
5. realm の**全クライアントの secret** がスクリプトへ直書きされていない

## 検出しないこと

- **Keycloak が実際に受け付ける secret が realm ファイルの値と一致しているか**は検査しない。
  env 上書き（`ABAC_SEED_CLIENT_SECRET`）が必要な環境では利用者が指定する
- **`directAccessGrantsEnabled` が false になった場合**は救えない。
  その時は password grant 自体が使えないので、**設計から見直しが要る**（本仕様書の射程外）

## 実測の結果（着手後に追記）

`Integration Stack` run **`32572199986`**（`develop` + `#983` + `#439` + 本修正）で
**PASS 18 / FAIL 0**。投入は 401 にならず、`#972` の門が完走した。

```
[11/13] PASS  POST /bff/documents → 201（ABAC が許可を返した）
[12/13] PASS  GET /bff/documents（許可あり）→ 200・1 件・作成した文書を含む
[13/13] PASS  GET /bff/documents（poc-operator）→ 200・0 件（deny-by-default が効いている）
結果: PASS 18 / FAIL 0
```

### 🔴 これで `#972` に残していた未確認事項 2 件が解けた

| 未確認だったこと | 実測 |
| --- | --- |
| develop 取り込み後も `PASS 18 / FAIL 0` か | **成立する** |
| `#439`（BFF セッション）が Bearer 経路を壊していないか | **壊していない**。段 3〜6 でトークンを取得し、段 7〜13 が Bearer で通った |

`AddBffSession` の「**既定スキームを変えない**」という記述は、**実態と一致している**。
（本日午前の時点では「見込み」としか書けなかったもの。**これで実測になった。**）

### 途中で 1 回落ちた。原因は待ち時間の外れ値であり、上限は上げない

1 回目の実行（`32571313127`）は **`Wait for pods to become Ready` が 602 秒でタイムアウト**した。
ただし診断ダンプでは**全 pod が `1/1 Running`** で、切れた直後に揃っていた。

| run | wait 秒 |
| --- | ---: |
| `32564471717` | 30 |
| `32565007348` | 35 |
| `32565225639` | 34 |
| `32566511817` | 30 |
| `32567729826` | 29 |
| `32570351938` | 27 |
| **`32571313127`** | **602（タイムアウト）** |

🔴 **通常は 27〜35 秒で、今回だけ 20 倍の外れ値**である。「上限が厳しすぎる」ではないので
**`--timeout` は上げない** —— 上げると**本当に停止したときに隠れる**。再実行で通った。
`IADR-0248` が記録した「readiness を待つ費用は 33 秒」は現在も妥当である。
