---
title: 作業仕様書 — #1686 ci-latency の逆転を、knowledge と platform のテストをシャードに分けて解消する
type: spec
status: done
related_ids: [NFR, IADR-0232, IADR-0123]
author: Claude Opus 5.5 (worker)
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
issue: "#1686"
---

# 作業仕様書 — #1686 knowledge と platform のテストをシャードに分ける

## 起点

- issue #1686（`ci-failure-issue.yml` の自動起票）。`ci-latency` の週次 run 36381319788（2026-09-28・develop `337f830`）が exit 1:
  **`build-and-test` の中央値 189 秒 > `claude-review` の最小 148 秒**（IADR-0232 決定 8。余裕 -41 秒）。
- 利用者裁定: **knowledge のテストの脚をシャーディングして逆転を解消する。閾値は変えない。**
- ［2026-09-28 範囲の拡張］knowledge を分けた後に律速が platform の脚へ移ったため、platform のシャード化も同じ裁定
  （「テストを分割して逆転を解消する」。2026-09-28 に #1686 の方針として選ばれた）の範囲に入れた（コーディネータ経由の指示）。
  集約ジョブの setup-node をやめる案は別の関心事として本件に入れない。
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
  | ConversionService.Tests | 50.4 | 232 |
  | GraphService.Tests | 37.4 | 761 |
  | DataSourceService.Tests | 26.5 | 331 |
  | DashboardService.Tests | 25.9 | 93 |
  | IngestionService.Tests | 25.7 | 104 |
  | RetrievalService.Tests | 23.3 | 454 |
  | WikiService.Tests | 18.5 | 129 |
  | AiAnalysisService.Tests | 17.1 | 161 |
  | FeedbackService.Tests | 15.5 | 39 |
  | Knowledge.IntegrationTests（`Category!=Integration`） | 14.2 | 62 |
  | Knowledge.Contracts.Tests | 8.2 | 90 |

  🔴 ［2026-09-28 訂正］割り当てを決めた時点では、同時に始まった 4 本（Conversion / AiAnalysis / Dashboard / DataSource）の
  結果を取り違え、Conversion=25.9・AiAnalysis=26.5・Dashboard=17.1・DataSource=50.4 と読んでいた。シャード化後の
  run 36430633926 では各シャードが 4 本ずつしか走らないので件数から一意に同定でき、上表はその同定で直した値である。
  取り違えた値で均した結果、シャードの合計は 92.5 / 96.6 / 128.2 秒と 3/3 に偏った（実測の結果を参照）。

- カバレッジ: `check-coverage-floor.js` は `src` 配下の `coverage.cobertura.xml` をすべて歩いて集計する。Cobertura は
  `<試験プロジェクト>/TestResults/<guid>/` に出るので、シャードで試験プロジェクトが重ならなければ展開後のパスは衝突しない。
- `scripts/check-ci-latency.js` は正常（週次の出力どおり）。直す対象は律速の脚。

## 設計

1. **行列を「ユニット × テストのシャード」にする。** `discover-units` がランナー既定の node で
   `scripts/plan-backend-test-shards.js --units "$json"` を呼び、`legs`（`{unit, label, key, projects}` の配列）を出力する。
   `backend-build` は `matrix.include: fromJSON(legs)`。`backend-format` は従来どおり `units` を使う。
2. **割り当ては `scripts/backend-test-shards.json`**（ユニット → 試験プロジェクトの配列の配列）。`ci.yml` にはユニット名も試験プロジェクト名も
   書かない（決定 2 の「ユニット追加時に CI の編集は不要」を保つ）。設定に無いユニットは 1 脚で `backend.slnx` 全体を試す（現在は該当なし）。
3. **シャードの脚も `backend.slnx` 全体を restore / build する。** 絞るのはテストだけで、シャードの試験プロジェクトだけを載せた一時の slnx
   （`src/<unit>/backend/ci-test-shard.slnx`。コミットしない）へ `--no-build` で `dotnet test` を掛ける。フィルタとカバレッジ収集は従来と同じ。
4. **取りこぼし・二重を fail-closed にする。** 導出器は「`backend.slnx` の試験プロジェクト（`Microsoft.NET.Test.Sdk` を PackageReference する
   csproj ＝ `dotnet test` が試験として走らせる集合）＝ シャードの和・各 1 回」を検査し、崩れ（未割り当て・二重・試験でない名前・古いユニット・
   1 シャード・空シャード）があれば exit 1 → `discover-units` が落ち、集約 `build-and-test` が赤になる。
5. **artifact 名を脚ごとに一意にする**（`coverage-<key>`。`knowledge-1`〜`knowledge-3`、`platform-1`〜`platform-2`）。
6. **脚の名前**: `backend-build (<label>)`。`backend-build (knowledge 1/3)` 〜 `(3/3)`、`backend-build (platform 1/2)` / `(2/2)`。

### シャード数と割り当て

- 3 シャード。最長の DocumentService.Tests（1 本で約 55 秒）を持つシャードは他を軽くした（同じランナーで重いものと並べると 4 並列の競合で伸びるため）。

  | シャード | 試験プロジェクト | 合計（秒） |
  | --- | --- | --- |
  | 1/3 | DocumentService / FeedbackService / Knowledge.Contracts.Tests / Knowledge.IntegrationTests | 92.5 |
  | 2/3 | DataSourceService / IngestionService / WikiService / DashboardService | 96.6（割り当て時の読みでは 111.7） |
  | 3/3 | GraphService / AiAnalysisService / ConversionService / RetrievalService | 128.2（同 113.1） |

- 見積もり: 各シャードの test ≒ 30〜55 秒（最長 1 本が下限）。脚の step ≒ 7 + 55 + 55 ≒ 115 秒以下 → knowledge は platform（109 秒）と同程度になる。
- ［2026-09-28 組み替え］上表は最初の割り当て（run 1〜4）である。シャード化後の各脚のログ（run 36430633926 / 36435375359）の所要から、最長脚が
  最小になるよう組み替えた。最も重い 3 本（Conversion 41〜51 秒・Graph 32〜35 秒・Document 28 秒）を別の脚へ分け、Conversion の脚には軽いものだけを組ませた。
  シャード数は 3 のまま（4 脚にして Conversion を単独にする案は、固定費 約 78 秒を払う脚を 1 本増やすだけで、軽い同居なら単独とほぼ同じになる見込みのため）。

  | シャード | 試験プロジェクト | 合計（秒） |
  | --- | --- | --- |
  | 1/3 | ConversionService / AiAnalysisService / FeedbackService / Knowledge.Contracts.Tests | 81.8 |
  | 2/3 | GraphService / RetrievalService / WikiService / Knowledge.IntegrationTests | 100.0 |
  | 3/3 | DocumentService / DataSourceService / IngestionService / DashboardService | 99.8 |

  結果（run 5）: Conversion は軽い同居でも 44.9 秒で縮まず、最長脚は縮まなかった（結果の節を参照）。割り当ては戻さない（戻しても最長脚は同じで、
  各脚の合計はこちらのほうが均等なため）。
- platform（範囲の拡張後）: 2 シャード。根拠は run 36360190149 / 36431967761 の platform の脚のログ（4 並列の Total time。同時に始まる 4 本は
  Passed 行の名前空間の件数で同定）: Platform.Bff.Tests 62.8 / 65.5、LlmGateway 32.3 / 30.9、AuthorizationService 26.4 / 24.0、McpServer 22.0 / 18.0、
  NotificationService 20.4 / 19.9、Platform.Shared.Infrastructure 18.5 / 16.8、Platform.Shared.Kernel 9.6 / 9.1。

  | シャード | 試験プロジェクト | 合計（秒・2 run の平均） |
  | --- | --- | --- |
  | platform 1/2 | Platform.Bff.Tests | 64.2 |
  | platform 2/2 | AuthorizationService / LlmGateway / McpServer / NotificationService / Platform.Shared.Infrastructure / Platform.Shared.Kernel | 124.9 |

  Bff 1 本が test の下限で、残り 6 本の合計を 4 並列で割った値（約 31 秒）より重い。3 シャード以上は Bff の下限が縮まず、脚ごとの固定費
  （ランナーの準備・取得・キャッシュ 約 18 秒＋restore / build 約 40 秒）を 1 脚ぶん増やすだけなので採らない。

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
| `.github/workflows/ci.yml` `backend-build` の脚の名前の注記「platform は `backend-build (platform)` を保つ」（範囲の拡張で誤りになる） | **直す**（注記のみ。手順・行列は変えない） |
| `scripts/scripts.repo.test.js` #1686 節「platform はシャードしない脚」の検査（範囲の拡張で誤りになる） | **直す**（実データの節を platform にも掛ける） |
| `.ai-context/adr/IADR-0232` の 2026-09-28 追記の platform の記述（決定 2 との関係・残余・実測） | **追記 2 で更新**（本文は直さず、追記 2 が置き換えを明記する） |
| `docs/tech/composable-component-guide.md` §2.5「`backend.slnx` に csproj を登録する」（監査の指摘。試験プロジェクトを足す手順に JSON への追記が抜ける） | **追記**（knowledge / platform ではシャードの設定へも足す。表示テキストに ID は書かない） |
| `.ai-context/specs/**`・`CHANGELOG.md` | 除外（凍結記録・生成物） |

## 受け入れ基準

- [x] knowledge のテストが 3 つの脚、platform のテストが 2 つの脚で並列に走り、19 試験プロジェクト（knowledge 12・platform 7）がいずれかの脚で
      ちょうど 1 回走る（ログの「Test run for」で確かめた）。
- [x] 必須 check 名 `build-and-test` は変わらず、全脚の成功を集める。ruleset・ブランチ保護は変えない。起動条件（`on:`）は変えない。
- [x] 「試験プロジェクトの集合 ＝ シャードの和・各 1 回」を repo test が knowledge と platform の実データで固定し、崩すと赤になる（変異で確かめた）。
- [x] カバレッジ artifact が脚ごとに一意（key と label の一意を repo test が固定）で、集約の `--report-only` が従来と同じ母集合を集計する。
- [x] 律速の経路（最も遅い脚の所要）が改善前（run 36360190149 で 177 秒）より縮む（最終構成で 130〜146 秒）。
- [ ] `ci.yml` を触らない PR の `build-and-test` の完了が閾値 148 秒を下回る —— **未達の見込み**（最終構成の見積もり 約 158〜174 秒。結果を参照）。
      判定はマージ後の ci-latency の週次 run に委ねる。
- [x] 閾値（`check-ci-latency.js`）は変えない。テストの skip・無効化をしない。

## テスト方針

- `scripts.repo.test.js` に #1686 節（4 件）: 実データの集合一致 / 導出器の崩れ 6 種 / `ci.yml` の配線（集約が前段 3 つの結果を「success 以外は失敗」で判定することを含む。監査 M6） / 脚の手順を dotnet のスタブで走らせる振る舞い。
- 変異（コミット後）: ①設定から試験プロジェクトを 1 つ落とす ②`ci.yml` の artifact 名を `coverage-${{ matrix.unit }}` へ戻す ③脚の手順で
  一時の slnx ではなく `backend.slnx` を試す ④試験プロジェクトを 2 シャードに重ねる ⑤platform のシャードから試験プロジェクトを 1 本落とす
  ⑥platform の Bff を 2 シャードに重ねる ⑦（組み替え後）1/3 から ConversionService を落とす ⑧2/3 から IntegrationTests を落とす
  ⑨Document を 1/3 にも重ねる ⑩Conversion を 3/3 にも重ねる ⑪（監査 M6）集約 `build-and-test` の `BUILD_RESULT != "success"` を `= "failure"` へ弱める
  —— いずれも repo test が赤になることを確かめ、`git show HEAD:<path>` で戻す。
- 本 PR 自身の CI（`backend-build (knowledge 1/3〜3/3)` / `(platform 1/2〜2/2)`）が実測になる。

## 計画書との差異

- 差異: なし。

## 結果

### 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → exit 0（#1686 節 4 件を含む）。CI の `scripts-tests` も緑。
- `check-trace-blocks` / `gen-knowledge-graph --check` / `check-workflow-job-refs` / `check-doc-links` / `check-cross-repo-refs` /
  `check-plan-id-qualification` / `check-doc-updated` / `check-adr-numbering` → OK。`check-commit-messages --range=origin/develop..HEAD` → 適合。
- actionlint 1.7.7（`-shellcheck=`）→ 指摘 0。
- 変異（コミット後・`scratchpad/impl-1686-mutate.sh`）: ①シャードから WikiService.Tests を落とす（repo test 赤・`--check` exit 1
  「どのシャードにも無い」）②artifact 名を `coverage-${{ matrix.unit }}` へ戻す（赤「脚ごとに一意でない」）③シャードの脚が
  `backend.slnx` 全体を試す（赤「一時の slnx へ絞ってテストしていない」）④WikiService.Tests を 2 シャードへ重ねる（赤「二重に走る」）。
  ⑤（platform の追加後）Platform.Shared.Kernel.Tests を platform のシャードから落とす（repo test 赤「platform: シャードの和が … 一致しない」・
  `--check` exit 1）⑥Platform.Bff.Tests を platform の 2 シャードに重ねる（赤「platform: … 二重に走る」）。
  いずれも `git show HEAD:<path>` で戻し、`git diff --quiet` を確かめた。

### PR の CI（run 36430633926・head d650501）

- 全ジョブ緑。脚は `backend-build (platform)` / `backend-build (knowledge 1/3)` / `(2/3)` / `(3/3)`。
- 取りこぼし・二重なし: 3 つの脚のログの「Test run for」は 4 本ずつ、計 12 本で重複なし（1/3: Document / Contracts / Feedback /
  IntegrationTests、2/3: Dashboard / Ingestion / DataSource / Wiki、3/3: AiAnalysis / Conversion / Graph / Retrieval）。失敗 0。
  各脚の Cobertura は 4 件ずつ上がり、集約の `--report-only` が緑。

| 脚 | Restore, build and test | build | test（壁時計） |
| --- | --- | --- | --- |
| knowledge（改善前・run 36360190149） | 157 秒 | 54.7 秒 | 90.5 秒 |
| knowledge 1/3 | 96 秒 | 54.7 秒 | 31.0 秒 |
| knowledge 2/3 | 94 秒 | 56.5 秒 | 26.0 秒 |
| knowledge 3/3 | 111 秒 | 55.7 秒 | 43.1 秒（ConversionService 1 本で 41.3 秒） |
| platform | 103 秒 | — | — |

- check 群の開始（13:43:26）からの完了オフセット:
  - backend-build の最後の脚（knowledge 3/3）の完了 **144 秒**、platform の脚 140 秒（改善前の run では knowledge 185 秒・platform 136 秒）。
  - 🔴 本 PR の `build-and-test` の完了は **240 秒**。本 PR は `ci.yml` を触るので `submodule-backend-build (ai-stock-trading)`（AST・完了 220 秒）が
    走り、集約がそれを待った。これは #1551 の設計どおり（`ci.yml` / gitlink / 共通 props を触る PR だけ）で、本件の改善対象ではない。
  - `ci.yml` を触らない PR の見積もり: 最後の脚 144 秒 ＋ 集約の自前の所要（改善前 run で脚の完了から 15 秒）≒ **159 秒**
    （改善前の同じ見積もりは 185 ＋ 15 ＝ 200 秒。律速の経路で **41 秒**縮んだ）。
- run 1 時点の判断（platform を分ける前）: 律速は platform の脚へ移った。これを受けて platform も分けた（下の run 3）。

### PR の CI（run 36433900638・head 85dd86f。platform も 2 シャード）

- 全ジョブ緑。脚は `backend-build (knowledge 1/3〜3/3)` / `backend-build (platform 1/2)` / `(platform 2/2)`。
- platform の取りこぼし・二重なし: 1/2 は Platform.Bff.Tests の 1 本、2/2 は Authorization / Mcp / Notification / LlmGateway /
  Shared.Infrastructure / Shared.Kernel の 6 本（計 7 本・重複なし・失敗 0）。
- Platform.Bff.Tests は単独の脚で **27.7 秒**（4 並列の下では 63〜65 秒）。platform の脚の step は 65 / 80 秒（従来 103〜110 秒）。
- 🔴 この run は `discover-units` がランナー待ちで 39 秒遅れて始まり（run 1・2 は 7〜10 秒）、knowledge 2/3 のキャッシュ復元が 57 秒掛かった
  （従来 5〜8 秒）。オフセットはその分だけ膨らんでいる。脚が 4 → 5 本へ増えたことが待ちに効いたかは、この 1 run では切り分けられない。

### PR の CI（run 36437032894・head d832b9a。knowledge を組み替えた後）

- 全ジョブ緑。knowledge の各脚の「Test run for」は 4 本ずつ（1/3: AiAnalysis / Conversion / Feedback / Contracts、2/3: Graph / Wiki /
  IntegrationTests / Retrieval、3/3: Dashboard / Ingestion / Document / DataSource）で、計 12 本・重複なし・失敗 0。
- 脚の test（壁時計）は 46.7 / 34.8 / 40.6 秒。**ConversionService.Tests は軽いものとだけ同居させても 44.9 秒**で、組み替え前の 3/3（41.3 / 51.3 秒）と
  ほとんど変わらなかった。ConversionService の所要は同居の重さではなく、それ自身の重さで決まっている。DocumentService は重いもの（DataSource）と
  同居して 28 → 39 秒に伸びた。**組み替えで最長脚は縮まなかった**（脚の開始から完了まで 133 秒。組み替え前は 130〜146 秒）。
- この run も `discover-units` がランナー待ちで 28 秒遅れて始まった。

### 計測表

オフセットは check 群の開始からの秒数。`build-and-test` の実測は、本 PR が `ci.yml` を触るので AST の合成ビルドを待った値である。
**見積もりは全 run で同じ式にそろえた**: 普通の PR の見積もり ＝ 脚の開始の遅れ（ランナー待ちの無い run の実測 13 秒）＋ 最も遅い脚の所要
（脚の開始から完了まで）＋ 集約ジョブ自身の所要 15 秒。ランナー待ちとキャッシュ復元の遅れはこの式で除かれる（run 3 の knowledge 2/3 の
キャッシュ 57 秒だけは括弧内に除いた値を併記する）。

| run | 構成（knowledge / platform） | platform の脚の完了 | 全脚の完了 | `build-and-test` の完了（実測） | 最も遅い脚の所要 | 普通の PR の見積もり |
| --- | --- | --- | --- | --- | --- | --- |
| 36360190149（改善前） | 1 / 1 | 136 秒 | 185 秒 | 200 秒 | 177 秒（knowledge） | 200 秒（実測） |
| 36430633926（run 1） | 3 / 1 | 140 秒 | 144 秒 | 240 秒 | 132 秒（knowledge 3/3） | 160 秒 |
| 36431967761（run 2） | 3 / 1 | 155 秒 | 155 秒 | 308 秒 | 142 秒（platform） | 170 秒 |
| 36433900638（run 3） | 3 / 2 | 139 / 160 秒 | 195 秒 | 285 秒 | 145 秒（knowledge 2/3。キャッシュ遅れを除くと 134 秒） | 173 秒（162 秒） |
| 36435375359（run 4・head 52398539） | 3 / 2 | 113 / 113 秒 | 160 秒 | 337 秒 | 146 秒（knowledge 3/3） | 174 秒 |
| 36437032894（run 5・head d832b9a・組み替え後） | 3 / 2 | 134 / 141 秒 | 172 秒 | 262 秒 | 133 秒（knowledge 1/3） | 161 秒 |
| 36438716595（run 6・head b1d08af2・develop 取り込み後。ランナー待ちなし） | 3 / 2 | 108 / 112 秒 | 142 秒 | 299 秒 | 130 秒（knowledge 1/3） | 158 秒 |

- 最終構成（knowledge 3・platform 2）の見積もりは run 3〜6 で **約 158〜174 秒**（中央 160〜162 秒）。改善前の 200 秒からは 26〜42 秒縮んだ。
  ランナー待ちの無かった run 6 では、全脚の完了が 142 秒、集約の自前 15 秒を足して約 157〜158 秒である。
- 🔴 **普通の PR で閾値 148 秒を下回るとは言えない。** 最終構成の 4 run の見積もりはすべて 148 秒を上回った（最良の run 6 で約 158 秒）。律速は knowledge の脚で、その下限は
  「脚の準備・取得・キャッシュ 約 18 秒＋restore / build 約 60 秒＋ConversionService.Tests 1 本 約 45 秒（シャードの割り当てでは縮まない）」≒ 123 秒、
  ここに脚の開始の遅れ 13 秒と集約の 15 秒が乗る。
- 残余（いずれも本 PR では行わない。裁定事項）: 集約ジョブの setup-node をやめる（約 5 秒）／各脚に重なる restore / build（約 60 秒）を
  ビルド成果物の再利用で減らす（IADR-0232 の別の手）。ConversionService.Tests 自体を速くする（試験の中身の変更。本件の範囲外）。
  判定はマージ後に `ci.yml` の epoch 以降の PR が 3 本たまった時点の ci-latency の週次 run で確かめる（IADR-0241）。
- 本 PR は #1686 を閉じない（`Refs #1686`）。逆転は縮んだが解消した実測は得られていないため。
