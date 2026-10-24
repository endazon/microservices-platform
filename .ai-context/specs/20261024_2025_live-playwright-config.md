---
title: 作業仕様書 — [E-31] live 用 Playwright config・setup project（TOTP ログイン → storageState）・readiness 門
type: spec
status: done
related_ids: [NFR, NFR-10, IADR-0569, IADR-0570, IADR-0294, IADR-0624]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション。トークンを扱わない）
  - planning:projects/microservices-platform/07_adr/ADR-0090（門は依存ごと。全 skip を緑にしない）
  - planning:projects/microservices-platform/07_adr/ADR-0026（認証は IdP。MFA 必須）
issue: "#2025"
---

# 作業仕様書 — live 用 Playwright config と setup（E-31）

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。親は #2013（設計はコメント 3「稼働中の PoC で E2E を流すための設計」§1〜§2）。
> 先行の E-30（#2024・IADR-0570）が試験アカウント 6 名と資格情報の供給経路（OpenBao → ESO → Secret `platform-infra/e2e-accounts`）を置いた。
> 🔴 **本 PR は稼働中の PoC に触れない**（コードと設定だけ。実ログインの実測は PoC での確認手順として PR に書く）。秘密の値は書かない。

## 受け入れ基準（issue #2025）

- [x] `E2E_LIVE` 未設定で 0 件
- [ ] 設定時に 6 役割の state ファイルが出来る —— **マージ後に PoC で確認（手順は PR #2223 本文）**。偽の BFF と Keycloak に対しては 6 個出来た
- [x] readiness 不成立は exit 2
- [x] state・trace に cookie 値が含まれない（成果物に state も cookie 値も含まれない、と読んだ。下の注記）
  - 0 件・exit 2・値域外の失敗は L1 の `e2e/live-opt-in.smoke.spec.ts` が固定（PR #2223 の CI で緑）。
  - 「成果物に cookie 値・パスワードが出ない」は、偽の BFF と Keycloak に対して確かめた（6 passed・state 6 個・成果物の秘密 0 件）。
    **稼働の PoC での実ログインは未実施**（本 PR は PoC に触れない）。

### 受け入れ基準の読み方（判断）

- 「state・trace に cookie 値が含まれない」: storageState は再利用のために cookie を持つのが本質なので、「**成果物（レポート・trace・
  test-results）に state も cookie 値も含まれない**」と読む。state は `E2E_AUTH_DIR`（既定 `src/platform/frontend/playwright/.auth/`。
  gitignore・レポートの外）にだけ書く。setup の trace・スクリーンショット・動画は切る（trace は `fill` の値＝パスワードと、
  応答の `Set-Cookie` を記録するため）。

## 判断

1. **opt-in（IADR-0569 決定 4）**: `E2E_LIVE` が未設定・空なら全 project を 0 件にして exit 0（`--pass-with-no-tests`）。`1` だけが live で、
   それ以外の値（例示の `<set-me>` のまま・`true` など）は config の読み込みで失敗させる（［2026-10-24 / PR #2223 の AI レビュー 🟡］初版は
   「`1` 以外は 0 件」で、例示を写したまま直し忘れると黙って 0 件の緑になった）。
   `E2E_LAYER` は `ro`/`rw`/`trading`/`llm` のカンマ区切り・既定 `ro`。値域外、または `E2E_LIVE=1` 無しの `E2E_LAYER` は config の読み込みで失敗させる。
   判定は `e2e/live/support/env.ts` の純関数に置き、**L1 の Playwright の spec `e2e/live-opt-in.smoke.spec.ts`** で固定する（live の config を子プロセスで
   起動して、未設定・空・`1`・それ以外の 4 通りと exit 2 を見る。稼働の環境には触れない。Vitest の include は `src/**` だけなので e2e の support を拾わない）。
   `E2E_INSECURE_TLS=1` の警告は、ワーカーごとに読み込まれる config ではなく globalSetup で 1 回だけ出す。
2. **readiness 門**: `globalSetup`（`e2e/live/support/readiness.ts`）が live のときだけ走り、
   ① 12 個の資格情報の環境変数が揃っている（名前だけを報告。空・空白だけ・例示の `<set-me>` のままは未設定に数える ——
   ［2026-10-24 / PR #2223 の AI レビュー 🟢］`<set-me>` のまま門を通すと実の Keycloak へ 6 回の失敗ログインを流し、brute force 保護でロックされうる）② `${E2E_KC_URL}/realms/platform/.well-known/openid-configuration` が 200
   ③ `${E2E_EDGE_URL}/bff/auth/me` が 401、を確かめる。満たさなければ理由を出して **exit 2**（skip の緑とも失敗の赤とも区別。
   `check-stack-ready.js --live` と同じ扱い）。
3. **setup（`e2e/live/auth.setup.ts`）**: 役割ごとに 1 回ログインする（`/bff/auth/login?returnUrl=/` → Keycloak の `#username`/`#password`/`#kc-login`
   → `#otp` → BFF の callback → SPA）。`/bff/auth/me` が 200 で subject を持ち、役割を見分けるロール（`platform-admin` / `platform-operator` /
   `trading-owner`）がその役割の宣言と一致し、BFF のオリジンの cookie が保存されたことを確かめてから storageState を `<authDir>/<role>.json` に書く
   （身元の口は利用者名を返さないので名前では見ない。判断 9）。
   `retries: 0`（brute force 保護。基準書 §5）。失敗した段（ログイン画面・OTP 画面・コールバック）を本文に attach する（URL は path だけ。query は載せない）。
4. **TOTP**: `scripts/lib/totp.js` を `createRequire` で読む（自前の実装を増やさない）。秘密は Keycloak が持つ生の文字列なので
   `totp(base32Encode(raw))`（IADR-0570 決定 3）。周期の残りが 5 秒未満なら次の周期まで待ってから計算する。
5. **到達先**: `E2E_EDGE_URL`（既定 `https://localhost`）・`E2E_KC_URL`（既定 `https://keycloak.localhost`）。`webServer` は置かない。
   `E2E_INSECURE_TLS=1` のときだけ `ignoreHTTPSErrors`（警告を出す）。
6. **L1 の config は `e2e/live/**` を `testIgnore` する**（IADR-0569 決定 3）。L1 の既定の testMatch は `*.ro.spec.ts` も拾うため。
7. **`.env.e2e`**: `e2e/live/.env.e2e` があれば `process.loadEnvFile` で読む（既存の環境変数は上書きしない）。
   例示 `e2e/live/.env.e2e.example` は変数名と `<set-me>` だけ。`.env.e2e` はリポジトリ直下の `.gitignore` の `*.e2e` が既に外す。

8. **秘密は `fill` で入れない**（実装中の実測で追加）。偽の BFF と Keycloak に対して setup を流したら、`locator.fill(password)` の値が
   step の題として html レポートに 6 名分残った。`fillSecret`（`evaluate` で値を入れ input / change を起こす）に替え、再測で 0 件になった。
9. **身元の確かめ方**: `/bff/auth/me` は名前・subject・ロールを返す（利用者名は返さない）。役割を見分けるロール
   （`platform-admin` / `platform-operator` / `trading-owner`）がその役割の宣言と一致することを見る。
10. **Knip**: live の config は既定名でなく、`E2E_LIVE` が無いと setup と門を読まないので、`auth.setup.ts` と `readiness.ts` を entry に名指しした。
    config が読み込み時に標準出力へ書くと Knip の JSON が壊れる（実測）ので、案内は標準エラーへ書く。

## 母集合（規則 9・10）

- 誤りの側の文字列: 「`playwright.live.config.ts` と `e2e/live/` はまだ無い」「live-setup … `retain-on-failure`」。`docs/`・`.ai-context/adr/`・`src/` を走査した。
  - 追随したもの: `docs/tests/E2E_STANDARDS.md` §2.1（現状・setup の trace）・§9。
  - 除外したもの: IADR-0569 決定 3 の表（凍結。setup の trace を `off` にした変更は IADR-0624 が記録する）。

## 検証

```bash
cd src && pnpm install --frozen-lockfile
cd src && pnpm --filter @platform/frontend run test:e2e:live            # E2E_LIVE 無し → 0 件・exit 0
cd src && E2E_LAYER=ro pnpm --filter @platform/frontend run test:e2e:live   # config の読み込みで失敗（exit ≠ 0）
cd src && E2E_LIVE=1 E2E_EDGE_URL=http://127.0.0.1:9 E2E_KC_URL=http://127.0.0.1:9 pnpm --filter @platform/frontend run test:e2e:live  # exit 2
cd src && pnpm run test:coverage && pnpm run lint && pnpm run typecheck && pnpm run format:check
cd src && pnpm run build && CI=1 pnpm run test:e2e                         # L1 は変わらない
```

- 実ログイン（6 役割の state）は手元に Keycloak が無い。偽の BFF と Keycloak（ログイン画面・OTP 画面・callback・`/bff/auth/me` を真似る
  小さな HTTP サーバ。scratchpad に置き、コミットしない）に対して setup を通し、6 個の state が出来ること・レポートと test-results に
  cookie 値とパスワードが出ないことを確かめる。稼働の PoC での確認手順は PR 本文に書く。
