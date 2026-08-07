---
title: 作業仕様書 — 他リポジトリ issue 表記を短縮形へ揃え、機械検査へ載せる
type: spec
status: done
related_ids: [NFR, IADR-0115, IADR-0140]
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - "../adr/IADR-0140_cross-repo-issue-ref-checker.md"
  - "../specs/20260804_issue-478_staged-policy-citation-fix.md"
  - "../specs/20260806_issue-560_planning-pin-follow.md"
author: Claude（実装）
created: 2026-08-07
updated: 2026-08-07
---

# 作業仕様書 — 他リポジトリ issue 表記を短縮形へ揃え、機械検査へ載せる（#507）

## 起点

- issue [#507](https://github.com/endazon/microservices-platform/issues/507)（親: [#454](https://github.com/endazon/microservices-platform/issues/454)）
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
  「クロスリポジトリの issue / PR 番号の修飾」「issue / PR 番号の修飾」

## 対象とする 2 つの型

| 型 | 例 | 実害 |
| --- | --- | --- |
| **型 1: 長い表記**（短縮形でもフルパス形式でもない第 3 の表記） | `project-planning#50` / `ai-stock-trading#122` | **無し**（リポジトリ名として解決されず誤リンクしない）。規約が禁じる表記ゆれ |
| **型 2: 列挙形の 2 番目以降が裸** | `planning#206 / #207` の `#207` | **有り**。本リポジトリの実在 issue へ静かに誤リンクする |

型 2 は #507 のコメント（2026-08-06）で追記された。PR #561 が**規約の書いてある当のファイルの中で**
犯し、`check-commit-messages.js` は書式しか見ないため green で通過した。**止める機械は着手時点で
ひとつも無い。**

## 母集合（是正前・実測）

**誤りの側から引く式**を組んだ。正しい表記（`planning#` / `AST#`）を検索語にすると、誤った表記
（`project-planning#` / 裸の `#NNN`）を構造的に取りこぼす。

```console
# 型 1: 長いリポジトリ名 + #数字。フルパス形式（endazon/... の直後）は規約が許すので除く
$ git grep -nE '(^|[^\w/-])(project-planning|ai-stock-trading)#[0-9]+' -- . ':!planning' ':!src/ai-stock-trading'

# 型 2: 修飾付き参照の**直後**に続く裸の #数字（区切りは / ／ , ， 、 ・ ･）。
# git grep では「直後」の条件を書ききれないため、走査は検査器の findViolations で数えた。
$ node -e 'const {findViolations}=require("./scripts/check-cross-repo-refs.js");
           const {execSync}=require("child_process"), fs=require("fs");
           const files=execSync("git ls-files -- . :!planning :!src/ai-stock-trading",
             {encoding:"utf8",maxBuffer:1e8}).trim().split("\n");
           let plain=0,code=0;
           for(const f of files){ let t; try{t=fs.readFileSync(f,"utf8")}catch(e){continue}
             if(t.includes("\0"))continue;
             plain+=findViolations(t,{markdown:true}).length;      // 表示テキスト
             code +=findViolations(t,{markdown:false}).length;}    // 全文
           console.log("plain:",plain,"all:",code,"in-code:",code-plain);'
plain: 70 all: 88 in-code: 18
```

**全ファイル（`*.md` 以外も含む）を走査したうえで、ヒットは実測で 100% が `.md` であった**（着手時点）。

なお**是正後に同じ式を回すと `plain: 2` が残る**。内訳は `scripts/scripts.repo.test.js` に置いた
**検出力確認用のフィクスチャ文字列**（`project-planning#50` と `planning#206 / #207` を 1 行に含む
`.md` を生成して exit 1 になることを確かめるテスト）である。**検査対象を `.md` 以外へ広げると、
検査器自身のテストが検査器に止められる**——これが決定 3 で範囲を `*.md` に切った理由の実測である。

| 型 | 全 occurrence | うち表示テキスト（コードスパン外） | うちコードスパン / フェンス内 |
| --- | --- | --- | --- |
| 型 1（長い表記） | **55** | 54 | 1 |
| 型 2（列挙裸） | **33** | 16 | 17 |
| 合計 | **88** | 70 | 18 |

内訳（型 1・55 件）: `project-planning#` = 14 件（7 ファイル）／`ai-stock-trading#` = 41 件（14 ファイル）。

**#507 本文は「10 箇所前後」と見積もっていたが、実測は 88 件**であった。差の主因は 2 つ。

1. #507 本文の検索式 `grep -rn "project-planning#"` は `project-planning` しか見ておらず、
   **同型の `ai-stock-trading#NNN` を丸ごと取りこぼしていた**。規約は `AST#NNN`（短縮形）を定めており、
   `ai-stock-trading#122` は `project-planning#50` と**まったく同じ第 3 の表記**である。
   母集合の取り方そのものを間違えると効かない（#541 の教訓）ため、**「リポジトリ名の裸書き」という
   誤りの側から**式を引き直した。
2. 型 2（#507 コメントで追記）は元の検索式にそもそも掛からない。

型 2 が全ファイルのどこに出たか（表示テキストの 16 件）:

| ファイル | 件数 |
| --- | --- |
| `docs/adr/README.md` | 1（`AST#217/#208`） |
| `docs/specs/20260730_issue-420-421_report-and-trade-model-routing.md` | 1 |
| `docs/specs/20260801_impl-handoff-kit-sync.md` | 4 |
| `docs/specs/20260802_impl-handoff-kit-sync.md` | 1（裸 11 個） |
| `docs/specs/20260803_issue-460_ai-review-permission-denials.md` | 1（裸 7 個） |
| `docs/specs/20260804_issue-478_staged-policy-citation-fix.md` | 3 |
| `feedback/20260802_review-allowlist-diff-and-denial-labeling.md` | 2 |
| `feedback/20260803_ai-workflow-grep-sort-and-submodule-git-c.md` | 2 |
| `feedback/20260805_sc09-11-admin-ops-contract-gaps.md` | 1 |

## 決定

### 決定 1: 短縮形へ揃える。明示リンクは**リンクテキストだけ**短縮形にし URL は保つ

#507 は「Markdown リンク（`[project-planning#197](https://github.com/…)`）は判断を分ける」としていた。
**分けない**——リンクテキストも短縮形 `planning#197` へ揃え、URL はそのまま残す。

- 規約が許すフルパス形式は `<owner>/<repo>#NNN`（例 `endazon/project-planning#197`）であって
  `project-planning#197` ではない。**現状の表記はどちらでもない第 3 の表記**であり、
  「フルパス形式として残す」という選択肢はそもそも成立しない。
- 明示リンクは URL 側が自動リンクを担っているので、テキストを短縮形にしても機能は 1 ミリも落ちない。
- 本リポジトリのフルパス形式（`endazon/...#NNN`）の使用実績は**ゼロ**である（実測）。混在を許す
  例外を新設する理由が無い。

この判断は `.claude/rules/traceability.md` に 1 行残す（#507 の受け入れ基準）。

### 決定 2: 検査は「表示テキスト（コードスパン・コードフェンスの外）」だけを見る

型 2 の実害は **GitHub の自動リンク**であり、自動リンクは**インラインコード（`` ` ``）と
コードフェンスの中では効かない**。したがって:

- 反例（`.claude/rules/traceability.md` の `` 誤: `planning#146 / #149 / #160` ``）
- 是正作業を記録した仕様書が引用する「誤った文字列そのもの」（`docs/specs/20260804_issue-478_*` の 12 件、
  `docs/specs/20260806_issue-560_*` の 2 件）

はいずれもバッククォートの中にあり、**害が無く、かつ書けなければ規約も是正記録も書けない**。
コードスパン除外は「例外リストの運用」ではなく**害の定義そのもの**なので、腐らない。

型 1（表記ゆれ）にも同じ文脈規則を適用する。理由は、grep 式や履歴引用をコードスパンで書く必要が
同様に存在し、規則を型ごとに変えると説明も実装も割れるため。**ただし是正は検査より広く行い**、
コードスパン内の型 1（`deploy/local/README.md` の 1 件）も同じファイル内で表記が割れないよう直す。

### 決定 3: 検査対象ファイルは git 管理下の `*.md`（submodule 配下を除く）

| 範囲 | 扱い | 理由 |
| --- | --- | --- |
| 追跡下の `*.md`（`docs/` / `.claude/` / `feedback/` / ルート / `deploy/` ほか） | **検査する** | 自動リンクが効く（＝実害が出る）のは GitHub がレンダリングする Markdown。母集合 88 件は**実測で 100% が `.md`** |
| コミット件名・本文・PR タイトル | **検査する**（`check-commit-messages.js` 経由） | #561 は本文と PR タイトルの両方で犯した。ここは `.md` の走査では届かない |
| `planning/` / `src/ai-stock-trading/` | **範囲外** | 別リポジトリの成果物。submodule pin を動かさない原則に従う |
| `*.md` 以外（`.js` / `.json` / `.cs` / `.yml` 等） | **範囲外** | (a) 自動リンクが効かず型 2 の実害が無い。(b) 実測で違反 0 件。(c) **検査器とその自己試験・repo テストは検出対象文字列をソース中に必ず持つ**ため、広げると自己参照の偽陽性を除外特例で潰す運用が要る（除外の腐りが新たな穴になる） |
| `CHANGELOG.md` | 走査対象だが実測 0 件 | 生成物だが `.md` であり除外していない。将来コミット件名由来の違反が載る前に、コミット件名の検査が PR 段階で止める |

### 決定 4: 検査は既存の CI 呼び出し口から到達させる（新ワークフローを足さない）

`.github/workflows/` は GitHub App 権限で編集できない。新スクリプトを置いただけでは CI に載らない。
2 つの既存呼び出し口へ相乗りする。

| 呼び出し口（既存・実測） | 経路 | 何を検査するか |
| --- | --- | --- |
| `.github/workflows/ci.yml` `scripts-tests` → `node scripts/scripts.test.js`（`REQUIRE_REPO_TESTS=1`）→ companion `scripts/scripts.repo.test.js` | repo テストが `check-cross-repo-refs.js` を `--self-test` と素実行で子プロセス起動 | **リポジトリの `*.md` 全体** ＋ 検査器の自己試験 |
| `.github/workflows/ci.yml` `commit-messages` → `node scripts/check-commit-messages.js` ／ `.github/workflows/pr-title.yml` → 単一件名モード | `check-commit-messages.js` が本モジュールを `require` | **コミット件名・本文・PR タイトル** |

詳細な選定理由と却下案は [IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md)。

## 検査の仕様（`scripts/check-cross-repo-refs.js`）

- `LONG_RE`: `(?<![\w/-])(project-planning|ai-stock-trading)#\d+`
  - 負の後読みで `endazon/project-planning#50`（規約が許すフルパス形式）を**除外**する。
- `ENUM_RE`: 修飾付き参照 `(<owner>/)?(planning|AST|project-planning|ai-stock-trading)#\d+` の**直後**に
  区切り（`/` `／` `,` `，` `、` `・` `･`。前後の空白/タブ可）＋ 裸の `#\d+` が 1 個以上続く形。
  - **「他リポジトリの修飾語の直後に続く列挙」だけ**を裸と判定する。単独の `#454` や
    `#450（FR-17/18）・#451（FR-19/20）` は修飾語が直前に無いので**掛からない**（偽陽性を出さない）。
  - 空白のみの区切り（`planning#206 #207`）は**採らない**。スカッシュ既定件名の
    ` (#123)` と衝突するため（既知の限界。下記）。
- Markdown モード（`*.md` と `--markdown`）ではコードフェンスとインラインコードを**同じ長さの空白へ潰して**
  から走査する（行番号・桁がずれない）。コミットメッセージは非 Markdown モード（GitHub はコミット
  メッセージのバッククォートをコードスパンとして描画せず、自動リンクは効く）。
- `--self-test`: 正のケース（検出すべき 12 種）と負のケース（検出してはならない 12 種）を固定する。
- 終了コード: 違反 0 件で 0、1 件以上で 1。

### 既知の限界（検出しない形・意図的）

1. 区切りが助詞・空白のみの列挙（`planning#206 と #207`）。区切り文字を空白まで広げると
   ` (#123)`（スカッシュ既定件名）と衝突し、正当な件名を落とす。
2. `*.md` 以外のファイル（決定 3）。
3. HTML コメント `<!-- -->` の中（レンダリングされないが、潰していない。実測 0 件）。

## 受け入れ基準

- [x] 母集合を先に数え、内訳を本仕様書に記録した（88 件）。
- [x] `git grep -nE '(^|[^\w/-])(project-planning|ai-stock-trading)#[0-9]+'` の結果が **0 件**。
- [x] 型 2 の表示テキスト 16 件が 0 件（コードスパン内の 17 件は決定 2 により意図的に残す）。
- [x] 揃え方の判断が `.claude/rules/traceability.md` に残っている。
- [x] 検査器が型 1・型 2 を検出し、既知の 3 つの偽陽性候補で fail しない。
- [x] 検査器が既存の CI 呼び出し口から到達する（決定 4）。
- [x] `--self-test` が正・負の両ケースを固定する。
- [x] 変異試験 M1〜M4 で「壊すと落ちる」ことを実測した（下記）。

## 検証（実測）

| コマンド | exit code（実測） |
| --- | --- |
| `node scripts/check-cross-repo-refs.js --self-test` | **0**（自己試験 36 件 all passed） |
| `node scripts/check-cross-repo-refs.js` | **0**（`*.md` 517 件・違反 0） |
| `node scripts/check-doc-links.js --self-test` | **0** |
| `node scripts/check-doc-links.js` | **0**（446 件） |
| `node scripts/check-commit-messages.js --range origin/develop..HEAD` | **0** |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **0**（269 tests passed） |
| `node scripts/check-test-spec-coverage.js` | **0** |
| `node scripts/check-test-traceability.js` | **0** |
| `node scripts/check-permission-denials.js` | **0** |

### 変異試験（壊すと落ちるか。**すべて実測値**）

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M1 | `docs/adr/README.md` の `（planning#50）` を `（project-planning#50）` へ戻す | fail | **exit 1**。`docs/adr/README.md:138 [長い表記] project-planning#50 → planning#50` を 1 件検出。復元後 **exit 0** |
| M2 | `docs/adr/README.md` の `AST#217/AST#208` を `AST#217/#208` へ戻す | fail | **exit 1**。`[列挙形の修飾漏れ] AST#217/#208 → AST#217/AST#208`。復元後 **exit 0** |
| M2b | 同上の状態で **CI 呼び出し口**（`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`）を回す | fail | **exit 1**（相乗り先から実際に落ちる＝CI に載っていることの実証） |
| M3 | 本リポジトリの正当な参照を足す（`#454` / `#450（FR-17/18）・#451（FR-19/20）` / `PR #561 / #563 / #568` / `planning#206 / planning#207` / `endazon/project-planning#197` / `半角スペース + (#123)`） | pass | **exit 0**（偽陽性ゼロ） |
| M4 | `--self-test` の負のケースを壊す（`LONG_RE` の負の後読みを除去＝フルパス形式まで違反にする） | fail | **exit 1**。負例 3 件が FAIL（`endazon/project-planning#50` / フルパス形式の列挙 / `my-ai-stock-trading#1`） |
| M4b | 同上の状態で CI 呼び出し口を回す | fail | **exit 1**（`自己試験が失敗した` で AssertionError） |
| M5 | コミット**本文**へ `planning#201 / #202 / #203` を書く（一時 git リポで実施） | fail | **exit 1**。`本文の 列挙形の修飾漏れ … → "planning#201 / planning#202 / planning#203"` |
| M5b | 同じコミットを正しい表記で作る | pass | **exit 0** |
| M5c | PR タイトル単一件名モードへ `chore(NFR): planning pin を planning#206 / #207 へ進め` を渡す | fail | **exit 1**（`pr-title.yml` の経路） |

**素通りしたものは無い。** ただし検査器が構造的に見ないもの（助詞・空白区切りの列挙、`*.md` 以外の
ファイル、HTML コメント内）は上記「既知の限界」のとおりで、これらは変異させても落ちない
（＝意図した設計であり、隠していない）。

## 変更したファイル

| 分類 | ファイル |
| --- | --- |
| 検査器（新規・本リポ固有） | `scripts/check-cross-repo-refs.js` |
| CI 結線 | `scripts/scripts.repo.test.js`（`--self-test` ＋ 実データ走査 ＋ 検出力確認）／`scripts/check-commit-messages.js`（件名・本文・PR タイトル） |
| 検査器の索引 | `scripts/README.md`（スクリプト表・実行例・CI ジョブ表） |
| 規約 | `.claude/rules/traceability.md`（決定 1 の 1 行 ＋ 検査器への導線） |
| 実装 ADR | `docs/adr/IADR-0140_cross-repo-issue-ref-checker.md` ＋ `docs/adr/README.md` |
| 是正（型 1） | `deploy/local/README.md` / `docs/adr/IADR-0064` / `IADR-0077` / `IADR-0112` / `IADR-0113` / `IADR-0114` / `docs/adr/README.md` / `docs/functional/FR-11_llm-egress-routing.md` / `docs/specs/` 8 本 |
| 是正（型 2） | `docs/adr/README.md` / `docs/specs/` 6 本 / `feedback/` 3 本 |
| 環流 | `feedback/20260807_kit-cross-repo-issue-ref-check.md` |

`planning/` と `src/ai-stock-trading/`（submodule pin）は変更していない。

## 計画への環流

`scripts/check-commit-messages.js` は IADR-0115 の**分類 B**（キット＋固有デルタ）である。本 PR が
足したのは「本リポにしか存在しないスクリプトの呼び出し」（許容される固有デルタ種 3）だが、
**規約自体はキット配布の `.claude/rules/traceability.md` に書かれている**ため、検査器ごとキットへ
環流する価値がある。[`feedback/20260807_kit-cross-repo-issue-ref-check.md`](../../feedback/20260807_kit-cross-repo-issue-ref-check.md) に記録した。
