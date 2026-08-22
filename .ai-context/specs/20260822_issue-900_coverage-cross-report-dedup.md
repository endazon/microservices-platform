---
title: 作業仕様書 — カバレッジ集計をレポート跨ぎで重複排除する（#900）
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0118
  - IADR-0123
  - IADR-0232
  - IADR-0236
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0030（バックエンドアプリケーション層標準）"
issue: "#900"
---

# 作業仕様書: カバレッジ集計のレポート跨ぎ重複排除（#900）

## 起点

- 実装 issue: `#900`（`#899` の暫定対応に対する「正しい直し方」）
- 検出: `#899`（`#897` のマージで develop の `Integration` が赤くなった）
- 関連: `IADR-0118`（床の方式）／ `IADR-0123`（帰属と二重記載）／ `IADR-0232`（門を回収先へ移した）
- 起点 ID は **無採番 `NFR`**（測定基盤のメタ作業。`traceability.repo.md`「`NFR` の採番」が
  「メタ作業は代表例で、製品の作業にも当たる番号が無いことはある」と認めている形）

## 分岐点（実コードで裏取りした）

`scripts/check-coverage-floor.js` は **レポート内は行単位（`<class>` ごとの `<line>` 走査）、
レポート間はレポート単位の集計値の単純加算**である。
行単位のパーサは既にあるのに、行の同一性はレポートを跨いだ瞬間に捨てられている。

| 場所 | 実際にやっていること |
| --- | --- |
| `parseCobertura()` `:422-472` | `classes` を回して `classLineStats()` の `stats` を `totals` へ畳むだけ。**行番号を返り値に残さない** |
| `mergeTotals()` `:508-518` | 数値 4 本（`lines` / `covered` / `branches` / `coveredBranches`）を加算するのみ |
| `aggregateReports()` `:525` | `mergeTotals(parsedList)` を呼ぶだけ |

`IADR-0232:391-396` の 2026-08-22 追記も同じことを言っている ——
「本検査はレポートを跨いで重複排除しないため、共有ライブラリの行は**参照するテストプロジェクトの数だけ**分母に載る」。

したがって改修は **「パーサを作り替える」ではなく「`parseCobertura` の返り値に行の同一性を持たせ、
`aggregateReports` で畳む」** で足りる。

## 重複の規模（依存グラフから実測。cobertura 実測ではない）

作業ツリーに `coverage.cobertura.xml` は **0 件**（`git ls-files` / `find` とも）。
よって `.csproj` の `ProjectReference` 推移閉包と `.cs` の行数概算で規模を推定した。

| プロジェクト | 到達するテストプロジェクト数（＝分母に載る部数） | 行数 |
| --- | ---: | ---: |
| `Platform.Shared.Contracts` | **15** | 122（概算） |
| `Platform.Shared.Infrastructure` | **14** | **772**（`coverage-floor.json:93` の CI 実測 `lines-valid`。独立概算 768・差 0.5%） |
| `Knowledge.Contracts` | **12** | 284（概算） |
| 各サービス本体 | 2 | — |

- テストプロジェクトは **16 本**（＝`integration.yml` が記録する「レポート 16 件」と一致）
- `Platform.Shared.Infrastructure` を直接 `ProjectReference` する `.csproj` は **`src/` 配下で 14 本**
  （`git grep` は 15 本ヒットするが 1 本は `templates/` 配下でビルド対象外）

### 🔴 「772 × 14 = 分母の 41.7%」は言い方を正す

`772 × 14 = 10,808` は分母 25,896 の 41.74% で算術は合うが、
**14 部のうち 1 部は正当な計上である。過剰計上は 13 部ぶん = 10,036 行（分母の 38.8%）。**
また重複は `Platform.Shared.Infrastructure` だけの問題ではなく、
`Platform.Shared.Contracts`（15 部）と `Knowledge.Contracts`（12 部）も同じ機構で載る。

**これらは上限の算術であって cobertura 実測ではない。** 実測は `integration.yml` の run からしか出ない（後述）。

## 着手前の母集合（自分で引いた。規則 9・10）

床の値を変えるので、**誤りの側の文字列（`38` / `39`）で全文書を走査**した（記憶で追随先を挙げない）。

```
git grep -nwE '38' -- ':!src/ai-stock-trading' ':!*.lock' | grep -Ei 'line|床|floor|カバレッジ|coverage'
git grep -nwE '39' -- ':!src/ai-stock-trading' ':!.ai-context/specs' ':!*.lock' | grep -Ei 'line|床|floor'
```

### 追随が要る箇所（live）

| # | 箇所 | 現在の記述 | 種別 |
| --- | --- | --- | --- |
| 1 | `src/coverage-floor.json:112-113` | `"line": 38 / "branch": 27` ＋ `$comment` の根拠欄 | **値の正本** |
| 2 | `.github/workflows/ci.yml:579` | `床 line 38 / branch 27 は…全量の実測から置かれている` | 平文 |
| 3 | `.github/workflows/integration.yml:11` | `カバレッジ床（line 38 / branch 27）は全量の実測から` | 平文 |
| 4 | `.github/workflows/integration.yml:89-90` | `床 line 38 / branch 27 は` | 平文 |
| 5 | `.github/workflows/integration.yml:126-127` | `期待値: レポート 16 件 / line 38.49%（9967 / 25896） / branch 27.32%（1838 / 6727）` | 平文（実測期待値） |
| 6 | `.ai-context/adr/IADR-0116_*.md:110` | 表の `` `line 38` / `branch 27` `` 未満は fail ＋「3 度目は #899」 | **機械検査が拾う** |
| 7 | `docs/tests/TEST_STRATEGY.md:76` | `（現在 `line 38` / `branch 27`）未満 → fail` | **機械検査が拾う** |
| 8 | `docs/tests/TEST_STRATEGY.md:260-262` | `［2026-08-22 追記 / #899］…3 度目の置き直しで line 39 → 38` | 追記ブロック（**新しい追記を足す**） |
| 9 | `.ai-context/adr/IADR-0232_*.md:110` | `床 line 38 / branch 27（#899 で 39 → 38）は` | 平文 |
| 10 | `.ai-context/adr/IADR-0232_*.md:128` | `フラグ無しで床 38 / 27 を強制（#899 で 39 → 38）` | 平文 |
| 11 | `.ai-context/adr/IADR-0232_*.md:398-399` | `現在の期待値は レポート 16 件 / line 38.49%…床は line 38 / branch 27（余裕 0.49pt）` | 平文（実測期待値） |
| 12 | `.ai-context/adr/IADR-0118_*.md` 決定 2 | 🔴 **置き直しの記録が 2 回で止まっている**（下記） | **3 度目と 4 度目を追記** |
| 13 | 🔴 `scripts/scripts.repo.test.js:5471` | `★ 床の値は **2 度**置き直された` | **コメントが既に陳腐化している** |

### 🔴 #12 と #13 は本作業の母集合の引き直しで新たに見つけた

いずれも `#902` のコミットメッセージが自己批判している
「**機械検査の母集合を自分の母集合として採用した**」誤りと**同じ形の残骸**である。

- **#12**: `.ai-context/adr/IADR-0118_*.md` を `899` で grep すると **0 件**、frontmatter も
  `updated: 2026-08-15` のまま。決定 2 の日付つき追記は 3 つあるが、置き直しの記録は
  **［2026-08-07 追記］（`34 → 33`）と［2026-08-15 追記 / #574］（`33 → 39` / `17 → 27`）の 2 回だけ**である
  （［2026-08-04 追記］は「据え置き・根拠差し替え」であって置き直しではない）。
  一方 `IADR-0116:110` と `TEST_STRATEGY.md:260` は「3 度目」を記録済みで、
  **`IADR-0118` だけが履歴を 2 回で止めている。** 本作業で 3 度目（`#899`）と 4 度目（本件）を併せて足す。
- **#13**: `:5471` のコメントは live だが、機械検査（`:5482` の
  `` /床[^\n]{0,12}?`line (\d+)`\s*\/\s*`branch (\d+)`[^\n]*未満/g ``）は
  **バッククォート付きの並記形 ＋ 同一行の「未満」**しか拾わないため、この平文コメントを拾わない。
  `#899` も見落としている。

### 触らない箇所（凍結記録）

`.ai-context/specs/**`（確定済み）／ `.ai-context/adr/IADR-0195_*.md:134,145-147,160,210,221,261`（決定と当時の実測ログ）／
`.ai-context/adr/IADR-0232_*.md:384-387`（`［2026-08-22 追記 / #899］`より前の「当時の記録」ブロック）／
`docs/tests/TEST_STRATEGY.md:251-258`（`［2026-08-15 追記 / #574］`ブロック）／
`.ai-context/adr/IADR-0118_*.md:137,153,181,187`・`IADR-0138_*.md:153,283` の日付つき追記。

`docs/DEFINITION_OF_DONE.md:60` と `scripts/check-coverage-floor.js` は
**値を書かず JSON を参照する正しい形**なので追随不要。

### 機械検査が拾う母集合（`scripts.repo.test.js:5482`）

- 正規表現: `` /床[^\n]{0,12}?`line (\d+)`\s*\/\s*`branch (\d+)`[^\n]*未満/g ``
- 母集合: `git ls-files -- ':!src/ai-stock-trading' ':!CHANGELOG.md'` の全追跡ファイル（自テストを除く）
- 0 件走査の門: `stated >= 2`
- **実際に当たるのは上の #6 と #7 の 2 箇所だけ**で、門の `>= 2` はそこでちょうど飽和している。
  `ci.yml` / `integration.yml` / `IADR-0232` の平文はバッククォートも「未満」も無いので**拾わない**。
  🔴 **この検査の母集合を自分の母集合にしてはならない。**

## 設計

### D1. 行の同一性を `parseCobertura` の返り値へ持たせる

`classLineStats()` に行の配列（`entries`）を返させ、`parseCobertura()` は
**集計対象として残った行**（ユニット除外・生成コード除外を通り抜けた行）だけを
`lineEntries` として返す。`aggregateReports()` がそれを 1 つの `Map` へ畳む。

行数は CI 実測で全レポート合計 25,896 行・レポート 16 件の規模であり、
数万件の小さなオブジェクトを持つだけなのでメモリ上の問題にはならない。

### D2. 🔴 キーは `(class name, 正規化した filename, 行番号)` の 3 つ組

**`(filename, 行番号)` では駄目である。** `IADR-0123` が選択肢 C を明示的に退けた理由がそのまま当たる ——
`Foo` と `<Foo>d__2`（非同期ステートマシン）のように**同一行を異なる観点で計測している行**を潰してしまう。
潰す際にどちらの `hits` を採るかは恣意的であり、coverlet や reportgenerator のどの集計とも一致しなくなる。

`<class name>` をキーへ含めれば、**レポート跨ぎの重複だけを畳み、同一レポート内の
`Foo` / `<Foo>d__2` は別エントリのまま残る。**

### D3. 🔴 キーに生の `filename` を使ってはならない

`unitOfFilename()` は**同じファイルをレポートごとに違う文字列で返す**（`relative` / `absolute` /
`source-joined`）。`IADR-0123` 決定 4 の CI 実測は「そのまま(相対) 645 / `<sources>` 結合 1391」であり、
**同一 CI 実行の中で両形が混在している**。生パスをキーにすると重複排除が一部にしか効かず、
しかも診断には何も出ない（無音の部分適用）。

キーは `attribution.resolved` を `src/<unit>/` 以降へ正規化した経路とする（`SRC_UNIT_RE` の
マッチ位置から切り出す）。帰属できない行（`unit === null`）は正規化できないので**生パスをキーにし、
その件数を診断へ出す**。

### D4. 被覆の畳み込み（OR）

同じキーの行が複数レポートに出たら、フィールドごとに `max` を採る。

| フィールド | 畳み方 | 意味 |
| --- | --- | --- |
| `hits` | `max` | `hits > 0` が 1 つでもあれば被覆＝**OR**。`countLinesUnique()` が class 内で既に採っている規則と同じ |
| `branches` | `max` | 分岐分母 |
| `coveredBranches` | `max` | 分岐分子 |

**`coveredBranches > branches` にはならない** —— 各レポートで `c_i <= b_i` なので
`max(c_i) <= max(b_i)` が成り立つ。

🔴 **分岐の `max` は「測定定義の変更」である。** Cobertura は `condition-coverage="50% (1/2)"` の
**カウントしか持たず、どの分岐が通ったかの識別子が無い**。したがってレポート間で厳密な OR は取れない。
`max` は和集合を**過小評価**する（レポート A が分岐 1 を、レポート B が分岐 2 を通していても `1/2` のまま）。
`IADR-0123` 決定 4 の 2026-08-04 追記により、**分岐の定義の変更は床の置き直しとセットでしか行えない。**

### D5. 適用順（除外が先、重複排除が後）

除外（ユニット・生成コード）は**レポートごとに従来どおり先に**適用し、生き残った行だけを畳む。

順序で最終値は変わらない —— 除外の述語は正規化キーの純関数だからである
（同じキー ⇒ 同じ `src/<unit>/…` 接尾辞 ⇒ 同じ `unit` ⇒ 同じ `generatedKindOf` の判定）。
除外を先に置くのは、**`excluded` / `generated` の診断値を従来と同じ「単純和」のまま保つ**ためである。

### D6. 🔴 重複排除するのは `totals` だけ。`beforeExclusion` は単純和のまま

| 値 | 畳み方 | 理由 |
| --- | --- | --- |
| `totals`（**床が判定に使う**） | **重複排除後** | 本改修の目的 |
| `excluded` / `generated` | 単純和（従来どおり） | `IADR-0123` の混入行数（133 行）等の**既存の診断値の意味を変えない** |
| `beforeExclusion` | **単純和（従来どおり）** | 🔴 `IADR-0123` 決定 4 の照合は **coverlet の `lines-valid` のレポート横断の単純和**と比べている。ここを重複排除すると照合が壊れる |
| `beforeGeneratedExclusion` | 単純和（従来どおり） | 同上（`#571` の前後比較用） |
| `beforeCrossReportDedup`（**新設**） | 単純和 | 重複排除の**前後比較用の観測点** |

そのために `parseCobertura` は「レポート内の集計値（重複排除前）」を `undeduped` として返し、
`aggregateReports` は `beforeExclusion` を `undeduped + excluded + generated` の単純和として組む。
**今日と同じ値になる**（従来は `totals + excluded + generated` で、当時の `totals` が `undeduped` そのものだった）。

### D7. 行番号を持たない `<line>`

`parseLineElement()` は `number === null` を返しうる。**識別できない行は畳めない**ので、
別バケツ（`unkeyed`）へ入れて単純和のまま `totals` へ足し、件数を診断へ出す。
1 レポートなら従来と完全同値になる。

### D8. 診断（0 行なら notice）

🔴 **`<class name>` がレポート跨ぎで安定していることは未確認である**（手元に実レポートが 0 件）。
`IADR-0138` 決定 3・`IADR-0195` 決定 2 と同じ「除外量 0 = フィルタ素通り」の作法で可視化する。

- 毎回出す: 重複排除の前後の値・**落とした行数**・**レポート数の内訳**（2 部 n1 / 3 部 n2 / …）・
  正規化できなかったキーの件数・行番号を持たない `<line>` の件数
- **落とした行が 0 行なら notice**（fail でも warn でもない。レポート 1 件なら正常に 0 行になる）

## 受け入れ基準

1. 同じ `(class name, 正規化 filename, 行番号)` が複数レポートにあっても**分母に 1 回だけ**載る
2. 被覆は OR で畳む（1 つのレポートで被覆されていれば被覆）
3. 分岐は `max` で畳み、**その定義変更を IADR に明記**する
4. `beforeExclusion` は単純和のままで、`lines-valid` との照合が壊れない
5. 重複排除量・レポート数の内訳・非正規化キー件数が毎回診断に出る／**0 行なら notice**
6. 床を重複排除後の **CI 実測**から置き直し、根拠を `src/coverage-floor.json` に残す
7. `--self-test` に下の変異試験を固定する
8. 上表 #1〜#13 の追随先がすべて新しい床と整合する

## 変異試験（`--self-test` へ追加する 6 ケース）

🔴 **受け入れ基準を rate で書いてはならない。** 「同じレポートを 2 部与えても集計値が変わらない」は
正しいが、**同じレポート 2 部では被覆率は動かない**（分子分母が等倍で増えるため 50% のまま）。
率が動くのは「同じ行を違う被覆で載せた 2 部」のときだけ。
**rate で書くと重複排除を外しても緑のまま通る。**

| # | ケース | assert |
| --- | --- | --- |
| 1 | 同一レポート 2 部で `totals` 不変 | `lines` / `covered` / `branches` / `coveredBranches` を**個別に**（rate では書かない） |
| 2 | 被覆の違う 2 部を OR で畳む（`A(hits=1,0)` ＋ `B(hits=0,0)`） | `lines 2 / covered 1`。現行実装は `lines 4 / covered 1`。**唯一 rate でも差が出る** |
| 3 | 1 レポートだけなら現行と完全同値 | `FIXTURE_ATTRIBUTED` で `beforeExclusion.lines === 4` が `lines-valid` と一致し続ける |
| 4 | `Foo` / `<Foo>d__2` を潰さない | 同一 filename・同一行番号の違うクラス 2 つで `lines === 2` |
| 5 | filename の形が違う 2 レポートでも畳む | A は relative、B は `<sources>` ＋相対の source-joined |
| 6 | 重複排除量 0 行なら notice | `attributionMessages` に notice が出る |

### 🔴 「変異が当たった」の担保 —— ケース 1 と 2 の**両方**を必ず置く

`check-backend-libraries.js` 規則 5 が踏んだ「(a) だけでは静かに no-op になる」型の穴を避ける。
**片方だけでは片方向の穴が開く** ——

- ケース 1（不変）だけ: 畳み込みを「常に全部潰す」実装にしても通る
- ケース 2（差が出る）だけ: 畳み込みを「何もしない」実装では落ちるが、過剰に潰す実装は通る

あわせて `scripts.repo.test.js` から、**「素朴な合算」と「畳み込み後」を同じ入力で比較して
差が出ることを assert** する（重複排除が no-op に退化したら fail する形）。

## 🔴 床の実測はローカルで取れない —— 人間の操作が 1 回要る

- `find src -name coverage.cobertura.xml` は **0 件**
- Docker デーモンが無く統合テストが skip されるため **CI と同じ母集合を作れない**
- 床は「統合テストを含む全量」から置く決まりなので、根拠になる実測は `integration.yml` の run からしか出ない

### 手順

1. 重複排除の実装 PR を**床据え置き（`line 38` / `branch 27`）のまま**出す
   （`ci.yml` の PR 側は `--report-only` なので落ちない。**門は `integration.yml` にしかない**）
2. **利用者に Web UI の Actions タブから `integration.yml` を PR ブランチ指定で手動実行してもらう**
   （`workflow_dispatch` を API から叩くと 403。既定トークンは metadata=read のみ。`IADR-0232:380` が同じことを記録している）
3. ログの実測で床を確定 → 同一 PR へ追加コミット
4. 🔴 **分岐も必ず測り直す**（現行 27 に対し実測 27.32% で**余裕 0.32pt しかない**）。
   `IADR-0195` 決定 3 の「切り下げが機能する床を与えないなら 1 つ下の整数を採る」の判定を**再度当てる**

**マージ後の develop push で `integration.yml` が回るのを待つ形（`#899` と同じ「床が割れてから直す」）は採らない。**

## 1 PR に収まる（分割不可）

`IADR-0123` 決定 4 の追記が「**分岐の定義の変更は床の置き直しとセットでしか行えない**
（新 IADR ＋ `src/coverage-floor.json` を同一 PR で）」と名指しで禁じている。
`IADR-0230` の束ねも `src/coverage-floor.json`・`.github/workflows/**`・`docs/tests/**` が
M-A 外なので使えず、**単独 PR** である。

## 採番とマージ順の制約

- 本作業は **`IADR-0236`** を使う（現在の最大は `IADR-0233`。`IADR-0235` は並行の `#885` が予約）
- `check-adr-numbering.js` は**欠番なし**を fail で見るため、
  🔴 **`#885`（`IADR-0235`）が develop に着地するまで本 PR の `scripts-tests` は赤い。**
  マージは `#885` の後に行う（PR を出すのは先でよい）
- `.github/workflows/ci.yml` は `#882` PR1 も触る予定がある。本作業が触るのは `:579` のコメント 1 行のみ。
