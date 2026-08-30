---
title: lingui 検査範囲の許可リストを撤去し、両ユニット全体を構造的に対象化する
type: spec
status: done
related_ids: [NFR, ADR-0031, ADR-0066, ADR-0067, IADR-0125, IADR-0311, IADR-0312]
author: Claude (implementation agent)
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0066_frontend-feature-isolation-and-import-direction.md (§理由)
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
related_specs:
  - ./20260830_adr-0067-frontend-layer-classification.md
---

# 仕様書: issue #1078 — lingui の `files` 許可リストを撤去する

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（非機能。i18n の退行防止）
- 画面（SC）: SC-04 / SC-18 / SC-19 / SC-20 / SC-21（検査対象へ入る側）
- 関連 ADR: **ADR-0066 §理由**（「許可リストの保守が人に戻り、伸ばし忘れが規則の穴になる」と
  **本件の lingui `files` を名指しで例示している**）／ ADR-0031（Lingui 採用）／ ADR-0067（層分類）
- 関連 IADR: IADR-0125 決定 6（i18n の適用範囲を platform foundation に限り、既存 11 画面を繰り延べた
  **その繰り延べの消化**にあたる）／ IADR-0311（#1087 が同じファイルの直前の変更を入れた）

## 目的・背景

`src/eslint.config.js` の lingui ブロックの `files` は**画面を作るたびに人が伸ばす許可リスト**であり、
「i18n 化したのに検査されない」状態を生んでいる。ADR-0066 §理由 は**この運用そのものを規則の穴の例**
として挙げている。本作業はこの許可リストを**撤去**し、対象を構造（ユニット全体）で決める。

## 自分で引いた母集合（着手前・基点 `a2c7e5b1`）

**issue 本文の数えは使わない**（`.claude/rules/traceability.repo.md` §是正・追随の母集合の取り方）。
issue 本文は #1065 マージ前に測られており、**現在の develop では成立しない**。

### 軸 1 — `features/` の実在ディレクトリと `files` の突合（issue と同じ軸）

| | issue 本文 | 🔴 自分の実測 |
| --- | ---: | ---: |
| `knowledge` の feature ディレクトリ | 19 | **17** |
| `files` が覆う feature | 13 | **12** |
| 漏れている feature | 6 | **5** |

差の理由は #1065 である。**`abac` / `scope-filter` は `features/` から `lib/` へ移送済み**であり、
もはや feature ではない。issue の「6 件」は `scope-filter` を feature として数えていた。

漏れている 5 件: `sc04-wiki` / `sc18-graph` / `sc19-private-notes` / `sc20-obsidian-settings` /
`sc21-ai-suggestions`。

### 軸 2 — 誤りの側から引く（規則 1・5）

**軸 1 は `features/` しか見ていない。** 本当の母集合は「**i18n 済みなのに lingui 規則が効いていない
ファイル**」である。ESLint 自身の `calculateConfigForFile()` で、lingui マクロを import している全
ファイルについて lingui 規則が有効かを判定した（自作の glob 再実装で測らない）。

| | |
| --- | ---: |
| 走査した `.ts` / `.tsx` | 290 |
| lingui マクロを使うファイル | 68 |
| うち lingui 規則が**有効** | 49 |
| 🔴 うち lingui 規則が**効いていない** | **19** |

内訳（9 グループ ＋ 意図的除外 1）:

| 置き場 | ファイル | 軸 1 で見えたか |
| --- | ---: | --- |
| `knowledge/.../features/sc18-graph` | 4 | ✅ |
| `knowledge/.../features/sc19-private-notes` | 3 | ✅ |
| `knowledge/.../features/sc20-obsidian-settings` | 2 | ✅ |
| `knowledge/.../features/sc21-ai-suggestions` | 2 | ✅ |
| `knowledge/.../features/sc04-wiki` | 1 | ✅ |
| `knowledge/.../lib/scope-filter` | 2 | ❌（`lib/` は軸 1 の外） |
| 🔴 `platform/.../components/notifications` | 2 | ❌ |
| 🔴 `platform/.../app/routing/breadcrumbs.ts` | 1 | ❌ |
| 🔴 `platform/.../features/index.ts`（合成点） | 1 | ❌ |
| `platform/.../lib/i18n/i18n.test.tsx` | 1 | — **意図的除外**（`ignores` がテストを外す） |

**軸 1 で終わらせていたら platform 側の 4 ファイルを落としていた**（規則 5 の実例がまた出た）。

### 除外したものと理由（規則 6）

- **`**/*.{test,spec}.{ts,tsx}`**: 既存 `ignores` のとおり除外を維持する。テストコードの文言は UI ではない。
- **`platform/frontend/src/lib/api/generated` / `locales`**: `eslint.config.js` の全体 `ignores` が既に外している
  （orval / lingui compile の生成物）。**本作業で新たに除外を足していない。**
- **`packages/ui`**: lingui マクロの使用が **0 件**（IADR-0125 決定 1 の「`@platform/ui` に表示文言を入れない」が
  実際に守られている）。対象へ入れても効果が無いため範囲を広げない。
- **`ai-stock-trading`**: 別プロジェクトの submodule（IADR-0120）。`lingui.config.ts` も抽出対象から外している。

## 適用範囲を広げると何件出るか（実測）

`files` を**両ユニット全体**へ広げて `pnpm exec eslint` を実行した（計測用の一時 config で測定）。

| | |
| --- | ---: |
| **lingui error 合計** | **35** |

| ルール | 件数 |
| --- | ---: |
| `lingui/no-expression-in-message` | 20 |
| `lingui/no-unlocalized-strings` | 15 |

| 置き場 | 件数 |
| --- | ---: |
| `knowledge/.../sc18-graph` | 13 |
| 🔴 `platform/.../lib/api` | **9** |
| `knowledge/.../sc19-private-notes` | 5 |
| `knowledge/.../sc20-obsidian-settings` | 4 |
| `knowledge/.../sc21-ai-suggestions` | 4 |

issue 本文の「26 件」は knowledge 側だけの数えである。**platform の `lib/api` に 9 件ある**ことは
issue が測っていない。`ApiError` のメッセージは `components/ui/apiErrors.ts` 経由で**画面に出る**ため、
これは「UI ではないから除外してよい」ものではない。

## 決定（本作業で採る形）

### 決定 1 — `files` の列挙を撤去し、両ユニット全体を対象にする

```
'platform/frontend/src/**/*.{ts,tsx}'
'knowledge/frontend/src/**/*.{ts,tsx}'
```

**19 行の列挙が 2 行になる。** 以後、画面・feature・共有ディレクトリを足しても
`eslint.config.js` を触る必要が無く、**伸ばし忘れが構造的に起こり得なくなる。**

これは `lingui.config.ts` の `catalogs[].include`（＝カタログ抽出範囲）と**同一の範囲**である。
**従来は抽出範囲のほうが広く、lint 範囲だけが狭かった** —— この不一致こそが本 issue の実体である。

### 決定 2 — 除外を 1 つも作らない（`lib/api` も i18n する）

`platform/.../lib/api` の 9 件を除外すれば PR は小さくなるが、**除外リストを作った時点で
「保守が人に戻る」構図が再発する**。`ApiError` の文言は利用者に表示されるため、実際に i18n する。

**ja の表示は変わらない。** カタログの msgid は原文そのもの（`sourceLocale: 'ja'`・ハッシュ ID ではない）
であり、`i18n._(msg\`…\`)` は未活性でも msgid にフォールバックする。日本語文字列を assert している
既存テスト（`apiErrors.test.ts` ほか）は**そのまま通る**ことを実測で確かめる。

書き方は既存の前例 `components/notifications/notificationMessages.ts`（React 外のモジュールで
`i18n._(msg\`…\`)` を使う）に揃える。**新しいパターンを持ち込まない。**

### 決定 3 — 検査器は足さない（許可リストごと消えるため）

判断の根拠は次節。

### 決定 4 — 範囲定義を共有モジュールへ切り出すことは**しない**

`lingui.config.ts` と `eslint.config.js` が同じ範囲を持つため、共有 `.mjs` へ括り出す案を検討した。
**採らない。** 範囲は 2 行であり、括り出すと新しいモジュール・knip 登録・TS からの `.mjs` 読み込みと
いう 3 つの可動部が増える。CLAUDE.md 禁止事項「過剰な抽象化」に当たる。
代わりに**両ファイルへ相互参照のコメント**を置く。

## 「同型の事故が 2 回起きたら」の判定

issue 本文は「**本件は 1 回目である**」とし、1 回目なので記録に留めよと書いている。
🔴 **自分で数えた結果、これは誤りである。**

`git rev-parse --is-shallow-repository` = **false**（履歴は打ち切られていない。出典に使える）。
各パスの追加コミットを引いた:

| 取りこぼした対象 | 入った PR | 日付 | 許可リストは伸びたか |
| --- | --- | --- | --- |
| `features/sc04-wiki` | #233 → #1009 | 2026-07-11 / 08-23 | ❌ |
| `features/sc18〜sc21` | #1009 | 2026-08-23 | ❌ |
| `components/notifications` | #1021 | 2026-08-28 | ❌ |
| `app/routing/breadcrumbs.ts` | #1045 | 2026-08-29 | ❌ |
| `lib/scope-filter` | #1065 | 2026-08-30 | ❌ |

**独立した日付で少なくとも 4 回、同じ形の取りこぼしが develop に入っている。** さらに
**取りこぼしかけて PR 内で捕まえた例が 2 回**ある（#1065 の `abac`・#1087 の `lib/i18n` と `Layout.tsx`。
どちらも「この行を足さないと静かに検査されなくなる」とコメントに書き残されている）。

**したがって閾値（2 回）は満たしている。**

**それでも検査器は足さない。** 閾値が求めるのは「同型の事故を止めること」であって
「検査器を増やすこと」ではない。決定 1 で**許可リストそのものが消える**ため、
「`features/` の一覧と `files` を突合する検査器」は**検査する対象を失う**。
**穴を塞ぐ最短の手段は検知ではなく消去である。** 検査器を足せば、消えた許可リストを見張る
コードだけが残り、保守の対象が 1 つ増える。

## 受け入れ基準

- [x] `files` が実在ディレクトリと 1 対 1 で対応する（**列挙を撤去したので恒久的に対応する**）
- [x] `pnpm run lint` が error 0 件
- [x] 解消は `eslint-disable` でも `files` からの除外でもなく、実際に i18n している
- [x] `node scripts/check-i18n-catalogs.js` が通り、`pnpm run i18n` に再生成差分が無い
- [x] `typecheck` / `test` / `build` / `format:check` が通る
- [x] 規則が実際に効くことを注入試験で確かめた

## 注入試験（規則が効くことの証跡）

結果は PR 本文へ貼る。手順は #1087 と同じで、**新たに対象へ入った各グループへ違反を 1 件ずつ注入し、
`pnpm exec eslint` が error を出すこと**を確認したうえで注入を戻す。

## 影響範囲

- `src/eslint.config.js`（lingui ブロックの `files` と説明コメント）
- `src/lingui.config.ts`（相互参照コメントのみ）
- `src/knowledge/frontend/src/features/{sc18-graph,sc19-private-notes,sc20-obsidian-settings,sc21-ai-suggestions}/**`
- `src/platform/frontend/src/lib/api/{ApiError.ts,apiClient.ts}`
- `src/platform/frontend/src/locales/{ja,en}/messages.{po,ts}`（再生成）
