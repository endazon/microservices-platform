---
title: 存在秘匿の検査器が public client を前提にしていたのを、標準フローのクライアント（confidential 可）へ緩める
issue: "#1413"
plan_refs:
  - SC-13
  - SC-15
  - FR-05
  - ADR-0032
  - ADR-0078
adr_refs:
  - IADR-0429
  - IADR-0273
  - IADR-0427
status: done
created: 2026-09-11
---

# 作業仕様書: 検査器のクライアント選択（#1413）

## 起点

- `check-password-reset-mail.js --self-test` が「宣言から対象を選べる」で `public client を選べない`。稼働の実行も前提で止まり、
  integration-stack の門（T-10 / T-16 / T-17）が何も測らない。`check-login-existence-disclosure.js` も同じ関数を使う。
- #1393 / PR #1402（IADR-0429）が唯一の public client `platform-spa` を撤去した（意図どおり。戻さない）。

## 確認したこと（Keycloak 24・稼働クラスタ・2026-09-11）

検査器はトークン交換をしない。confidential の `bff` で `GET /realms/platform/protocol/openid-connect/auth?client_id=bff&response_type=code&
redirect_uri=<登録済み>&code_challenge=…` は **200** でログイン画面を返し、本文に `login-actions/reset-credentials?client_id=bff&tab_id=…`
のリンクを含む。client secret は認可エンドポイントでは要らない（要るのはトークン交換だけ）。

## 設計

| 対象 | 変更 |
| --- | --- |
| `check-password-reset-mail.js` | `pickPublicClient` → `pickBrowserFlowClient`: standard flow 有効・bearer-only でない・redirectUri を持つクライアントから、public があればそれ、無ければ `bff`、無ければ先頭（決定的）。戻り値に `publicClient` を持たせる |
| 同 self-test | 陽性（confidential のみの realm で `bff` を選ぶ）・優先（public があればそれ）・陰性対照（bearer-only / standard flow 無効 / redirectUri なし → null）・**ラチェット**（実データの realm に public client が無いこと＝IADR-0429 が保たれていること） |
| `check-login-existence-disclosure.js` | 同関数へ追随。前提メッセージの文言を「標準フローのクライアント」へ |

`platform-spa` は復活させない（IADR-0429）。ラチェット試験が復活を赤にする（戻すなら新 IADR）。

## 走査した母集合（規則 2・9）

`pickPublicClient|public client` で `scripts/` `docs/` `.github/` を走査: 2 スクリプト（変更）。`scripts/README.md`・`docs/tests/SC-13_login.md`・
`docs/tests/SC-15*` に該当記述なし（据え置き）。

## 受け入れ基準

- [x] `check-password-reset-mail.js --self-test` 30 件緑（26 → 30）・`check-login-existence-disclosure.js --self-test` 25 件緑
- [x] 実データの realm で `bff`（confidential）が選ばれる
- [x] 変異（public 必須へ戻す）で self-test が赤
- [ ] develop の integration-stack で門が前提で止まらず T-10 / T-16 / T-17 を測る
