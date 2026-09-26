---
title: 作業仕様書 — #1551 MSP の PR の CI で、submodule ユニット（AST）のバックエンドを MSP の構成でビルドし単体テストする
type: spec
status: done
related_ids: [NFR, IADR-0060, IADR-0065, IADR-0232]
author: Claude Opus 5.5 (worker)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
issue: "#1551"
---

# 作業仕様書 — #1551 submodule ユニットのバックエンドを PR で MSP の構成で試す

## 起点

- issue #1551（#1492 の監査・2026-09-25）。`ci.yml` の `discover-units` は submodule を取らずにチェックアウトするので、
  ユニットの行列は platform と knowledge だけになる。AST のコードは MSP の `src/Directory.Build.props` を import-chain で
  取り込んだ構成（合成）でビルドされるが、テストはマージ後の `integration.yml`（push(develop) と日次）でしか走らない。
  AST のテストが MSP の構成でだけ落ちる場合、PR の段階では見えない。
- 起点 ID は **無採番の `NFR`**（CI 構成というメタ作業。計画の NFR 表に当たる番号が無い）。

## 編集前に確かめた事実（2026-09-26）

- `ci.yml` の `backend-build`（matrix 脚）と `backend-format` は checkout の後に submodule を取るが、行列を決める
  `discover-units` は取らない。**脚の中の取得は行列に効かない**（AST の脚は生まれない）。`backend-build` の注記
  「未取得だと自動発見に現れず対象外になる」は、取得している位置が違うため実態と食い違っていた。
- 合成の仕組み: AST の `Directory.Build.props` が `GetPathOfFileAbove` で MSP の `src/Directory.Build.props` を Import する
  （`RMG012` / `RMG082` の `WarningsAsErrors` と xUnit1051 の許可リストが AST にも届く）。AST の `Directory.Packages.props`
  は親を Import しない（CPM は AST 自身の宣言）。SDK は作業ディレクトリから `global.json` を探すので、リポジトリ直下から
  `dotnet` を呼ぶ限り MSP の `global.json` が効く。
- `integration.yml` の合成手順: `src/*` の submodule を非再帰で init → NuGet キャッシュ → `setup-dotnet 10.0.x` →
  `dotnet restore / build --configuration Release / test` を `src/<unit>/backend/backend.slnx` ごとに実行（`--filter` なし）。
- AST の Docker を要るテストは `Category=Integration`（AST 自身の CI も `--filter "Category!=Integration"`）。
- 所要: `integration.yml` の直近 run（36228230229）で AST の restore → build → 全テスト（コンテナ込み）が **6 分 3 秒**。
  AST 自身の CI は単体テストを 4 シャードで約 3 分。

## 設計

1. `ci.yml` にジョブを 2 つ足す（ジョブ ID は新設。**必須 check 名は 1 つも変えない**）。
   - `submodule-changes`: PR の差分（`origin/<base>...HEAD`）に、submodule ユニットの gitlink（`.gitmodules` の
     `src/<unit>` から導出。**ユニット名をワークフローへ書かない**）・`.gitmodules`・`src/Directory.*`（合成の共通 props）・
     `global.json`・`ci.yml` 自身のいずれかがあれば `build=true`。push では走らせない（回収先は `integration.yml` が
     全量で持つ）。差分を確定できなければ安全側で `build=true`。
   - `submodule-backend-build`（matrix: 導出した submodule ユニット）: `integration.yml` と同じ取得・キャッシュ・SDK で
     `src/<unit>/backend/backend.slnx` を restore → build（Release）→ test（`--filter "Category!=Integration"`）。
     カバレッジは集めない（MSP の床の母集合から AST は除外。AST の床は AST の CI が持つ）。
2. 既存の集約ジョブ `build-and-test` の `needs:` へ 2 つを足し、`image-build` と同じ形で判定する
   （`submodule-changes` は success 必須、`submodule-backend-build` は success か skipped）。**PR をマージ前に止める経路になる。**
3. CI 時間: path フィルタで、gitlink・共通 props・`ci.yml` を触らない PR では `submodule-changes`（数秒）だけが走る。
   NuGet はキャッシュする（キーに AST の `Directory.Packages.props` も入れる）。
4. `discover-units` には submodule を取らせない（取らせると全 PR で AST の脚が生まれ、`backend-format`・カバレッジ集計にも
   入る）。この判断を `discover-units` の注記に書き、`backend-build` / `backend-format` の食い違った注記を直す。

## 母集合（規則 1〜10）

誤りの側＝「PR の CI が submodule ユニットをビルド・テストしている」と読める記述を引いた。
語: `discover-units` / `submodule 未取得` / `自動発見` / `build-and-test` ＋ AST / `lint` / `取り込まれる` / `post-merge`。

| 箇所 | 判定 |
| --- | --- |
| `.github/workflows/ci.yml` `backend-build` / `backend-format` の取得ステップの注記 | **直す**（脚の中の取得は行列に効かない） |
| `docs/how-to/adding-a-unit-submodule.md`「サービス CI 発見は編集不要」節と「実例: ai-stock-trading は … `lint` / `build-and-test` に取り込まれる」 | **直す**（実態と違う。本変更後の形へ） |
| `docs/tests/TEST_STRATEGY.md`「`build-and-test` が全ユニットの `backend.slnx` を自動発見して test する」 | **直す**（全ユニットを自動発見して test するのは `integration.yml`。PR の `build-and-test` は本変更の後も AST のカバレッジを集めない） |
| `src/README.md`「CI は編集不要（`ci.yml` は自動発見する）」 | 変えない（本変更の後も submodule ユニットは `.gitmodules` から導出され、CI の編集は要らない） |
| `docs/ai-workflow.md` 必須チェック表の `build-and-test` 行 | **追記**（名前は変えず、集約する中身が増えたことを書く） |
| `.ai-context/adr/IADR-0232` | 日付つき追記（PR から外していた「submodule ユニットの単体テスト」を path フィルタつきで PR へ戻す） |
| `.ai-context/specs/**`・`CHANGELOG.md` | 除外（凍結記録・生成物） |
| `src/ai-stock-trading`（submodule） | 除外（他リポジトリ） |

## 受け入れ基準

- [ ] gitlink / 共通 props / `global.json` / `.gitmodules` / `ci.yml` を触る PR で `submodule-backend-build (ai-stock-trading)` が走り、
      AST のバックエンドを MSP の構成で restore → build → 単体テスト（`Category!=Integration`）する。
- [ ] それ以外の PR では重いジョブは走らず、`build-and-test` は従来どおり緑になる（skipped を合格として扱う）。
- [ ] 必須 check 名は変わらない。`build-and-test` が新しいジョブの失敗を拾う。
- [ ] ワークフローの起動条件（`on:`）は変わらない。
- [ ] NuGet パッケージをキャッシュする。

## テスト方針

- 本 PR 自身が `ci.yml` を変えるので、path フィルタにより `submodule-backend-build (ai-stock-trading)` が本 PR で走る —— これが実測になる。
- ローカルでも同じ手順（submodule を init し、リポジトリ直下から `dotnet restore / build -c Release / test --filter "Category!=Integration"`）を走らせる。
- 構造の回帰は `scripts.repo.test.js` に #1551 節を足して固定する（集約ジョブが新しいジョブを needs に持ち、skipped だけを合格にすること・
  path フィルタが gitlink と共通 props を見ること・`discover-units` が submodule を取らないこと）。

## 計画書との差異

- 差異: なし。

## 結果

- ローカル（Windows・SDK 10.0.401）で CI と同じ手順を実行した: `git submodule update --init src/ai-stock-trading`（gitlink 7a7a8a1）→
  リポジトリ直下から `dotnet restore` / `dotnet build --no-restore --configuration Release`（0 エラー・警告 1）/
  `dotnet test --no-build --configuration Release --filter "Category!=Integration"` → **22 アセンブリ・合格 9051・スキップ 4・失敗 0**。
  最長は RiskManagementService.Tests の 5 分 19 秒（アセンブリ間は並列）。
- 差分判定の正規表現を合成した一覧で試した（`src/ai-stock-trading` / `src/Directory.Build.props` / `src/Directory.Packages.props` /
  `global.json` / `.gitmodules` / `ci.yml` → 真、`src/ai-stock-trading/...` のファイル・`src/platform/Directory.Build.props` /
  `images.yml` / `docs/README.md` / `src/ai-stock-tradingX` → 偽）。
- `scripts.repo.test.js` の #1551 節 5 件は、`child_process` とネットワークを塞いだ下で #1551 の試験だけを走らせて緑を確かめた。
  変異（`build-and-test` の needs から `submodule-backend-build` を外す）で 1 件が落ちることも確かめた（戻し済み）。
  🔴 **本ブランチでは `node scripts/scripts.test.js` の全量をローカルで走らせていない。** 同じ試験の #852 節が稼働側の門
  （`check-password-reset-mail.js` など）を引数なしで起こすため（#1550。PR #1578 がその穴を塞ぐ）。全量は CI の `scripts-tests` で確かめる。

## ［2026-09-26 追記 / 監査への対応］

- N1: `ci.yml` は `permissions:` を持たず、リポジトリ既定は書き込み可である。新しい 2 ジョブに `permissions: { contents: read }` を置き、
  checkout を `persist-credentials: false` にした（submodule は public で、`.gitmodules` の https URL を匿名で取れるので資格情報は要らない）。
  他のジョブの権限は本 PR では変えない（ワークフロー全体の姿勢は別 issue の候補として PR 本文に記録）。
- N2: 差分判定の `printf … | grep -Eq` は、step 自身の `set -euo pipefail` の下で、大きな差分の先頭で一致すると書き手が SIGPIPE（141）で
  落ち「一致したのに build=false」になる。here-string（`grep -Eq -- "$pattern" <<< "$changed"`）へ替え、step を切り出して git / jq を
  スタブにして実際に走らせる試験を足した（6 万行の差分の先頭・末尾で一致 → true、不一致 → false）。旧形へ戻すとこの試験が落ちることを
  ローカルで確かめた（SIGPIPE を再現）。`images.yml` の同じ形は、step が既定シェル（`bash -e`・pipefail なし）で走るため
  パイプの終了コードは grep のものになり、この経路は生じない —— 変えていない。
- 指摘の小項目: `for u in $units` を `while IFS= read -r u … <<< "$units"` へ替えた。
