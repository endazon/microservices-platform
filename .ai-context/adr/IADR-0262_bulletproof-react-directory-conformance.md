---
title: IADR-0262 計画のディレクトリ構成へ適合させる際の区分の割り当てと、@foundation エイリアスの扱いを確定する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0031
  - ADR-0019
  - IADR-0056
  - IADR-0120
  - IADR-0121
  - IADR-0124
  - IADR-0125
author: claude
created: 2026-08-23
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# IADR-0262: 計画のディレクトリ構成へ適合させる際の区分の割り当てと、`@foundation` エイリアスの扱いを確定する

- 状態: Accepted
- 日付: 2026-08-23
- 起点: #785（フロントのディレクトリ構成が Bulletproof React に従っていない）
- 番号 `IADR-0262` は統括側が確定させた（先着尊重により `IADR-0261` は先に完了した #438 が確保した）

## コンテキスト

計画 `13_frontend-stack`（`status: fixed` / updated 2026-08-22）§ディレクトリ構成 は、ユニット内 SPA の
構成をツリーで示し、**Feature 内部の分割（`api/ components/ hooks/ routes/ stores/ types/`）まで含めて
適合を必須**と定めた（利用者裁定 2026-08-16 → 2026-08-22 に再確定）。実測では `features/*/` の内部分割は
リポジトリ全体で **0 件**であり、ユニット直下のツリー項目も platform / knowledge のどちらにも 1 つも無い。

適合作業は 3 つの判断を先に要求する。**この 3 つを決めずに feature の分割から始めると、
決め方によっては分割をやり直すことになる**（issue #785 本文の指摘）。

1. `@foundation` エイリアスの向き先をどうするか
2. 計画が列挙する 6 区分に当てはまらないモジュール（純関数の表示写像）をどこへ置くか
3. 中身の無い区分をどう扱うか

## 決定 1: `@foundation` エイリアスは名前を変えず、区分ごとに向き先を差し替える

計画 §ディレクトリ構成 は「**本項が必須とするのはディレクトリ構成であって、エイリアス名ではない**」と
明記し、エイリアスの扱いを射程外としている。したがって適合のためにエイリアス名を変える必要は無い。

**変えない側を選ぶ。** 理由は 2 つある。

- `src/ai-stock-trading` は別リポジトリの submodule であり、**本リポジトリからは是正できない**
  （IADR-0120）。計画も「エイリアス名の変更は AST の 43 ファイルへ波及する」と実測を添えている。
  名前を変えれば、こちらの都合で他リポジトリを壊すことになる。
- 雛形 `templates/unit-template/frontend/tsconfig.json` は `@foundation/*` を新ユニットの契約として
  配っている。名前を変えると、複製済みのユニットすべてがその時点で壊れる。

実測では、`@foundation` の利用形は **すべて `@foundation/<区分>`** であり、サブパスの無い
`from '@foundation'` は **0 件**である。よって前置詞ごとに向き先を差し替えれば、利用側を 1 行も
書き換えずに実ディレクトリだけを動かせる。

| エイリアス | 向き先（適合後） | 計画の対応項目 |
| --- | --- | --- |
| `@foundation/config` | `platform/frontend/src/app/config` | `app/`（config） |
| `@foundation/i18n` | `platform/frontend/src/app/i18n` | `app/`（i18n） |
| `@foundation/routing` | `platform/frontend/src/app/routing` | `app/`（router） |
| `@foundation/api` | `platform/frontend/src/lib/api` | `lib/` |
| `@foundation/auth` | `platform/frontend/src/lib/auth` | `lib/` |
| `@foundation/ui` | `platform/frontend/src/components/ui` | `components/` |
| `@foundation/notifications` | `platform/frontend/src/components/notifications` | `components/` |
| `@foundation/testing` | `platform/frontend/src/testing` | `testing/` |

**区分の割り当ては実装側の判断ではない。** 計画 §ディレクトリ構成 の 2026-08-22 追記が
「`config` / `i18n` / `routing` が `app/`、`api` / `auth` が `lib/`、`ui` / `notifications` が
`components/`、`testing` が `testing/`」と対応を明示しており、それをそのまま写した。

**帰結として `@foundation` は「ディレクトリ名」ではなく「platform 基盤の公開面の名前」になる。**
ユニット間の依存規則（ADR-0019 / IADR-0056 の「可変ユニットが参照してよいのは `@foundation` と
`@platform/ui` の 2 つ」）はもともと**依存の向きの規則**であって配置の規則ではないため、
この読み替えで規則の意味は変わらない。

## 決定 2: feature 内部の 6 区分は閉じた集合とし、`utils/` を新設しない

計画は feature 内部の区分を括弧書きで 6 つ（`api/ components/ hooks/ routes/ stores/ types/`）
列挙している。実装には、そのどれにも「関数」としては当てはまらないモジュールがある——
`citations.ts` / `syncState.ts` / `driftView.ts` / `scopeSelection.ts` / `abacVocabulary.ts` 等、
**値集合と型、およびその表示写像を持つ純関数モジュール**である。

これらは `types/` に置く。`utils/` を feature 内へ新設しない。

- 雛形 `templates/unit-template/frontend/src/features/sample/types/index.ts` は `types/` の役割を
  「画面の都合で組み立てる**表示用の型**」と定義している。上記モジュールはいずれも
  「表示用の型＋その型に付随する写像」であり、この定義の延長にある。
- `utils/` を足すと **7 つ目の区分**になる。計画の列挙を実装側の判断で広げることになり、
  「解釈の余地が無い」（issue #785）としてきた前提を実装が自分で崩す。
- 逆に `components/` へ寄せる案は採らない。これらは**描画なしで試験できるよう意図的に DOM から
  切り離されている**（`driftView.ts` の冒頭がその理由を記録している）。`components/` へ入れると
  その意図が読めなくなる。

## 決定 3: 中身の無い区分もフォルダと `.gitkeep` で枠を残す

雛形 README（PR #777）が定めた作法をそのまま採る——「中身が無い区分も、フォルダと `.gitkeep` だけは
置いてある。何も無いと**その構成要素が意図的に不在なのか単に作り忘れなのかが一見して分からない**」。

knowledge ユニットでは、直下の `app/ assets/ components/ hooks/ lib/ locales/ stores/ testing/ types/ utils/`
と、各 feature 配下の `hooks/` `stores/` が空になる（クライアント状態の hook と Zustand ストアは
現時点で 0 件。サーバー状態の hook 12 本はすべて `api/` に入る）。**使わない区分のフォルダを消さない。**

## 決定 4: feature の公開面（`index.ts`）を全 feature に置き、feature 跨ぎの参照はそこへ寄せる

分割前は 5 ファイルが他 feature の内部ファイルを直接参照していた（実測 12 行。
`../abac/confidentiality` / `../scope-filter/scopeSelection` 等）。分割で実パスが変わるためどのみち
書き換えが要る。書き換え先を**公開面**（`../abac` / `../scope-filter`）にして、Bulletproof React の
「feature の外から触ってよいのは index が再輸出したものだけ」を同時に満たす。

ルートを持たない feature（`abac` / `scope-filter`）にも `index.ts` を置く。**ルートの有無と
公開面の有無は別の話**であり、公開面が無い feature は「内部のどこを触ってよいか」が読めない。

## 決定 5: 適合は 2 段に分ける。第 1 段は knowledge、第 2 段は platform

計画への完全適合は platform の `foundation/` 分解を含む。分解は実パスを動かすため、
**それを固定している 3 ファイルの同時更新を要求する**（実測）。

| ファイル | 固定している実パス | 更新しないとどうなるか |
| --- | --- | --- |
| `.github/workflows/pr-size.yml` | `src/platform/frontend/src/foundation/api/generated/**` / `.../i18n/locales/**` | 生成物が PR サイズに算入され、除外が静かに空振りする |
| `.github/workflows/frontend.yml` | 同上（codegen / i18n の再生成差分検査の対象パス） | 再生成差分を検査しなくなる（緑のまま無検査） |
| `scripts/scripts.repo.test.js` | 上記 2 つの実パスの実在検査、および `foundation/i18n/index.ts` の直読み | **テストが赤になる** |

3 番目は「実パスの改名で検査が静かに空振りするのを止める」ために置かれたガードであり、
**設計どおりに鳴っている**。よって分解は、この 3 ファイルを同じ PR で更新できる作業単位で行う。

第 1 段（knowledge の feature 分割）は決定 1 により **platform の分解を待たずに実施でき、
第 2 段でやり直しにならない** —— knowledge 側の `@foundation/...` import は 1 行も変わらないためである。

**［2026-08-28 追記 / #785］第 2 段（platform の `foundation/` 分解）を完了した。** 作業仕様書は
`.ai-context/specs/20260828_platform-frontend-bulletproof-stage2.md`。実測:

- `platform/frontend/src/` 直下が計画のツリーの 11 区分 ＋ `main.tsx` になった
  （`foundation/` `test/` `App.tsx` は消えた）。上表の 3 ファイルは同じ PR で更新し、
  `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` は 615 件全緑。
- 決定 1 の対応表の 8 行は**そのまま適用できた**。加えて 2 点を決めた。
  - **決定 1 の対応表へ第 9 行を足す: `@foundation/ai-chat` → `platform/frontend/src/components/ai-chat`**
    （計画の対応項目は `components/`）。本 ADR 起草時の表は 8 区分だったが、これは計画
    §ディレクトリ構成 の 2026-08-22 実測を写したためであり、その後 #788 で第 9 区分
    `ai-chat` が加わっていた。**既存 8 行は 1 行も動かしていない。**`src/eslint.config.js` が既に「共通シェルに載る文言なので
    `foundation/ui` と同じ規則の下に置く」として `ui` と同じ files 配列へ入れており、
    唯一の利用者も共通シェル（`Layout.tsx`）である。
  - **Lingui カタログはユニット直下の `locales/` へ出した（対応表は変えていない）。** 決定 1 は
    `@foundation/i18n` → `src/app/i18n` を定めるが、これはエイリアスの向き先であって
    カタログの置き場ではない（`@foundation/i18n/locales/...` の外部 import は実測 0 件）。
    計画ツリーは `locales/  # ja / en（Lingui）` と中身まで名指ししており、
    雛形 README も「`locales/` はアプリホストである `platform/frontend` が持つ」と書いている。
    `app/i18n/locales/` に留めると platform でも `locales/` が空になり、
    **ツリーの区分が 1 つ誰にも満たされない**状態が残る。
- 決定 3（中身の無い区分も `.gitkeep` で枠を残す）は `assets/ hooks/ stores/ types/ utils/` に適用した。
  `locales/` は上のとおり実体を持つので `.gitkeep` は置いていない。
- **§結果 の「悪い影響 / トレードオフ」のうち「第 2 段が完了するまで platform と knowledge で
  レイアウトが揃わない」は解消した。** エイリアスと実配置の対応を 3 箇所
  （`tsconfig.app.json` / `vite.config.ts` / `vitest.config.ts`）で読むことは変わらない。
- **フォローアップの退行防止検査器は引き続き置かない。** 「同型の事故が 2 回起きたら」の条件は
  第 2 段でも満たされていない（1 回目も起きていない）。

## 結果

- 良い影響: 計画への不適合 13 件（feature 内部分割）が 0 件になる。feature の公開面が全 feature に
  揃い、feature 跨ぎの内部参照が消える。新ユニットの雛形（`templates/unit-template`）と実装の
  構成が一致し、「雛形と実装が食い違う」状態（#784 / #785 が記録）が knowledge については解消する。
- 悪い影響 / トレードオフ: 第 2 段が完了するまで、platform と knowledge でレイアウトが揃わない。
  `@foundation` がディレクトリ名と一致しなくなるため、エイリアスと実配置の対応を
  `tsconfig` / `vite.config.ts` / `vitest.config.ts` の 3 箇所で読むことになる（現在も 3 箇所である）。
- フォローアップ: 第 2 段（platform の `foundation/` 分解）。退行防止の検査器
  （`features/*/` 直下に 6 区分以外を置かせない）は「同型の事故が 2 回起きたら」の条件を
  まだ満たさないため置かない。

## 関連

- Supersedes: なし
- Superseded by: なし
