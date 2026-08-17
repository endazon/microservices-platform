---
title: 作業仕様書 — git シムを PATH 差し替えから child_process フックへ替え、Windows で完走させる
type: spec
status: done
related_ids:
  - NFR
  - IADR-0179
  - IADR-0183
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/docs/ai-implementation-workflow-guide.md (§8 メタ作業の統制)"
related_specs:
  - "../adr/IADR-0183_false-green-warning-on-worktree-state.md"
---

# 作業仕様書: git シムのクロスプラットフォーム化（#851）

## 1. 起点となる ID（トレーサビリティ）

- **無採番 `NFR`**（テストハーネスの移植性＝メタ作業。[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）
- 起票: [#851](https://github.com/endazon/microservices-platform/issues/851)
- **`scripts.repo.test.js` は companion（本リポ所有）であり、キット環流は不要である。**

## 2. 事象

`node scripts/scripts.test.js` が **Windows でのみ**偽陰性で落ちる。

```text
AssertionError: 走査母集合を git ls-files から引く検査器と MODE.TRACKED の宣言が食い違う
  actual: []   expected: [ 'check-cross-repo-refs.js', 'check-plan-id-qualification.js' ]
  at scripts/scripts.repo.test.js:5075
```

**既存事象であることを切り分けた** —— `develop`（`7aa0976`）を別 worktree へ取り出し、
**本ブランチの変更を一切含まない状態で同じ assertion が同じ値で落ちる**ことを確認した。

## 3. 原因（実測で切り分けた。**当初の見立ては誤っていた**）

### 原因 1: PATH 区切りをコロンで直書きしていた

`scripts.repo.test.js:5056` が `PATH: \`${shimDir}:${process.env.PATH}\`` としており、
Windows の区切り `;` に合わない。**ただしこれを直しても解消しなかった。**

### 原因 2: 🔴 `.cmd` ラッパーを足す案は成立しない

**当初は「拡張子なしのシムが実行できないだけなので `.cmd` を足せばよい」と見立てたが、
実測でこれは否定された。**

| 起動方法 | シムを経由するか |
| --- | --- |
| `execFileSync('git', args)`（shell:false） | **素通り** |
| `spawnSync('git', args)`（shell:false） | **素通り** |
| `execSync('git …')`（shell 経由） | ★ 経由する |
| `execFileSync(<絶対パス>/git.cmd, args)` | **`EINVAL`** |

`options.env.PATH` でも親の `process.env.PATH` でも素通りする。
**Node は shell:false での `.cmd` / `.bat` 実行を拒否する**（CVE-2024-27980 対策）ため、
PATH 解決がシムを飛ばして実体の `git.exe` に当たる。

**本リポの検査器の git 起動は `execFileSync('git'` 20 件 / `spawnSync('git'` 3 件 /
`execSync(\`git` 5 件**であり、**本テストが見る 2 本**
（`check-cross-repo-refs.js` / `check-plan-id-qualification.js`）**も `execFileSync` である**。
→ **`.cmd` ラッパーでは永久に捕まらない。**

## 4. 決めたこと

**PATH 経由の実行ファイル差し替えをやめ、`child_process` を JS レベルでフックする。**

- テストは既に `spawnSync(process.execPath, [script], …)` で検査器を起動している。
  **`--require <probe>` を 1 つ足すだけでよい**（`NODE_OPTIONS` の引用符問題も避けられる）
- probe は `execFileSync` / `spawnSync` / `execFile` / `spawn` / `execSync` / `exec` を包み、
  `git` 呼び出しの引数を `GIT_SHIM_LOG` へ**旧シムと同じ書式**（引数をスペース連結して 1 行）で追記する
- **プラットフォーム分岐を作らない。** 同じ機構を Linux でも使う ——
  **CI とローカルが同じものを測る状態を保つ**

### 範囲が狭まっていないこと

**旧シムは PATH 経由で起動される git をすべて捕まえた。** 新方式は**直接の子プロセス**しか見ない。
差が出るのは「検査器が入れ子でプロセスを起動し、その孫が git を呼ぶ」場合である。

**実測: 検査器は入れ子起動をしない。**

```text
grep -lnE "(spawnSync|execFileSync|execSync)\((['\"`])?(bash|sh|process\.execPath|node)" scripts/check-*.js
  → 0 件
```

→ **旧シムと同じ範囲を覆う。**

### むしろ厳密になった点

旧シムは `bash -lc 'command -v git'` で実体を探しており、**bash が無い環境では前提 assertion で落ちた**。
新方式は bash に依存しない。また `execSync`（shell 経由）も**シェルの PATH 解決に頼らず**捕まえる。

## 5. 変更したファイル

| ファイル | 変更 |
| --- | --- |
| `scripts/scripts.repo.test.js` | シム生成（`git` シェルスクリプト）→ probe 生成（`git-probe.js`）／`env.PATH` の差し替え → `--require probe`／`assert.ok(realGit, …)` の削除（旧機構の前提であり、新機構では意味を持たない） |

**期待値は 1 つも変えていない** —— `TRACKED_CHECKERS` / `HEAD_CHECKERS` / 母集合 38 件はそのままである。

## 6. 検証（実測）

```text
node scripts/scripts.test.js
  ✓ 651 tests passed        exit=0     ← Windows で完走
```

### 変異試験 —— 「壊すと落ちる」を実測した

| 変異 | 結果 |
| --- | --- |
| `--require probe` を外す（probe の注入を無効化） | **NG**。`actual: [] / expected: ['check-cross-repo-refs.js','check-plan-id-qualification.js']` で**起票時とまったく同じ失敗を再現** |

**probe を戻すと 651 件が再び全通過することも確認した。**

### `execSync` 経路も実効している

`HEAD_CHECKERS`（`check-doc-updated.js` / `check-landed-subjects.js`）の突合が pass している。
**これらは shell 経由の起動を含むため、probe の `execSync` 分岐が働いていることの裏づけになる。**

## 7. 注意（ローカル実行時）

`planning` submodule の作業ツリーが**そのブランチの pin とずれている**と、
`check-kit-sync` 系のテストが `[unclassified]` 等で落ちる。**テストの不具合ではない。**

```bash
git submodule update planning   # そのブランチの pin へ合わせる
```

本作業でも一度これを踏み、`.mcp.json` が未分類として上がった（`planning` が別ブランチ用に
`2c78212` のままだった）。**CI は pin どおりチェックアウトするため発生しない。**
