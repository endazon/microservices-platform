---
title: 作業仕様書 — #1686 ci-latency の裁定（2026-10-01）のうち、ビルド成果物の再利用（見送り→閉包の build へ再裁定）と集約ジョブの setup-node の撤去
type: spec
status: done
related_ids: [NFR, IADR-0232]
author: Claude Opus 5.5 (worker)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
issue: "#1686"
---

# 作業仕様書 — #1686 ビルド成果物の再利用（見積もりで中止）と集約ジョブの setup-node の撤去

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **無採番の `NFR`**（CI 構成というメタ作業）。FR / UC / SC: なし。
- 関連: IADR-0232（決定 2・決定 8、2026-09-28 追記 1〜3）。前段の作業仕様書 `.ai-context/specs/20260928_issue-1686_knowledge-test-sharding.md`（#1693）。
- issue #1686 の裁定（2026-10-01・利用者）: 閾値は緩めない。①ビルド成果物の再利用（restore / build を共有ジョブで 1 回だけ行い各シャードで再利用）
  ②ConversionService.Tests の高速化（別作業）③集約ジョブの setup-node の撤去（約 5 秒）を、それぞれ別の PR で行う。

## 目的・背景

`ci-latency`（IADR-0232 決定 8）は「`build-and-test` の完了の中央値 ＜ `claude-review` の最小（148 秒）」を週次で監視する。#1693 の後も、
`ci.yml` を触らない PR の `build-and-test` の完了は見積もりで約 158〜174 秒であり、閾値を下回らない。本作業は裁定の ① と ③ を扱う。

## 対象範囲

- 対象: `.github/workflows/ci.yml` の `backend-build`（行列）・集約 `build-and-test`。IADR-0232 への日付つき追記。
- 対象外: ConversionService の試験・ソース（別作業が並行して扱う）。閾値（`scripts/check-ci-latency.js`）。シャードの割り当て（`scripts/backend-test-shards.json`）。

## 編集前に確かめた事実（2026-10-02）

- 依存の形: `discover-units` → `backend-build`（行列 5 脚: knowledge 1/3〜3/3・platform 1/2〜2/2）→ 集約 `build-and-test`（`needs:` ＋ `if: always()`）。
  脚は 1 本の step で restore → build（Release・`backend.slnx` 全体）→ `dotnet test --no-build`（一時の `ci-test-shard.slnx`）→ Cobertura を upload。
- 集約 `build-and-test` の step: 結果判定 → checkout → `coverage-*` の download → **setup-node（node 20）** → `node scripts/check-coverage-floor.js --self-test` →
  `--report-only`。**node は使っている**（検査器 2 回）。検査器と依存（`lib/ci-annotate.js` / `lib/excluded-units.js`）は `fs` / `path` / `os` だけの
  CommonJS で、版に依存する API（`fs.globSync` / `toSorted` / `Object.groupBy` / `.at(` / `structuredClone` / ESM）を使わない（grep で確かめた）。
  同じワークフローの `discover-units` は #1693 以降、setup-node を置かずにランナー既定の node で `plan-backend-test-shards.js` を呼んでおり、CI で緑である。
- 所要の内訳（過去の run は本作業では開けない。数値は前段の作業仕様書の実測と #1686 のコメントから取る）:
  - 普通の PR の `build-and-test` の完了 ＝ 脚の開始の遅れ Δ0（13 秒）＋ 最も遅い脚の所要 L ＋ 集約ジョブ自身の所要 A（15 秒）。最終構成で 158〜174 秒。
  - 律速の脚（knowledge）の L ≒ 準備・取得・キャッシュ S（約 18 秒）＋ restore R（約 7 秒）＋ build B（約 55 秒）＋ 最も重いシャードの test T（約 45 秒）
    ＋ 残り（Cobertura の upload・step の起動。約 5〜20 秒）＝ 130〜146 秒（実測）。

## 設計

### ① ビルド成果物の再利用 —— **見積もりで改善しないため実装を中止した**

案: ユニットごとの共有ジョブ `backend-compile (<unit>)` が restore / build を 1 回行い、試験プロジェクトの出力（bin / obj）を `actions/upload-artifact` で上げ、
各シャードの脚は `needs:` でそれを待って download し `dotnet test --no-build` だけを行う。

**クリティカルパスの式で比較した（規則 10: 導出値は計算し直した）。**

- 現行: P₀ ＝ Δ0 ＋ (S ＋ R ＋ B ＋ T ＋ r) ＋ A
- 案: P₁ ＝ Δ0 ＋ (S ＋ R ＋ B ＋ U) ＋ Δq ＋ (s′ ＋ D ＋ T ＋ r) ＋ A
  - U: 成果物の upload（圧縮込み）、Δq: 後段ジョブのランナー割り当て待ち、s′: 後段の脚の準備（ランナー起動・checkout・setup-dotnet）、D: download と展開
- 差: **P₁ − P₀ ＝ U ＋ Δq ＋ s′ ＋ D ＞ 0**（S・R・B・T・r は両辺で同じ。共有ジョブも準備 S を払う）。
  restore / build（R ＋ B ≒ 62 秒）は**どちらの形でも律速の経路に直列に 1 回載る**。現行でも各脚の build は**別のランナーで並列に**走っているので、
  5 脚で 5 回払っている build は「待ち時間」ではなく「ランナー時間」である。共有にしても経路から外れる秒は 0 で、成果物の受け渡しの分だけ経路が伸びる。
- 数値の見積もり（手元で測った成果物の大きさ。`dotnet build src/knowledge/backend/backend.slnx -c Release`、SDK 10.0.401）:
  - knowledge の bin / obj 全体: 約 1.4 GB・9,339 ファイル（gzip 後 約 522 MB）。`--no-build` に要る試験プロジェクトの `bin/Release` だけでも 12 本で約 839 MB
    （ConversionService.Tests の bin ＋ obj は gzip 後 約 31 MB / 元 約 84 MB）。シャードごとに分けても 1 脚あたり約 200〜270 MB（gzip 後 約 75〜100 MB）。
  - U ≒ 5〜15 秒、D ≒ 3〜10 秒、Δq ≒ 3〜10 秒（ランナー待ちの run では 28〜39 秒の実測あり）、s′ ≒ 8 秒（S から NuGet キャッシュと submodule の取得を除いた分）。
  - **P₁ − P₀ ≒ +19〜+43 秒**（普通の PR の見積もり 158〜174 秒 → 約 177〜217 秒）。閾値から遠ざかる。
- 他の注意点（中止のため実装では確かめていない。再検討時の論点として残す）: 後段の脚は試験プロジェクトの `obj/project.assets.json` 等も要る
  （`--no-build` でも MSBuild の評価は走る）／ジョブが 5 → 7 本に増え、ランナー待ち（実測 28〜39 秒）が増える方向に効く／
  `--no-build` 自体は現行でも使っており、シャードの選別（一時の slnx）は変わらない。
- 前例: IADR-0232 の却下 E（frontend の `e2e` を `build-test` へ依存させて二重ビルドを解消する案）が同じ理由で採られていない ——
  「本 IADR の目的はランナー時間の節約ではなく PR の待ち時間の短縮であり、目的に対して逆向きになる」。

**代替案（実装していない。裁定に掛ける）**: 「脚は自分のシャードの試験プロジェクトの推移閉包だけを build し、`backend.slnx` 全体の build は
並列の別ジョブ（律速の経路の外）で検証する」。#1693 が「推移閉包へ絞る」案を退けた理由（どの試験からも参照されない
`Knowledge.Bff.Endpoints` などのビルドが PR から落ちる）は、全体の build を別ジョブに残すことで解消する。

- 手元の実測（knowledge・4 コア・クリーンな bin / obj から build のみ・2 回）: 全体 27.0 / 27.7 秒（29 プロジェクト）、
  1/3（Conversion を含む）6.8 / 6.2 秒（11 プロジェクト）、2/3 17.7 / 12.7 秒（18）、3/3 14.1 / 9.8 秒（12）。
- CI の build（全体 約 55 秒）へ同じ比で写すと、1/3 約 13 秒・2/3 約 25〜35 秒・3/3 約 20〜28 秒。脚の所要は 1/3 約 93 秒・2/3 約 103 秒・3/3 約 102 秒、
  全体の build を検証する別ジョブは約 83 秒（いずれも S ＋ R ＋ B ＋ T ＋ r で計算）。knowledge の最長脚は約 133 秒 → 約 103 秒。
- そのとき律速は platform 2/2（脚の開始から約 110 秒。#1693 の run 3 実測）へ移り、普通の PR の見積もりは 13 ＋ 110 ＋ 15 ≒ **138 秒**（③ 込みで約 133 秒）。
  platform にも同じ形を掛ければさらに縮む見込みだが、platform は AST の submodule を要するため本作業の手元では測っていない。
- 代償: ジョブがユニットごとに 1 本増える。集約の `needs:` に検証ジョブを足す（必須 check 名は変わらない）。

### ③ 集約ジョブの setup-node の撤去 —— 実装した

- `build-and-test` の `actions/setup-node@v7`（node 20）の step を外し、`check-coverage-floor.js` をランナー既定の node で呼ぶ（`discover-units` と同じ）。
- **node は使っているが、setup-node は要らない**と判断した。理由: 検査器は版に依存する API を使わない（上の事実）。ランナー既定の node は同じワークフローの
  `discover-units` が既に依存しており緑である。node が無ければ step が落ちて集約が赤くなる（黙って緑にはならない＝ fail-closed は保たれる）。
- 節約: 約 5 秒（IADR-0232 決定 6 の実測「setup-node 5s」）。集約ジョブは必須 check の律速の経路の末尾にあるので、そのまま待ち時間から引かれる。
- 必須 check 名・ジョブ ID・`needs:`・`if: always()`・起動条件（`on:`）は変えない。

## 母集合（規則 9〜11）

- 規則 9（誤りの側の文字列で走査）: 語 `setup-node` を `git grep`（`.ai-context/specs`・`CHANGELOG.md`・`src/ai-stock-trading` を除く）で引いた。

  | 箇所 | 判定 |
  | --- | --- |
  | `.github/workflows/ci.yml` `build-and-test` の setup-node の step | **外す**（本変更） |
  | `.github/workflows/ci.yml` `commit-messages` / `scripts-tests` / `static-checks` / `static-checks-units` の setup-node | 変えない（律速の経路に無い並列ジョブ。裁定の対象外） |
  | `.github/workflows/ci.yml`:132（決定 6 の束ねの注記「setup-node 5s」） / :791（`discover-units` の注記） | 変えない（記述は変更後も真） |
  | `.ai-context/adr/IADR-0232` :500 / :507 / :539（「集約の setup-node の撤去」は残余・裁定事項） | 本文は直さず、**日付つき追記**で実施を記録する |
  | `scripts/check-ai-workflow-config.js`:57 / :532（setup-node → node の許可の対応表） | 変えない（他のジョブの setup-node が残るので結果は不変） |
  | `scripts/plan-backend-test-shards.js`:37 / `scripts/action-versions.json` / `scripts/check-action-versions.js` / IADR-0068 / `claude-*.yml` の注記 | 変えない（集約ジョブを指していない） |
  | `scripts/scripts.repo.test.js` の `build-and-test` の検査（#1551 / #1686 節） | 変えない（setup-node を検査していない。`needs:` と判定の形は不変） |

  ① は実装していないので、①で影響するジョブ・ステップ（`backend-build` の 5 脚の restore / build / test・upload、集約の download）は変更なし。
- 規則 10（自分の記述で新たに誤りになるもの）: IADR-0232 の 2026-09-28 追記 3「残余」の「集約ジョブの setup-node をやめる」は本変更で実施済みになる
  → 追記で更新を明記する。導出値（見積もりの差 +19〜+43 秒、代替案の 138 秒）は他人の数え（#1686 のコメントの「約 60 秒が対象」）を転記せず、
  上の式から計算し直した。🔴 #1686 のコメントの「ビルド成果物の再利用（約 60 秒が対象）」は**ランナー時間の対象**であり、待ち時間の対象ではない。
- 規則 11（窓の是正）: 該当なし。本作業は時間差の窓を扱う検査・是正ではない（所要時間の経路の見積もりであり、「前の端」「後の端」を突き合わせる形の選択が無い）。

## 受け入れ基準

- [x] ①: クリティカルパスの式で現行と比べ、改善しないことを根拠つきで示し、実装しない（無理に入れない）。代替案を数値つきで示す。
- [x] ③: 集約 `build-and-test` に setup-node の step が無く、`check-coverage-floor.js` の self-test と `--report-only` がランナー既定の node で走る。
- [x] 必須 check 名（`build-and-test`）・ジョブ ID・`needs:`・`if: always()`・起動条件（`on:`）を変えない。
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` と文書・トレーサビリティの検査器が通る。
- [ ] PR の CI で `build-and-test` が緑で、集約ジョブの所要が約 5 秒縮む（PR 後に測る）。

## テスト方針

- 振る舞いの検査は CI 自身（集約ジョブの self-test と `--report-only` が走って緑になること）。node の版の固定は試験にしない
  （検査器の追加は同型事故 2 回から。node が無ければ step が落ちる＝ fail-closed）。
- 手元: `node scripts/check-coverage-floor.js --self-test`（node 22）・ワークフロー YAML の構文（actionlint が無ければ python の yaml 読込）・
  `scripts.test.js`・検査器群。

## 計画書との差異

- 差異: なし（計画書は NFR の運用・保守のみ。① の中止は計画ではなく #1686 の裁定に対する見積もりであり、再裁定を仰ぐ）。

## 未決事項

- ~~① を中止し代替案（推移閉包の build ＋ 全体の build の別ジョブ）へ置き換えるかは**利用者の再裁定**を仰ぐ。~~ → 下の［2026-10-02 追記］で解消。

## 結果

- 手元の検証は本作業のコミットのとおり（報告に記す）。PR の CI で確かめること: 集約ジョブの「Self-test coverage floor checker」が
  ランナー既定の node で緑、集約ジョブの所要（従来 約 15 秒）が約 10 秒前後になること。週次の判定は ci-latency の run に委ねる（#1686 は開いたまま）。

［2026-10-02 追記 / #1686］再裁定と代替案の実装

## 再裁定（2026-10-02・利用者。コーディネータ経由）

1. ① ビルド成果物の再利用は見送り、代替案「各脚は自分のシャードの試験プロジェクトの推移閉包だけを build し、`backend.slnx` 全体の build は
   律速の経路の外の別ジョブで検証する」を採る。
2. ③ setup-node の撤去は「外す」で確定（先のコミットをそのまま残す）。

要件: 全体 build の失敗を必須 check（集約 `build-and-test`）が取りこぼさない／律速の経路を延ばさない（並列・脚より短いことを見積もりで示す）／
閉包は ProjectReference の推移に従い、プロジェクト一覧をワークフローへ複写しない／各シャードは自分で建てたものだけを試し、選別は変わらない／
platform も同じ形（AST の submodule の取得は既存の手順のまま）／必須 check 名・`on:`・paths を変えない。

## 設計（追記）

- `backend-build`（脚）の step: 一時の slnx（`ci-test-shard.slnx`）を先に作り、それを `dotnet restore` → `dotnet build --no-restore -c Release` →
  `dotnet test --no-build`（フィルタ・カバレッジ収集は従来どおり）。シャードしない脚（`SHARD_PROJECTS` が空）は `backend.slnx` 全体のまま。
  閉包は MSBuild が ProjectReference から辿る（一時の slnx に載るのは `scripts/backend-test-shards.json` の試験プロジェクトだけ）。
- 新ジョブ `backend-verify-build (<unit>)`: `needs: discover-units`、行列は `units`。取得（submodule）・NuGet キャッシュ（保存あり）・setup-dotnet・
  `backend.slnx` 全体の restore / build。テストは走らせない。ユニット名を書かない。
- 集約 `build-and-test`: `needs:` に `backend-verify-build` を足し、`VERIFY_BUILD_RESULT` を「success 以外は exit 1」で判定する。
- 脚の NuGet キャッシュは `actions/cache/restore`（復元のみ）。閉包だけの中身を全体のキーで保存しないため。
- IADR は IADR-0232 への日付つき追記（2026-10-02 追記 2）。IADR-0490 を新設しない理由は同追記に書いた（同じ監視への一連の手当てで、
  2026-09-28 追記 1 の設計の置き換えであり、正を 2 か所に分けない）。

## クリティカルパスの見積もり（追記・規則 10: 計算し直した）

式: 普通の PR の完了 ＝ Δ0（13 秒）＋ 最も遅いジョブの所要（脚と `backend-verify-build` の最大）＋ A（③ 後 約 10 秒）。
各ジョブ ＝ 準備 S 18 ＋ restore R ＋ build B ＋ test T ＋ 残り r 8（脚）。

手元の build の実測（クリーンな bin / obj。knowledge は build のみ・2 回、platform は restore 込み・1 回）と CI への写し（CI の全体 build ÷ 手元の全体 build の比）:

| ジョブ | 手元（秒） | CI の B（見積もり） | T（#1693 の実測） | 所要（見積もり） |
| --- | --- | --- | --- | --- |
| knowledge 1/3（Conversion を含む・11 プロジェクト） | 6.2〜6.8 | 約 13 | 47 | 約 93 |
| knowledge 2/3（18 プロジェクト） | 12.7〜17.7 | 約 25〜35 | 35 | 約 93〜103 |
| knowledge 3/3（12 プロジェクト） | 9.8〜14.1 | 約 20〜28 | 41 | 約 94〜102 |
| platform 1/2（Bff・7 プロジェクト） | 9（全体 14） | 約 24 | 28 | 約 85 |
| platform 2/2（13 プロジェクト） | 11（全体 14） | 約 30 | 約 35 | 約 91 |
| backend-verify-build (knowledge) | 27〜28（全体） | 55 | — | 約 83 |
| backend-verify-build (platform) | 14（全体） | 38 | — | 約 62 |

- 最も遅いのは knowledge 2/3 で約 93〜103 秒。検証ジョブ（約 83 / 62 秒）は脚より約 10 秒以上短く、律速の経路に載らない見積もり。
- **普通の PR の完了 ≒ 13 ＋ 93〜103 ＋ 10 ＝ 約 116〜126 秒**（本追記の前は 158〜174 秒。閾値 148 秒に対し余裕 約 22〜32 秒）。
  ConversionService.Tests の高速化（別 PR）は含めない。

## 母集合（追記・規則 9）

誤りの側＝「シャードの脚も `backend.slnx` 全体を restore / build する」「推移閉包へ絞る案は採らない」と読める記述。語: `全体を restore` / `backend.slnx 全体` /
`推移閉包` / `ci-test-shard` / `ビルドは backend` / `閉包` / `backend-build`（docs）を `git grep`（`.ai-context/specs`・`CHANGELOG.md`・`src/ai-stock-trading` を除く）で引いた。

| 箇所 | 判定 |
| --- | --- |
| `.github/workflows/ci.yml` `backend-build` の手順と注記・NuGet キャッシュ | **直す**（本変更） |
| `.github/workflows/ci.yml` 新ジョブ `backend-verify-build`・集約の `needs:` と判定 | **足す** |
| `scripts/plan-backend-test-shards.js`:16 の方式の注記「各脚は `backend.slnx` 全体を restore / build し」 | **直す** |
| `scripts/scripts.repo.test.js` #1686 節の配線・手順の試験（脚が全体を build することを固定していた） | **直す**（閉包・検証ジョブ・集約の判定・キャッシュの復元のみを固定） |
| `docs/ai-workflow.md` 必須チェック表の `build-and-test` 行 | **追記**（`backend-verify-build` も拾う。trace ブロックに本仕様書を足した） |
| `scripts/backend-test-shards.json`:4「ここに無いユニットは 1 脚で backend.slnx 全体を試す」 | 変えない（変更後も真） |
| `.ai-context/adr/IADR-0232` 2026-09-28 追記 1 の :485 / :493 | 本文は直さず、**2026-10-02 追記 2 で置き換えを明記** |
| `docs/how-to/adding-a-unit-submodule.md`:90 / `docs/tests/TEST_STRATEGY.md`:118 | 変えない（行列の自動発見・AST を含まない、は変更後も真） |
| 他の「推移閉包」（IADR-0162 / 0196 / 0236 / 0280・`check-backend-libraries.js` ほか） | 対象外（別の文脈の語） |

規則 11: 該当なし（窓の是正ではない）。

## 受け入れ基準（追記）

- [x] シャードの脚は一時の slnx を restore / build / `test --no-build` し、`backend.slnx` 全体の build は `backend-verify-build` が並列に検証する。
- [x] 集約 `build-and-test` が `backend-verify-build` を `needs:` に持ち、success 以外を失敗と判定する（repo test が固定。変異で赤を確認）。
- [x] `backend-verify-build` は `discover-units` だけを待ち、脚はそれを待たない（repo test が固定）。所要の見積もりは最も遅い脚より短い。
- [x] プロジェクト一覧・ユニット名をワークフローに書かない（repo test が固定）。
- [x] 手元で knowledge 3 シャード・platform 2 シャードの閉包 build と `test --no-build` が通り、19 試験プロジェクトがすべて走り失敗 0。
- [x] 必須 check 名・`on:`・paths を変えない。
- [ ] PR の CI で、各脚の build が縮み、`backend-verify-build` が最も遅い脚より先に終わる（PR 後に測る）。

## 結果（追記）

- 手元の閉包 build → `test --no-build --filter "Category!=Integration"`（SDK 10.0.401・クリーンな bin / obj から各シャード単独）:
  - knowledge 1/3: build 7 秒。Contracts 90 / AiAnalysis 161 / Feedback 39 / Conversion 232（skip 9）。2/3: build 15 秒。IntegrationTests 62 / Wiki 129 /
    Retrieval 454 / Graph 761。3/3: build 13 秒。DataSource 331 / Dashboard 93 / Ingestion 104 / Document 978。件数は #1693 の CI 実測の表と一致・失敗 0。
  - platform（AST の submodule は gitlink の e392843 を作業ツリーの外へ取り出してつないだ。検証後に外した）: 全体 restore＋build 14 秒（18 プロジェクト）。
    1/2: restore＋build 9 秒（7 プロジェクト）・Bff 791（skip 1）。2/2: 11 秒（13 プロジェクト）・McpServer 242 / Notification 104 / Authorization 577 /
    Shared.Kernel 42 / LlmGateway 326 / Shared.Infrastructure 484。失敗 0。
- 変異（`scratchpad/msp1686a-mut/` に退避した ci.yml から戻した）: ①集約の `needs:` から `backend-verify-build` を落とす ②`VERIFY_BUILD_RESULT` の判定を `= "failure"` へ弱める
  ③`backend-verify-build` に `backend-build` を待たせる ④脚のキャッシュを `actions/cache` へ戻す ⑤脚の restore を `backend.slnx` へ戻す ⑥脚の build を `backend.slnx` へ戻す
  —— いずれも `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が exit 1。戻した後の `cmp` で一致を確かめた。
