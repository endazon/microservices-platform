---
title: scripts/setup.sh に submodule 初期化と pnpm install を足す（素のクローンで platform slnx / frontend が実走できない）
type: spec
status: done
related_ids: [NFR, IADR-0180, IADR-0087, IADR-0065, IADR-0056]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: setup.sh は submodule も pnpm も知らない（#1349）

## 起点

- NFR（運用保守。**無採番** —— issue #1349 自身が計画側の非機能要件表に当たる番号が無いメタ／運用作業と
  宣言している。計画側に不足があるわけではないので環流はしない）。
- issue: #1349（出典: project-planning PR #574 横断監査 2026-09-09 指摘 B-7）

## 現状（実測）

`scripts/setup.sh`（109 行）を素のクローンで実行すると:

1. `src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj:20` が submodule
   `src/ai-stock-trading/backend/Bff/AiStockTrading.Bff.Endpoints` を `ProjectReference` するため、
   L85-93 の自動発見 `dotnet restore` が **submodule 未取得のまま** platform slnx を restore しようとして失敗する。
2. L95-99 は Node ブロックが**コメントアウトのまま**残っており、しかも `npm ci` を使っている
   （本リポジトリの実ツールチェーンは pnpm。`src/package.json` の `packageManager: "pnpm@10.33.0"`、
   ワークスペースルートは `src/`、メンバーは `src/pnpm-workspace.yaml`）。有効化されていても誤ったツールを呼ぶ。

## 母集合の引き直し（規則 1・2・9、除外理由込み）

**誤りの側**＝「submodule 未初期化 or pnpm 未導入のまま dotnet/pnpm を呼ぶ環境セットアップ経路」で
リポジトリ全体を走査した。

```
grep -rln "npm ci \|\| npm install|git submodule" .   # 拡張子を絞らない（規則 3）
grep -rln "pnpm install|--frozen-lockfile" --include='*.sh' .
```

トラッキング下（`git ls-files` 相当、submodule 内部・未追跡の `planning/` を除く）でヒットしたのは
**`scripts/setup.sh` の 1 件のみ**。

### 除外したものと理由（規則 6）

- **`src/ai-stock-trading/scripts/setup.sh`**: 別リポジトリ（submodule）の成果物。本リポジトリの
  issue #1349 の射程（本リポの `scripts/setup.sh`）ではない。触れる場合は AST 側の issue が要る。
- **`planning/`（untracked）**: セッション開始時点で既にワークツリーに存在した未追跡ディレクトリ
  （`git status` の `?? planning/`）。`.gitignore` 対象外の副産物と思われるが、追跡下ファイルではなく
  本 issue の変更対象にもならない。中の `impl-handoff-kit/repo-template/scripts/setup.sh` は**キット配布物
  のテンプレート**であり、CLAUDE.md「キットは bootstrap 専用であり、既存リポジトリに追随義務は無い」
  （ADR-0048 決定 6）により、こちらから同期する義務は無い。
- **`.github/workflows/copilot-setup-steps.yml`**: `actions/checkout@v7` に `submodules:` を指定しておらず
  pnpm install も持たない ―― 構造的には同じ「未整備」クラスだが、(a) `scripts/setup.sh` を呼ばない独立
  経路であり「同じ壊れたパターンの複製」ではなく単に**別の未実装**、(b) 拡張は issue の宣言した
  ファイル領域（`scripts/setup.sh`）を越え、並列作業の非重複判定を崩す、(c) issue #1349 の受け入れ基準は
  いずれも `scripts/setup.sh` の呼び出しを主語にしている。よって**本 issue の対象外**として記録に留め、
  必要なら別 issue で扱う。
- **`security.yml` / `codeql.yml` の `find ... -not -path './templates/*'` パターン**: 上記
  copilot-setup-steps.yml のコメントが指す「同一パターン」は自動発見の `find` 除外規則であり、
  submodule / pnpm の欠落とは無関係（現物を確認して確定）。対象外。

## 決定

### 決定 1: submodule 初期化はパスを絞り、dotnet restore より前に置く

```bash
git submodule update --init src/ai-stock-trading || log "submodule 初期化に失敗しました（継続）"
```

- **パスを 1 つに絞る**（全 submodule 一括にしない）。`.gitmodules` は現状 1 エントリのみだが、将来
  増えても「本スクリプトが要るのは `Platform.Bff.csproj` が参照する 1 本だけ」という前提を保つ。
- **冪等**: 既に populate 済みのワークツリーで再実行しても `git submodule update --init` は無害（差分なし
  なら何もしない）。
- **fail-open**: 既存の `dotnet restore || log "restore でエラー（継続）"` と同じ idiom。ネットワーク不通・
  認証不可でも後続（.NET restore・pnpm install）を止めない。
- **配置**: L74（.NET SDK 自己修復ブロックの直後）と L85（自動発見 restore の直前）の間。
  `Platform.Bff.csproj` の `ProjectReference` を restore が解決できるようにするため、**restore より前**
  という制約は issue 本文の実測どおり厳守する。

### 決定 2: pnpm install はコメントアウトの npm ブロックを置き換える（gated・fail-open）

```bash
if command -v pnpm >/dev/null 2>&1 && [ -f src/package.json ]; then
  log "pnpm install --frozen-lockfile（src/）を実行します"
  (cd src && pnpm install --frozen-lockfile) || log "pnpm install でエラー（継続）"
else
  log "pnpm または src/package.json が無いため pnpm セットアップをスキップ"
fi
```

- **cwd は `src/`**（pnpm workspace ルート。CLAUDE.md 技術スタック節・`frontend.yml` の既存 CI 呼び出しと
  同じ形）。サブシェル `(cd src && …)` でスクリプト本体の cwd を変えない（後続の `.sln`/`.slnx` 自動発見は
  リポジトリルート基準のまま）。
- **ゲート条件**: `pnpm` コマンドの存在 ＋ `src/package.json` の実在。.NET ブロックの
  「在れば使う → 無ければ入れる」（#824）とは非対称にする ―― pnpm の自動導入（corepack 等）は本 issue の
  受け入れ基準に無く、**技術非依存の安全設計**（該当しないスタックでは何もしない）を優先する。devcontainer
  の `node` feature は pnpm を同梱しないため、pnpm 不在環境では skip ログのみで継続する（fail-open）。
- **`--frozen-lockfile`**: CI と同じ再現性の保証（issue 受け入れ基準・CLAUDE.md 双方の要求）。
- **配置**: submodule 初期化・.NET restore の**後**（コメントを踏襲し「Node.js」節をそのまま実体化する。
  Python の例示ブロックは変更しない）。

### 決定 3: 既存のスタイル・fail-open idiom を変えない

- `log()` ヘルパーをそのまま使う。
- 日本語コメントの密度・体裁（見出し `# --- … ---`、注記 `【…】`、参照コメント）を既存ブロックに合わせる。
- スクリプトの構造（`set -u` のみ・`exit 0` で終える・DOTNET_CLI_TELEMETRY_OPTOUT を最初に export）は
  変更しない。

### 決定 4: テストは `k8s-local-up.test.js` と同じ stub-on-PATH 方式（opt-in トークン機構は使わない）

`scripts/setup.test.js` を新設する。

- **方式**: 記録スタブ（`git` / `pnpm` / `dotnet`）を一時 `bin/` へ置き `PATH` の先頭に挿す。
  `spawnSync('bash', [UP_SCRIPT], { cwd, env })` で実スクリプトを無改変で実行し、`STUB_LOG` に採取した
  argv 列をアサートする。
- **`k8s-local-up.test.js` から採らないもの**: `OPTIN_TOKENS` / `matchesToken` / `SYNTHETIC_CONTAMINATION`
  等の opt-in ゲート横断機構。`setup.sh` は opt-in フラグを 1 つも持たないため、この機構は不要
  （findings で明示された制約どおり）。
- **固定する不変条件**（受け入れ基準 1〜4 の写像）:
  1. `git submodule update --init src/ai-stock-trading` が呼ばれる（パスが正しいことを含む）。
  2. `pnpm install --frozen-lockfile` が **cwd=src** で呼ばれる（stub 側で cwd を記録し照合する。
     bash の `cd` はサブプロセスの作業ディレクトリに反映されるため、stub 実行時の `$PWD` を採る）。
  3. 上記 2 コマンドのいずれかが失敗する stub（非 0 終了）を返しても、`setup.sh` 全体は **exit 0** のまま
     （fail-open の回帰固定）。
  4. 実行順序: submodule init は dotnet restore（自動発見ループ）より前。
- **実クラスタ・実ネットワークには触れない**（`k8s-local-up.test.js` と同じ副作用ゼロの原則）。
- dotnet はスタブでよい（`.sln`/`.slnx` が無ければ restore ループ自体が動かないため、本テストの実行
  ディレクトリを一時 workdir にするか、リポジトリルートで実行して `command -v dotnet` を偽装するかを選ぶ
  必要がある。後者を採る ―― `dotnet` を記録スタブに差し替え、`.NET SDK 自己修復ブロック` は
  `command -v dotnet` が真を返すため即座に自動発見ループへ進み、実 slnx を「restore」呼び出しとして記録する
  だけで実際のビルドは起きない）。

## 変更ファイル

- `scripts/setup.sh`（本体）
- `scripts/setup.test.js`（新設）
- `scripts/README.md`（インベントリ行・使い方リスト）

## 受け入れ基準（issue 本文の Given-When-Then を写す）

- [x] Given submodule が未初期化の素の環境 / When `bash scripts/setup.sh` を実行する /
      Then `git submodule update --init src/ai-stock-trading` 相当の初期化が行われる
- [x] Given `node_modules` が未インストールの環境 / When 同スクリプトを実行する /
      Then `pnpm install --frozen-lockfile`（`src/` 配下）が行われる
- [x] Given 対応後 / When `dotnet build src/platform/backend/backend.slnx` と `pnpm run lint`（`src/`）を
      実行する / Then いずれも依存不足のエラーなく実行できる（本ワークツリーは submodule 既 populate・
      pnpm 既 install 済みのため、setup.sh 実行後も両コマンドが通ることを確認する）
- [x] Given submodule・pnpm が利用できない環境 / When `setup.sh` を実行する /
      Then 既存の技術非依存の安全設計どおり fail-open で継続する（既存挙動を壊さない）

## 変えていないもの

- `.NET SDK` 自己修復ブロック（L10-74）・自動発見 restore ループ（L76-93）・Python 例示ブロック（L101-105）。
- `DOTNET_CLI_TELEMETRY_OPTOUT` の位置・`log()` ヘルパーの実装。
- CI（`ci.yml` 等）は本 issue の対象外（findings のとおり `scripts/setup.sh` を呼ばないため）。
