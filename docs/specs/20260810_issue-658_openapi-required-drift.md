---
title: 作業仕様書 — openapi.yaml の required と C# 非 null 性の乖離 10 件を是正する（#658）
type: work-spec
status: draft
related_ids:
  - NFR
  - IADR-0132
  - IADR-0159
  - IADR-0122
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
---

# 作業仕様書: openapi の required 乖離（#658）

## 起点

- **NFR**（契約と実装の一致）／[IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md)／
  [IADR-0159](../adr/IADR-0159_openapi-dto-drift-checker.md)
- 起点 issue: **#658**（出所は #525 / PR #657 で `required` 検査を全数へ当てた結果）

## 母集合（自分で引き直した）

**issue 本文は起点であって結論ではない。** #658 自身が
「**1 件ずつ `openapi.yaml` の実データを開いて**確かめてから着手する」と書いている。実際に開いた結果、
**10 件のうち 7 件は是正すべき乖離ではなかった。**

### 軸 1: issue 番号で引く

```console
$ git ls-files -z | xargs -0 grep -ln '#658'
scripts/openapi-dto-drift-allowlist.json
docs/adr/IADR-0132_openapi-required-from-csharp-nullability.md
docs/adr/IADR-0159_openapi-dto-drift-checker.md
docs/specs/20260810_issue-525_access-scope-granted.md
```

**引き継ぎは 4 ファイルにある**（是正先の宣言・2 つの ADR のフォローアップ・#525 の申し送り）。

| ファイル | 本 PR での扱い |
| --- | --- |
| `scripts/openapi-dto-drift-allowlist.json` | **追随させる**（ラチェット撤去・`requiredExceptions` 新設） |
| `docs/adr/IADR-0132_…` | **追随させる**（フォローアップ 1 へ回収の追記） |
| `docs/adr/IADR-0159_…` | **追随させる**（決定 4 と申し送りへ回収の追記） |
| **`docs/specs/20260810_issue-525_access-scope-granted.md`** | **★ 追随させない**（下記） |

> **★ #525 の作業仕様書は書き換えない。** `.claude/rules/traceability.md` が
> 「**確定済み（過去 PR の）`docs/specs/` は書いた時点の記録であり、後から注記を足すのは記録の改竄にあたる**」
> と定めている。同仕様書の「是正は #658」という申し送りは**書いた時点で正しく、いまも履歴として正しい**
> —— 回収した事実は本仕様書と [[IADR-0159]] / [[IADR-0132]] に残す。
> **（母集合の規則 6「除外したものとその理由を書く」の適用。**
> 初版はこの行を書かずに「4 つすべてを追随させる」とだけ書いており、
> **本 PR のレビュー 🟡 で指摘された。規約を作った当人が自己適用を落としていた。）**

### 軸 2: 計画書の現状

`06_technical/13_frontend-stack.md` は SPA スタックを定めるが、**`required` の起こし方は定めていない**。
これは実装側の決定であり、正本は IADR-0132 である。**計画への環流は不要**（引いたうえで書いている）。

### 軸 3: 検査器の生出力（baseline を空にして全数を取る）

```console
$ node -e "…requiredMismatchBaseline=[]…" && node scripts/check-openapi-dto-drift.js
[check-openapi-dto-drift] 契約（openapi.yaml）と C# DTO の不一致 10 件を検出しました:
```

**10 件。** issue 本文の一覧と一致する（起点は正しい）。**が、内訳の判定は違う。**

### 軸 4: **1 件ずつ実データを開く**（#658 の指示。ここで判定が覆った）

| # | 項目 | C# の実体 | 契約の実体 | 判定 |
| --- | --- | --- | --- | --- |
| 1-3 | `ConversionJobDto.diagramsCoded` / `diagramsRetained` / `hasCorrection` | 非 null・既定値つき | 応答スキーマ・`required` に無い | **本物の乖離** |
| 4 | `SearchRequest.topK` | `int TopK = 10` | 要求スキーマ | **偽陽性** |
| 5 | `AnalysisTaskRequest.taskType` | `= AnalysisTaskType.Analyze` | 要求スキーマ | **偽陽性** |
| 6 | `AnalysisDataRange.topK` | `int TopK = 8` | 要求スキーマ | **偽陽性** |
| 7 | `CompletionApiRequest.maxTokens` | `int MaxTokens = 4096` | 要求スキーマ | **偽陽性** |
| 8 | `EmbedApiRequest.purpose` | `= EmbedPurpose.Index` | 要求スキーマ | **偽陽性** |
| 9-10 | `UpdateDataSourceRequest.config` / `defaultAttributes` | `Dictionary<string,string>? = null` | 要求スキーマ・`required` に在る | **意図的な差**（下記） |

## 判定が覆った 2 つの理由

### 理由 1: **IADR-0132 論点 B の B1 は応答スキーマの規則である**（4〜8 の 5 件）

IADR-0132 の**表題そのものが「応答スキーマの `required` は…」**であり、決定 2 の系は明示している:

> **要求スキーマの `default` は落とさない** —— `AnalysisAskRequest.topK` 等は
> 「送らなければこの値になる」という本来の意味で機能しており、**応答側とは別の話である**。

**B1 の理屈は応答側でしか成り立たない。** B1 は「C# の既定値はシリアライズの省略と無関係で、
`System.Text.Json` は必ず出力する」と言う —— これは**サーバが書き出す**側の話である。
要求側では逆で、**C# の既定値は「クライアントが送らなかったときの値」そのもの**であり、
`required` を足すと**送信を強制して既定値の意味を殺す**。

`check-openapi-dto-drift.js`（#525）は **A1＋B1 を全スキーマへ無差別に当てていた**。
5 件は債務ではなく**検査器の規則が広すぎた**結果である。

> **★ 同型の失敗が #525 でも起きている。** `DataSourceDto` の 10 件は
> 「`required` を折り返しで読めない」パーサ欠陥による偽陽性で、**債務として据え置きかけた**。
> 今回は**規則の適用範囲**による偽陽性である。**手口は違うが、
> 「検査器の出力をそのまま債務として信じた」点は同じ**である。

### 理由 2: **`UpdateDataSourceRequest` の `required` は #627 の是正そのもの**（9-10 の 2 件）

issue 本文はこの 2 件を「**嘘の必須。外すのは緩和なので安全**」に分類していた。**これは誤りである。**

```yaml
# docs/api/openapi.yaml
UpdateDataSourceRequest:
  description: |
    **`config` / `defaultAttributes` は必須である**（#627 の AI レビュー 🟡）——
    省略を許すと「送り忘れ」で秘密が黙って消える。消すときは `{}` を明示する。
  required: [name, sourceType, connectionUri, config, defaultAttributes]
```

**C# が nullable なのは、省略を検知して 400 で拒否するための手段である。**

```csharp
// **Config / DefaultAttributes を省略した要求はサービスが 400 で拒否する**（AI レビュー 🟡 / #627）。
// PUT は全置換なので、省略を受理すると「送り忘れ」で秘密（apiToken 等）が黙って消える。
public record UpdateDataSourceRequest(…, Dictionary<string, string>? Config = null, …);
```

**`required` を外すと #627 で塞いだ「送り忘れで秘密が黙って消える」経路が契約上また開く。**
「C# の非 null 性が唯一の権威」という検査器の前提が、**ここでは成り立たない** ——
nullable であることが「省略された」を表現する手段だからである。

## 判断

### 判断 1: 検査器を**要求側 / 応答側で分ける**（4〜8 を宣言ではなく規則で消す）

5 件を allowlist へ 1 件ずつ書くこともできる（#658 の受け入れ基準 2 の字面）。**採らない。**

- **同じ形の項目が今後増えるたびに宣言が要る。** 要求スキーマの既定値つきメンバーは
  「省略可」が**正しい設計**であって例外ではない。**例外表に正しい設計を並べると、
  次に読む人が「これらは直すべき債務だ」と読む。**
- **`entries` は広すぎる。** `entries` は `required` の判定だけでなく
  **プロパティ集合の突合ごと**その項目を検査から外す（`findDrift` の
  `allowed.has(key)` は `missing-in-openapi` / `missing-in-csharp` にも効く）。
  `topK` を `entries` に入れると、**`topK` が契約から丸ごと消えても検出されなくなる。**

**規則で消す。** 到達性を `openapi.yaml` から機械的に導く:

1. `paths:` を歩き、`requestBody:` / `responses:` のどちらのサブツリーで `$ref` が現れたかを記録する
2. `components.schemas` 内の `$ref` グラフで**推移閉包**を取る（`AnalysisDataRange` は
   `AnalysisTaskRequest` の下にぶら下がる入れ子なので、直接参照だけでは要求側と判らない）
3. **要求側にのみ到達する**スキーマでは、`missing-in-required` を
   「非 null **かつ既定値を持たない**」に限定する

**安全側の既定を選ぶ**: 応答側に 1 度でも到達すれば従来どおり B1 を当てる。
どちらからも到達しないスキーマも**従来どおり**当てる（IADR-0132 決定 5 と整合）。
**緩める方向へ倒れるのは「要求側にのみ到達する」と確定したときだけ**である。

実測: 要求側にのみ到達するのは **23 スキーマ**、どちらからも到達しないものは **0** である。

### 判断 2: `requiredMismatchBaseline` を**廃止して `requiredExceptions` へ置き換える**（9-10）

9-10 は**債務ではなく意図的な差**なので、ラチェットではなく**理由つきの宣言**が正しい置き場である。
ただし前述のとおり `entries` は広すぎるので、**`required` の判定だけを外す**第 3 の宣言を足す:

```json
"requiredExceptions": [
  { "schema": "UpdateDataSourceRequest", "property": "config", "reason": "…#627…" }
]
```

`entries` と違い、**プロパティ集合の突合は生きたまま**である ——
`config` が契約から消えれば `missing-in-openapi` で落ちる。

`requiredMismatchBaseline` は **0 件になるので配列ごと消す**。
ラチェットは「減らす一方の債務」を置く場所であり、**空の配列を残すと
「また据え置いてよい」と読める**。据え置くべき債務はもう無い。

### 判断 3: 応答側 3 件（`ConversionJobDto`）は `required` を足す

IADR-0132 決定 1（A1）＋決定 2（B1）がそのまま当たる。**費用は #525 が実測済み**
（生成物 5 行・`typecheck` 緑）だが、**測り直して確かめる**——#525 の実測は
`411d042` 時点であり、以後 openapi は複数回変わっている。

**決定 2 の系も当てる**: `required` に入れたプロパティから `default` を落とす
（応答側で `required` と `default` が同居すると契約が自己矛盾する）。**該当の有無を実データで確かめる。**

**［実測結果］宣言したことの結果をここに書く**（結果を ADR にだけ置くと、仕様書の読み手が
「確かめると書いてあるが、確かめたのか」を追えない）:

| 確かめたこと | 結果 |
| --- | --- |
| 再生成差分（`pnpm run codegen`） | `bff.schemas.ts` **3 行**（`?:` → `:`）＋ `conversion.faker.ts` **2 行**。#525 の実測（5 行）と一致 |
| `pnpm run typecheck` | **緑** |
| 決定 2 の系（`default` との同居） | **該当なし** —— 当該 3 プロパティは `default` を持たない |

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#658） | 本 PR での確かめ方 |
| --- | --- | --- |
| 1 | 応答側 3 件へ `required` を足し再生成差分をコミット | `pnpm run codegen` の差分 ＋ `pnpm run typecheck` |
| 2 | 要求側 5 件は呼び出し側から確認してから決める | **確認した結果、規則の側を直した**（判断 1） |
| 3 | `UpdateDataSourceRequest` の 2 件を `required` から外す | **外さない。** 判定が覆った（理由 2）。`requiredExceptions` へ宣言する |
| 4 | baseline から片づけた分を消す | **配列ごと消す**（判断 2） |
| 5 | `scripts.repo.test.js` の上限検査を引き下げる | 「baseline は存在しない」＋「`requiredExceptions` は理由を持つ」へ置き換える |
| 6 | 変異試験 | 下記 |

### 変異試験

| 変異 | 期待 | 確かめ方 | 実測 |
| --- | --- | --- | --- |
| M1: `ConversionJobDto.diagramsCoded` を `required` から外す | 落ちる | **ファイル変異** | **落ちた** |
| M2: `requiredExceptions` から `UpdateDataSourceRequest.config` を消す | 落ちる（宣言が効いていることの側） | **ファイル変異** | **落ちた** |
| M3: **要求側の既定値を C# から外す**（`int TopK = 10` → `int TopK`） | **落ちる**（緩めすぎていない側） | **ファイル変異** | **落ちた** |
| M4: 到達性判定を無効化（全スキーマを応答側扱い） | 5 件が再び報告される | **★ ファイル変異では回していない** | 自己試験「応答側では既定値があっても required を要求する」で固定 |
| M5: `requiredExceptions` の `reason` を空にする | `--self-test` が落ちる | **ファイル変異** | **落ちた** |

**M3 が要である。** 判断 1 は検査を緩める方向なので、**緩めすぎていないことを主張するテストが要る。**

> **★ M4 だけ確かめ方を変えた（宣言と実施の差を隠さない）。** 到達性は
> `openapi.yaml` 全体から導く値なので、「無効化する」変異は検査器のコードを一時的に壊す形にしかならず、
> **元へ戻し忘れると気づけない**。代わりに `findDrift` へ空の `requestOnly` を渡す自己試験を置き、
> **同じ主張（応答側では B1 が当たる）を恒久的に固定した**。
> 変異は 1 回きりだが自己試験は毎回走るので、**この項目については後者の方が強い**。

## 射程外

- **型の不一致**（IADR-0159 決定 3 が見ないと決めている）。
- **`nullable: true` と `required` の同居の是非**。`UpdateDataSourceRequest.config` は
  `required` かつ `nullable: true` である。「キーは在れ、値は null でよい」と読めるが、
  サービスは null を 400 で拒否する。**契約の書き方としては別の論点**であり、
  本 PR は `required` の突合だけを扱う。**申し送る。**
- **パスと C# 端点の突合**（IADR-0156 / #647 の領域）。
