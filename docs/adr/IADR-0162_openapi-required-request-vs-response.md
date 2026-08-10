---
title: IADR-0162 openapi の required は要求側と応答側で規則を分ける
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0122
  - IADR-0132
  - IADR-0159
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
---

# IADR-0162: `required` の規則を要求側と応答側で分ける（#658）

- 状態: Accepted
- 日付: 2026-08-10
- 決定者: claude（実装）

## 起点・関連

- **NFR**（契約と実装の一致）。実装 issue: **#658**
- 作業仕様書: [20260810_issue-658](../specs/20260810_issue-658_openapi-required-drift.md)
- 前提: [[IADR-0132]]（応答側の規則）・[[IADR-0159]]（検査器）・[[IADR-0122]]（C# が契約の正本）

## コンテキストと課題

[[IADR-0159]] 決定 3 の検査器は、[[IADR-0132]] の論点 A1 ＋ B1 を
**`components.schemas` の全体へ無差別に**当てていた。その結果 **10 件が不一致**として出て、
ラチェット（`requiredMismatchBaseline`）へ据え置かれていた。

**#658 の指示どおり 1 件ずつ実データを開いたところ、是正すべき乖離は 3 件だけだった。**

| 内訳 | 件数 | 実体 |
| --- | --- | --- |
| 応答スキーマの `required` 漏れ | **3** | **本物の乖離**（`ConversionJobDto`） |
| 要求スキーマの「既定値つきメンバー」 | **5** | **検査器の規則が広すぎた**（決定 1） |
| 要求スキーマの「嘘の必須」 | **2** | **意図的な差**（決定 2） |

## 決定 1: **B1 は応答スキーマの規則である。要求側では既定値つきメンバーを `required` に要求しない**

[[IADR-0132]] の**表題そのものが「応答スキーマの `required` は…」**であり、決定 2 の系は明示している:

> **要求スキーマの `default` は落とさない** —— `AnalysisAskRequest.topK` 等は
> 「送らなければこの値になる」という本来の意味で機能しており、**応答側とは別の話である**。

**B1 の理屈は応答側でしか成り立たない。** B1 は
「C# の既定値はシリアライズの省略とは無関係で、`System.Text.Json` は必ず出力する」と言う
—— これは**サーバが書き出す**側の話である。要求側では逆に、
**C# の既定値こそが「クライアントが送らなかったときの値」**であり、
`required` を足すと**送信を強制して既定値の意味を殺す**。

したがって規則を分ける:

| スキーマの到達性 | `missing-in-required` の判定 |
| --- | --- |
| **要求側にのみ**到達する | 非 null **かつ既定値を持たない**とき |
| 応答側に到達する／どちらからも到達しない | 従来どおり非 null なら常に（A1 ＋ B1） |

`wrongly-required`（nullable なのに `required`）は**両側で見る** —— 嘘はどちらでも嘘である。

**到達性は宣言せず `openapi.yaml` から導く。** `paths:` を歩いて `requestBody:` /
`responses:` のどちらのサブツリーで `$ref` が現れたかを記録し、
`components.schemas` の `$ref` グラフで**推移閉包**を取る。
**入れ子を推移的にたどることが要る** —— `AnalysisDataRange` はどの `requestBody:` からも
直接参照されておらず、`AnalysisTaskRequest.range` の下にぶら下がっているだけである。

**安全側の既定を選ぶ。** 緩む方向へ倒れるのは「**要求側にのみ**到達する」と確定したときだけで、
応答側に 1 度でも到達すれば従来どおりであり、どちらからも到達しないものも従来どおりである
（[[IADR-0132]] 決定 5「`/bff/` から到達しない 5 個にも `required` を入れる」と整合）。
実測では要求側にのみ到達するのは **23 スキーマ**、どちらからも到達しないものは **0** である。

## 決定 2: **`UpdateDataSourceRequest` の 2 件は「嘘の必須」ではない。外さない**

#658 の本文はこの 2 件を「**外すのは緩和なので安全**」に分類していた。**誤りである。**

```yaml
UpdateDataSourceRequest:
  description: |
    **`config` / `defaultAttributes` は必須である**（#627 の AI レビュー 🟡）——
    省略を許すと「送り忘れ」で秘密が黙って消える。消すときは `{}` を明示する。
  required: [name, sourceType, connectionUri, config, defaultAttributes]
```

**C# が nullable なのは、省略を検知して 400 で拒否するための手段である。**
`required` を外すと **#627 で塞いだ「送り忘れで秘密が黙って消える」経路が契約上また開く**。

「C# の非 null 性が唯一の権威」という [[IADR-0122]] 由来の前提は、**ここでは成り立たない** ——
nullable であることが「省略された」を表現する手段だからである。

**この理由は契約の文言だけでなく実装のテストが裏付けている**（宣言の根拠を推測で書かない）——
`DataSourceUpdateEndpointTests.Put_OmittingReplaceableField_IsRejected` が
`[Theory]` ＋ `[InlineData("config")]` / `[InlineData("defaultAttributes")]` で
**省略時に `BadRequest` を返し、かつ実体が無傷であること**を主張している。
**契約だけが守っている状態ではない。**

## 決定 3: 宣言は **`requiredExceptions`**（`entries` を使わない）

#658 の受け入れ基準 2 は「`allowlist` の `entries` へ理由つきで宣言する」と書いていた。**採らない。**

**`entries` は広すぎる。** `entries` は `required` の判定だけでなく
**プロパティ集合の突合ごと**その項目を検査から外す。
`UpdateDataSourceRequest.config` を `entries` に入れると、
**`config` が契約から丸ごと消えても検出されなくなる。**

`requiredExceptions` は `required` の判定**だけ**を外し、
`missing-in-openapi` / `missing-in-csharp` は見続ける。

| 宣言 | 外れるもの | 使いどころ |
| --- | --- | --- |
| `entries` | プロパティ集合 ＋ `required` | **契約へ出さないのが正しい**（`SearchRequest.scope`） |
| **`requiredExceptions`** | `required` の判定のみ | **プロパティは双方に在るが、必須の判定だけ意図的に違う** |

## 決定 4: ラチェット（`requiredMismatchBaseline`）は**空配列でも残さず撤去する**

10 件が 0 件になった。**空の配列を残すと「また据え置いてよい」と読める。**
据え置くべき債務はもう無く、意図的な差は決定 3 の宣言が理由つきで持つ。
`--self-test` と `scripts.repo.test.js` が**キーの復活そのもの**を落とす。

## 理由

- 決定 1 は [[IADR-0132]] の**射程を正しく読み直しただけ**であり、新しい方針ではない。
  検査器が ADR より広く効いていたのを ADR に合わせた。
- 決定 1 を**宣言（allowlist）ではなく規則**で実現したのは、
  要求スキーマの既定値つきメンバーが**例外ではなく正しい設計**だからである。
  **例外表に正しい設計を並べると、次に読む人が「これらは直すべき債務だ」と読む。**
  実際、#658 の本文がそう読んでいた。
- 決定 2・4 は「**検査器の出力をそのまま債務として信じない**」という #525 の教訓の続きである。
  #525 では**パーサ欠陥**による偽陽性 10 件を債務として据え置きかけた。
  今回は**規則の適用範囲**による偽陽性 5 件と、**意味の取り違え**による 2 件だった。
  **手口は違うが、根は同じである。**

## 結果

### 是正した 3 件（応答側）

`ConversionJobDto.diagramsCoded` / `diagramsRetained` / `hasCorrection` を `required` へ足した。
[[IADR-0132]] 決定 1（A1）＋決定 2（B1）がそのまま当たる典型例である
（3 件とも既定値つきだが、応答本文には必ず出る）。

**決定 2 の系（`required` と `default` の同居禁止）に該当は無かった** ——
この 3 プロパティは `default` を持たない（実データで確認）。

| 対象 | 結果 |
| --- | --- |
| `pnpm run codegen` の再生成差分 | `bff.schemas.ts` 3 行（`?:` → `:`）＋ `conversion.faker.ts` 2 行 |
| `pnpm run typecheck` | 緑 |
| ラチェット | **10 → 0（撤去）** |

### 変異試験（規則が効いていること・緩めすぎていないこと）

| 変異 | 実測 |
| --- | --- |
| M1: `ConversionJobDto.diagramsCoded` を `required` から外す | **落ちる** |
| M2: `requiredExceptions` から `UpdateDataSourceRequest.config` を消す | **落ちる** |
| **M3: `SearchRequest` の `int TopK = 10` から既定値を外す** | **落ちる** |
| M4: 全スキーマを応答側扱いにする（到達性を無効化） | 要求側 5 件が再び報告される（自己試験で固定） |
| M5: `requiredExceptions` の `reason` を空にする | `--self-test` が落ちる |

**M3 が要である。** 決定 1 は検査を緩める方向なので、
**緩めすぎていないことを主張する変異が無ければ「何も見ない判定器」と区別が付かない。**

### 検出しないこと（正直に書く）

- **`nullable: true` と `required` の同居の是非は見ない。**
  `UpdateDataSourceRequest.config` は `required` かつ `nullable: true` であり、
  契約は「キーは在れ、値は null でよい」と読めるが、サービスは null を 400 で拒否する。
  **契約の書き方としては別の論点**であり、申し送る。
- **型の不一致**（[[IADR-0159]] 決定 3 が見ないと決めている）。
- **到達性は行内の `$ref` だけを見る。** 実データを引いた結果は以下である（**「該当が無い」で済ませない**）:

  | 形 | 実データ | 扱い |
  | --- | --- | --- |
  | `allOf` | **1 箇所**（`AttributeValuesResponse.dictionary`） | **正しく取れている** —— `allOf: [{ $ref: "…" }]` が 1 行に収まっており、行内の `$ref` を拾うため `TagDictionaryResponse` は応答側と判定される（実測） |
  | `oneOf` / `anyOf` | **0 箇所** | —— |
  | `requestBody` 直下のインライン `schema`（`$ref` でない） | **0 箇所** | —— |

  **行をまたぐ `allOf` / `oneOf` は追わない。** いま無いだけで、書けば静かに漏れる
  （応答側の合成を見落とすと**緩む方向**へ倒れる）。**申し送る。**

## 申し送り

- **`nullable: true` ＋ `required` の同居**（上記）。契約の読み手にとって意味が曖昧である。
- **`requiredExceptions` を増やすときは、契約と C# のどちらが正しいかを先に決めること。**
  `scripts.repo.test.js` が件数の上限（2）を持っており、増やすには意図的な操作が要る。
- **行をまたぐ `allOf` / `oneOf` を書いたら到達性が漏れる。** いま実データには 1 行に収まる `allOf` が
  1 箇所あるだけで正しく取れているが、**折り返した瞬間に静かに緩む**（応答側の合成を見落とす向き）。
  同型が 2 回起きたら到達性の解析を YAML パーサへ載せ替えること
  （`CLAUDE.md`「検査器・規約の追加は同型の事故が 2 回起きたら」）。

## 関連

- Supersedes: なし（[[IADR-0132]] は**変更していない**。射程を読み直しただけである）
- Superseded by: なし
