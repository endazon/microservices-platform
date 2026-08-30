---
title: ADR-0067 フロントエンド層分類の是正と import 方向規則の両ユニット配備
type: spec
status: done
related_ids: [NFR, ADR-0067, ADR-0066, ADR-0031, IADR-0308, IADR-0262, IADR-0311]
author: Claude (implementation agent)
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0067_frontend-layer-classification-and-composition-point.md
  - planning:projects/microservices-platform/07_adr/ADR-0066_frontend-feature-isolation-and-import-direction.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md (§ディレクトリ構成)
related_specs:
  - ./20260830_issue-1065_feature-import-isolation.md
---

# 仕様書: ADR-0067（層分類の是正）を実装し、`import/no-restricted-paths` を platform へ配備する

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（非機能。保守性）
- ユースケース（UC）: なし
- 画面（SC）: なし（共通シェル `Layout` の置き場が動くため SC-01〜SC-11 の記述に波及する）
- 関連 ADR: **ADR-0067**（本作業の起点。Accepted 2026-08-30）/ ADR-0066（決定 2 を部分改定される側）/ ADR-0031
- 関連 IADR: IADR-0308（規則を platform へ配備できなかった記録）/ IADR-0262 決定 1（`@foundation` エイリアス。**覆さない**）/ IADR-0057（可変ユニットは `@foundation` のみ参照可。**覆さない**）
- 計画書リンク: planning リポジトリの `projects/microservices-platform/07_adr/ADR-0067_frontend-layer-classification-and-composition-point.md`（隣接クローンまたは GitHub 上で読む）

## 目的・背景

ADR-0066 決定 3 は `import/no-restricted-paths` による依存方向の機械強制を必須としたが、
#1065（PR #1077）は **knowledge ユニットにしか配備できなかった**。原因は「`@foundation` 設計との衝突」
ではなく、**ADR-0066 が原典（Bulletproof React）から向きの規則だけを採り、層の分類を採らなかったこと**
である（ADR-0067 §根本原因）。

本作業は ADR-0067 の決定 1〜6 を実装し、**両ユニットへ規則を配備する**。
**方向の禁止は 1 つも緩めない。**

## 自分で引いた実測（着手前）

計画側の実測を信用せず、`origin/develop` `2631eff1`（本ブランチの基点）で自分で走査した。
走査器は使い捨てスクリプト（`src/platform/frontend/src` 配下の `.ts/.tsx` を全走査し、
相対 import と `@foundation/*` エイリアスを実体パスへ解決してから ADR-0066 決定 2 の分類で判定）。
`vi.mock()` の文字列とコメント中の言及は import 文ではないので数に入れない。

| 向き | ファイル | import 文 | 内訳 |
| --- | ---: | ---: | --- |
| `features` → `app` | **1**（`features/index.ts` ＝合成点） | **3** | `routing` 3 |
| `shared` → `app` | **8** | **13** | `config` 5 / `i18n` 3 / `routing` 5 |
| `shared` → `features` | 0 | 0 | — |

ファイル別:

| ファイル | `config` | `i18n` | `routing` |
| --- | ---: | ---: | ---: |
| `components/ui/Layout.tsx` | 1 | — | 4 |
| `components/ui/Layout.test.tsx` | 1 | — | 1 |
| `components/ai-chat/AiChatPanel.test.tsx` | — | 1 | — |
| `components/notifications/NotificationBell.test.tsx` | — | 1 | — |
| `components/notifications/notificationMessages.test.ts` | — | 1 | — |
| `lib/api/apiClient.ts` | 1 | — | — |
| `lib/api/orvalMutator.test.ts` | 1 | — | — |
| `lib/auth/AuthProvider.tsx` | 1 | — | — |

**ADR-0067 の実測と向きの内訳は一致する。** 件数の差は 1 点だけで、
ADR は `shared → app` を「9 ファイル」としているが、9 件目の `lib/api/sse.test.ts` が持つのは
`vi.mock('@foundation/config/runtimeConfig', …)` の**文字列**であり `import` 文ではない
（ADR 自身の内訳表も同じ注記を付けている）。**import 文の数 13 は一致する。**
環流が報告した「16 件・すべて `features → app`」は**向きの内訳が誤り**であった
（16 は `3 + 13` の合計としては合うが、本体は `shared → app` である）。

そのほか、規則を書くうえで要る実測:

- `testing/` は `@foundation/i18n`（→ `app/i18n`）と `@foundation/routing/shell`（→ `app/routing`）を引く
  （`renderUnitRoute.tsx` 3 文・`setup.ts` 1 文）。**ADR-0067 決定 5 が `testing/` を第 4 の層にした理由の実物である。**
- `testing/` を引いているのは `components/notifications/NotificationBell.test.tsx` の 1 文だけで、
  **本番コードからの参照は 0 件**である。
- `assets/` `hooks/` `stores/` `types/` `utils/` は platform では**空**（`.gitkeep` のみ）。

## 対象範囲

- 対象:
  - **移送**（決定 1・2・6）: `app/config` → `config/`、`app/i18n` → `lib/i18n/`、`components/ui/Layout.{tsx,test.tsx}` → `app/`
  - **エイリアスの向き先**（5 箇所）: `@foundation/config` / `@foundation/i18n`。**エイリアス名と個数は変えない**（IADR-0262 決定 1）
  - **ESLint**: `import/no-restricted-paths` を **platform ユニットへ配備**し、ゾーン定義を決定 5 の 4 層へ改める
  - lingui の `files` 許可リストの追随（移送で静かに検査対象から外れるのを防ぐ）
  - 移送によって誤りになる自分の記述（`docs/` ・ README ・ 雛形 ・ 検査スクリプト）の追随
- 対象外:
  - **#1078**（lingui 許可リストに 6 feature が欠けている件）。本 PR は**壊さない**が**直さない**
  - `src/ai-stock-trading`（submodule。本ワークツリーでは未チェックアウト。ADR-0067 フォローアップ 3 は当該リポジトリ側の作業）
  - `.ai-context/specs/` と `.ai-context/superpowers/` の確定済み記録（凍結。本文を書き換えない）
  - 計画側の `13_frontend-stack` §ディレクトリ構成 の追随（ADR-0067 フォローアップ 1 が「本 PR で実施する」＝計画リポ側で完了済み）

## 母集合の引き方と除外理由（`traceability.repo.md` 規則 2・9・10）

記憶で挙げず、**誤りの側の文字列で追跡下の全ファイルを走査**した。

| 走査した文字列 | 目的 |
| --- | --- |
| `app/config` / `app/i18n` | 決定 1・2 の移送で誤りになる記述 |
| `components/ui/Layout` / `foundation/ui/Layout` | 決定 6 の移送で誤りになる記述 |
| `SHARED_DIRS` / `featureIsolationZones` | ゾーン定義の実体 |
| `@foundation/config` / `@foundation/i18n` | エイリアス利用側（**向き先だけが動くので変更不要**であることの確認） |

除外したもの（理由つき）:

| 除外 | 理由 |
| --- | --- |
| `.ai-context/adr/IADR-0308`・`.ai-context/specs/*` | 凍結記録。本文プロズを後から書き換えない（CLAUDE.md / `traceability.repo.md` §凍結の射程） |
| `src/platform/frontend/src/locales/*.po` の `#: …/components/ui/Layout.tsx` | 生成物。`pnpm run i18n` の再生成で追随する（手で書き換えない） |
| `src/ai-stock-trading/**` | 別プロジェクトの submodule |
| `.ai-context/adr/IADR-0262` | **除外しない。** live な IADR であり、日付つき追記で追随させる（`traceability.repo.md` §Superseded な ADR の引用） |

## 設計

### 1. 移送（決定 1・2・6）

| 移送元 | 移送先 | 根拠 |
| --- | --- | --- |
| `platform/frontend/src/app/config/runtimeConfig.{ts,test.ts}` | `platform/frontend/src/config/` | 決定 1 |
| `platform/frontend/src/app/i18n/{index.ts,i18n.test.tsx}` | `platform/frontend/src/lib/i18n/` | 決定 2 |
| `platform/frontend/src/components/ui/Layout.{tsx,test.tsx}` | `platform/frontend/src/app/` | 決定 6 |

- **翻訳カタログ（`locales/`）は動かさない**（決定 2 のただし書き）。`lib/i18n/index.ts` から見た
  相対の深さは `app/i18n/` と同じ（`../../locales/...`）なので import は変わらない。
- **`Layout` の置き場は `app/` 直下**とする（`app/routing/` の中ではない）。理由は実装 ADR に残す。
- `Layout.tsx` の `./notifications`（`components/ui/notifications`）は `@foundation/ui/notifications` になる（app → shared。合法）。
- `Layout` を引いているのは `app/routing/shell.tsx`（import）と `app/routing/initialChunk.test.ts`（`vi.mock`）の 2 箇所だけで、
  どちらも `app/` の中なので**相対参照**に変える。**`@foundation/ui/Layout` は消える**（可変ユニットは Layout を引いていない。実測）。

### 2. エイリアスの向き先（5 箇所。ずれると静かに割れる）

`@foundation/config` → `src/config`、`@foundation/i18n` → `src/lib/i18n`。

1. `src/platform/frontend/tsconfig.app.json`
2. `src/platform/frontend/vite.config.ts`
3. `src/vitest.config.ts`
4. `src/knowledge/frontend/tsconfig.json`
5. `templates/unit-template/frontend/tsconfig.json`

（1〜3 は README が「3 箇所とも同じ向き先を持たせる」と書いている組。4・5 は可変ユニット側の解決。）

### 3. ESLint（本作業の眼目）

**🔴 flat config は同一ルールを後勝ちで置換する。** 競合するブロックを新設せず、
**既存ブロックを編集する**（`eslint.config.js` 冒頭と 281 行目付近の警告）。

- `SHARED_DIRS` に **`config` / `assets` / `locales`** を足す（決定 1・5）。
- ゾーンを決定 5 の 4 層へ改める。
  1. feature どうし（決定 1。既存）
  2. shared → features / app 禁止（既存。target が増える）
  3. features → app 禁止（既存）
  4. **testing → features 禁止**（新設）
  5. **本番コード → testing 禁止**（新設。`testing/` は参照される側にならない）
- **5 はテストファイルに掛けない。** 「本番コードから参照しない」が決定 5 の文言であり、
  テストがテストユーティリティを引くのは正しい（実測 1 件）。
  **`import/no-restricted-paths` の `target` は glob を受けるが、本リポジトリでは使わない** ——
  minimatch はパス区切りに `/` を要求し、Windows の絶対パス（`\`）と一致しない。
  **CI（Linux）で効いてローカル（Windows）で静かに 0 件になる**形になるためである。
  かわりに**「本番コード限定のブロック」を後段に置いて 5 を足す**（同一ルールの後勝ちを**意図して**使う）。
- **合成点（決定 4）は既存のブロックレベル `ignores`（`platform/frontend/src/features/index.ts`）が担う。**
  ゾーン側に 2 本目の除外を書くと同じパスが 2 箇所に載って片方が腐る。
- **`settings['import/resolver'].node.extensions` を platform ブロックにも置く**
  （IADR-0308 の教訓。無いと `.ts/.tsx` が 1 件も解決されず**規則が静かに 0 件で通る**）。

### 4. lingui の `files` 許可リストの追随（壊さない）

- `platform/frontend/src/app/i18n/**` → `platform/frontend/src/lib/i18n/**`
- `platform/frontend/src/components/ui/**` は残す（`Layout` 以外が居る）。**`platform/frontend/src/app/Layout.tsx` を足す**
  —— 足さないと i18n 化済みのシェルが**静かに検査されなくなる**。
- **#1078 が指す 6 feature の欠落は本 PR では直さない**（別 issue の射程）。

### 5. 追随（走査で挙げた母集合）

`src/eslint-suppressions.json`（`app/i18n/i18n.test.tsx` のキー）/ `scripts/scripts.repo.test.js`（`app/i18n/index.ts` を読む検査）/
`src/platform/frontend/README.md`（ツリー・エイリアス表）/ `templates/unit-template/README.md` ＋ `frontend/src/config/.gitkeep` /
`docs/tech/composable-component-guide.md` / `docs/screens/SC-01〜SC-11`・`docs/screens/SC-10` / `docs/tests/SC-16` /
`.ai-context/adr/IADR-0262`（日付つき追記）。

## 受け入れ基準

- [x] `app/config` `app/i18n` が `src/` 直下 `config/` と `lib/i18n/` へ移り、`Layout` が `app/` へ移っている
- [x] `@foundation` のエイリアス名・個数が変わっていない（9 本のまま）
- [x] `import/no-restricted-paths` が **platform ユニットにも掛かっている**
- [x] 4 層（shared / features / app / testing）すべてがゾーン定義に現れる
- [x] **注入した違反が両向きで検出される**（§注入試験）
- [x] `pnpm run lint` が **0 errors**（基点コミットと同じ 0 errors / 9 warnings）
- [x] typecheck / test / build / format:check と各検査スクリプトが通る（§検証の実測）

## テスト方針

- **規則が効くことは注入で示す**（宣言では不合格）。一時ファイルで各向きの違反を作り、`pnpm run lint` が
  error を出すことを実測し、削除して緑に戻ることを実測する。両方の出力を仕様書へ残す。
- 移送そのものの回帰は既存のテスト（`i18n.test.tsx` / `runtimeConfig.test.ts` / `Layout.test.tsx` /
  `initialChunk.test.ts`）が持つ。**テストは中身を変えず一緒に移す。**

## 計画書との差異

- 差異: なし（決定 1〜6 をそのまま実装する）。
  なお ADR の `shared → app` の「9 ファイル」は `vi.mock` の文字列 1 件を含む数え方であり、
  `import` 文だけなら 8 ファイル・13 文である。**判断には影響しない**（ADR も件数ではなく向きを使うと明記している）。

## 注入試験（規則が効くことの証跡）

**宣言では不合格。** 一時ファイルで各向きの違反を作り、`pnpm exec eslint <files>` の出力を実測した。

### 🔴 先に見つけた穴 —— エイリアスで書かれた越境は素通りしていた

分類の是正とゾーンの配備だけでは足りなかった。同じ内容を相対とエイリアスの 2 通りで書いて比べた。

```
$ pnpm exec eslint platform/frontend/src/components/__zone_probe_rel.ts                    platform/frontend/src/components/__zone_probe_alias.ts
.../components/__zone_probe_rel.ts
  1:27  error  Unexpected path "../app/routing/router" imported in restricted zone. …  import/no-restricted-paths

✖ 1 problem (1 error, 0 warnings)
```

**`@foundation/routing/router` を書いた側は 1 件も報告されなかった。** `import/no-restricted-paths` は
解決できた import しか見ず、node リゾルバは tsconfig の `paths` を解決しないためである
（IADR-0308 が拡張子で踏んだのと同じ「静かに 0 件」）。**platform の内部参照は 26 ファイル・59 文が
`@foundation/*` で書かれている**ので、このままでは規則は platform でほぼ何も守らない。
対処（tsconfig の `paths` を読む最小のリゾルバ）は [IADR-0311](../adr/IADR-0311_layer-zone-enforcement-and-alias-resolution.md) 決定 1。

### 注入 —— platform（5 方向。エイリアスを解決させたあと）

```
$ pnpm exec eslint platform/frontend/src/lib/__probe_shared_to_app_alias.ts                    platform/frontend/src/lib/__probe_shared_to_app_rel.ts                    platform/frontend/src/features/__probe_features_to_app.ts                    platform/frontend/src/components/__probe_prod_to_testing.ts                    platform/frontend/src/testing/__probe_testing_to_features.ts

.../components/__probe_prod_to_testing.ts
  1:23  error  Unexpected path "@foundation/testing/bffResponse" imported in restricted zone.
               本番コードからテストユーティリティ（testing/）を参照しない（ADR-0067 決定 5）。…
.../features/__probe_features_to_app.ts
  1:27  error  Unexpected path "@foundation/routing/nav" imported in restricted zone.
               features から app を参照しない（ADR-0067 決定 5。合成点は app 層なので対象外）。
.../lib/__probe_shared_to_app_alias.ts
  1:27  error  Unexpected path "@foundation/routing/nav" imported in restricted zone.
               共有層（components / hooks / lib / stores / types / utils / config / assets / locales）から
               features・app を参照しない（ADR-0067 決定 5。…）。
.../lib/__probe_shared_to_app_rel.ts
  1:27  error  Unexpected path "../app/routing/nav" imported in restricted zone.  （同上）
.../testing/__probe_testing_to_features.ts
  1:26  error  Unexpected path "../features" imported in restricted zone.
               テストユーティリティ（testing/）から features を参照しない（ADR-0067 決定 5）。

✖ 5 problems (5 errors, 0 warnings)
```

### 注入 —— knowledge（相対・エイリアス・本番 → testing）

```
$ pnpm exec eslint knowledge/frontend/src/lib/__probe_shared_to_app.ts                    knowledge/frontend/src/components/__probe_alias_shared_to_app.ts

.../components/__probe_alias_shared_to_app.ts
  1:1   error  '@knowledge/app/.probe_target' import is restricted from being used by a pattern. …  no-restricted-imports
  1:19  error  Unexpected path "@knowledge/app/.probe_target" imported in restricted zone. 共有層…  import/no-restricted-paths
.../lib/__probe_shared_to_app.ts
  1:19  error  Unexpected path "../app/.probe_target" imported in restricted zone. 共有層…          import/no-restricted-paths

✖ 3 problems (3 errors, 0 warnings)

$ pnpm exec eslint knowledge/frontend/src/lib/__probe_prod_to_testing.ts
.../lib/__probe_prod_to_testing.ts
  1:19  error  Unexpected path "../testing/probeUtil" imported in restricted zone.
               本番コードからテストユーティリティ（testing/）を参照しない（ADR-0067 決定 5）。…

✖ 1 problem (1 error, 0 warnings)
```

**副次的な効果**: エイリアス リゾルバにより `@knowledge/*` もゾーンから見えるようになった。
従前は `no-restricted-imports` の 1 本だけが止めていた（#1065 のコメントが「解決できず素通りする」と
書いていたとおり）。**二重の網になった。**

### 逆向きの確認 —— 許すべきものが緑であること（決定 4・5）

注入ファイルを削除したうえで、**規則が通してよい実在の 6 ファイル**を明示して走らせた。

```
$ pnpm exec eslint platform/frontend/src/features/index.ts                    platform/frontend/src/components/notifications/NotificationBell.test.tsx                    platform/frontend/src/testing/renderUnitRoute.tsx                    platform/frontend/src/lib/api/apiClient.ts                    platform/frontend/src/lib/auth/AuthProvider.tsx                    platform/frontend/src/app/Layout.tsx
.../app/Layout.tsx
  34:17  warning  Fast refresh only works when a file only exports components. …  react-refresh/only-export-components

✖ 1 problem (0 errors, 1 warning)
```

| ファイル | 通ってよい理由 |
| --- | --- |
| `features/index.ts` | **合成点。層としては app**（決定 4）。`@foundation/routing/*` を 3 文引く |
| `components/notifications/NotificationBell.test.tsx` | **テストファイル**が `@foundation/testing` を引く（決定 5 の禁止は本番コード限定） |
| `testing/renderUnitRoute.tsx` | **testing → app**（`@foundation/routing/shell` / `@foundation/i18n`）。決定 5 が明示的に許す |
| `lib/api/apiClient.ts` / `lib/auth/AuthProvider.tsx` | `@foundation/config` は**決定 1 により shared** になった（shared → shared） |
| `app/Layout.tsx` | **app → app**（`@foundation/routing/*`）と **app → shared**。警告 1 件は移送前と同じもの |

## 検証の実測

| コマンド | 結果 |
| --- | --- |
| `pnpm run lint` | **0 errors / 9 warnings**（基点 `2631eff1` と同一。移送で `components/ui/Layout.tsx` の警告が `app/Layout.tsx` へ移っただけ） |
| `pnpm run typecheck` | 5 ワークスペースすべて Done（`ai-stock-trading` submodule を `git submodule update --init` して実行。AST は `@foundation/config` / `@foundation/i18n` を 1 文も使っておらず、ADR-0067 §結果 の「波及は生じない」を実測で確認した） |
| `pnpm run test` | 1271 件中 **1 件失敗**。`platform/frontend/src/lib/api/orvalMutator.test.ts`（`res.data.arrayBuffer is not a function`）。**基点コミットでも同じ 1 件が失敗する**ことを stash して実測済み（Node 24 の既知事象。本作業とは無関係） |
| `pnpm run build` | 成功（`vendor-echarts` の 500 kB 警告は従前どおり） |
| `pnpm run format:check` | All matched files use Prettier code style |
| `pnpm run i18n` | 再生成。差分は `#: platform/frontend/src/components/ui/Layout.tsx` → `.../app/Layout.tsx` の**参照行 8 箇所のみ**（ja / en 各 4）。メッセージ数は 657 / 未翻訳 0 で不変 |
| `check-route-manifest` | OK（画面 17 件） |
| `check-chunk-budget` | OK（初期ロード 616.20 kB ＝床ちょうど。必須チャンク 5 本すべて実在） |
| `check-i18n-catalogs` | OK |
| `check-trace-blocks` / `check-doc-links` / `check-doc-type-vocabulary` / `gen-knowledge-graph --check` | すべて OK |
| `pnpm exec knip` | devDependencies 4 / unlisted 1 / exports 16 / types 17 ＝ `knip-baseline.json` と一致。**新設した `.cjs` は `knip.jsonc` の `entry` へ足した**（import 文が 1 つも無い実在の入口。`eslint.templates.config.js` と同じ扱い） |

> **`check-knip.js` は Windows で knip を spawn できない**ため、`pnpm exec knip` を直接走らせて
> baseline と突き合わせた（上表）。CI（Linux）では従来どおり `check-knip.js` が走る。

## 実装 ADR

判断が要った点は [IADR-0311](../adr/IADR-0311_layer-zone-enforcement-and-alias-resolution.md) に残した
（エイリアス リゾルバ／`testing` 逆方向の表し方／合成点の除外位置／`Layout` の置き場／`main.tsx` の扱い）。
**番号は本ブランチ時点の最大値 `IADR-0309` ＋ 1 で採った。並行 PR があるためマージ時に引き直してよい。**

## 未決事項

- なし。
