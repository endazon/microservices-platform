---
title: セッション失効（無効化 → 次の要求で 401）に対するブラウザ側の反応をブラウザ E2E で固定する
type: spec
status: done
related_ids:
  - NFR
  - SC-17
  - ADR-0026
  - ADR-0032
  - IADR-0251
  - IADR-0273
  - IADR-0330
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - "02_requirements/01_requirements.md §セキュリティ（セッション管理）「アカウント無効化時・退職時の全セッション即時失効」"
  - "07_adr/ADR-0032（SPA 認証は BFF セッション方式 / Token Handler。SPA はトークンを扱わない）"
  - "07_adr/ADR-0026（認証・アカウント管理の方式）"
  - "05_screens/01_screens.md §SC-17 アクション「無効化→全セッション即時失効」"
---

# 作業仕様書: セッション失効に対する SPA 側の反応のブラウザ E2E（#439）

## 背景

#439（go-live ブロッカー）の 2026-09-05 の棚卸しは、残射程を 2 つに整理した。

1. 稼働クラスタでの実疎通（`KC-SERVICES0057` 非発生 / `logout_token` 受理 / 直後の `/bff/auth/me` が 401）
   → **#1168 が 2026-09-04 に実測して CLOSED**。
2. 「ログイン → 利用 → 無効化 → 即時失効」の **Playwright E2E が 1 本も無い**
   → 「#466 ＋ #1168 の従属物であり単独では着手できない」として**起票せず据え置き**。

**#466 は 2026-09-05 に、#1168 は 2026-09-04 に CLOSED になった。** 据え置きの条件は解けたので、
2 の受け皿を本作業で作る。**ただし後述のとおり、Playwright の土台では「無効化」そのものを
起こせない。** 何を測り、何を測らないかを本書で確定させる。

## 母集合（規則 9・10。**誤りの側の文字列で走査してから挙げた**）

基点は `origin/develop` `3663b2ba`。

```console
$ git rev-parse --is-shallow-repository
false
```

（本書は `git log` / `git blame` を出典に引いていないが、引ける状態であることを先に確かめた。）

### 母集合の範囲 —— ブラウザ E2E は 1 ディレクトリしか無い

```console
$ git ls-files | grep -iE '(^|/)e2e/' | sed 's#/[^/]*$##' | sort -u
src/ai-stock-trading/...      ← 除外（別プロジェクトの submodule）
src/platform/frontend/e2e
src/platform/frontend/e2e/support
```

```console
$ git ls-files 'src/platform/frontend/e2e/*.spec.ts' | wc -l
19
```

**除外理由**: `src/ai-stock-trading` は submodule（別プロジェクト）であり、本リポジトリの
計画 ID 体系にも認証方式にも属さない（`.claude/rules/traceability.repo.md`）。
`knowledge/frontend` に `e2e/` は**存在しない**（上の列挙が根拠）ので、母集合は
`src/platform/frontend/e2e/` の 19 spec ＋ 土台 1 本で閉じている。

### 陰性の実測（3 通りの語で引いた）

```console
$ git grep -lniE "disable|revok|失効|無効化" -- src/platform/frontend/e2e
src/platform/frontend/e2e/sc19-private-notes.smoke.spec.ts
src/platform/frontend/e2e/sc20-obsidian-settings.smoke.spec.ts
（2 件。いずれも同期トークン／端末の失効であって、アカウント無効化ではない）

$ git grep -lnE "\b401\b" -- src/platform/frontend/e2e
src/platform/frontend/e2e/support/bffSession.ts
（1 件。土台が未認証を表現するための 401 であって、失効の試験ではない）

$ git grep -lniE "disable-user|DisableUser|/disable" -- src/platform/frontend/e2e
（0 件）
```

### 陽性対照（走査が生きている）

```console
$ git grep -lni "login" -- src/platform/frontend/e2e | wc -l
20
```

**結論: 「セッションが失効した後の SPA の振る舞い」を踏むブラウザ E2E は 1 本も無い。**
2026-09-05 の棚卸しの実測（19 spec 中 0 件）を、基点を変えて再現した。

### 規則 10（この変更で新たに誤りになる自分の記述を引き直す）

```console
$ git grep -nE "無効化.*(E2E|即時|401)|即時失効|(E2E|Playwright).*(無効化|失効)" -- docs src .claude scripts
（38 行。うち live な権威文書は docs/authz/bff-session-design.md /
  docs/screens/SC-17_user-account-management.md / docs/tests/SC-17_user-account-management.md）
```

いずれも**「稼働クラスタでの端から端の実測が未達である」**と書いており、本作業はそこを動かさない
（下記「測らないもの」）。したがって**誤りに変わる記述は無い。** 唯一、`docs/tests/SC-17` の
ブラウザ E2E 節は「置いた E2E の一覧」であり、本作業で 1 本増えるので**追記する**。

## 🔴 測れないもの —— 土台には Keycloak も BFF も無い（推定ではなく実測）

| 事実 | 根拠 |
| --- | --- |
| Playwright はビルド済みプレビューに対してだけ走る | `src/platform/frontend/playwright.config.ts` の `webServer.command` は `pnpm run preview` のみ。基盤は 1 つも起動しない |
| 身元はネットワーク層のスタブ | `e2e/support/bffSession.ts` が `page.route('**/bff/**')` で受け切る。**「契約の写しであって後段ではない」と土台自身が宣言している** |
| CI でも同じ | `.github/workflows/frontend.yml` の `e2e` ジョブは `pnpm run test:e2e` だけ（基盤の起動ステップが無い） |
| 統合スタックの CI に Playwright は乗っていない | `.github/workflows/integration-stack.yml` に `playwright` の語が 1 つも無い。実基盤の外形確認は `scripts/verify-oidc-edge-flow.sh`（curl）である |
| その curl 側にも管理者資格情報は無い | 同スクリプトは password grant で**利用者**のトークンを取るだけで、Admin API を叩く口を持たない（`grep -niE "admin-cli|ADMIN" scripts/verify-oidc-edge-flow.sh` → 0 件） |
| そもそも非対話で人のセッションを作れない | 多要素認証が必須で直接付与を全クライアントで閉じている（`docs/screens/SC-17_user-account-management.md` §未決事項 2 が既に記録している） |

**したがって「Keycloak の利用者を無効化する」段は、本土台では起こせない。**
偽の無効化を「無効化した」と書けば、緑は嘘になる。**書かない。**

なお **稼働クラスタでの「無効化（全セッション失効）→ 5 秒後に `/bff/auth/me` が 401」は
#1168 が 2026-09-04 に陰性・陽性の対で実測済み**である（本書はそれを再現しない）。

## 対象範囲

- **対象**: セッションが BFF に honour されなくなった直後の **SPA の振る舞い**。
  - 利用中に次の保護要求が 401 を返したとき、**その 401 で即座に**再認証へ倒れること
    （クライアント側の猶予・キャッシュで失効が遅れないこと）。
  - 再読み込みしたとき、セッション Cookie が honour されない身元応答で**未認証として扱われる**こと。
- **対象外（送り先つき）**:
  - Keycloak → BFF のバックチャネル通知の到達、BFF 側のチケット破棄 → **#1168（実測済み・CLOSED）**
    と `Platform.Bff.Tests`（`BackchannelLogoutTests` / `RedisTicketStoreTests` / `BffSessionFlowTests`）。
  - 実基盤を伴うブラウザ往復（実 Keycloak でのログイン → 管理者による無効化 → 再要求）。
    **受け皿が無い。** 本 PR ではこれを新設しない（§未決事項）。

## 設計

`e2e/support/bffSession.ts` の `handlers` は**関数を受け取れる**（`(call) => reply(status, body)`）。
可変フラグ 1 つで「セッションが生きている / 境界層がもう honour しない」を切り替える。
**土台には手を入れない**（既存 19 spec の振る舞いを変えない）。

🔴 **フラグを倒すのはテストではなく画面の操作である。** 無効化は
`POST /bff/admin/users/{id}/disable`（SC-17 の「無効化（全セッション失効）」ボタン）で起き、
**その要求を観測したときにだけ**倒す —— 実際の境界層で失効を起こすのがまさにこの要求だからで、
テスト側で勝手に倒すと「画面の操作と失効の因果」が消え、フラグの付け替えを試験と呼ぶことになる。

**無効化される利用者は、その席に座っている当人**にする（`sessionUser()` の `subject` と
一覧の行 `id` を一致させる）。他人を無効化しても自分のセッションは失効しないので、
1 つのブラウザセッションで失効を観測するにはこれしかない。

```
（無効化の前）
  → GET /auth/me                      = 200 身元
  → GET /admin/users                  = 200 一覧      ★ 陽性対照（同じ要求が成功する）
（画面から「無効化（全セッション失効）」を押す）
  → POST /admin/users/{id}/disable    = 200           ← ここでフラグが倒れる
（以後、境界層はこのセッションを honour しない）
  → すべての /bff/*                   = 401
```

観測点は 2 つ置く。

1. **即時性**: 無効化が成功すると `useUserAccountActions` の `onSuccess` が一覧を
   invalidate するので、**失効後の最初の `/bff/*` が `GET /admin/users`** になる。
   その 401 で `apiClient` の 401 ハンドラが発火し、`AuthProvider` が
   `/bff/auth/login` へトップレベル遷移する。**観測した呼び出しの並びが
   `disable → GET /admin/users → GET /auth/login` に完全一致すること**を主張する ——
   間に成功した往復が 1 つも無い、が「次の要求で」の機械的な意味である。
   あわせて `GET /admin/users` が 1 回しか出ていない（＝再試行で往復を挟んでいない）ことを見る。
2. **Cookie が honour されないこと**: 同じ状態で読み込み直すと、身元が 401 になり
   `/login` へ落ちる。**保護画面の内容が 1 つも残らない**ことも対で見る。

🔴 **陽性対照を必ず同じテストの中に置く。** 失効前に同じ要求が 200 で通り画面が描かれることを
先に主張しないと、「そもそも画面が出ていないだけ」と区別できない。

配置: `src/platform/frontend/e2e/session-revocation.smoke.spec.ts`。
**`sc<NN>-` 形式にしない** —— `scripts/check-route-manifest.js` 判定 3 は
「画面 feature ごとに `sc<NN>-*.smoke.spec.ts` が 1 本あるか」を見るので、画面に属さない
本 spec を `sc17-` と名乗らせると**画面ごとの網羅の数え方を汚す**（既存の
`login.smoke.spec.ts` / `bundle-splitting.smoke.spec.ts` と同じ扱いにする）。
マニフェストの行は**要らない**（判定 3 は `sc<NN>-` に一致するファイル名からしか SC を拾わない）。

## 受け入れ基準

- [x] 失効前に、保護画面が描かれ保護要求が成功する（★陽性対照）
- [x] 失効後の**最初の**保護要求が 401 になり、**その直後に**再認証へのトップレベル遷移が起きる
      （間に成功する要求が無いことを、観測した呼び出しの並びで主張する）
- [x] 失効後に再読み込みすると未認証として扱われ、保護画面の内容が描かれない
- [x] 応答を用意していない `/bff/*` が 1 件も無い（`expectBffTrafficIsComplete`。空振りを緑にしない）
- [x] `pnpm run typecheck` / `pnpm run lint` / `pnpm run format:check` / `pnpm run test:e2e` が通る
- [x] `check-route-manifest` / `check-test-traceability` / `check-trace-blocks` が通る
- [ ] 🔴 **実 Keycloak の利用者を無効化してブラウザ往復で 401 を観測する** —— **本作業では満たさない**
      （上記「測れないもの」）。**#439 は open のままにする。**

## テスト方針

- 受け入れ基準 1〜3 をそのまま 2 本の `test()` へ写す（即時性 / 再読み込み）。
- 起点 ID は各 `test()` の直前のコメントに置く（`scripts/check-test-traceability.js` の規約）。

### 変異試験（**実測した**。宣言ではない）

いずれも「変異を当てる → 再ビルド → `playwright test session-revocation` → 戻す」で測った。
**2 本が別々の変異で落ちる**ことを確かめてある（両方が同じ 1 点にぶら下がっていない）。

| # | 変異 | 実測 |
| --- | --- | --- |
| M1 | `apiClient` の 401 分岐から `unauthorizedHandler()` を消す | **1 failed / 1 passed** —— 即時性の 1 本目だけが落ちる（`/auth/login` が並びに現れない） |
| M2 | 無効化成功後の `invalidateQueries` を「読むだけ」に変える（＝次の保護要求を出さない） | **1 failed / 1 passed** —— 1 本目だけが落ちる（`toEqual` が 2 件不足で失敗） |
| M3 | 身元の問い合わせを `on401: 'silent'` から `'handle'` に変える | **1 failed / 1 passed** —— 再読み込みの 2 本目だけが落ちる（`/login` へ落ちずに再認証遷移へ倒れる） |
| — | 境界層のチケット破棄を壊す | **落ちない（想定どおり）**。後段は土台の外であり、`Platform.Bff.Tests` が持つ |

## 実行した検証（実出力）

```console
$ pnpm run typecheck            # 6 workspace projects
platform/frontend typecheck: Done

$ pnpm run lint
✖ 10 problems (0 errors, 10 warnings)     # 既存の react-refresh warning のみ。新規は 0

$ pnpm run format:check
All matched files use Prettier code style!

$ pnpm --filter @platform/frontend run test:e2e
  52 passed (6.8s)                        # 既存 50 ＋ 本 spec 2

$ node scripts/check-route-manifest.js
[check-route-manifest] OK: 画面 17 件とマニフェスト 17 行（除外 0 件）が対応し、
  81 件の e2e / 試験仕様に誤った主張はありません（ブラウザ E2E は 17 画面ぶん・除外 0 件）。

$ node scripts/check-test-traceability.js
[check-test-traceability] OK: 仕様書のある起点 ID 55 件中 52 件が写像済み（未写像 3 件はすべて allowlist 済み）。

$ node scripts/check-trace-blocks.js
[check-trace-blocks] OK: 171 件の Markdown に trace ブロックの違反はありません。
```

🔴 **`SC-13` を起点 ID から外した。** 当初は再読み込み後にログイン画面へ落ちることを主張するので
書いていたが、`check-test-traceability` が「allowlist の減らし忘れ」で fail した ——
`scripts/test-traceability-allowlist.json` は SC-13 の削除条件を
**「実環境での E2E（Playwright）を `src/` 配下へ置いた時点」**と定めており、
本 spec は実環境ではないので**条件を満たしていない**。allowlist を緩めるのではなく、
**主張していない ID を書かない**側で直した。

## 計画書との差異

- 差異: **あり（射程の不足）。** 計画（#439 §退行防止）は「E2E（Playwright）: ログイン → 利用 →
  管理者による無効化 → 即時失効の一連」を求めるが、**ブラウザ E2E の土台に認可サーバが無く、
  管理者による無効化を起こせない。** 本作業はそのうち **SPA 側の反応**だけを固定し、
  残りは #439 に残す（新規起票はしない —— 実基盤ブラウザ E2E の受け皿は #439 そのものである）。

## 未決事項

1. 🔴 **実基盤を伴うブラウザ E2E の受け皿が無い。** 統合スタックの CI は curl の外形確認で
   完結しており、そこへ Playwright を載せるかは**未裁定**である（載せるなら実行時間の上限を
   定めた決定記録の改定が要る）。本作業では判断しない。
2. 非対話で人のセッションを作れない制約（多要素認証必須・直接付与の閉鎖）は
   `docs/screens/SC-17_user-account-management.md` §未決事項 2 と同じもので、
   1 の前提でもある。
