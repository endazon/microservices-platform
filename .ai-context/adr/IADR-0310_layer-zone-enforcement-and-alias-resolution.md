---
title: IADR-0310 層ゾーンの機械強制にエイリアス解決を与え、`testing` の被参照禁止を本番コード限定で表す
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0067
  - ADR-0066
  - ADR-0031
  - IADR-0308
  - IADR-0262
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0067_frontend-layer-classification-and-composition-point.md
  - planning:projects/microservices-platform/07_adr/ADR-0066_frontend-feature-isolation-and-import-direction.md
---

# IADR-0310: 層ゾーンの機械強制にエイリアス解決を与え、`testing` の被参照禁止を本番コード限定で表す

> **番号は本ブランチ時点の最大値（`IADR-0309`）＋ 1 で採った。** 並行して開いている PR があるため、
> 統括側がマージ時に採番を引き直してよい。

- 状態: Accepted
- 日付: 2026-08-30
- 起点: 計画 ADR-0067（Accepted 2026-08-30）の実装。作業仕様書 `.ai-context/specs/20260830_adr-0067-frontend-layer-classification.md`

## 起点・関連

- 関連する計画書 ID: **ADR-0067**（決定 1〜6）/ ADR-0066 決定 3（機械強制の義務）/ ADR-0031
- 関連する実装 ADR: IADR-0308（knowledge にしか配備できなかった記録）/ IADR-0262 決定 1（`@foundation` エイリアス。**覆さない**）
- 関連する実装仕様書: `.ai-context/specs/20260830_adr-0067-frontend-layer-classification.md`

## コンテキストと課題

ADR-0067 は層の分類を原典（Bulletproof React）へ戻し、「決定 5 の 4 層をそのままゾーン定義にできる」と
書いている。**移送と分類の是正はそのとおりで足りたが、機械強制の側に ADR が触れていない穴が 2 つ在った。**

### 穴 1 —— `import/no-restricted-paths` は `@foundation/*` を解決できない

`import/no-restricted-paths` は**解決できた import しか見ない**。IADR-0308 は node リゾルバの
`extensions` を足すことでこれに対処したが、**エイリアスは依然として解決されない**。

実測（2026-08-30。`platform/frontend/src/components/` に置いた 2 つの検査ファイル）:

| import 文 | 規則の反応 |
| --- | --- |
| `import … from '../app/routing/router'` | **error**（node リゾルバが解決する） |
| `import … from '@foundation/routing/router'` | **報告なし**（解決できず素通り） |

**platform ユニットの内部参照は 26 ファイル・59 文が `@foundation/*` で書かれている**（実測）。
つまり分類だけ直してゾーンを置いても、**platform では規則がほぼ何も守らない。**
IADR-0308 が踏んだ「静かに 0 件で通る」と同じ形が、原因を変えて残る。

### 穴 2 —— 「本番コードから `testing/` を参照しない」はゾーンだけでは書けない

決定 5 は `testing/` を第 4 の層とし、代償として **「本番コードから参照しない」** を課す。
ところがゾーンの `target` はディレクトリであり、**同じディレクトリに居るテストファイルを区別しない**。
実測では `components/notifications/NotificationBell.test.tsx` が `@foundation/testing/bffResponse` を
引いており、これは決定 5 の文言（「本番コードから」）では違反ではない。

## 検討した選択肢

### 穴 1 について

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | `eslint-import-resolver-typescript` を devDependency に足す | 標準的だが**依存が 1 つ増える**。knip はリゾルバ名を文字列でしか見ないため未使用 devDependency として湧き、`knip-baseline.json` の更新も要る |
| B | platform 内部の `@foundation/*` 参照を相対 import へ書き換える（knowledge が `@knowledge` に対して採った手） | **却下。** 26 ファイル・59 文の書き換えになり、テストの `vi.mock('@foundation/…')` も連鎖する。ADR-0067 が求めていない大規模改変で、レビュー単位としても不当に大きい |
| **C（採用）** | **tsconfig の `paths` を読む最小のリゾルバを本リポジトリに置く** | 依存ゼロ。**エイリアス表を増やさない**（tsconfig が正本のまま）。約 80 行 |

### 穴 2 について

| 案 | 内容 | 評価 |
| --- | --- | --- |
| D | ゾーンの `target` に glob を書いてテストファイルを外す | **却下。** `import/no-restricted-paths` は glob の `target` を minimatch で照合し、minimatch は `/` 区切りを要求する。Windows の絶対パス（`\`）と一致しないため **CI（Linux）でだけ効いてローカルでは静かに 0 件**になる。本リポジトリが繰り返し踏んでいる形そのもの |
| E | 規則を置かず、人が守る | **却下。** ADR-0066 決定 3 が機械強制を義務づけている |
| **F（採用）** | **本番コード限定のブロックを後段に置き、そこでだけゾーンを 1 本増やす** | ESLint の `files` / `ignores` は OS 差を吸収する。**flat config の「同一ルールは後勝ちで置換」を意図して使う** |

## 決定

**決定 1**: **`src/eslint-import-resolver-unit-alias.cjs` を置き、`import/no-restricted-paths` に
tsconfig の `paths` エイリアスを解決させる。** リゾルバは**エイリアス表を持たない** ——
`eslint.config.js` の `tsconfigAliases()` が `platform/frontend/tsconfig.app.json` /
`knowledge/frontend/tsconfig.json` を `typescript` の `readConfigFile`（JSONC を読む）で読み、
`settings` 経由で渡す。**エイリアスの正本を 4 つ目にしないための構造である。**

**決定 2**: **`testing/` の被参照禁止は「本番コード限定のブロック」で表す。** 各ユニットに
`ignores: ['**/*.{test,spec}.{ts,tsx}']` を持つ 2 本目のブロックを置き、
`featureIsolationZones(unit, { productionOnly: true })` で**同じゾーン一式 ＋ 1 本**を宣言する。
ゾーンの本体は 1 つの関数が持つので、2 本になって片方が腐ることはない。
**`no-restricted-imports` はこのブロックで宣言しない**（宣言すると前のブロックの禁止リストが丸ごと消える）。

**決定 3**: **合成点（決定 4）の除外は、既存のブロックレベル `ignores`
（`platform/frontend/src/features/index.ts`）に担わせる。** ゾーン側にも同じパスを書かない。

**決定 4**: **共通シェル `Layout` の置き場は `app/` 直下**（`app/Layout.tsx`）とする。
`app/routing/` の中に入れない —— `Layout` はルート定義ではなくアプリシェルであり、
`app/routing/shell.tsx`（ルート要素）とは役割が違う。`App.tsx` の兄弟に置くのが読み手に素直である。

**決定 5**: **`main.tsx`（`src/` 直下のエントリ）はアプリケーション層として扱い、ゾーンの `target` に置かない。**
決定 5 の表はディレクトリを分類しており `main.tsx` は表に無いが、エントリは定義上 app 層であり、
app 層に課される制約は無い（app は shared と features を参照してよい）。**target にしない＝無制限**が正しい。

## 理由

- **決定 1 が無いと、本 PR は「規則を配備した」と言えるのに何も守っていない。** ADR-0067 の目的は
  「platform へ規則を配備できる形を与える」ことであり、解決できない import を素通りさせる配備は
  IADR-0308 が記録した失敗の再演である。**注入試験でしか気付けない**ため、本 PR は注入の証跡を仕様書に残した。
- **決定 1 で案 C を採るのは、依存よりも「表を増やさないこと」を重く見たからである。** エイリアスの
  向き先は既に 3 箇所（tsconfig / vite / vitest）に在り、README が「3 つとも同じ向き先を持たせる」と
  書いている。ESLint 専用の 4 つ目を作れば、**lint だけ古い向き先で緑**という壊れ方が新しく生まれる。
  tsconfig を読む形なら、今後エイリアスを足しても ESLint 側は 1 行も触らなくてよい。
- **決定 2 が案 D を退けるのは、glob の `target` が OS で挙動を変えるからである。** 「CI で効いて
  ローカルで効かない」検査は、ローカルで緑を見た人が誤った結論を持つ点で**無い検査より悪い**。
- **決定 3 は「同じパスを 2 箇所に書かない」規律の適用である。** 合成点のパスは既にブロックの
  `ignores` に在る。ゾーンへも書くと、片方だけ直す事故が起き得る。
- **決定 4 は ADR-0066 決定 1 の言い回しと揃う** ——「feature の組み合わせは `app/` で行う」。
  シェルは合成の器であり、ルータの部品ではない。

## 結果

- 良い影響:
  - **platform ユニットにも依存方向の規則が実効的に掛かる。** エイリアスで書かれた越境も報告される
    （注入試験で 5 方向すべて実測）。
  - **knowledge ユニットも副次的に強くなる。** `@knowledge/*` がゾーンから見えるようになり、
    既存の `no-restricted-imports`（自ユニット内での `@knowledge` 禁止）と二重の網になった。
  - **エイリアスの向き先を変えても ESLint 設定を触らなくてよい**（tsconfig が正本のまま）。
- 悪い影響・トレードオフ:
  - **本リポジトリ所有のリゾルバが 1 本増える**（約 80 行）。ESLint プラグインの内部仕様
    （リゾルバを名前で `require` する／絶対パスは 2 番目の候補で解決される）に依存しており、
    `eslint-plugin-import` の major 更新時に確認が要る。
  - **`import/no-restricted-paths` の宣言がユニットあたり 2 本になる。** ゾーン本体は共通関数なので
    内容の二重管理は無いが、**ブロックを増やすときは後勝ちの置換に注意が要る**（コメントに明記した）。
- フォローアップ:
  - `ai-stock-trading`（submodule）側の ESLint に同じ規則を持たせることは ADR-0067 フォローアップ 3 が
    当該リポジトリの作業として持つ。**本リポジトリからは及ぼさない**（IADR-0120）。
  - lingui の `files` 許可リストの欠落（#1078）は本 PR の射程外。**本 PR は移送に合わせて追随させただけ**である。

## 関連

- Supersedes: なし
- Superseded by: なし
