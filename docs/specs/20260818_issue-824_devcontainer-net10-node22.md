---
title: 作業仕様書 — devcontainer を net10.0 / Node 22 へ追随させ、setup.sh を .NET SDK を「入れる側」にする
type: spec
status: done
related_ids:
  - NFR
author: claude
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - "../../CLAUDE.md（自動化・検証・安全 / 技術スタック別ルール）"
related_specs:
  - ../how-to/local-development.md
  - ../tech/tech-requirements.md
related_adrs:
  - ../adr/IADR-0048_dotnet10-target-framework.md
  - ../adr/IADR-0180_blocked-judgments-expire.md
issue: "#824"
related_issues:
  - "#823"
---

# 作業仕様書: devcontainer を net10.0 / Node 22 へ追随させ、setup.sh を「入れる側」にする

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 該当なし（NFR。開発環境の再現性）
- ユースケース（UC）: 該当なし
- 画面（SC）: 該当なし
- 関連 ADR: [[IADR-0048]]（バックエンドは .NET 10 / C# 13）、[[IADR-0180]]（環境依存の判定に前回値を据え置かない）
- 計画書リンク: [`CLAUDE.md`](../../CLAUDE.md) §自動化・検証・安全「再現可能な環境」／§技術スタック別ルール

## 目的・背景

`.devcontainer/devcontainer.json` の宣言がリポジトリの実際の要求から取り残されている。

| 宣言（是正前） | リポジトリの実際の要求 | 実害 |
| --- | --- | --- |
| `"image": "mcr.microsoft.com/devcontainers/dotnet:8.0"` | `src/Directory.Build.props` は `net10.0` | SDK 8 では `net10.0` をターゲットにできず `dotnet build` が通らない |
| node feature `"version": "20"` | `src/package.json` の `engines.node` は `>=22` | pnpm / Vite 6 / Vitest 3 の要求を満たさない |

`.devcontainer/` は「AI がビルド・テストを実走できる環境を用意する」ための装備である（`CLAUDE.md`
§自動化・検証・安全）。それが目的を果たしていない。PR #823 は、コンテナに `dotnet` が無いことを
根拠に大玉 17 件を「着手不可」と誤判定した（[[IADR-0180]] の事例）。

## 対象範囲

- 対象: `.devcontainer/devcontainer.json` / `scripts/setup.sh` / `scripts/kit-sync-classification.json`（分類の是正のみ）
- 対象外（**他レーンの領分**。本作業では触らない）:
  - `.github/workflows/ci.yml` —— 並行レーンが編集中
  - `scripts/scripts.repo.test.js` —— 並行レーンが編集中
- **突合検査器は置かない（issue #824「やること」3 番目の任意項目への回答）。理由は領域の都合ではなく規約である** ——
  `CLAUDE.md`「**検査器・規約の追加は『同型の事故が 2 回起きたら』を条件とする（1 回目は記録に留める）**」。
  devcontainer が `net10.0` へ追随しなかった事故は**今回が 1 回目**であり、条件を満たさない。**本作業は記録に留める。**
  同型（版の宣言がスタックへ追随しない）が**2 回目に起きたら**、そのとき `scripts/scripts.repo.test.js` へ置く。
  なお `setup.sh` の版導出は突合検査器ではない（後述 §設計）。
- 対象外（記録であり書き換えない）: `docs/adr/` / 確定済み `docs/specs/` / `docs/superpowers/` / `feedback/` に残る「.NET 8」「node 20」表記。これらは**過去の状態の記録**であり、遡及書き換えの対象ではない（`.claude/rules/traceability.repo.md` §Superseded / Deprecated な ADR を引用するときの書式）。

## 母集合の引き方と結果（規則 9・10）

**誤りの側の文字列**で追跡下の全ファイルを走査した（拡張子で絞らず、パス除外のみ。`planning/` と
`src/ai-stock-trading` を除外）。

### 軸 1 — `dotnet:8.0` / `net8.0` / `.NET 8` / `devcontainers/dotnet` / `DOTNET_VERSION`

```
git grep -nI -E 'dotnet:8\.0|devcontainers/dotnet|net8\.0|dotnet 8|\.NET 8|DOTNET_VERSION' \
  -- . ':!planning' ':!src/ai-stock-trading'
```

生きた宣言の該当は **`.devcontainer/devcontainer.json:3` の 1 件のみ**。他の全ヒットは
`docs/adr/IADR-0048` / `docs/adr/IADR-0186` / `docs/adr/README.md` / 確定済み `docs/specs/*` /
`docs/superpowers/*` / `docs/tech/*` / `feedback/*` の**記録・経緯の記述**であり、除外した
（是正済みであることを既に本文で述べているものを含む）。

### 軸 2 — `"version": "20"` / `node-version` / `Node 20` / `.nvmrc` / `engines`

```
git grep -nI -E '"version": "20"|node-version|Node 20|node 20|node20|nodejs 20|Node\.js 20|node:20' \
  -- . ':!planning' ':!src/ai-stock-trading' ':!.github/workflows'
git grep -nI -E '\.nvmrc|"node":|engines|volta' -- . ':!planning' ':!src/ai-stock-trading' \
  ':!**/pnpm-lock.yaml' ':!**/package-lock.json'
git ls-files | grep -iE 'nvmrc|node-version'
```

- 生きた宣言の該当は **`.devcontainer/devcontainer.json:7` の 1 件のみ**。
- `docs/specs/*` のヒットは**過去の PR の diff の引用**であり除外。
- `.nvmrc` / `node-version` ファイルは**存在しない**（`git ls-files` が 0 件）。
- **`src/package.json:9` が `"node": ">=22"` を宣言している** —— これが本リポの生きた正であり、
  Node 22 へ揃える根拠になる。

### 軸 3 — `.github/workflows/` の実測（他レーンの領分だが、根拠として測る）

```
grep -rn "node-version" .github/workflows/
```

**issue #824 本文の「CI も 22」は正確ではない。実測では CI は混在している:**

| ワークフロー | node-version |
| --- | --- |
| `frontend.yml`（131 / 235 行）・`frontend-tests.yml`（96 行） | **22** |
| `ci.yml`（20 箇所）・`openapi.yml` / `changelog.yml` / `pr-title.yml` / `image-mapping.yml` / `planning-pin-freshness.yml` / `doc-links-planning.yml` | **20** |

`CLAUDE.md`「Node は CI と揃え **22** を使う」はフロントエンド節の規約であり、Node を実際に使う
ビルド（pnpm / Vite / Vitest）は 22 で走っている。`src/package.json` の `engines.node: ">=22"` も
22 を要求する。したがって **devcontainer は 22 が正**である。
**`ci.yml` 側の 20 を 22 へ揃えるかは本作業の対象外**（他レーンの領分。必要なら別 issue）。

### 軸 4 — 規則 10（是正後の語で引き直す）

是正後、`.devcontainer` / `devcontainer` を含む追跡下の全ファイルを引き直した。

```
git grep -nIl -E 'devcontainer|\.devcontainer' -- . ':!planning' ':!src/ai-stock-trading'
```

該当 10 ファイルのうち、**版を具体的に述べている生きた記述は 1 つも無かった**
（`AI_SETUP.md:32` / `CLAUDE.md:80` / `docs/ai-workflow.md:96,220` / `docs/how-to/local-development.md:57-58` /
`scripts/README.md:37` はいずれも「`.devcontainer/` が環境を用意する」という役割の記述にとどまる）。
よって**追随して書き換えるべき手順書・README は無い**。

### 軸 5 — キット分類表（本作業で新たに誤りになる自分の記述）

`scripts/kit-sync-classification.json` が `.devcontainer/devcontainer.json` を **分類 A
（キットとバイト一致であるべき）** に置いており、実際にキット版とバイト一致であることを確かめた。

```
diff planning/tools/impl-handoff-kit/repo-template/.devcontainer/devcontainer.json \
     .devcontainer/devcontainer.json   # → 差分なし
```

**この状態で devcontainer.json を編集すると `check-kit-sync.js` が drift で落ちる。**
本作業は固有デルタを意図して入れるので、分類を **B（種 2. 技術スタックとその CI 配線）** へ改める。
キットは「C#/.NET 例」の汎用雛形として `dotnet:8.0` / node 20 を配っており、本リポの
`.NET 10` / `Node 22` はスタック固有の恒久デルタである（環流債務ではないため X ではない）。

## 設計

### 1. `.devcontainer/devcontainer.json`

- `image` を `mcr.microsoft.com/devcontainers/dotnet:8.0` → **`mcr.microsoft.com/devcontainers/dotnet:10.0`**
- node feature の `version` を `"20"` → **`"22"`**
- それ以外（`name` / `features` の構成 / `postCreateCommand` / `extensions` / `remoteEnv`）は**キット土台のまま据え置く**（固有デルタを版の 2 値だけに絞る）

**タグの実在確認（issue #824 が課した必須条件）は §実在確認 に生の出力を残した。**

### 2. `scripts/setup.sh`（issue の「やること」4 番目の判断）

**判断: 「在れば使う」から「無ければ入れる」へ改める。**

理由:

- devcontainer image の是正は**devcontainer で起動したときにしか効かない**。PR #823 が誤判定した
  のは devcontainer ではないセッションコンテナであり、そこを埋めるのは `setup.sh` しかない
  （`CLAUDE.md` §自動化・検証・安全は `.devcontainer/` と `scripts/setup.sh` の**両方**を
  「実走できる環境を用意する」装備と位置づけている）。
- issue #824 自身が `dotnet-install.sh --channel 10.0` で入り、両ユニットのビルド・テストが
  通ることを実測している（Passed 446 / 574）。方法は実証済みである。

**オフライン環境での失敗の扱い: 完全に fail-open とする。** `setup.sh` は「該当しないスタックでは
何もせず正常終了する」設計であり、pin 鮮度検査も明示的に fail-open にしてある。ネットワーク不通・
ダウンロード失敗はログを出して継続し、`exit 0` を維持する。**インストールできなければ、従来どおり
「dotnet が無いので restore をスキップ」に落ちるだけで、退行はしない。**

**チャネルは `src/Directory.Build.props` の `TargetFramework` から導出する**（`net10.0` → `10.0`）。
版をここに直書きすると、それ自体が次の追随漏れ点になる（規則 10）。
**これは「突合検査器」ではない** —— 不一致を検出して落とすのではなく、
**構成上そもそも不一致になりようがなくする**導出である。導出に失敗したときは
インストールをスキップする（勝手な既定版を打たない）。

### 3. `scripts/kit-sync-classification.json`

`.devcontainer/devcontainer.json` を A → B（種 2）へ移し、理由を書く。

## 受け入れ基準

- [ ] `.devcontainer/devcontainer.json` の `image` が **実在確認済みの** `net10.0` ビルド可能なタグである
- [ ] node feature の版が `src/package.json` の `engines.node: ">=22"` と一致する（= 22）
- [ ] `scripts/setup.sh` が `dotnet` 不在時に SDK の導入を試み、**失敗しても `exit 0` する**
- [ ] `node scripts/check-kit-sync.js` が通る（分類表の是正込み）
- [ ] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る
- [ ] 文書検査器一式（doc-links / doc-status / doc-type / cross-repo-refs / plan-id / adr-numbering / reading-budget）が通る

## 実在確認（issue #824 の必須条件）

§報告 に生の出力を残す。要点:

- `https://mcr.microsoft.com/v2/devcontainers/dotnet/tags/list` に **`10.0` が実在**（GA。`10.0-preview` は別タグとして併存）
- image config の `DOTNET_SDK_VERSION=10.0.302` / `variant=10.0-noble` を実測
- node feature の `version` の `proposals` に **`"22"` が実在**（`ghcr.io/devcontainers/features/node:1` の `dev.containers.metadata`）
- `https://dot.net/v1/dotnet-install.sh` が HTTP 200 で取得できる

## テスト方針と実測結果

版の突合そのものの機械検査は**上の理由（同型の事故 1 回目）で置かない**。本作業の変更は
既存の `scripts.test.js` / `check-kit-sync.js` で回帰を見て、`setup.sh` は**実走**で確かめた。

| 試験 | 条件 | 結果 |
| --- | --- | --- |
| A | `dotnet` 不在・ネットワーク到達可 | `.NET SDK 10.0.400` を導入し、**knowledge / platform の両ユニットの `dotnet restore` が成功**。`EXIT=0` |
| B | `dotnet` も `curl` も PATH に無い（オフライン相当） | 「導入をスキップ（継続）」をログし **`EXIT=0`**（fail-open） |
| C | `src/Directory.Build.props` から版を導出できない | 「版を導出できないため導入をスキップ（継続）」をログし **`EXIT=0`**（既定版を打たない） |
| D | `$HOME/.dotnet` に既存の導入がある | 再ダウンロードせず PATH へ追加し restore まで到達。`EXIT=0` |

チャネル導出の実測: `sed -n 's|.*<TargetFramework>net\([0-9][0-9.]*\)</TargetFramework>.*|\1|p'`
→ **`10.0`**。

**受け入れ基準「devcontainer の image で `dotnet build` が両ユニットとも通る」は、image を
直接起動せずに実測した** —— 同じ SDK 系列（`10.0`）を `dotnet-install.sh` で入れた状態で
両ユニットの restore が通ることを試験 A で確かめ、image 側は `DOTNET_SDK_VERSION=10.0.302`
をレジストリの config blob から読んで確認した。**コンテナランタイムが本環境に無いため
`docker run` での直接確認はしていない**（親への申し送り）。

## 計画書との差異

- 差異: なし。`CLAUDE.md` の「.NET 10 / C# 13」「Node は CI と揃え 22」に追随させる作業である。

## 未決事項

- **issue #824 本文の「CI も 22」は実測と食い違う**（`ci.yml` は 20）。`ci.yml` は他レーンが編集中の
  ため本作業では触らない。**`ci.yml` の Node を 22 へ揃えるかは別途判断が要る**（親へ確認）。
- キット側（`project-planning` の `tools/impl-handoff-kit/`）も `dotnet:8.0` / node 20 のままである。
  汎用雛形としてそれが妥当かは計画側の判断であり、本リポからは環流しない（種 2 の恒久デルタ）。
