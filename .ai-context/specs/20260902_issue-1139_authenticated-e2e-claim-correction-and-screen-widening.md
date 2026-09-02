---
title: 作業仕様書 — 「認証済みの画面は E2E で実走できない」の残存記述を是正し、13 画面のスモークをセッション付きへ広げる（#1139）
type: spec
status: done
related_ids:
  - SC-01
  - SC-02
  - SC-03
  - SC-04
  - SC-05
  - SC-06
  - SC-07
  - SC-08
  - SC-09
  - SC-10
  - SC-11
  - SC-18
  - SC-21
  - NFR
  - ADR-0031
  - ADR-0032
  - IADR-0009
  - IADR-0124
  - IADR-0166
  - IADR-0330
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_spa-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0032_bff-session.md
---

# 作業仕様書: 「実走できない」の是正と、13 画面へのセッション付きスモークの拡張（#1139）

## 起点となる計画書（トレーサビリティ）

- 画面: `05_screens/01_screens.md` §共通シェル・§SC-01〜SC-11 / §SC-18 / §SC-21
  （主要素・固定文言・**描いてはいけないもの**・ロール限定）。
- ADR-0031（SPA スタック）・ADR-0032（BFF セッション方式。SPA はトークンを扱わない）。
- 実装 ADR: IADR-0330（認証済み画面のブラウザ E2E は `/bff/*` のネットワーク層スタブで踏む）・
  IADR-0009（存在秘匿）・IADR-0124（ルート木）・IADR-0166 決定 2（凍結記録は書き換えない）。

## 前提の実測（自分で確かめた。#1099 / PR #1142 の申し送りを鵜呑みにしない）

`src/platform/frontend/e2e/support/bffSession.ts` を読んだ。成立の機序は次のとおりである。

- SPA から出る HTTP は `foundation/api/apiClient` の 1 箇所に収束し、宛先は**同一オリジンの
  相対パス `/bff/*`**。したがって `page.route('**/bff/**')` で全量を受け切れる。
- 認証状態の一次情報は **`GET /bff/auth/me` の応答だけ**（`AuthProvider`）。
  ブラウザ側は HttpOnly Cookie を読まない。**応答を差し替えればセッションは成立する。**
- 応答を用意していない `/bff/*` は 500 ＋ `unhandled` へ積まれ、`expectBffTrafficIsComplete` が
  「1 件も観測していない」「用意し忘れた」の両方を落とす。

`sc12` / `sc17` / `sc19` / `sc20` の 4 本が現に踏んでいることを読んで確認した。
**「認証済みの画面は E2E で実走できない」は誤りである** —— この結論は本作業の前提であり、
本作業の中でも実走（後述「実測」）で再確認する。

## 母集合（規則 9・10 に従い、自分で引いた）

🔴 **issue 本文の 6 件を転記していない。** 「誤りの側の語」で追跡下を走査し、表記ゆれを列挙して
引き直した。走査は作業ツリー（`develop` `66a78f82`）に対して行った。

```console
$ git grep -n -e "実走できない" -e "認証済みの画面を" -e "認証済みの導線を" -e "認証済みの遅延ルート" \
    -- ':!src/ai-stock-trading'
$ git grep -n -e "認証後の画面" -e "認証を要する画面" -e "ログイン後の画面" -e "認証済み画面" \
    -e "踏めない" -e "実行できない" -e "検証できない" -e "往復で成立" -e "InMemoryWebStorage" \
    -- ':!src/ai-stock-trading' ':!.ai-context' ':!CHANGELOG.md'
$ git grep -n -e "ログイン画面到達" -e "ログイン画面へ到達" -e "到達までを検証" -e "ログイン画面まで" \
    -- ':!src/ai-stock-trading' ':!.ai-context' ':!CHANGELOG.md'
$ git grep -n -e "E2E" -e "Playwright" -- 'docs/tests/*.md'   # 限界節の全数
$ git grep -n -e "Playwright" -e "smoke.spec" -- 'docs/*'      # docs/tests の外
```

### 除外（黙って飛ばさず、理由を書く）

| 除外 | 理由 |
| --- | --- |
| `.ai-context/adr/` `.ai-context/specs/` `.ai-context/superpowers/` | **凍結記録**（IADR-0166 決定 2。当時の判断の記録であり、後から本文を書き換えない）。14 件が該当する |
| `CHANGELOG.md` | 生成物（`scripts/gen-changelog.js` ＋ CI が更新する。手で書き足さない） |
| `src/ai-stock-trading/**` | 別リポジトリ（submodule）。本リポジトリからは変更しない |

### 是正する（live な記述）

| # | ファイル | 箇所 | issue に載っていたか |
| --- | --- | --- | --- |
| 1 | `docs/tests/SC-01_search-chat.md` | §E2E「限界」 | ○ |
| 2 | `docs/tests/SC-21_ai-suggestion-list.md` | §🔴 E2E の限界 ＋ 変異試験表 | ○ |
| 3 | `docs/how-to/session-handoff.md` | 波 C の自己訂正（1 箇所） | △ 「2 箇所」とあったが**実際は 1 箇所**（下記） |
| 4 | `src/knowledge/frontend/src/features/adminFlow.test.tsx` | 冒頭コメント | ○ |
| 5 | `src/knowledge/frontend/src/features/searchFlow.test.tsx` | 冒頭コメント | ○ |
| 6 | 🔴 `src/platform/frontend/playwright.config.ts` | 冒頭コメント「ログイン画面到達までを検証する」 | **×（issue が取りこぼしていた）** |
| 7 | 🔴 `.github/workflows/frontend.yml` | e2e ステップのコメント「ログイン画面到達を検証」 | **×（同上）** |

### issue との差（規則 9 の要求。数えを転記せず、差を書く）

- 🔴 **issue は「6 箇所」と書いていたが、live な母集合は 7 箇所である。**
  6・7 は #1099 の**宣言ファイル領域が `src/platform/frontend/e2e/` だった**ために視野の外にあった
  （`playwright.config.ts` は `e2e/` の**外**、`frontend.yml` は別ディレクトリ）。
  誤りの側の語を「実走できない」だけに固定すると捕まらず、**「ログイン画面到達（までを）検証」という
  別の言い回し**で初めて出る。規則 10（是正のたびに引き直す）が効いた実例である。
- 🔴 **`docs/how-to/session-handoff.md` は 2 箇所ではなく 1 箇所である。**
  同ファイルの `677` 行「実走できない経路だったため」は **実 Keycloak を要する TOTP の段**の話であり、
  **これは今も正しい**（本作業のスタブは Keycloak を代替しない。#466 の射程）。
  ここを直すと**正しい記述を誤りへ書き換える**ことになるため、対象外とする。
- `docs/tests/SC-13` / `SC-14` / `SC-15` の「Keycloak を起動しないと検証できない」も**正しい**。
  ログイン画面そのもの・MFA・メール経路は ID プロバイダの実物を要し、`/bff/auth/me` の
  差し替えでは代替できない。対象外。
- `docs/tests/SC-05` / `SC-06` の「画面テストでは踏めない」は **API を直接叩く経路**の話で、別事象。対象外。
- `docs/tests/FR-22` の「E2E は置いていない（BFF 端点が入るまで）」は**通知端点の実装状況**の話で、
  「認証済みは実走できない」ではない。**本 issue の母集合ではない**ため触らない（別途要観察）。

## 判断: 13 画面へ広げるか

**広げる（全 13 画面）。**

- IADR-0330 決定 1 は「画面ごとのブラウザ E2E は**未認証リダイレクト 1 本 ＋ セッションを与えた本体**で
  構成する」を既に決めている。4 画面だけ従い 13 画面が従わないのは**決定の不徹底**であって設計ではない。
- 広げない理由になり得るのは「原理的に踏めない」だけであり、それは実測で否定されている。
- 検出力の差が実害である —— 未認証 1 本だけの spec は**ルートを消しても落ちない**（#918）。
  13 画面はいま**ルートの実在すら E2E で守られていない。**

### 置き方（IADR-0330 決定 2 の適用）

**陽性対照と陰性対照を対で置く。** 画面の性格で 2 型に分ける。

| 型 | 対象 | 陽性対照 | 陰性対照 |
| --- | --- | --- | --- |
| A. ロール限定画面 | SC-05〜SC-07・SC-09〜SC-11 | 権限ロールで見出し・左ナビ項目が出る | 同じ「見つかりませんでした」が出て、**管理端点を呼びにも行かない**（存在秘匿。IADR-0009） |

🔴 **型 A の当て方には 2 つの含意がある**（実装中に決めた。IADR-0332 決定 2・3）。

- **陽性は運用者で当てる**（管理者ではなく）。管理者だけで測ると、許可ロールの列挙から
  運用者が落ちても気づけない —— **緩い側のロールが陽性対照を兼ねる。**
- **陰性は「隣のロール」で当てる。** SC-09 は管理者限定なので陰性は**運用者**にする。
  ここを「ロール無し」にすると、**運用者が紛れ込む変異を検出できない**（実測: 変異 C）。
  運用者にも開く 5 画面は、隣のロールが存在しないため「ロール無し」で当てる。
| B. 全利用者の画面 | SC-01〜SC-04・SC-08・SC-18・SC-21 | 見出しと画面固有の固定文言が出る | 画面が「描いてはいけないもの」を描かない（計画の禁止事項を写像） |

既存 15 本の**未認証リダイレクト spec は残す**（認証ガードそのものの固定であり、本体とは別の観点）。

## 受け入れ基準

1. 上の母集合 7 件が実測に合う文面へ直っている（「できない」→「この spec は踏んでいない／
   踏む形は `support/bffSession.ts`」）。**「実走できない」と読める live な記述が 0 件**であること
   （陽性対照として、正しく残す `SC-13/14/15`・`session-handoff.md:677` が**残っている**ことも確かめる）。
2. 13 画面すべてに**セッションを与えた本体**の test が増えており、各画面で陽性・陰性が対になっている。
3. `pnpm exec playwright test` が全件緑。
4. 🔴 **ルート定義を壊すと落ちる**ことを 1 本で変異試験し、落ちた件数を記録する
   （未認証 1 本は落ちない —— それが基準①の限界そのものである）。
5. `docs/` の表示テキストに計画 ID / IADR / 仕様書名を書かず trace ブロックへ入れている。
6. `/verify` 相当（typecheck / lint / format:check / `check-route-manifest.js` /
   `check-doc-links.js` / `check-trace-blocks.js` / `check-doc-updated.js` / `scripts.test.js`）が緑。

## やらないこと

- 🔴 **実 BFF ＋ Keycloak を起こす真の E2E**（#466 の射程）。本作業はネットワーク層のスタブに閉じ、
  **後段の実応答との一致は固定しない。**「後段まで固定した」と書かない（IADR-0330 決定 5）。
- `src/knowledge/frontend/` の配置是正（#1131 / #1123 が並行中）。触るのは
  `adminFlow.test.tsx` / `searchFlow.test.tsx` の**冒頭コメントだけ**である。
- `.ai-context/` の凍結記録の書き換え。

## 実測

### 環境（この条件を書かないと結果が読めない）

- `pnpm` は Volta のシムが `packageManager` の 10.33.0 へ切り替えようとして **ENOENT で失敗する**。
  🔴 **しかも exit code は 0 である** —— `pnpm install` も `pnpm run build` も
  「成功した」形で何もせずに返っていた（**危うく「ビルドが通った」と記録するところだった**）。
  原因は共有の pnpm ツールディレクトリを別プロセスが並行して入れ直していることで（`_tmp_*` が 19 個）、
  回避は `npm_config_manage_package_manager_versions=false`（10.34.5 で実行）。
  **以降のコマンドはすべてこの環境変数を付けて実行した。**
- `src/ai-stock-trading`（submodule）が未 populate だと `tsc -b` が
  `Cannot find module '@ai-stock-trading/features'` で落ちる。`git submodule update --init` で解消した。

### ブラウザ E2E

| | 変更前 | 変更後 |
| --- | --- | --- |
| spec ファイル | 19 本 | 19 本（増やしていない） |
| テスト件数 | **28 件** | **47 件**（+19） |
| セッションを与えた画面 | 4 画面 | **17 画面**（SPA ルートを持つ全画面） |
| 実行時間 | 5.8 秒 | 9.4 秒（4 並列） |

内訳: ロール限定 6 画面（SC-05〜SC-07 / SC-09〜SC-11）は陽性・陰性で各 2 件、
全利用者 7 画面（SC-01〜SC-04 / SC-08 / SC-18 / SC-21）は陽性・陰性を 1 件に束ねた。

### 変異試験（検出力の実測）

🔴 **最初に採った変異は測れなかった。3 回作り直している。**

| # | 変異 | 結果 |
| --- | --- | --- |
| A | 画面のルートのパスを改名する | 🔴 **`tsc -b` が落ちてビルドできない。** パスは型付きルータの union に入るため、改名は型検査が先に捕まえる。**E2E の検出力はこの変異では測れない**（#918 当時の「0 件」は、E2E を走らせる前に止まる変更に対する測定だった） |
| B | ルート合成の順序を変える（受け皿を先頭へ） | ビルドは通るが **47 件すべて緑＝変異が効いていない。** ルータは宣言順ではなく特異度で解決するため、順序は意味を持たない |
| C | **ロール条件を緩める**（管理者限定の画面へ運用者を通す。1 語） | ✅ **1 件が落ちた** —— 新設した陰性対照。**未認証の 1 本は落ちなかった**（それが従前の限界そのものである） |

🔴 **B で「47 件すべて緑」を得たとき、私は一度それを「変異が検出されなかった」と読みかけた。**
実際には **A の直前の実行が古い `dist` に対して走っており**、変異は成果物に入っていなかった。
`grep -rl "graph-mutated" dist/` という**陽性対照**を置いて初めて分かった。
**「落ちなかった」を報告する前に、変異が成果物に届いていることを別の手段で確かめること。**

**変異はすべて `git checkout` で戻し、残渣 0 を走査で確認した**（`git status` と文字列走査の両方）。

### 検査

| 検査 | 結果 |
| --- | --- |
| `pnpm run typecheck` | 緑（全パッケージ） |
| `pnpm run lint` | 緑（0 errors / 10 warnings。warning はすべて既存の `react-refresh`） |
| `pnpm run format:check` | 緑（1 件を `--write` で整えた） |
| `pnpm run test:e2e` | **47 passed** |
| `node scripts/check-route-manifest.js` | OK（17 画面ぶんの E2E・除外 0 件・誤った主張 0 件） |
| `node scripts/check-doc-links.js` | OK（1063 件） |
| `node scripts/check-trace-blocks.js` | OK（166 件） |
| `node scripts/check-cross-repo-refs.js` | OK（2746 件） |
| `node scripts/check-plan-id-qualification.js` | OK（2276 件） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **674 tests passed** |
| `pnpm run test`（Vitest） | 1271/1272。🔴 **落ちた 1 件は本作業と無関係**（`orvalMutator.test.ts`。ローカル Node 24 だけの赤で CI の Node 22 では緑。既知）。別実行で `OperationsDashboardPage.test.tsx` の 1 件が 5 秒でタイムアウトしたが再実行で緑＝負荷依存のふらつきであり、**どちらも本 PR の差分に無い** |

### 母集合の是正結果（陰性結論には陽性対照を対で）

是正後に同じ語で引き直し、**live な「実走できない」系の記述が残っていない**ことを確認した。
🔴 **陽性対照**: 正しく残すべき記述 —— `session-handoff.md` の実認可サーバを要する経路、
`SC-13/14/15` の「認可サーバを起動しないと検証できない」、`FR-21` の結合テスト、
`coverage-floor.json` の Docker —— は**すべて残っている**（走査が空振りしていない証拠）。
