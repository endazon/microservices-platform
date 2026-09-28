---
title: 作業仕様書 — #1686 ci-latency の逆転を、knowledge のテストをシャードに分けて解消する
type: spec
status: in-progress
related_ids: [NFR, IADR-0232, IADR-0123]
author: Claude Opus 5.5 (worker)
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
issue: "#1686"
---

# 作業仕様書 — #1686 knowledge のテストをシャードに分ける

## 起点

- issue #1686（`ci-failure-issue.yml` の自動起票）。`ci-latency` の週次 run 36381319788（2026-09-28・develop `337f830`）が exit 1:
  **`build-and-test` の中央値 189 秒 > `claude-review` の最小 148 秒**（IADR-0232 決定 8。余裕 -41 秒）。
- 利用者裁定: **knowledge のテストの脚をシャーディングして逆転を解消する。閾値は変えない。**
- 起点 ID は **無採番の `NFR`**（CI 構成というメタ作業）。

## 編集前に確かめた事実（2026-09-28）

- `ci.yml`: `discover-units`（`src/*/backend/backend.slnx` の glob → `units`）→ `backend-build`（行列: ユニット。restore → build
  （Release）→ `dotnet test --filter "Category!=Integration" --collect:"XPlat Code Coverage"` → `coverage-<unit>` を upload）→
  集約 `build-and-test`（`needs:` ＋ `if: always()`。`coverage-*` を `merge-multiple` で展開し `check-coverage-floor.js --report-only`）。
- 必須 check 名（`docs/ai-workflow.md` の表）は **`build-and-test`（集約）** であり、`backend-build (<unit>)` の脚は必須ではない。
  → 脚の名前が変わっても恒久 pending にならない。集約ジョブの名前・`needs:` は変えない。
- PR の CI run 36360190149（2026-09-27・成功）の内訳（ジョブのステップ時刻とログ）:

  | 脚 | Restore, build and test | restore | build | test |
  | --- | --- | --- | --- | --- |
  | knowledge | 157 秒（23:53:44 → 23:56:21） | 約 7 秒 | 54.7 秒 | 90.5 秒（12 プロジェクトを 4 並列） |
  | platform | 109 秒（23:53:44 → 23:55:33） | 約 3 秒 | 38.1 秒 | 64.6 秒（最長 1 本 62.8 秒） |

  check 群の開始（23:53:22）から `build-and-test` の完了（23:56:42）まで **200 秒**。knowledge の脚の完了（23:56:27）が律速。
- knowledge の試験プロジェクトごとの所要（同ログの `Total time`。4 並列の実測。どの結果がどのプロジェクトかは
  「Test run for」の開始時刻と `完了 − 所要` の突き合わせ、および試験件数で同定した）:

  | 試験プロジェクト | 秒 | 件数 |
  | --- | --- | --- |
  | DocumentService.Tests | 54.6 | 978 |
  | DataSourceService.Tests | 50.4 | 232 |
  | GraphService.Tests | 37.4 | 761 |
  | AiAnalysisService.Tests | 26.5 | 331 |
  | ConversionService.Tests | 25.9 | 93 |
  | IngestionService.Tests | 25.7 | 104 |
  | RetrievalService.Tests | 23.3 | 454 |
  | WikiService.Tests | 18.5 | 129 |
  | DashboardService.Tests | 17.1 | 161 |
  | FeedbackService.Tests | 15.5 | 39 |
  | Knowledge.IntegrationTests（`Category!=Integration`） | 14.2 | 62 |
  | Knowledge.Contracts.Tests | 8.2 | 90 |

- カバレッジ: `check-coverage-floor.js` は `src` 配下の `coverage.cobertura.xml` をすべて歩いて集計する。Cobertura は
  `<試験プロジェクト>/TestResults/<guid>/` に出るので、シャードで試験プロジェクトが重ならなければ展開後のパスは衝突しない。
- `scripts/check-ci-latency.js` は正常（週次の出力どおり）。直す対象は律速の脚。

## 設計

1. **行列を「ユニット × テストのシャード」にする。** `discover-units` がランナー既定の node で
   `scripts/plan-backend-test-shards.js --units "$json"` を呼び、`legs`（`{unit, label, key, projects}` の配列）を出力する。
   `backend-build` は `matrix.include: fromJSON(legs)`。`backend-format` は従来どおり `units` を使う。
2. **割り当ては `scripts/backend-test-shards.json`**（ユニット → 試験プロジェクトの配列の配列）。`ci.yml` にはユニット名も試験プロジェクト名も
   書かない（決定 2 の「ユニット追加時に CI の編集は不要」を保つ）。設定に無いユニットは 1 脚で `backend.slnx` 全体を試す（platform は従来どおり）。
3. **シャードの脚も `backend.slnx` 全体を restore / build する。** 絞るのはテストだけで、シャードの試験プロジェクトだけを載せた一時の slnx
   （`src/<unit>/backend/ci-test-shard.slnx`。コミットしない）へ `--no-build` で `dotnet test` を掛ける。フィルタとカバレッジ収集は従来と同じ。
4. **取りこぼし・二重を fail-closed にする。** 導出器は「`backend.slnx` の試験プロジェクト（`Microsoft.NET.Test.Sdk` を PackageReference する
   csproj ＝ `dotnet test` が試験として走らせる集合）＝ シャードの和・各 1 回」を検査し、崩れ（未割り当て・二重・試験でない名前・古いユニット・
   1 シャード・空シャード）があれば exit 1 → `discover-units` が落ち、集約 `build-and-test` が赤になる。
5. **artifact 名を脚ごとに一意にする**（`coverage-<key>`。knowledge は `knowledge-1`〜`knowledge-3`、platform は `platform`）。
6. **脚の名前**: `backend-build (<label>)`。platform は `backend-build (platform)` のまま、knowledge は `backend-build (knowledge 1/3)` 〜 `(3/3)`。

### シャード数と割り当て

- 3 シャード。最長の DocumentService.Tests（1 本で約 55 秒）を持つシャードは他を軽くした（同じランナーで重いものと並べると 4 並列の競合で伸びるため）。

  | シャード | 試験プロジェクト | 合計（秒） |
  | --- | --- | --- |
  | 1/3 | DocumentService / FeedbackService / Knowledge.Contracts.Tests / Knowledge.IntegrationTests | 92.5 |
  | 2/3 | DataSourceService / IngestionService / WikiService / DashboardService | 111.7 |
  | 3/3 | GraphService / AiAnalysisService / ConversionService / RetrievalService | 113.1 |

- 見積もり: 各シャードの test ≒ 30〜55 秒（最長 1 本が下限）。脚の step ≒ 7 + 55 + 55 ≒ 115 秒以下 → knowledge は platform（109 秒）と同程度になる。

### 必須 check 名の扱い（選んだ方法と理由）

- **「必須 check 名を変えずに済む形」を採った。** 必須は既に集約ジョブ `build-and-test`（IADR-0232 決定 2）であり、脚 `backend-build (...)` は必須ではない。
  したがって新しい集約ジョブは要らず、既存の `build-and-test` が `needs: backend-build` で全シャードを集める（`BUILD_RESULT` は行列の全脚が
  success のときだけ success）。ruleset・ブランチ保護は変えない。

### 採らなかった案

- `--filter "FullyQualifiedName~<名前空間>"` で分ける: シャード外の試験プロジェクトも testhost を起こし（カバレッジ込みで数秒ずつ）、
  名前空間の規約に依存する。一時の slnx のほうが「どのプロジェクトを走らせるか」を明示できる。
- シャードの脚の build を試験プロジェクトの推移閉包へ絞る: どの試験からも参照されない `Knowledge.Bff.Endpoints` などのビルドが PR から落ちる。
- 割り当てを自動（重み付き LPT）にする: 試験プロジェクトを足しても黙って配置されるが、重みを持つ別の表が要り、割り当てがレビューで読めなくなる。
  明示の配列 ＋ fail-closed の検査にした（足したら 1 行足す）。
- 集約ジョブの新設: 必須 check は既に集約ジョブなので不要。

## 母集合（規則 1〜10）

誤りの側＝「`backend-build` の行列はユニットの軸（1 ユニット 1 脚）」「artifact は `coverage-<unit>`」と読める記述を引いた。
語: `backend-build (` / `coverage-${{ matrix` / `coverage-(platform|knowledge)` / `matrix: platform` / `ユニットを matrix の軸` / `行列 \`backend-build\``。
`git grep`（`.ai-context/specs`・`CHANGELOG.md`・`src/ai-stock-trading` を除く）の結果:

| 箇所 | 判定 |
| --- | --- |
| `.github/workflows/ci.yml` `backend-build` 冒頭注記「ユニットを matrix の軸にして並列化する」 | **追記**（#1686 以降の軸） |
| `.github/workflows/ci.yml` `discover-units` / `backend-build` / upload | **直す**（本変更） |
| `.github/workflows/ci.yml` `backend-format` の「ユニットを matrix の軸にする」 | 変えない（整形はユニットの行列のまま） |
| `docs/ai-workflow.md` 必須チェック表の `build-and-test` 行 | **追記**（脚はシャードごと・名前が変わっても恒久 pending にならない） |
| `docs/how-to/adding-a-unit-submodule.md`:90 / `docs/tests/TEST_STRATEGY.md`:118 | 変えない（「行列はユニットを自動発見する」は変更後も真） |
| `.ai-context/adr/IADR-0232` の `backend-build (knowledge)` 2 分 44 秒（:103 / :323） | 変えない（当時の実測。凍結） |
| `.ai-context/adr/IADR-0232` | **日付つき追記** |
| `scripts/README.md` | **行を足す**（新スクリプト） |
| `scripts/scripts.repo.test.js` #683 節の検査器の母集合 | **NOT_CHECKERS へ足す**（導出器は検査器ではない。58 本は据え置き） |
| `.ai-context/specs/**`・`CHANGELOG.md` | 除外（凍結記録・生成物） |

## 受け入れ基準

- [ ] knowledge のテストが 3 つの脚で並列に走り、12 試験プロジェクトがいずれかの脚でちょうど 1 回走る（ログの「Test run for」で確かめる）。
- [ ] 必須 check 名 `build-and-test` は変わらず、全脚の成功を集める。ruleset・ブランチ保護は変えない。起動条件（`on:`）は変えない。
- [ ] 「knowledge の試験プロジェクトの集合 ＝ シャードの和・各 1 回」を repo test が実データで固定し、崩すと赤になる（変異で確かめる）。
- [ ] カバレッジ artifact が脚ごとに一意で、集約の `--report-only` が従来と同じ母集合を集計する。
- [ ] PR の CI で `build-and-test` の完了オフセット（check 群の開始から）が改善前（run 36360190149 で 200 秒・週次の中央値 189 秒）より縮む。
- [ ] 閾値（`check-ci-latency.js`）は変えない。テストの skip・無効化をしない。

## テスト方針

- `scripts.repo.test.js` に #1686 節（4 件）: 実データの集合一致 / 導出器の崩れ 6 種 / `ci.yml` の配線 / 脚の手順を dotnet のスタブで走らせる振る舞い。
- 変異（コミット後）: ①設定から試験プロジェクトを 1 つ落とす ②`ci.yml` の artifact 名を `coverage-${{ matrix.unit }}` へ戻す ③脚の手順で
  一時の slnx ではなく `backend.slnx` を試す —— いずれも repo test が赤になることを確かめ、`git show HEAD:<path>` で戻す。
- 本 PR 自身の CI（`backend-build (knowledge 1/3〜3/3)`）が実測になる。

## 計画書との差異

- 差異: なし。

## 結果

（PR の CI 完走後に追記する）
