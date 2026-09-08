---
title: setup.sh が submodule 初期化と Node 依存導入まで行い、素の環境で platform と frontend が実走できる
type: spec
status: done
related_ids: [NFR, ADR-0032, ADR-0037, IADR-0056, IADR-0058, IADR-0087, IADR-0117, IADR-0121, IADR-0180, IADR-0338]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 環境を用意するのは devcontainer と本スクリプトの両方である（#1349）

## 起点

- NFR（運用保守。**無採番** —— 工程の統制であり、計画側の非機能要件表に当たる番号が無い。
  `traceability.md`「起点 ID の種別」の 2 に当たるので**環流しない**）
- 実装 ADR: [[IADR-0180]]（環境は devcontainer と本スクリプトの両方で用意する。`#824`）／
  [[IADR-0087]]（bash stub-on-PATH の試験方式）／[[IADR-0121]]（pnpm workspace。ルートは `src/`）
- issue: #1349（出典: planning#574 横断監査 指摘 B-7）

## 現状（実測。`develop` `867ed7ff`）

`scripts/setup.sh` は **.NET SDK の導入と `dotnet restore` しか行わない。**

| 依存 | 現状 | 欠けると何が起きるか |
| --- | --- | --- |
| .NET SDK | **在れば使う → 無ければ入れる**（`#824` / [[IADR-0180]]） | —— |
| **submodule `src/ai-stock-trading`** | 🔴 **記述が無い** | `Platform.Bff.csproj:20` が submodule 内の `AiStockTrading.Bff.Endpoints` を `ProjectReference` するため、**platform slnx が restore もビルドもできない** |
| **Node 依存（pnpm）** | 🔴 **npm のコメントアウトのみ**（`:96-98`）。しかも pnpm workspace と不一致 | `pnpm run lint` / `typecheck` / `test:coverage` が一切走らない |

⇒ **素の環境では、restore ループが platform slnx で必ずエラーになる**（`|| log` で握られるので
「継続」と表示されるだけで、**依存が足りないことは伝わらない**）。

🔴 **順序が本質である。** submodule の初期化は **restore ループより前**でなければ意味がない ——
後ろに置くと、その回の restore は失敗したままである。

## 母集合（規則 1・2・9。**誤りの側**＝「CI は用意しているのに setup.sh が用意していないもの」で引いた）

CI のワークフローが checkout の後に置いている「依存の用意」step を全数走査した
（`grep -n "submodules\|pnpm\|setup-node" .github/workflows/*.yml`）。

| CI が行っていること | setup.sh | 判定 |
| --- | --- | --- |
| `Fetch unit submodules (src/*, public, non-recursive)`（8 ワークフロー） | 🔴 無し | **本件** |
| `pnpm/action-setup` ＋ `pnpm install --frozen-lockfile`（frontend 系 2 本） | 🔴 無し | **本件** |
| `actions/setup-node`（Node 本体） | 対象外 | **除外**（Node の導入は SessionStart hook が走る環境の前提であり、hook 自身が node で書かれている） |
| `dotnet restore` | 在り | ✅ |

### 除外したものと理由（規則 6）

- **Node 本体の導入**: 本スクリプトを起動する SessionStart hook が Node で書かれている以上、
  Node が無ければそもそも到達しない。**入れ子の前提を作らない。**
- **`pnpm` 本体の導入**: `corepack` が使えない環境が実測で在る（本リポジトリの既知問題）。
  **在れば使う・無ければスキップ**に留める —— [[IADR-0180]] の「在れば使う → 無ければ入れる」は
  .NET SDK について採った判断であり、**pnpm へ自動で広げない**（1 回目。記録に留める）。
- **private submodule**: `.gitmodules` に `src/ai-stock-trading`（public）1 件しか無い。
  private ユニットのトークン（[[IADR-0058]] 型）が要る経路は**現に存在しない**。

## 決定

### 決定 1: 🔴 **submodule の初期化は restore ループより前に置く**

後ろに置くと、その回の restore は失敗したままである。**順序そのものが受け入れ基準**であり、
試験もそれを固定する。

### 決定 2: 🔴 **パスを直書きせず `.gitmodules` から導出する**

CI の `Fetch unit submodules` step と**同じ導出**（`git config --file .gitmodules --get-regexp`
→ `src/` で始まる path だけ）を使う。`src/ai-stock-trading` と書くと、**それ自体が次の追随漏れ点**に
なる（`traceability.repo.md` 規則 10。.NET の版を直書きしないのと同じ理由）。

`src/` で絞るのは CI と同じ理由である —— **ユニット submodule だけを取り、再帰しない。**

### 決定 3: **pnpm は「在れば使う」に留める**（決定 2 の脚注と対）

`corepack` が動かない環境が実測で在るため、**無ければスキップして継続**する。
`--frozen-lockfile` を使う（CI と同じ）—— ロックを更新してしまうと、
**セットアップが差分を作る**という別の事故になる。

### 決定 4: **fail-open を崩さない**

本スクリプトは `set -u` のみで `set -e` を持たない。追加するブロックも
`|| log ...` で受け、**セットアップの失敗でセッションを止めない**（[[IADR-0180]] の設計思想）。

### 決定 5: **矛盾している npm のコメントアウトは消す**

`:96-98` は `npm ci` を示唆するが、本リポジトリは pnpm workspace であり
ルートに `package.json` は無い（`src/` に在る）。**残すと誤った手順を教える。**
Python のコメントアウトは**そのまま残す** —— `scripts.repo.test.js` が
「python は opt-in であり利用保証が無い」という判断の**根拠として引いている**（消すと記録が浮く）。

## 試験（[[IADR-0087]] の bash stub-on-PATH。副作用ゼロ）

`scripts/setup-sh.test.js` を新設する。`git` / `pnpm` / `dotnet` / `curl` を PATH 上の記録スタブへ
差し替え、雛形リポジトリ（`.gitmodules` ＋ `src/` ＋ ダミー slnx）で `setup.sh` を実走し、
**発行コマンド列**へアサートする。

1. `git submodule update --init src/ai-stock-trading` が発行される
2. 🔴 **それが `dotnet restore` より前に発行される**（順序）
3. パスは `.gitmodules` から導出される（`.gitmodules` を書き換えると発行されるパスも変わる）
4. `src/` 直下でない submodule は取らない
5. `pnpm install --frozen-lockfile` が `src/` で発行される
6. `pnpm` が PATH に無ければ**発行されず、スクリプトは exit 0 で終わる**
7. `git` が失敗しても exit 0 で終わり、restore まで進む（fail-open）

## 受け入れ基準

- [x] submodule が未初期化の素の環境で `bash scripts/setup.sh` が初期化を行う
- [x] `node_modules` 未導入の環境で `pnpm install --frozen-lockfile`（`src/`）が行われる
- [x] submodule・pnpm が使えない環境でも fail-open で継続する（既存挙動を壊さない）
- [x] 🔴 初期化が restore より前に起きる（順序を試験で固定した）

## 変えていないもの

- .NET SDK の導入・PATH 追加・restore ループの本体。**1 行も触っていない。**
- Python のコメントアウト（上の決定 5）。
- `.devcontainer/`。**本スクリプトは devcontainer の代替ではなく併走である**（[[IADR-0180]]）。
