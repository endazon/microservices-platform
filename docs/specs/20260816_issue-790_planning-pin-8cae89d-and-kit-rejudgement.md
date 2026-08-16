---
title: 作業仕様書 — 計画 pin を 8cae89d へ進め、キット追随の分類 X 2 件を裁定後の実測で再判定する（#790）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0115
  - IADR-0130
  - IADR-0139
  - IADR-0183
  - IADR-0190
  - IADR-0192
  - IADR-0200
  - IADR-0201
  - IADR-0204
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/tools/impl-handoff-kit/HOWTO.md (§B-5 キット版を採る前に実走して差を確かめる)"
  - "../../planning/docs/ai-implementation-workflow-guide.md (§8 必読規約の予算 51,200 バイト)"
  - "../../planning/tools/impl-handoff-kit/repo-template/scripts/kit-sync-classification.example.json"
  - "../../planning/projects/microservices-platform/INDEX.md (決定 47・48)"
related_specs:
  - "../adr/IADR-0204_kit-catchup-deferral-with-expiry-ratchet.md"
  - "../adr/IADR-0201_class-c-rejudgement-and-fail-closed-kit-checks.md"
  - "../how-to/plan-id-range-history-annex.md"
  - "20260816_issue-755_planning-pin-4d6a7d6-catchup.md"
  - "20260815_issue-756_kit-superiority-three-checkers.md"
---

# 作業仕様書: 計画 pin `8cae89d` の追随と、分類 X 2 件の再判定（#790）

## 1. 起点となる ID（トレーサビリティ）

- 起点 ID: **NFR**（計画の追随・キット同期・検査器＝メタ作業）。
  **無採番の根拠は `.claude/rules/traceability.md`「無採番 `NFR` を許す 2 つの場合」の場合 2**
  ＝「**ID 列はあるが、その作業に当たる番号が無い場合**」である。計画側の `NFR-01`〜`NFR-27` は
  27 件とも**稼働する製品の要件**（性能 / 可用性 / スケーラビリティ / セキュリティ / 運用・保守 / 拡張性）で、
  **文書・規約・キット同期の統制を扱う番号は 1 件も無い**（別紙
  [`plan-id-range-history-annex.md`](../how-to/plan-id-range-history-annex.md) §4 で実測済み）。
  **場合 2 は環流しない**（計画側に不足があるわけではない。[[IADR-0179]] 決定 2）。
  着手前に計画側の ID 列を実際に見て判断した（§2.2 の走査がその実測でもある）。
- 起点 issue: **#790**（pin 更新 A ＋ キット追随の再判定 F の束ね。束ねの判定は §7）
- pin: `4d6a7d6` → **`8cae89d`**（5 コミット）
- 関連 ADR: 実装ADR **[[IADR-0204]]**（本作業で新設）／[[IADR-0201]]（部分改定）／
  [[IADR-0192]]（分類表と X の追跡義務）／[[IADR-0115]] 決定 2・3／[[IADR-0130]]（0 件走査の門）／
  [[IADR-0183]]（worktree 警告）／[[IADR-0200]]（必読予算の母集合）。計画 ADR: 該当なし
- 分類（[[IADR-0141]] 決定 4 ＝ 監査強度の分岐）: **機械検査を改修する**（既存検査器 2 本の差し替え・
  回帰テストの改訂・ラチェット新設）→ 全面 1 巡 ＋ 是正差分 1 巡

## 2. 母集合（**自分で引き直した結果**。issue 本文の値は転記していない）

**時点: 2026-08-16、worktree `/home/user/wt-pin`、ブランチ `chore/nfr-planning-pin-8cae89d`、base `origin/develop` = `d7d6cd8`。**

### 2.1 pin の差分

```console
$ git -C planning log --oneline 4d6a7d6..8cae89d
8cae89d feat(NFR): キットの 2 検査器へ実装側の検出力を環流する（planning#373 planning#374）
f279c69 docs: 取り込み経路の owner / department を 2 段の解決順で確定する（planning#371 planning#372）
42b43a0 docs: 環流記録の凍結の射程と、PR 束ねの射程を裁定する（planning#369 planning#370）
5e53b9d fix: シェル経由の子プロセス起動を 5 箇所とも直す（planning#367）
8a04929 docs(cross-project): 第 3 回還流棚卸しを記録し、ガイド §11 の pin 鮮度監視の記述を実態へ訂正する

$ git -C planning diff --stat 4d6a7d6 8cae89d -- projects/microservices-platform
 .../05_screens/01_screens.md                 |  3 ++-
 .../06_technical/09_datasource-connectors.md | 28 +++++++++++++++++----
 projects/microservices-platform/INDEX.md     |  6 +++--
 3 files changed, 30 insertions(+), 7 deletions(-)
```

**計画書本体が動く初めての pin 更新である**（従前の #753 / #755 系はいずれも計画本文の外だった）。
ただし動いたのは SC-06 の既定属性と 2 段解決順の記述であり、**ID の新設・廃止は含まない**（§2.2）。

### 2.2 計画 ID レンジの引き直し（**5 種**。companion `traceability.repo.md` の義務）

**新旧両 pin で同じコマンドを走らせ、出力を並べて突き合わせた。**
`git -C planning show` 系（`grep <rev>` / `ls-tree <rev>`）を使い、**submodule の作業ツリーは一切変更していない**。

```bash
for SHA in 4d6a7d6 8cae89d; do
  for KIND in FR NFR UC SC; do
    git -C planning grep -h -o -E "\b$KIND-[0-9]{2}\b" $SHA -- projects/microservices-platform \
      | sed "s/.*$KIND-//" | sort -u
  done
  git -C planning ls-tree -r --name-only $SHA -- projects/microservices-platform/07_adr/ \
    | grep -oE 'ADR-[0-9]{4}' | sort -u
done
```

生の出力（そのまま貼る。加工していない —— 規則 7）:

```
===== pin 4d6a7d6 =====                        ===== pin 8cae89d =====
FR:  01 … 22                                   FR:  01 … 22
NFR: 01 … 27                                   NFR: 01 … 27
UC:  01 … 11                                   UC:  01 … 11
SC:  01 … 21                                   SC:  01 … 21
ADR files: 46   ADR range: 0001 0046           ADR files: 46   ADR range: 0001 0046
ADR 欠番: なし（1..46 連番）                    ADR 欠番: なし（1..46 連番）
```

| 種別 | 旧（走査基準 `4d6a7d6`） | 新（走査基準 `8cae89d`） | 差 |
| --- | --- | --- | --- |
| `FR` | `01..22`（22 件） | `01..22`（22 件） | **不変** |
| `NFR` | `01..27`（27 件） | `01..27`（27 件） | **不変** |
| `UC` | `01..11`（11 件） | `01..11`（11 件） | **不変** |
| `SC` | `01..21`（21 件） | `01..21`（21 件） | **不変** |
| `ADR` | `0001..0046`（46 件・欠番なし） | `0001..0046`（46 件・欠番なし） | **不変** |

- **「引き直して不変だった」ことを実測として記録している**（引き直さずに「不変」とは書いていない）。
- **`ADR` の欠番は `seq 1 46` との `diff` で機械的に確かめた**。「46 件あるから連番」は連番の証明にならない。
- **この世代から `NFR` を引き直しの対象へ加えた**（従前は 4 種）。`NFR-01..27` は companion の規範であり、
  他の 4 種と同じく計画側が動けば腐る。別紙にもその旨を記録した。
- 記録先: [`docs/how-to/plan-id-range-history-annex.md`](../how-to/plan-id-range-history-annex.md)。
  走査基準行は companion `.claude/rules/traceability.repo.md` を `4d6a7d6` → `8cae89d` へ（**sha 置換のみ・±0B**）。

### 2.3 キット追随の母集合

`node scripts/check-kit-sync.js` を pin 前進後に実行（キット 115 件を分類表と全数突合）。

| 時点 | 結果 |
| --- | --- |
| pin 前進直後 | **exit 1**・drift 2 件（`.claude/rules/traceability.md` / `scripts/scripts.test.js`） |
| 本 PR 完了時 | **exit 0**（A 76 / B 27 / C 4 / 対象外 8 ＝ 115） |

**issue 本文が挙げた drift 2 件は、私自身の実行でも同じ 2 件だった**（転記ではなく再現）。
差分量の実測: `traceability.md` は `diff -u` 52 行・**21,590B → 24,592B（+3,002B）**、
`scripts.test.js` は `diff -u` 175 行・**95,943B → 101,167B（+5,224B）**。

### 2.4 分類 X 再判定の母集合

issue が名指しした 2 本だけを見て終えていない。**分類表の B 全 25 件から X を名乗るものを引き直した**:

```bash
python3 -c "import json;d=json.load(open('scripts/kit-sync-classification.json'));
[print(k) for k,v in d['classes']['B'].items() if v.startswith('X.')]"
```

X を名乗るのは **`check-commit-messages.js` / `check-cross-repo-refs.js` / `check-doc-links.js` /
`check-plan-id-qualification.js` / `check-planning-pin-freshness.js` の 5 本**（`scripts.test.js` は
#757 で A へ戻っていた）。うち本 issue の対象は**裁定 planning#373 / planning#374 が動かした 2 本**で、
残る 3 本は別 issue の追跡下にある（除外理由は §2.6）。

### 2.5 必読規約の母集合（[[IADR-0200]] 決定 1・`check-reading-budget.js` が正）

| 集合 | pin 前進前 | 本 PR 完了時 | 判定 |
| --- | --- | --- | --- |
| **Claude Code**（`CLAUDE.md` ＋ `.claude/rules/*.md`） | **50,193B（98.0%）** | **50,182B（98.0%）** | warn・exit 0 |
| AGENTS.md 系 | 4,710B（9.2%） | 4,710B | ok（観測のみ） |
| Copilot | 2,850B（5.6%） | 2,850B | ok（観測のみ） |

**−11B**（走査基準の sha 置換は ±0B。companion 冒頭の「（キット配布物・分類 A）」→「（キット配布物）」で −11B）。
**もしキット版 `traceability.md` を取り込んでいれば 53,195B（103.9%）で fail** した（§4.1）。

### 2.6 除外したものと理由（**黙って落とさない**）

| 除外 | 理由 |
| --- | --- |
| `check-doc-links.js`（X） | 環流済みで追跡先（#736 / planning#337）が在る。裁定 planning#373 / planning#374 の射程外 |
| `check-plan-id-qualification.js`（X） | #756 で既にキット版へ差し替え済み（B 第 5 種 ＋ 種 3）。今回の裁定は触っていない |
| `check-planning-pin-freshness.js`（X） | #749 / [[IADR-0202]] が別途判定済み。ファイル領域が交差するため直列化する |
| `docs/how-to/session-handoff.md` | **並行 PR #789 が編集中**（交差）。「`scripts.test.js` は変更禁止（分類 A）」の記述が古くなるが、本 PR では触らない（[[IADR-0204]] 影響欄に明記し #793 へ送った） |
| `docs/specs/20260815_issue-454_*.md` | 同上（PR #789 と交差） |
| `CLAUDE.md` | 別 issue の領域（減量は #793）。本 PR では 1 バイトも触っていない |
| `planning/` の中身・`src/ai-stock-trading` | gitlink を進めるだけ・pin 据え置き |
| 過去 IADR（0115 / 0130 / 0145 / 0187）の「`scripts.test.js` は分類 A」記述 | **当時の記録**である。後付けの書き換えは記録の改竄にあたる（`traceability.repo.md` §Superseded の作法）。live な最新版 [[IADR-0201]] にだけ日付つき追記を入れた |
| `feedback/20260801_impl-handoff-kit-gaps.md` | 確定済みの環流記録（本文は書き換えない。[[IADR-0191]]） |

### 2.7 規則 10（この変更で**新たに誤りになる自分の記述**）の引き直し

**是正前の語では捕まらない**ため、3 軸で引いた（`--exclude-dir=planning`。`docs/specs/` と `CHANGELOG.md` は除く）。

| 軸 | 検索 | ヒット | 直したもの |
| --- | --- | --- | --- |
| 1 | `traceability.md` ＋（`分類 A`\|`バイト一致`） | 8 件 | `scripts/check-test-traceability.js:79`（「分類 A にしたため」→「キット配布物であり」）／`.claude/rules/traceability.repo.md:3`／`docs/adr/README.md`（IADR-0204 行を追加）／`IADR-0201`（日付つき追記 2 箇所）。**残り 4 件は当時の記録**（§2.6） |
| 2 | `scripts.test.js` ＋（`分類 A`\|`バイト一致`） | 12 件 | `scripts/kit-sync-classification.json`（`$comment` の「キット版 scripts.test.js（分類 A）」から分類名を外した）。**`session-handoff.md` は交差のため据え置き**（§2.6） |
| 3 | `本リポ版が優る` | 2 ファイル | `scripts/kit-sync-classification.json`（2 件の理由欄を新しい実測で書き直した）／`scripts/scripts.repo.test.js`（#757 のコメント。**#756 の判定を引く箇所は当時の記録として残し、試験そのものは新実測へ追随**） |
| 導出値 | pin sha `4d6a7d6` | 走査ではなく**計算し直した** | `kit-sync-classification.json` `$comment` の「（pin 4d6a7d6）」→ `8cae89d`／別紙の「現行 pin」 |

## 3. 変更内容（issue #790 の受け入れ基準との対応）

| # | 受け入れ基準 | 変更 | 充足 |
| --- | --- | --- | --- |
| 1 | pin が `8cae89d` に進んでいる | gitlink のみ前進（独立コミット） | ✅ |
| 2 | ID レンジ 5 種を**自分で引き直し**、新旧を並べて記録 | §2.2。別紙へ追記 | ✅ |
| 3 | `traceability.repo.md` の走査基準が新 sha を指す | sha 置換のみ（±0B） | ✅ |
| 4 | `check-kit-sync.js` が **exit 0** | A 76 / B 27 / C 4 / 対象外 8 | ✅ |
| 5 | 分類 X 2 件の再判定を**実走突合の証跡つき**で | §4.2 / §4.3。結論は「1 本は B（種 5）へ／1 本は X 継続で理由を書き直し」 | ✅ |
| 6 | `scripts.test.js` と `REQUIRE_REPO_TESTS=1` 版が緑 | 619 tests passed（両方） | ✅ |
| 7 | 必読規約の総量が**増えていない** | 50,193B → **50,182B**（−11B） | ✅ |

**基準 4 と 7 は、`traceability.md` をキット原文で上書きすると同時に満たせない**（§4.1）。
issue 本文の「キット原文で上書きするのが分類 A の定義である」は、+3,002B が予算に入る前提で書かれていた。
**実測で入らないことが判ったので、[[IADR-0192]] 決定 2 が定める「期限つきの暫定（X）」を採った。**

## 4. 実測と判定

### 4.1 分類 A の drift 2 件

#### (a) `.claude/rules/traceability.md` → **分類 A → B（X・期限つきの暫定）**

キット原文で上書きしてバイト一致（`cmp` 一致）まで確認したうえで、**予算試験が落ちることを実測して差し戻した**。

```
node scripts/check-kit-sync.js                   → exit 0（A 78 / B 25 / C 4 / 対象外 8）
node scripts/scripts.test.js                     → exit 1
  AssertionError: 必読合計が予算を超えた（53195B / 上限 51200B・超過 1995B）。
  内訳: CLAUDE.md=23198 / .claude/rules/traceability.md=24592 / .claude/rules/traceability.repo.md=5405
```

**1,995B を空ける手段は `CLAUDE.md`（別 issue の領域）か companion の減量しかない**（キット配布物は削れない
—— 削るとバイト一致が崩れ、同期のたびに手動マージが要る）。companion 5,405B から 1,995B（37%）を抜くのは
**規約の減量作業そのもの**であり、`scripts.repo.test.js` が companion の文言リテラルを 10 箇所以上で
固定しているため、pin 追随の PR で巻き込むと減量の判断が「予算を空けるため」に歪む。

**キット版の新規範 3 点は companion が本リポ固有の形で既に持つ**ため、**規範の空白は生じない**。

| キット版が新たに書いた規範 | companion の対応する記述 |
| --- | --- |
| `FR`/`UC`/`SC` の実在性を拡張点 `readPlanIds()` で見る | 「**FR / UC / SC の実在性**（#579）: スコープの ID が上のレンジに実在することを検査する（パーサは `check-test-traceability.js` と共用）」 |
| 修飾を件名・本文・PR タイトルの 3 面で見る | 「**issue / PR 番号は短縮形に寄せる**…**列挙形でも各番号を修飾する**」＋ 別紙 `commit-message-rules-annex.md` |
| 型 4（owner 誤り）・`〔〕` 区切り・`.md` 外走査 | 「**フルパス形式の owner は `endazon` ただ 1 つ**（#590）」「**`〔〕` で添える**（#586）」「対象は追跡下の全ファイル」 |

**保留の期限は機械が持つ**（[[IADR-0204]] 決定 1）: `scripts.repo.test.js` に
「キット版を取り込むと予算を超える」ことを assert するラチェットを置いた。**#793 の減量が着地して
超えなくなった瞬間に落ちて追随を促す。** 追跡: **#793**。

#### (b) `scripts/scripts.test.js` → **分類 A → B（X）・固有デルタ 1 か所**

キット原文で上書き（`cmp` 一致）したうえで実行し、**キット版の新試験が本リポでは原理的に通らない**ことを実測した。

```
$ node scripts/scripts.test.js
  ok  計画レンジに無い FR / UC / SC を違反として上げる
  ok  ゼロ埋めの桁数が違っても同じ ID として突き合わせる
AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:
+ actual  Set(54) { 'FR-01', … 'SC-21' }
- expected  null
```

**切り分け（本リポ固有の期待か / キット側の新しい要求か）**: **どちらでもない第 3 の型**である。

- キット版 `loadExistingPlanIds()` は `require('./check-test-traceability.js')` を探し、
  `readPlanIds` が関数なら `new Set(...)` を返す。**本リポ版と実装は完全に同一**である
  （キット版へ差し替えても同じく Set が返る＝**検査器の差ではない**）。
- キットは `check-test-traceability.js` を配っていない。ゆえに**キット既定では null**、
  **拡張点を埋めた配布先では Set** になる。試験はキット既定だけを断定している。
- すなわち**キットが「自分の配った拡張点を実際に埋めたリポジトリでだけ落ちる試験」を配っている**。

**固有デルタは当該 1 か所のみ**（拡張点の有無で期待値を選ぶ形へ変更。**early return による skip にはしない**
—— 実効している側が一度も試験されなくなるため。[[IADR-0204]] 決定 4）。追跡: **planning#380**（起票済み）。

**なお `check-kit-sync` が throw すると後続テストが 1 件も走らない。** 本作業では drift を解消してから
`scripts.test.js` を回し、さらに**落ちた assert を一時的に無効化して後続を全部走らせる**探索を行った
（前セッションでこの穴が違反 1 件を隠していた）。その結果**予算超過（4.1(a)）が 2 件目の失敗として現れた**。

### 4.2 分類 X 再判定: `scripts/check-commit-messages.js` → **X → B（種 5）**

HOWTO §B-5 の手順（同一入力・同一フラグで実走して差を確かめる）。実 diff 293 行（repo 620 行 / kit 616 行）。

```
PLAN_PROJECT=microservices-platform  node <各版> --title "<件名>"
                                         repo版        kit版
  feat(SC-99): 誤った計画 ID             exit=1 1行    exit=1 1行
  feat(SC-21): 正しい計画 ID             exit=0 0行    exit=0 0行
  feat(ADR-0099): 実在しない計画 ADR     exit=1 1行    exit=1 1行
  feat(IADR-0192): 実在する IADR         exit=0 0行    exit=0 0行
  docs(NFR): 関連 planning#206 / #207    exit=1 1行    exit=1 1行
  feat(FR-012): ゼロ埋め違い             exit=0 0行    exit=0 0行
```

**6 件名すべてで exit と検出行数が完全一致した。** #756 が「本リポ版が優る」根拠にした 3 点は、
planning#373 の受理でキット版が備えている。

| 機能 | キット版 | 本リポ版 |
| --- | --- | --- |
| `FR`/`UC`/`SC` の実在性（#579） | **あり**（拡張点 `readPlanIds()` 経由。本リポは拡張点を持つので実効） | あり |
| 件名・本文・PR タイトル 3 面のクロスリポ参照（#507） | **あり** | あり |
| コミット本文（`%b`）の収集 | **あり** | あり |
| `BOT_AUTHORS` の中身 | 7 エントリ・**完全一致** | 同一 |
| `BOT_AUTHORS` の **export** | 無し | あり（**利用側は 0 件**。`scripts.repo.test.js` / `pr-title.yml` はコメントで言及するだけ） |
| 置換点 `PLAN_PROJECT` | `<project-name>`（未設定） | `microservices-platform` |

**本リポ版にしか無い機能は 0**（export 1 件は利用者がいない）。**worktree 警告も本リポ版は持っていない**（実測）。
→ **キット版へ差し替え、置換点だけを埋めて B（種 5）。X から外れた。**

### 4.3 分類 X 再判定: `scripts/check-cross-repo-refs.js` → **X 継続（理由を新しい実測で書き直し）**

実 diff 971 行（repo 787 行 / kit 862 行）。**置換点はすべて環境変数で注入して同条件にした。**

**(1) 検出力 —— 同値。**

```
fixture（型1 長い表記 / 型2 スラッシュ列挙 / 型2 〔〕列挙 / 型3 空白区切り / 型4 owner 誤り / 非 md の空白区切り）
  repo 版: 違反 6 件 exit 1        kit 版: 違反 6 件 exit 1     ← メッセージ・提案文字列まで一致
owner 誤りの提案（acme/project-planning#5 → planning#5、acme/ai-stock-trading#6 → AST#6）
  repo 版・kit 版とも同一。src/ai-stock-trading#7 の偽陽性抑止・自リポのフルパス形式の受理も同じ
実データ（追跡下の全ファイル）
  repo 版: 走査 1626 件 / 除外 71 件 / 違反 0 件 exit 0
  kit 版 : 走査 1626 件 / 除外 71 件 / 違反 0 件 exit 0
自己試験: repo 85 件 / kit 86 件（kit は NUL 読み飛ばしの試験を持つ）
```

**#756 が「本リポ版が優る」根拠にした 6 件 / 4 件の差は消えた**（planning#374 の受理で型 4・`〔〕`・
`.md` 外走査・NUL 読み飛ばしがキット版に入った）。

**(2) しかしキット版へ「戻す」ことはできない。fail の向きが違う。**

```
$ node <kit版> …   # 走査対象 0 件の空リポジトリ
[check-cross-repo-refs] 走査 0 件 / 除外 0 件（scripts/ の非 Markdown）
[check-cross-repo-refs] OK: 0 件に他リポジトリ参照の表記違反はありません。   → exit 0  ← 門が無い
```

**キット版は 0 件走査の門（#664 / [[IADR-0130]]）を持たない。** これは**違反入力に対する出力の突合では
絶対に現れない差**であり、`scripts.repo.test.js` の変異試験（4 本の検査器へ空リポジトリを食わせる）が検出した。

| 機能 | キット版 | 本リポ版 |
| --- | --- | --- |
| 型 1〜4 ・`〔〕`・`.md` 外走査・NUL 読み飛ばし | あり | あり |
| `createChecker` の設定妥当性検査・置換点の環境変数注入・型 4 未検査の notice | **あり** | あり（#757 で載せた） |
| **0 件走査の門（fail-closed）** | **無し** | **あり** |
| **worktree 状態の警告（[[IADR-0183]]）** | **無し**（planning#374 の裁定文が明記） | **あり** |
| `EXCLUDED_DIRS` の export | 無し | あり |
| 未使用の `trackedMarkdown` の export | あり（主経路は使っていない） | 無し |

**判定**: [[IADR-0204]] 決定 3 のとおり **キット版を土台に採り、失う 2 点を目印つきで再付与**した。
検出力が同値である以上、キットの構造（設定妥当性検査・環境変数注入）を取り込むほうが次の追随が軽い。
**0 件走査の門は環流待ちなので X 継続。** 追跡: **planning#379**（起票済み）。

### 4.4 機能の欠落（issue が名指しした worktree 警告）

**あり。** キット版 `check-cross-repo-refs.js` は `scripts/lib/worktree-state.js` への結線を持ち込んでいない
（planning#374 の裁定文どおり）。差し替え後に `main()` 冒頭へ 4 行で再付与した（固有デルタ 種 3）。
**`check-commit-messages.js` については、本リポ版も worktree 警告を持っていなかった**ので欠落は無い。

## 5. 実施した変更（ファイル単位）

| ファイル | 変更 |
| --- | --- |
| `planning`（gitlink） | `4d6a7d6` → `8cae89d`（**独立コミット**。submodule の中身は不変） |
| `.claude/rules/traceability.repo.md` | 走査基準 sha 置換 ＋ 冒頭の「・分類 A」除去（−11B） |
| `docs/how-to/plan-id-range-history-annex.md` | pin 世代の記録を追加（5 種・走査コマンド・欠番検証）／「現行 pin」を更新 |
| `scripts/check-commit-messages.js` | **キット版へ差し替え** ＋ 置換点 `PLAN_PROJECT` |
| `scripts/check-cross-repo-refs.js` | **キット版へ差し替え** ＋ 置換点 6 つ ＋ 0 件走査の門 ＋ worktree 警告 |
| `scripts/scripts.test.js` | キット版へ差し替え ＋ 固有デルタ 1 か所（拡張点の有無で期待値を選ぶ） |
| `scripts/scripts.repo.test.js` | 除外ログの文言を新旧両対応へ／`EXCLUDED_DIRS` を振る舞いで検査／`trackedMarkdown` を主経路で検査／`CROSS_REPOS`・`SELF_NAMES` を `LONG_NAMES`・`DEFAULT_CHECKER.selfNames` から取得／**#790 ラチェット新設** |
| `scripts/kit-sync-classification.json` | A から 2 件を B（X）へ／2 検査器の理由欄を新実測で書き直し／`$comment` の pin と分類名 |
| `scripts/check-test-traceability.js` | コメントの「分類 A にしたため」を是正（規則 10） |
| `docs/adr/IADR-0204_*.md` | **新設**（決定 4 件） |
| `docs/adr/IADR-0201_*.md` | 日付つき追記 2 箇所（決定 2 と分類表の追記ブロック）／`related_ids` に IADR-0204 |
| `docs/adr/README.md` | IADR-0204 の索引行 |

## 6. 検証（実走した結果）

| 検査 | 結果 |
| --- | --- |
| `node scripts/check-kit-sync.js` | **exit 0**（A 76 / B 27 / C 4 / 対象外 8 ＝ 115） |
| `node scripts/scripts.test.js` | **exit 0** ✓ 619 tests passed |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **exit 0** ✓ 619 tests passed |
| `node scripts/check-reading-budget.js` | **exit 0**（50,182B / 98%・warn 帯） |
| `node scripts/check-cross-repo-refs.js` / `--self-test` | exit 0（1,626 件・違反 0）／自己試験 86 件 |
| `check-doc-links` / `check-adr-numbering` / `check-plan-id-qualification` / `check-doc-type-vocabulary` / `check-doc-status-vocabulary` / `check-planning-pin-freshness` | 結果は PR 本文（[[IADR-0183]] の順序で `git add -A` の後に実行） |
| `check-doc-updated` / `check-commit-messages` | **コミット後**に実行（HEAD を読むため） |

## 7. 束ねの判定（[[IADR-0139]]）

- 本 issue は **pin 更新（A）とキット追随の再判定（F）** を 1 本にまとめている。
  **同一資源**（キット同期の分類表 `kit-sync-classification.json` とその回帰テスト）に閉じており、
  **片方だけ進めると `check-kit-sync` が赤のまま残る**（pin を進めた瞬間に drift 2 件が出る）。
- issue 本文は「planning#370 の裁定で実効上限が 2 → 4 件になった直後の最初の適用例」と述べるが、
  **本リポの `CLAUDE.md` と [[IADR-0139]] は依然「名目 3 件・実効 2 件」である**（改定 IADR は #791 の担当）。
  本作業は **2 件**なので、改定前の上限でも収まる。**改定を先取りしていない。**
- **#791（束ねの上限改定）・#793（減量）は束ねない。** 資源が別であり、#793 は本 PR が起票した追跡先である。

## 8. この作業で扱わないこと

| 対象 | 理由 |
| --- | --- |
| 必読規約の減量（`CLAUDE.md` / companion） | **#793**（本 PR で起票）。本 PR で巻き込むと減量の判断が歪む |
| キット `scripts.test.js` の是正 | **planning#380**（本 PR で起票）。キット側の作業 |
| キット `check-cross-repo-refs.js` への 0 件走査の門の環流 | **planning#379**（本 PR で起票） |
| `docs/how-to/session-handoff.md` の古い記述 | 並行 PR #789 と交差（§2.6） |
| `IADR-0139` の実効上限 2 → 4 の改定 | **#791** の担当 |
| SC-06 の既定属性（`f279c69` で計画が動いた分） | 実装側の追随は #754 / #516 の射程。本 PR は pin を運ぶだけ |

## 9. 計画書との差異・逸脱

- **issue #790 本文の「3. 分類 A の drift 2 件を解消する ── キット原文で上書きするのが分類 A の定義である」に
  従えなかった。** 2 件とも上書きは実行したが、**上書きした状態では別の受け入れ基準（必読規約の総量が
  増えていない／テストが緑）が満たせない**ことを実測した（§4.1）。
  [[IADR-0192]] 決定 2 が X に「**期限つきの暫定**」を認めているため、追跡先とラチェットを付けて保留した。
  **issue 本文の想定（+3,002B が予算に入る）が実測と食い違っていた**ことが原因であり、判断は
  [[IADR-0204]] に残した。**人間の確認を求める。**
- **`.claude/rules/traceability.repo.md` は「sha 置換のみ」の指示だったが、冒頭 1 行から
  「・分類 A」の 4 文字を外した**（分類が B（X）へ移ったため、残すと誤りになる。規則 10）。**−11B**。
- **HOWTO §B-5 は「差があればキット版で上書きしない」までしか書いておらず、
  「土台はキット版・失う機能だけ再付与」という中間の着地を明示していない。** #756 の
  `check-plan-id-qualification.js` が先例（worktree 警告を 3 行で再付与）なので同じ形を採った。
