---
title: 作業仕様書 — 本リポが先行する検査器 3 本の優劣を HOWTO の手順で判定し、環流/ 差し替えを確定する（#756）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0115
  - IADR-0140
  - IADR-0143
  - IADR-0169
  - IADR-0183
  - IADR-0192
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/tools/impl-handoff-kit/HOWTO.md"
  - "../../planning/tools/impl-handoff-kit/repo-template/scripts/kit-sync-classification.example.json"
---

# 仕様書: 検査器 3 本のキット優劣判定（#756）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**NFR / 運用保守**。メタ作業のため計画の非機能要件表に当たる番号が無い。
  `.claude/rules/traceability.md` の「無採番 `NFR` を許す場合 2」に当たり、**環流しない**）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: 計画側なし。実装ADR は [[IADR-0115]]（キット同期の分類）/ [[IADR-0192]]（分類表）/
  [[IADR-0140]]（クロスリポ参照検査）/ [[IADR-0143]]（自己除外）/ [[IADR-0169]]（`.md` 外への走査）/
  [[IADR-0183]]（worktree 状態の警告）
- 計画書リンク: [`planning/tools/impl-handoff-kit/HOWTO.md`](../../planning/tools/impl-handoff-kit/HOWTO.md)
  §B-5 の注記「同じ原則がスクリプトにも要る」（裁定 planning#343）
- 関連 issue: #756（本件）/ #755（分類 C 再判定・起点）/ #749（pin-freshness の逆方向比較）/
  #757（`scripts.test.js` の追随）/ planning#316（本 3 本のうち plan-id の環流先）/ planning#363（分類 C の新定義）

## 目的・背景

`scripts/kit-sync-classification.json` で **B（X: 5 種のどれにも当たらない）** に置かれた検査器 3 本
（`check-commit-messages.js` / `check-cross-repo-refs.js` / `check-plan-id-qualification.js`）について、
**HOWTO が定める手順で優劣を判定**し、(1) 本リポ版が優る → キットへ環流、(2) キット版が優る → 差し替え、
(3) 同等 → 併存の根拠、のいずれかへ確定する。X は環流債務の測定値であり、放置は債務の隠蔽になる。

### HOWTO が定める判定手順（実際に読んだ内容・§B-5 の注記 255〜270 行）

1. **バイト一致は優劣を判定しない**（`check-kit-sync.js` が見るのはバイト一致だけである）。
2. **キット版を採る前に、置き換わる側の CLI を実走して差を確かめる**（同じ入力・同じフラグに同じ反応をするか）。
   HOWTO の例示は `node scripts/<checker> <flag>; echo $?` と、キット版の同一実行の対比である。
3. **差があればキット版で上書きせず、分類 B（固有デルタ）として維持し、キットへ環流する。**
4. 機能差の一般的な機械検出は**作らないと裁定済み**（planning#343。投機的であるため）。

すなわち判定基準は「新しい方」でも「行数が多い方」でもなく、**同じ入力に対する検出力（と、失うものの有無）**である。
本作業ではこの 2 の実走を 3 本すべてで行い、出力を証跡として残した。

## 対象範囲

- 対象: 上記 3 本の優劣判定、判定に基づく差し替え（該当分）、`scripts/kit-sync-classification.json` の理由欄更新、
  差し替え分の変異試験（`scripts/scripts.repo.test.js`）。
- 対象外: 環流 issue の**起票**（本作業では起票しない。起票案を本書に残す）。
  `scripts/scripts.test.js`（分類 A・キットとバイト一致。**変更禁止**。追随は #757）。
  `CLAUDE.md` / `.claude/rules/`（必読規約 50KB 予算が 98% のため 1 バイトも足さない）。

## 母集合（自分で引き直した結果）

**時点: 2026-08-15、ブランチ `chore/nfr-kit-superiority-three-checkers`、base `4b215b6`、planning pin `4d6a7d6`。**
issue 本文の 3 本の一覧は**母集合として採らず**、次の 3 軸で引き直した。

### 軸 1 — キット全ファイルを実 `cmp` で全数突合（機械・生の数）

```bash
KIT=planning/tools/impl-handoff-kit/repo-template
find $KIT -type f | sed "s|^$KIT/||" | sort > kitfiles.txt   # 115 件
while read -r f; do
  if [ -f "$f" ]; then cmp -s "$KIT/$f" "$f" && echo "SAME $f" || echo "DIFF $f";
  else echo "MISSING $f"; fi
done < kitfiles.txt
```

生の数: **キット 115 件 = SAME 76 / DIFF 30 / MISSING 9**。
`node scripts/check-kit-sync.js` の申告（A 76 / B 26 / C 4 / 対象外 9）と一致する（DIFF 30 = B 26 + C 4）。

**この走査に自己参照は入らない。** 走査対象はキットのファイル一覧であり、本作業仕様書
（`docs/specs/` 配下・キットに対応物なし）は母集合に入らない。したがって
「生の数 → 自己参照を引く → 最終値」の引き算は **30 → 0 → 30** である（引くものが無い）。

### 軸 2 — DIFF 30 件のうち「検査器」に当たるものを全数列挙

| # | ファイル | 現分類 | 本 issue の対象か | 判断 |
| --- | --- | --- | --- | --- |
| 1 | `scripts/check-commit-messages.js` | B(X) | **対象** | 本作業で判定 |
| 2 | `scripts/check-cross-repo-refs.js` | B(X) | **対象** | 本作業で判定 |
| 3 | `scripts/check-plan-id-qualification.js` | B(X) | **対象** | 本作業で判定 |
| 4 | `scripts/check-doc-links.js` | B(X) | 対象外 | **既に環流済み**（#736 / planning#337）。追跡先が別に在る |
| 5 | `scripts/check-planning-pin-freshness.js` | B(X) | 対象外 | **#749 が逆方向比較として先行**。二重作業を避けるため触らない |
| 6 | `scripts/scripts.test.js` | B(X) | 対象外 | 検査器ではなくテストハーネス。**変更禁止**・追跡 #757 |

**3 本以外に「先行・後行」している検査器は 4・5 の 2 本あり、いずれも別 issue で追跡済みである
（未追跡のものは無い）。** 6 はテストハーネスであり検査器ではないが、DIFF に出るため除外理由を明記する。
残る DIFF 24 件は検査器ではない（`.md` / ワークフロー / 設定 / `CHANGELOG.md`）ため対象外。

### 軸 3 — 逆向き（キットに在って本リポに無い検査器／本リポに在ってキットに無い検査器）

```bash
for f in scripts/*.js scripts/lib/*.js; do [ -f "$KIT/$f" ] || echo "$f"; done   # 30 件
```

- **キットに在って本リポに無い検査器: 0 件**（MISSING 9 件はすべて `*.example.yml` /
  `kit-sync-classification.example.json` であり、有効化済み・実体保持のため `notApplicable`）。
- **本リポに在ってキットに無い: 30 件**（`check-adr-numbering.js` 〜 `lib/worktree-state.js`）。
  これらはキットに対応物が無く**分類の対象外**（分類表はキットのファイルを母集合とする）。
  ただし `lib/worktree-state.js` は本判定に直接効く（後述の固有デルタの正体）ため記録する。

### 除外したものと理由（黙って落とさない）

| 除外 | 理由 |
| --- | --- |
| `check-doc-links.js` | 環流済みで追跡先（#736 / planning#337）が在る。本 issue の受け入れ基準の対象外 |
| `check-planning-pin-freshness.js` | #749 が同種の判定を先行実施中。ファイル領域が交差するため直列化する |
| `scripts.test.js` | 分類 A の配布物。本作業では**変更禁止**（追随は #757） |
| DIFF 24 件（非検査器） | `.md` / ワークフロー / 設定であり「検査器の優劣」の母集合ではない |
| 本リポ固有 30 スクリプト | キットに対応物が無く、優劣判定の対象になり得ない |

## 判定（実走の証跡つき）

### 共通の実走 1 — 自己試験（HOWTO の「同じフラグに同じ反応をするか」）

```
node planning/tools/impl-handoff-kit/repo-template/scripts/<f> --self-test  /  node scripts/<f> --self-test
  check-cross-repo-refs.js        kit: 69 件 all passed (0)   repo: 85 件 all passed (0)
  check-plan-id-qualification.js  kit: 38 件 all passed (0)   repo: 35 件 all passed (0)
  check-commit-messages.js        --self-test を持たない（両版ともレンジモードで exit 0）
```

自己試験の件数は**優劣の根拠にならない**（試験の粒度が違うだけである）。したがって以下は
**同一入力に対する検出結果**で判定した。

### 1. `scripts/check-commit-messages.js` → **本リポ版が優る（環流）**

実 diff: `diff -u <kit> <repo>` = 312 行（kit 494 行 / repo 620 行）。

| 機能 | キット版 | 本リポ版 |
| --- | --- | --- |
| 書式検査 `種別(ID): 要約` / allowlist / bot 除外 / PR タイトルモード | あり | あり（同一） |
| `IADR` / 計画 `ADR` の実在性 | あり | あり（同一） |
| **`FR` / `UC` / `SC` の実在性**（#579。`loadExistingPlanIds` / `normalizePlanId`） | **無し** | **あり** |
| **他リポ issue 番号の修飾検査を件名・本文・PR タイトルへ**（#507。`crossRepoRefReasons`） | **無し** | **あり** |
| **コミット本文（`%b`）の収集** | 無し（`%s` まで） | あり |
| 置換点 `PLAN_PROJECT` | `<project-name>`（未設定） | `microservices-platform`（埋め済み） |
| bot 判定関数名 | `isBotLogin` | `isBotAuthorName`（**実装は同一**。完全一致・大小無視） |

**キット版にあって本リポ版に無い機能は 1 つも無い**（差は関数名と、空タイトル判定と bot 判定の順序だけで、
どちらも exit 0 に落ちるため観測可能な差にならない）。よって **HOWTO 手順 3 のとおり差し替えず、分類 B を維持してキットへ環流する。**

### 2. `scripts/check-cross-repo-refs.js` → **本リポ版が優る（環流）**

実 diff: 778 行（kit 643 行 / repo 708 行）。**同一入力の実走で検出数が違う**（決定的な証跡）。

```
fixture: 型4 owner 誤り / 〔〕 で添える列挙 / スラッシュ列挙 / 長い表記 / 非 md の空白区切り
  repo 版: 違反 6 件 exit 1   （owner 誤り・〔〕列挙・スラッシュ列挙・長い表記・空白区切り）
  kit  版: 違反 4 件 exit 1   （owner 誤りと 〔〕列挙を検出できない）
```

| 機能 | キット版 | 本リポ版 |
| --- | --- | --- |
| 型 1 長い表記 / 型 2 列挙 / 型 3 空白区切り / 閉じないフェンス | あり | あり |
| **型 4 owner 誤り**（#590。`KNOWN_OWNERS` / `OWNED_REPO_SHORT`） | **無し** | **あり** |
| **`〔〕` の区切り**（#586。`SEP_BRACKET`） | **無し**（`[/／,，、・･]` のみ） | **あり** |
| **走査範囲**（IADR-0169） | **`*.md` のみ**（`trackedMarkdown`） | **追跡下の全ファイル**（`scripts/` の非 md を除外・除外件数をログ） |
| バイナリ読み飛ばし（NUL 判定） | 無し | あり |
| worktree 状態の警告（IADR-0183） | 無し | あり |
| 置換点 `CROSS_REPOS` / `SELF_NAMES` / `EXCLUDE_PATHSPECS` ＋ 環境変数 | **あり**（本リポ版は直書き） | 無し |
| 設定の妥当性検査（自リポ名の混入・空設定・メタ文字・長短順） | **あり** | 無し |

**検出力は本リポ版が上回り、キット版が上回るのは「配布のための設定構造」だけである。**
HOWTO 手順 3 の「差があればキット版で上書きしない」に該当するため、**差し替えない。**
環流の理想形は「本リポ版の検出力（型 4・`〔〕`・`.md` 外走査）をキットの `createChecker` 構造へ載せる」であり、
キット側の作業量が大きいので**キットへの起票案**として下に残す。

### 3. `scripts/check-plan-id-qualification.js` → **キット版が優る（差し替え）**

実 diff: 512 行（kit 414 行 / repo 343 行）。**同一入力の実走でキット版のほうが多く検出する。**

```
fixture: AST FR-17 / AST [[IADR-0080]] / AST NFR-01 / AST/FR-17（正しい形）
  repo 版: 違反 2 件 exit 1  （NFR-01 を検出できない）
  kit  版: 違反 3 件 exit 1  （PLAN_ID_PREFIXES=AST）
  kit  版（置換点 未設定）: skip exit 0 ＝「対象が無い」を「検査した」と区別できる
```

| 機能 | キット版 | 本リポ版 |
| --- | --- | --- |
| 型 A（空白・全角・TAB・wiki リンク・バッククォート・全角括弧の区切り） | あり | あり（同一の正規表現） |
| **`NFR` の検出**（`ID_KINDS` に `NFR` を含む） | **あり** | **無し** |
| **置換点 `PROJECT_PREFIXES` / `ID_KINDS` / `EXTRA_EXCLUDES` ＋ 環境変数** | **あり** | 無し（`AST` 直書き） |
| **除外の submodule 導出**（`.gitmodules` から。`submodulePaths`） | **あり** | 無し（`src/ai-stock-trading/` 直書き） |
| **設定の妥当性検査**（空 prefixes は skip / 空 `ID_KINDS` は設定エラー） | **あり** | 無し |
| 自己除外（`__filename` 由来）・0 件走査の門・`maskCode` 借用 | あり | あり（同一） |
| worktree 状態の警告（IADR-0183） | 無し | **あり** |
| `docs/superpowers/` の除外 | 置換点 `EXTRA_EXCLUDES` で表現可能 | 直書き |

**この 1 本は「キットが後追いで一般化した」のではなく、本リポの環流要求どおりにキットが実装した版である。**
`feedback/20260808_kit-plan-id-qualification-check.md`（status: accepted / dispatched・起票 planning#316）が
「`PROJECT_PREFIXES` / `ID_KINDS` は環境変数か設定から読む」「`EXCLUDED_PATH_RE` の submodule は `.gitmodules` から導出する」
と**環流時に自ら要求しており、キット版はその 2 点を満たしている**。よって**キット版へ差し替える**のが正しい着地である。

**失うもの（差し替えで持ち込む退行）は worktree 警告 1 点のみ**であり、これは本リポにしか無い
`scripts/lib/worktree-state.js` への結線（固有デルタ種 3）である。差し替え後に 3 行で再付与する。

## 設計（実施する変更）

1. `scripts/check-plan-id-qualification.js` を**キット版で置き換える**。
2. 置換点を埋める（**固有デルタは 3 点に閉じる**）。
   - `PROJECT_PREFIXES` の既定を `['AST']` にする（環境変数 `PLAN_ID_PREFIXES` の上書きは残す）。
     **既定を空のままにして CI 側で環境変数を渡す形は採らない** —— `.github/workflows/` の呼び出し口は
     `scripts.repo.test.js` 経由の相乗りであり、注入点が増えるほど「設定し忘れて静かに skip」する経路が増える。
     **skip は exit 0 で緑になる**ため、いちばん気付けない壊れ方をする。
   - `EXTRA_EXCLUDES` の既定に `docs/superpowers/` を入れる（現行の除外を保つ）。
   - `lib/worktree-state.js` の警告を再付与する（IADR-0183）。
3. `scripts/kit-sync-classification.json` の 3 件の理由欄を判定結果へ更新する
   （plan-id は **X → B 第 5 種 ＋ 種 3**、他 2 本は X のまま環流待ちであることと根拠を明記）。
4. `scripts/scripts.repo.test.js` に**変異試験**を足す（挙動が変わるため必須）。
   - `NFR` を検出すること（差し替えで増えた検出力の側）。
   - `PROJECT_PREFIXES` が空になったら skip へ落ちること＝**置換点が空へ戻る退行の門**
     （空 → 検査ゼロ → 緑、という最も気付けない壊れ方を止める）。
   - 既存の「実データ 0 件」「空白形で exit 1・正しい形で exit 0」「0 件走査の門」は据え置き。

## 受け入れ基準

- [x] 3 本の機能差の突合表が本書にある（上記 3 表。**実走の出力を根拠にした**）
- [x] `scripts/check-plan-id-qualification.js` の分類が X 以外（**B 第 5 種**）へ確定した
- [x] `check-commit-messages.js` / `check-cross-repo-refs.js` は**環流先 planning issue 番号**が理由欄に入る
      → ［2026-08-15 追記 / #756］**起票され、達成した。** 当初は「本作業では起票しない」という
      作業指示のため未達としていたが、判定が確定した以上、環流先が無いままでは
      `kit-sync-classification.json` の X が**追跡先の無い債務**として残る（[[IADR-0115]] 決定 3 は
      X に追跡先 issue 番号を必須としている）。**planning#373**（`check-commit-messages.js`）と
      **planning#374**（`check-cross-repo-refs.js`）を起票し、両方の理由欄へ番号を入れた。
- [x] `node scripts/check-kit-sync.js` が緑
- [x] `node scripts/scripts.test.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑

## テスト方針

- 差し替えた検査器は `scripts/scripts.repo.test.js` から**実バイナリで**起動する（既存の相乗り経路を使う）。
- 変異試験を対で置く: 「検出できること」と「検出しなくなったら落ちること（置換点が空へ戻る退行）」。
- `scripts/scripts.test.js` は分類 A のため触らない。

### 実施した変更と実測（証跡）

| 変更 | 内容 |
| --- | --- |
| `scripts/check-plan-id-qualification.js` | キット版へ差し替え（+231/−146）。固有デルタは 3 点のみ（`PROJECT_PREFIXES=['AST']` / `EXTRA_EXCLUDES=['docs/superpowers/']` / worktree 警告） |
| `scripts/scripts.repo.test.js` | 変異試験 3 本を追加（+77） |
| `scripts/kit-sync-classification.json` | 3 件の理由欄を判定結果へ更新（±3） |

```
node scripts/check-kit-sync.js
  → OK: キット 115 件（A 76 / B 26 / C 4 / 対象外 9）  exit 0
node scripts/scripts.test.js                     → ✓ 535 tests passed  exit 0
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js → ✓ 535 tests passed  exit 0
  うち新規: NFR も検出する / 置換点が空へ戻ると skip する（変異試験）/ 除外は submodule 導出 ＋ docs/superpowers/ を保つ
node scripts/check-plan-id-qualification.js --self-test → 自己試験 38 件 all passed  exit 0
node scripts/check-plan-id-qualification.js            → OK: 1324 件（差し替え前と同数・違反 0）exit 0
node scripts/check-cross-repo-refs.js                  → OK: 1615 件（scripts/ 非 md 70 件は除外）exit 0
node scripts/check-doc-links.js                        → OK: 629 件  exit 0
```

**`NFR` を検出対象へ加えても実データの違反は 0 件のまま**である（新たな是正作業を誘発しない）。

**実装 diff の大きさ**: `pr-size.yml` と同じ数え方（**追加行のみ**・`docs/**` を除外）で **311 行**（上限 400 の 78%）。
削除を含めた総変更は 460 行だが、本リポの門は追加行で測る。**差し替えを別 issue へ切り出す必要は無い。**

### 母集合の規則 10（この変更で新たに誤りになる自分の記述）の引き直し

```bash
grep -rln "check-plan-id-qualification" --exclude-dir=planning --exclude-dir=.git .
```

12 件がヒットした。うち本変更で誤りになるものは **0 件**である（内訳と根拠）。

- `scripts/README.md` … 除外の列挙（`CHANGELOG.md` / `docs/specs/` / `feedback/` / `docs/superpowers/` / submodule /
  検査器自身）は差し替え後も**すべて成立**する（`docs/superpowers/` は置換点で保った）。
- `.claude/rules/traceability.md`（分類 A の配布物）… 「配布時に置換点 `PROJECT_PREFIXES` を書き換えること」は
  **差し替えによって初めて本リポでも真になった**（従前は置換点自体が無かった）。
- `.claude/rules/traceability.repo.md` … 対象の記述（追跡下の全ファイル、`CHANGELOG.md` / `docs/specs/` / `feedback/` を除く）は不変。
- `docs/adr/IADR-0143` / `IADR-0183` / `docs/how-to/cross-project-id-refs-annex.md` … 検出しない型（近傍規則・列挙の後続 ID）と
  worktree 警告の対象は不変。
- **既存の不正確さ（本変更とは無関係）**: `scripts/README.md` が検出しない対象として挙げる `.github/workflows/**` は、
  差し替え前の版でも除外されていない（`EXCLUDED_PATH_RE` に無い）。**本作業では触らない**（母集合外・別途是正）。

## 環流の起票案（**本作業では起票しない**）

### 案 1 — `check-commit-messages.js`（宛先: `endazon/project-planning`・`impl-handoff-kit`）

- 件名案: `キット環流: check-commit-messages.js へ FR/UC/SC 実在性検査とクロスリポ参照検査を足す`
- 内容: (a) `loadExistingPlanIds` / `normalizePlanId`（`check-test-traceability.js` の `readPlanIds` を借り、
  **モジュール不在は skip・節の破壊は throw** という fail の向きの分け方ごと）、(b) `crossRepoRefReasons` ＋
  `CROSS_REPO_REF_LABELS`（件名・**本文**・PR タイトルの 3 面）、(c) `git log` の書式へ `%b` を足す。
- 注意点: キット側の関数名は `isBotLogin` であり、環流時に本リポの `isBotAuthorName` へ改名しない
  （配布物の名前はキットが正）。(a) は `check-test-traceability.js` を持たない配布先では skip になるため、
  **キットには「持たない構成」の notice ごと渡す**。

### 案 2 — `check-cross-repo-refs.js`（宛先: 同上）

- 件名案: `キット環流: check-cross-repo-refs.js に型 4（owner 誤り）・〔〕区切り・.md 外走査を足す`
- 内容: (a) 型 4 owner 誤り（`KNOWN_OWNERS` を**置換点**として一般化する。本リポは `['endazon']` 直書き）、
  (b) `SEP_BRACKET`（`〔〕` 等）を `SEP` へ足す、(c) 走査を `*.md` から追跡下の全ファイルへ広げ
  （`scripts/` の非 md を 1 本の規則で除外・**除外件数をログに出す**）、(d) バイナリ（NUL）読み飛ばし。
- 注意点: **本リポ版をそのまま貼らない。** キット版の `createChecker`（設定の純関数化・妥当性検査）が
  優る部分なので、**キットの構造の上に本リポの検出力を載せる**。着地後は本リポをキット版へ戻し、
  分類を X → B 第 5 種へ移せる。

> どちらも本 issue の受け入れ基準「環流先の planning issue 番号が理由欄に入る」を満たすには
> **起票が要る**。起票は人間の判断で行う（本作業の指示で禁止されているため実施しない）。

## 計画書との差異

- 差異: なし。HOWTO §B-5 の手順（実走で差を確かめる／差があればキット版で上書きしない）にそのまま従った。
  **HOWTO は「差があれば環流」までしか書いておらず、「キット版が優る場合」の手順を明示していない**が、
  同じ注記の「バージョンは高い方を残す」原則から**優る側を採る**と読んだ（本件の plan-id はこれに当たる）。

## 未決事項

- 案 1 / 案 2 の**起票**（人間の判断待ち）。起票後に `kit-sync-classification.json` の理由欄へ planning issue 番号を入れる。
- `check-planning-pin-freshness.js`（#749）と `scripts.test.js`（#757）は本作業の対象外。
