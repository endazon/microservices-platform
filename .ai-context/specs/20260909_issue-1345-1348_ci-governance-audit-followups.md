---
title: 横断監査（planning#574（PR））が挙げた CI 統制の乖離 4 件を、写しを消して導出へ寄せる形で直す
type: spec
status: done
related_ids: [NFR, ADR-0007, IADR-0118, IADR-0141, IADR-0232, IADR-0235, IADR-0364]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 統制を「定めた」と「働いている」を読み分けられるようにする（#1345 / #1346 / #1347 / #1348）

## 起点

- NFR（運用保守。**無採番** —— 工程の統制であり、計画側の非機能要件表に当たる番号が無い。planning#311 のケース 2）
- 計画 ADR: `ADR-0007`（CI/CD）。裁定: planning#286（統制を定める記述には現在の実現手段を併記する）
- issue: #1345（床を強制する経路が無い）/ #1346（床値コメントと期待件数の乖離）/ #1347（定期棚卸しの不在）/
  #1348（必須チェック表の件数・廃止ジョブ名・突合検査器の不在）
- 出典はいずれも planning#574（PR）（横断監査 2026-09-09）。**4 件とも同じ文書群（`ci.yml` / `integration.yml` /
  `docs/ai-workflow.md`）を触るため 1 PR に束ねた**（利用者指示 2026-09-09「複数 issue を 1 PR にまとめてよい」。
  [[IADR-0116]] 規約 1 の例外として、本仕様書に理由を残す）。

## 現状（実測。基点 `develop` `7c9d184c`。`git rev-parse --is-shallow-repository` = `true` のため git 履歴は出典にしない）

| # | 実測 | 位置 |
| --- | --- | --- |
| #1345 | `integration.yml` の床 step は前段のテスト step が落ちると **skipped**。PR 側は `--report-only` | `integration.yml:151`（旧） / `ci.yml:763` |
| #1346 | 床コメント「88 / 68」が 3 箇所（正本は 90 / 75）。期待レポート件数「16 件」「17 件」（実物 19 件） | `integration.yml:11,89,126` / `ci.yml:753` / `coverage-floor.json:180` |
| #1347 | 「定期棚卸し」へ委ねる記述 3 箇所に対し、`.github/workflows/` 19 ファイルに該当ワークフロー無し | `CLAUDE.md:84` / IADR-0364:164 / IADR-0235:242 |
| #1348 | 「下表の 7 件」×2 に対し実表 8 行（CodeQL 除く）。`doc-links` ジョブの名指しが **5 文書 6 箇所**（issue が挙げた 2 箇所より多い） | 後掲「母集合」 |

## 母集合（規則 1・2・9・10）

**誤りの側の語で全文書を走査した。**

```
$ grep -rn '`[a-z0-9-]*` ジョブ' docs CLAUDE.md AGENTS.md AI_SETUP.md scripts/README.md | 現存ジョブを除く
  docs/traceability-appendix.md:28   doc-links
  docs/README.md:106                 doc-links
  docs/tests/SC-14_otp-mfa.md:31     realm-constraints
  docs/ai-workflow.md:112            doc-links
  docs/DEFINITION_OF_DONE.md:18      doc-links
  scripts/README.md:25 / :280 / :325 reading-budget / contract-schema / k8s-local-up-smoke
$ grep -n "88\|68\|16 件\|17 件" .github/workflows/integration.yml .github/workflows/ci.yml src/coverage-floor.json
  → issue が挙げた 5 箇所と一致（他に無い）
```

**issue #1348 は 2 箇所（DoD / ai-workflow）を挙げたが、走査では 9 箇所出た。** 廃止名は `doc-links` だけでなく
`realm-constraints` / `reading-budget` / `contract-schema` / `k8s-local-up-smoke` も残っていた（すべて [[IADR-0232]]
決定 6 で `static-checks` へ束ねたもの）。**記憶で挙げた 2 箇所だけ直していたら 7 箇所が残った** —— 規則 9 の実演である。

### 除外したものと理由（規則 6）

- `scripts/README.md:143`「ジョブ名（`doc-links` / `realm-constraints` 等）は**もう存在しない**」—— 過去形の言及であり正しい。
  検査器では「もう存在しない」を含む行だけを除外する（無条件に除外すると生きた指示の中の廃止名を拾えない）。
- `.ai-context/` 配下 —— 凍結記録。走査もしない。
- `CLAUDE.md:119`「CI の `lint` ジョブ」—— `lint` は `ci.yml` に実在する（陰性対照）。

## 決定

### 決定 1（#1346）: 🔴 **写しを消し、導出へ寄せる。数値を直さない**

床値のコメント 3 箇所は「90 / 75」へ書き換えるのではなく **「正本は `src/coverage-floor.json`。ここへ写さない」** に改めた。
期待レポート件数は **`check-coverage-floor.js` が追跡下の `*Tests.csproj`（`git ls-files`。除外ユニットは `isExcludedPath`）から
毎回導出して突き合わせる**（`countTrackedTestProjects` / `compareReportCount`）。

- 実物より**多い** → 二重実行の疑い → **fail**（`--report-only` では warn）。床では見えない型（#900 の重複排除で分母が倍にならない）なので件数で止める
- 実物より**少ない** → 出力を残さないプロジェクト（フィルタで 0 件・ビルド失敗）→ **warn**（fail-open。PR 側の `Category!=Integration` で正当に起きる）
- `git` が無い → skip を notice で明示

「19 件へ更新する」を採らなかったのは、次に 20 件になったとき同じ issue が立つからである（規則 10: 導出値は写さない）。
2026-09-09 の実測は 19 件（knowledge 12 ＋ platform 7）で、`coverage-floor.json` の注記にはこの実測を**数え方つきで**残した。

### 決定 2（#1345）: **床の評価をテストの合否から切り離す。政策は変えない**

- `integration.yml` のテストループは `set -e` で最初に落ちたユニットで抜けていた → **失敗を記録して全ユニットを走らせ切り、最後に exit 1**
- 床の 3 step（setup-node / self-test / 床）に `if: ${{ !cancelled() && steps.tests.outcome != 'skipped' }}` を付け、
  **テストが赤くても床は評価する**。`force_failure`（テスト step が skipped）のときだけ床も skip
- 🔴 **PR 段階で床を強制する経路は足していない。** 政策（`integration` を必須 check にする／PR 側に床相当を置く）は
  issue が「利用者引き取り事項」と明記しており、実装側で選ばない。代わりに `docs/ai-workflow.md` へ
  **「PR をマージ前に止める経路は 0 本である」と正直に書き、配備済みの手段と暫定手段を並べた**（planning#286 の形）

### 決定 3（#1347）: **定期棚卸しを配備する（`backlog-audit.yml` ＋ `scripts/backlog-audit.js`）**

- 週次（月曜 21:00 UTC）＋ `workflow_dispatch`。**列挙するだけで状態は書き換えない**（CLAUDE.md の `stocktake` と同じ立場）
- 見るもの 7 面: Proposed のまま止まった IADR／動いていない作業仕様書／draft・in-progress・pending の文書／
  写像後回しのテスト（allowlist）／`blocked*` ラベルで更新の止まった issue（**blocked 判定は棚卸しごとに再検証する**）／
  放置された `ci-failure` issue／動いていない PR
- 🔴 **「success だが無産出」を作り込まない**（受け入れ基準 2。AST 側で実測された事故）:
  指摘 0 件でも各節が「指摘なし」を明示した報告を必ず産出する／`--post` は棚卸し issue の本文を置き換えてコメントを足し、
  **読み戻して run 固有のマーカーが在ることを確かめる**（無ければ exit 1）／token 不在の `--post` は exit 1／
  GitHub 面の取得失敗は部分報告のまま exit 1／落ちたら `ci-failure-issue.yml` が別スレッドへ起票する
- **PR は作らない**ので #1237 の PAT 前提は要らない（GITHUB_TOKEN で issue の作成・更新は足りる）。Node 依存の導入も不要（標準モジュールのみ）
- 3 箇所の記述は**配備で真になった**ので本文は変えない。`CLAUDE.md` にだけ実現手段（ワークフロー名）を併記した
  （凍結記録 IADR-0364 / IADR-0235 への追記は、決定が変わっていないので置かない）
- 🔴 **初回 run（受け入れ基準 3）は本 PR のマージ後、`workflow_dispatch` で実走して確かめる**（PR からは起動しない設計）。
  手元では token 無しで報告の産出（指摘 75 件 = Proposed IADR 31 ＋ 古い仕様書 26 ＋ …）を実測した

### 決定 4（#1348）: **突合検査器を移植する（`check-workflow-job-refs.js`）。AST の実装は読めないので同じ目的で書いた**

- AST の `check-workflow-job-refs.js` は submodule 未 populate で読めない（参照実装として引用のみ）。**目的（文書とワークフロー実物の突合）だけを写し、
  本リポジトリの表の形に合わせて書いた**
- 3 面: ①必須チェック表（各行のジョブが出所の `.yml` の `jobs:` に在る・取り消し線の行は数えない・「下表の N 件」が行数と一致）
  ②`scripts/README.md` §検査（CI） のジョブ表 ③「`x` ジョブ」の言い回し（`docs/**` / `CLAUDE.md` / `AGENTS.md` / `AI_SETUP.md` /
  `.claude/rules/*.md`。過去形「もう存在しない」の行だけ除外）
- **[[IADR-0141]] の「同型の事故 2 回」を満たす**: 1 回目は `scripts/README.md:147` 自身が記録していた追随漏れ、2 回目が本 issue（3 文書 + 件数）
- 見ないもの: `paths:` / `types:`（#705 の回帰試験の役目）、`name:` 表示名（ブランチ保護が引くのはジョブ ID）
- `ci.yml` `static-checks` へ step を足した。**必須チェックの集合は変わらない**（既存ジョブへの step 追加）

## 変異試験（自己試験に対で入れた。いずれも実走）

| 検査器 | 変異 | 検知 |
| --- | --- | --- |
| coverage-floor | 期待より多い（38 vs 19） | fail |
| coverage-floor | 期待より少ない（18 vs 19） | warn（fail-open） |
| coverage-floor | 導出不能（null） | skip を明示 |
| job-refs | 「下表の 7 件」を残す | 落ちる（実データで実測。是正前 12 件の乖離を全部拾った） |
| job-refs | 必須チェック表のジョブ名を改名 | 落ちる |
| job-refs | 出所 `.yml` を無いファイルに | 落ちる |
| job-refs | 取り消し線の行（CodeQL） | 落とさない（陰性） |
| job-refs | 「もう存在しない」を外す | 落ちる（陽性対照） |
| job-refs | ワークフロー 0 件・表 0 行 | 緑にしない |
| backlog-audit | 指摘 0 件 | 7 節すべてが「指摘なし」を出す |
| backlog-audit | GitHub 面の取得失敗 | 報告に「取得できなかった」＋ exit 1 |

🔴 **検査器の初稿は本文中の別の表（Copilot 有効化の表・`action-versions` の挙動表）を必須チェック表・ジョブ表として拾った。**
見出し行（`| 必須にする check 名`・`| ジョブ |`）から空行までを表とみなす形へ直し、他の表を拾わないことを試験に入れた。

## 受け入れ基準

- [x] #1345: integration の先行テストが失敗しても床が評価される（step の `if:`）。政策は `docs/ai-workflow.md` に条件付きで明記し、暫定手段を並べた
- [x] #1346: 床値の写しを消した。期待件数は導出（19 件を実測）。二重実行は件数の突合で fail
- [x] #1347: `backlog-audit.yml` を配備。無産出を success にしない設計。初回 run はマージ後の dispatch で確認する（PR から起動しない）
- [x] #1348: 「7 件」→「8 件」、`doc-links` → `static-checks`（9 箇所）。突合検査器を移植し CI へ配線した

## 変えていないもの

- 床の値（90 / 75）と PR 側の `--report-only`。**門は 1 つのまま**（[[IADR-0232]] 改定 3）
- 必須チェックの集合（8 件）。`backlog-audit` は必須にしない
- `ci-failure-issue.yml`・`check-coverage-floor.js` の集計ロジック（件数の突合を足しただけ）

## 🔴 実装 ADR を置いていない理由

決定 1〜4 はいずれも既存の決定（[[IADR-0232]] 改定 3 の「門は 1 つ」、[[IADR-0118]] の床の運用）を変えず、実現手段を足しただけである。
採番の直列化（未マージ PR が `IADR-0418` 以降を予約している可能性）もあり、決定は本仕様書と各 issue に置いた。
