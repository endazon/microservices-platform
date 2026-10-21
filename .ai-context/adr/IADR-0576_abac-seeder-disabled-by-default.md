---
title: IADR-0576 投入器の主体 abac-seeder は realm の宣言で既定無効とし、投入器・運用スクリプトは実行の間だけ master の管理者で有効にして必ず戻す。資格情報は明示か稼働の Secret（OpenBao → ESO）から読み、宣言の値へ落ちない
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0124, IADR-0572, IADR-0485, IADR-0294, IADR-0133, IADR-0503]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密）
related_specs:
  - ../specs/20261022_abac-seeder-disabled-by-default.md
---

# IADR-0576: 投入器の主体を既定で無効にし、実行の間だけ有効にする（GHSA-rh44-px44-p48q）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: オーナー指示（2026-10-22）・claude（一時の有効化の形と資格情報の置き場）

## 起点・関連

- 起点: GHSA-rh44-px44-p48q（多層防御の強化。常時有効な管理主体を残さない）。
- 計画: **ADR-0124 決定 1**。要求 **NFR-18**。
- 前提: [IADR-0572](./IADR-0572_dev-secret-guard-all-service-account-clients.md)（守りの対象の導出。投入器の置き場）・[IADR-0485](./IADR-0485_paired-secrets-create-only-declarations.md)・[IADR-0294](./IADR-0294_mfa-enforcement-and-audit-events.md)（投入器は client_credentials）・[IADR-0133](./IADR-0133_abac-dev-seed.md)・[IADR-0503](./IADR-0503_republish-document-updated-admin-endpoint-and-driver.md)（再発行の駆動器）。

## コンテキストと課題

`abac-seeder` は platform-admin を持つ機密クライアントで、投入器（`seed-*.js`）と運用スクリプト（再発行・アーカイブの駆動器）が client_credentials で名乗る。realm の宣言で常に有効だった。資格情報は明示の env か、無ければ realm の宣言の値（公知の dev の値）から引いていた。このため、secret を入れ替えるとスクリプトが動かない（宣言の値で名乗る）か、宣言の値が稼働に残ることを前提にした運用になっていた。

## 検討した選択肢

1. **常時有効のまま、資格情報だけを Secret から読む** — 入れ替え後も動くが、管理主体が常に名乗れる状態は変わらない。
2. **既定で無効にし、実行の間だけ有効にする（採用）** — 有効な時間を実行の間に絞る。切り替えには master realm の管理者が要る（スクリプトは既に port-forward で Keycloak へ届いている）。
3. **実行のたびに一時の secret を発行して終わりに破棄する** — 切り替えより強いが、消費側（Vault・ESO）と対で書く運用（ADR-0124 決定 1）と衝突し、戻し損ねたときに secret が食い違う。

## 決定

1. **realm の宣言で `abac-seeder` を `enabled: false` にする。** 取り込み・後追いは無効で作り、稼働で有効のまま残っていれば後追いが宣言へ戻す（＝戻し損ねの安全網）。
2. **一時の有効化の口は `scripts/lib/abac-seeder-session.js` 1 本。** `ensureEnabled` は無効なら有効にして「この実行が有効にした」と控え、元から有効なら触らない。`restore` は控えたものだけを無効へ戻す。切り替えは master realm の管理者（明示 `KC_ADMIN_USER` / `KC_ADMIN_PASSWORD` ＞ Secret `platform-infra/keycloak-admin`）が Admin REST で行う。client は GET した表現を丸ごと PUT し、`enabled` だけを変える（項目を欠いた PUT は SA を消し得る）。
3. **戻しの確実さ**: 入口の `finally`（再発行の main・駆動器の run）・投入器の入口の `.then/.catch`・SIGINT と全体の上限（port-forward を残したまま戻してから片付ける）。戻しの失敗は名指して手で閉じる入口（`node scripts/lib/abac-seeder-session.js --live --disable`）を告げる。
4. **資格情報は 明示（`REPUBLISH_CLIENT_SECRET` / `ABAC_SEED_CLIENT_SECRET` 等）＞ 稼働の Secret `platform-infra/abac-seeder-oidc`。** realm の宣言の値へは落ちない。Secret は Vault `msp/abac-seeder-oidc` から ESO（Merge）が同期し、起動器は ESO の有無によらず土台を作る。入れ替えは保管先と認証基盤を対で書けば、次の実行から新しい値で名乗る。
5. 許可リストの `deferred[]` に `msp/abac-seeder-oidc` を足す（対になる秘密。SC-22 の画面では扱わない）。

## 結果

- 良い影響: 管理主体が名乗れるのはスクリプトの実行の間だけになる。secret を入れ替えた後もスクリプトが動く。
- 悪い影響: スクリプトの実行に master realm の管理者の資格情報が要る。同時に 2 本走らせると、先に終わった方が無効へ戻す（後の方は失敗で止まる）。

## 残余

1. 強制終了で戻し損ねた場合、次の起動の後追いか手で閉じるまで有効のまま残る。
2. 同時実行の排他は持たない（運用で 1 本ずつ）。
