---
title: 作業仕様書 — 2 回持ち越された負債の回収（orval-bff-only.cjs の知見・frontend-tests.yml の paths）（#558）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0131
  - IADR-0132
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
---

# 作業仕様書: 2 回持ち越された負債の回収（#558）

## 起点

- **NFR**（CI・可読性）。関連 IADR: **0131**（OpenAPI を BFF 契約の単一情報源）・**0132**
- 起点 issue: **#558**（出所 **#520** §未決事項 7・8 → **#519** §未決事項 5・6。**2 回持ち越し**）
- 親: **#454**

## 母集合（自分で引き直した）

### 軸 1: issue 番号で引く

```console
$ git ls-files -z ':!planning' ':!src/ai-stock-trading' | xargs -0 grep -ln '#558'
（0 件）
```

**引き継ぎの記述は 1 件も無い。** #558 が言う「送り先が『次に同ファイルを触る issue』としか書かれておらず、
誰にも割り当たっていない」は**現在も真である**（本 issue 自身が受け皿になっている）。

### 軸 2: **issue の「実測」を自分で引き直した** —— 数が動いていた

#558 は #520 の変異試験 M8 を引用して「**53 スキーマすべてが `bff.schemas.ts` に出力されている**」と書き、
同時に「**スキーマ数は着手時に自分で数えること**（他 PR のマージで動く）」と釘を刺している。**数え直した。**

```console
$ grep -c '^export interface ' src/platform/frontend/src/foundation/api/generated/bff.schemas.ts
69
$ # openapi.yaml の components.schemas を数える
69
```

**53 ではなく 69 である。** そして **`openapi.yaml` の宣言数と完全に一致する** ——
**素通りしている**という性質そのものは変わっていない。**根拠はコードにもある**:

```js
// src/orval-bff-only.cjs:61-75
const paths = {};
for (const [p, item] of Object.entries(spec.paths ?? {})) { … }
return { ...spec, paths };   // ← 差し替えるのは paths だけ。components は spread で素通り
```

> **★ 数は書かない。** #558 の助言どおり、**数を書くと次に読む人が古い数を信じる**
> （現に #558 自身が 53 で古くなっていた）。**コメントには性質と数え方だけを書く。**

### 軸 3: **`paths` の非対称を全数で取った** —— issue が挙げた 1 件では足りない

#558 は `docs/api/openapi.yaml` の 1 件だけを挙げるが、**2 つのワークフローの `paths` を集合で引くと 6 件ずれている**
（各ワークフローの自己参照を除く）。

| `frontend.yml` にあり `frontend-tests.yml` に無い | 追加するか | 理由 |
| --- | --- | --- |
| `docs/api/openapi.yaml` | **する** | 契約が生成型を変え、テスト対象のコードがその型で書かれている（#558 の指摘） |
| `src/orval.config.ts` | **する** | **生成の設定そのもの。** 変えれば生成型が変わり、テストのコンパイル対象が変わる |
| `src/orval-bff-only.cjs` | **する** | 同上（**本 PR が触るファイルでもある**） |
| `src/.prettierrc.json` | **しない** | **整形ゲートは `frontend.yml` の lint 相当ジョブが持つ**（#562）。`Frontend Tests` が走らせるのは **`pnpm run test:coverage` だけ**であり、整形を一切見ない |
| `src/.prettierignore` | **しない** | 同上 |
| `src/lingui.config.ts` | **しない** | **`vitest.config.ts` は `lingui.config.ts` を読まない** —— 使うのは babel マクロ（`@lingui/babel-plugin-lingui-macro`）だけである。カタログは**コミット済みの生成物**であり、`lingui.config.ts` 単独の変更は `test:coverage` の結果を変えない |

**除外の理由をここに書く**（母集合の規則 6）。**「対称にすること」自体は目的ではない** ——
**起動しても何も新しく確かめられないジョブを増やすと、CI 時間だけが伸びる。**

## 判断

### 判断 1: **コメントには「性質」を書き、数を書かない**

#520 の知見（`components.schemas` は素通りする）を `orval-bff-only.cjs` の冒頭へ置く。
**ただし「53」「69」といった数は書かない。** 代わりに**数え方（コマンド）**を書く。
**#558 自身が 53 で陳腐化していたことが、そのまま根拠である。**

### 判断 2: **`paths` は 3 件だけ足す**（6 件すべてではない）

軸 3 の表のとおり。**`Frontend Tests` が実際に走らせるのは `pnpm run test:coverage` の 1 本**であり、
それが結果を変え得る入力だけを足す。

### 判断 3: **「起動したこと」を確かめる。「緑になったこと」では足りない**

#558 が **#524 の先例**（skipped と success を取り違えると検査が働いていないことに気づけない）を挙げている。
**本 PR は `docs/api/openapi.yaml` を変更しないため、本 PR 自身では検証できない** ——
そのことを正直に書き、**確認の方法を残す**（§検証 参照）。

### 判断 4: **`.github/workflows/` の編集可否は、憶測せず実際に push して確かめる**

`CLAUDE.md` は「GitHub App 権限では編集不可」と書いている。**ただし `git log` を引くと
`.github/workflows/` への変更は実際に着地している**（`ce96eb8` #619・`44a3141` #618・`a49500e` #527 等）。
**「できない」と決めつけて part 2 を落とさない。** 実際に push し、**拒否されたらその事実を記録して分割する。**

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#558） | 確かめ方 |
| --- | --- | --- |
| 1 | `orval-bff-only.cjs` の冒頭コメントが `components.schemas` の素通りに触れている | 差分 |
| 2 | `frontend-tests.yml` の `paths`（**2 箇所**）に契約が入っている | 差分 ＋ 集合で再測 |
| 3 | **契約だけを変える PR で `Frontend Tests` が skipped にならず実行される** | **本 PR では検証できない**（§検証 に方法と限界を明記） |
| 4 | `pnpm run codegen` の再生成差分が出ない | 実走（**素の exit code**） |
| 5 | `node scripts/check-ai-workflow-config.js` が成功する | 実走（**素の exit code**） |

## 検証

- **コメントのみの変更で生成物が動かないこと**を `pnpm run codegen && git diff --exit-code` で確かめる。
- **`paths` の再測**: 軸 3 と同じ集合演算を再実行し、**残る差が意図した 3 件（prettier 2 件 ＋ lingui 1 件）だけ**になること。

### ★ 受け入れ基準 3 について正直に書く

**「契約だけを変える PR で `Frontend Tests` が起動する」ことは、本 PR では確かめられない。**
本 PR が変更するのは `src/orval-bff-only.cjs` と `.github/workflows/frontend-tests.yml` であり、
**`docs/api/openapi.yaml` を変更しない**。**変更してしまうと「契約だけを変える PR」ではなくなる**うえ、
契約を用の無い理由で触ることになる。

**確認は次に `openapi.yaml` だけを触る PR が行う。** そのとき見るのは
**「`Frontend Tests` が success か」ではなく「`skipped` になっていないか」**である（#524 の先例）。
**この申し送りを `frontend-tests.yml` のコメントへ置く** —— 仕様書だけに書くと、
**まさに本 issue が回収している「送り先が誰にも割り当たっていない」状態を作り直すことになる。**

## 射程外

- **`frontend.yml` 側の `paths` の見直し** —— 本 issue は `frontend-tests.yml` の欠落を埋めるものであり、
  逆向き（`frontend.yml` に不要なものが無いか）は射程外である。
- **prettier / lingui の 3 パス** —— 判断 2 のとおり**意図して足さない**。
