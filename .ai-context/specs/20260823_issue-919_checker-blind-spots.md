---
title: 作業仕様書 — #919 封じ込め検査器（check-backend-libraries.js）の不可視領域（dist/ 配下・UTF-16LE の .cs）の追跡
type: spec
status: done
related_ids:
  - NFR
  - ADR-0027
  - ADR-0030
  - IADR-0246
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
related_specs:
  - "../adr/IADR-0246_confinement-checker-blind-spots.md"
issue: "#919"
---

# 作業仕様書 — #919 封じ込め検査器の不可視領域（SX-1 / SX-2）の追跡

## 背景

issue #919 は `#903` の U4 変異試験で実測された `scripts/check-backend-libraries.js` の 2 つの
不可視領域（**SX-1**: `SKIP_DIRS` がディレクトリ**名**一致のため `dist/` という名のディレクトリ配下の
`.cs` が素通りする、**SX-2**: `.cs` の読み込みが `utf8` 固定のため UTF-16LE の `.cs` が読めず素通りする）
を追跡し、塞ぐか受容するかを判断する issue である。

## 着手前の確認（必須の再確認）

担当指示は「統括側で確認済みの前提」として、姉妹 issue #920（値レベル変異）は
「IADR-0246 決定 2 で解決済み・検査器は強化しない」と確定・コミット済みであり、
`scripts/check-backend-libraries.js` は「現在どのエージェントも触っていないので自由に編集してよい」
としていた。

**着手前に issue #919 本文と、対象ファイルを自分で読んで確認した結果、この前提は #920 についてのみ
正しく、#919 自体はスコープを再確認する前提が古かった**——本文・自己試験・`IADR-0246` を読んだ時点で、
**#919 が求める是正（SX-1 の `dist` 除外解除、SX-2 の BOM 判定つき読み込み、両方の変異試験）は
`scripts/check-backend-libraries.js` に既に実装済みであることが判明した**。

- `SKIP_DIRS`（57〜67 行目）: `dist` を含まない。コード内コメントが `#919 / SX-1` を名指しし、
  実測（追跡下の `dist/` 配下ファイル 0 件・走査時間の増分がノイズ以下）を根拠に載せている。
- `readSource()`（69〜92 行目）: BOM を見て復号する（UTF-16LE / UTF-16BE / UTF-8 BOM / BOM なし）。
  コード内コメントが `#919 / SX-2` を名指ししている。
- `--self-test`（1242〜1276 行目）: `★ SKIP_DIRS に dist を入れない（#919 / SX-1…）` ／
  `★ readSource: UTF-16LE を復号する（#919 / SX-2…）` を含む固定ケースが既に存在する。
- `IADR-0246`（Accepted）の決定 1 が SX-1 / SX-2 を「塞ぐ」と記録し、決定の内容は現在のコードと一致する。

**したがって本作業は「是正する」ではなく「是正済みであることを自分の手で検証し直し、
検証手順と実測を記録として残す」作業になった。** 是正コード自体は変更していない
（`scripts/check-backend-libraries.js` に diff は無い。下記「検証」参照）。

一方で `scripts/README.md` の該当行（26 行目）は `--self-test` の件数を **108 件**と記載しており、
現状の **117 件**と食い違っていた（stale）。これは担当範囲内（「`scripts/README.md` の該当行」は
編集許可対象）であり、本作業で是正した。

## 実測（自分で走査した結果。担当指示どおり `head` / `sed` で切っていない）

### dist/ 配下の実在確認

```
$ git ls-files | grep -E '(^|/)dist/' | wc -l
0
$ git ls-files | grep -E '(^|/)dist/' | grep -E '\.(cs|csproj|props|targets)$' | wc -l
0
```

追跡下に `dist/` という名のディレクトリは 1 件も無い（拡張子を問わず 0 件）。

### UTF-16LE の .cs の実在確認（BOM で分類）

追跡下の全 `.cs`（534 件）を Python でバイナリ先頭 3 バイトから分類した。

```
total_cs 534
utf16le 0 []
utf16be 0 []
utf8bom 18
nobom 516
```

UTF-16LE / UTF-16BE の `.cs` は 0 件。両方の不可視領域とも**現存 0 件**であることを自分の走査で
再確認した（IADR-0246 の実測値 498 件・utf16 0 件・utf8bom 15 件という記録と対応するが、その後
リポジトリが成長し現在は 534 件・utf8bom 18 件になっている。数値そのものは再走査で確認済み）。

## 変異試験（是正前 / 是正後の対照。担当指示どおり実施）

本番ファイル（`scripts/check-backend-libraries.js`）は既に是正済みのため、「是正前」の挙動を
再現するために、**スクラッチパッド上にのみ**是正前ロジック（`SKIP_DIRS` に `dist` を戻し、
`readSource` を `fs.readFileSync(abs, 'utf8')` 固定へ戻したコピー）を作り、本番ファイルには
一切手を加えずに対照実験を行った（作業ツリーの `check-backend-libraries.js` に diff が無いことは
下記「検証」で確認済み）。

合成ツリー（一時ディレクトリ）:

- SX-1: `src/platform/backend/Sample/Sample.Api/dist/Sneak.cs`（`using MassTransit;` を含む。
  `dist/` という名のディレクトリ配下に置く）
- SX-2: `src/platform/backend/Sample/Sample.Api2/Sneak16.cs`（`using MassTransit;` を含む本文を
  UTF-16LE + BOM でエンコード）
- 対照: `src/platform/backend/Sample/Sample.Api3/Sneak.cs`（SX-1 と同内容だが `dist/` の外に置く）

`scanTree()` を直接呼んで `current[<csproj>]` の中身を比較した。

**是正前（`dist` を `SKIP_DIRS` に含み・`readSource` を `utf8` 固定にしたコピー）**:

```
SX-1 (dist/ 配下): []          ← 素通り（未検出）
SX-2 (UTF-16LE):   []          ← 素通り（未検出）
control (dist の外・旧ロジック): ["MassTransit"]   ← 検出される（対照。旧ロジックが壊れているわけでは
                                                       なく、dist/ という名前とエンコーディングだけが
                                                       穴だったことを示す）
```

**是正後（現状の本番ファイル）**:

```
SX-1 (dist/ 配下): ["MassTransit"]   ← 検出
SX-2 (UTF-16LE):   ["MassTransit"]   ← 検出
```

是正前は SX-1・SX-2 とも素通り、対照（同内容を `dist/` の外へ）は旧ロジックでも検出される
——つまり「旧ロジック全体が壊れている」のではなく「`dist` という名前」「UTF-16LE エンコーディング」
という 2 点だけが不可視領域だったことを実測で確認した。是正後は両方とも検出される。

一時ファイル（合成ツリー・是正前ロジックのコピー）はスクラッチパッド
（`/tmp/claude-0/.../scratchpad/issue919/`）にのみ作成し、本リポジトリの作業ツリーには一切置いていない。

## 是正内容（本作業で実施した変更）

コード（`scripts/check-backend-libraries.js` / `scripts/backend-library-baseline.json`）は
**変更なし**（既に是正済みのため）。

`scripts/README.md` の該当行のみ是正した:

- `--self-test` の件数表記を **108 件 → 117 件**（stale の是正）。
- SX-1（`dist/` 除外）・SX-2（UTF-16LE 復号）が #919 / IADR-0246 で塞がれたこと、
  値レベル（#920）は意図して塞いでいないことを 1 文で追記し、コードコメント・IADR との対応を
  README からも辿れるようにした。

## 検証

```bash
cd /home/user/microservices-platform
node scripts/check-backend-libraries.js               # EXIT=0（新規混入 0 件、baseline 済み残件 11 件）
node scripts/check-backend-libraries.js --self-test    # 117 件 OK（#919 SX-1/SX-2・#920 の固定ケース含む）
node scripts/check-doc-links.js
node scripts/check-adr-numbering.js
git status --short                                     # check-backend-libraries.js に diff が無いことを確認
```

結果は担当報告（本体レポート）に記載。

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| SX-1（`dist/` 配下の `.cs`）・SX-2（UTF-16LE の `.cs`）それぞれについて実測する | ✅ 追跡下 0 件・0 件（両方とも実測で再確認） |
| `SKIP_DIRS` の除外方針（パス限定か除外解除か）を判断する | ✅ 既に IADR-0246 決定 1 のとおり `dist` を `SKIP_DIRS` から除外（除外解除）済み。追加の判断は不要 |
| BOM 検出等で UTF-16LE を読めるようにするか受容するかを判断する | ✅ 既に `readSource()` が BOM 判定つきで復号する形で実装済み |
| 変異試験で「実際に検出できるようになったこと」を固定する | ✅ `--self-test` に固定ケースが既に在り、加えて本作業で独立に前後比較を実施し同じ結論を得た |
| `--self-test` が壊れていない（117 件、#920 名指しケース含む） | ✅ 117 件 OK |
| ドキュメント（`scripts/README.md`）が現状と整合する | ✅ 108 → 117 に是正 |

## 計画書との差異

なし。issue #919 のスコープに対する是正はコード側で既に完了しており、本作業はその検証と
ドキュメントの stale 是正のみを行った。

## 申し送り

- issue #919 は本文の 3 項目（SKIP_DIRS の判断・BOM 判定の判断・変異試験の固定）すべてが
  コード上は既に満たされている。担当報告でその旨を明記し、issue のクローズ判断は
  統括側・issue 起票者に委ねる。
- `scripts/README.md` 以外にも「108 件」を記載している文書を母集合走査（`grep -rn "108 件"`）で洗い出した。
  `.ai-context/specs/20260822_issue-455_wolverine-shared-helper.md:186` は本検査器の自己試験の**当時の
  値**（78 → 108 件）を記録した凍結記録であり、`traceability.repo.md`「凍結の射程」により
  `.ai-context/specs/` の本文プロズは書き換えない対象——是正しない。残り 3 件
  （`20260817_issue-841_admin-entrypoint-https.md` / `20260816_issue-755_planning-pin-4d6a7d6-catchup.md` /
  `IADR-0201` / `IADR-0192`）は `localhost:50000` の参照件数や `check-kit-sync.js` の Windows パス偽陽性
  件数であり本検査器とは無関係。是正が要るのは `scripts/README.md` のみで、本作業で完了している。
