---
title: issue #1120 --extract-media の一時パスを本文から消し、抽出図を元の位置へ戻す
type: spec
status: draft
related_ids:
  - FR-12
  - UC-06
  - SC-07
  - ADR-0012
  - IADR-0008
  - IADR-0154
  - IADR-0298
  - IADR-0320
  - IADR-0352
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md
  - planning:projects/microservices-platform/04_workflows/03_conversion-flow.md
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
---

# 仕様書: `--extract-media` の一時パスを本文から消し、抽出図を元の位置へ戻す

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-12（原本の正規化変換）
- ユースケース（UC）: UC-06（原本を正規化変換する。代替フロー＝人手補正）
- 画面（SC）: SC-07（変換ジョブ管理。人手補正の 2 ペイン）
- 関連 ADR: ADR-0012（本文は pandoc・図は LLM・不可分は画像保持）、ADR-0014（オブジェクトストレージ）
- 実装 issue: #1120（出所は #1097 の実測）

## 目的・背景

pandoc の `--extract-media` は**本文中の画像参照を一時ディレクトリの絶対パスへ書き換える**。
`PandocConversionService` はその標準出力をそのまま `BodyConversionResult.Markdown` として返し、
`NormalizationService` はそれを土台に**図を末尾へ append** して保管する。結果:

1. 変換直後に `finally` が消す `/tmp/conv-XXXXXXXX/media/...` への**壊れた参照が保管物に残る**
2. 同じ図が**本文中（壊れた一時パス）と末尾（正しい `storage://`）の 2 度**現れる
3. 図が**元の位置から末尾へ寄る**ため「どの段落の図か」が失われる

#1097 で pandoc を実行時イメージへ入れて初めて観測された（従前は 1 度も実走していなかった）。

## 母集合（自分で走査した結果）

**issue の主張を転記していない。** 以下は本作業で引いた走査結果である。

### 走査 1: `--extract-media` の出力パスが本文へ流れる経路

`grep -rn "extract-media" --include=*.cs src` ＋ `grep -rn "BodyConversionResult|IBodyConverter|\.Markdown\b" --include=*.cs src/knowledge src/platform`

| # | 箇所 | 役割 | 本作業で触るか |
| --- | --- | --- | --- |
| 1 | `PandocConversionService.RunPandocAsync`（`Infrastructure/ExternalServices/`） | `--extract-media <mediaDir>` を渡し、**stdout をそのまま返す**。ここが一時パスの発生点 | **触る** |
| 2 | `PandocConversionService.ExtractFigures` | `mediaDir` 配下のファイルを `ExtractedFigure` へ写す。**ファイルパスを捨てている**ため本文との対応が付かない | **触る** |
| 3 | `PandocConversionService.ConvertAsync` の `finally` | `Directory.Delete(mediaDir, recursive: true)`。**保管前に消える**ことの根拠 | 触らない |
| 4 | `IBodyConverter` / `BodyConversionResult`（`Domain/Ports/`） | 本文と図の受け渡し契約。**署名は変えない** | 触らない |
| 5 | `NormalizationService.NormalizeAsync`（`Features/ConversionJobs/Normalize/`） | `new StringBuilder(body.Markdown)` を土台に**図を末尾へ append** し `SaveMarkdownAsync` する。**唯一の実消費者** | **触る** |
| 6 | `Program.cs:56` | `AddSingleton<IBodyConverter, PandocConversionService>()`。配線のみ | 触らない |

**`body.Markdown` を読む実装は `NormalizationService` ただ 1 つである**（陽性対照: 同じ走査で
`WikiJsGraphQlClient.cs:204,234` と `BffDocumentEndpointTests.cs:145` の `.Markdown` が引っ掛かった。
前者は Wiki.js ページ DTO の別プロパティ、後者は BFF の DTO であり、いずれも
`BodyConversionResult` とは無関係＝**走査は空振りしていない**）。

### 走査 2: 図を二重化する箇所

`NormalizationService.NormalizeAsync` の図ループの 2 分岐（コード化成功＝コードブロック append /
画像保持＝`FigureMarkdown.ImageEmbed` append）**のみ**。本文側の参照を消さずに末尾へ足すため、
本文に参照が残っている図はすべて二重になる。

### 走査 3: 保管された本文の下流（壊れた参照が波及する先）

- `DocumentNormalized` の購読（チャンク化・埋め込み＝索引本文）
- Wiki 同期（Wiki.js ページ本文）
- BFF `GET /bff/documents/{id}/content`
- 人手補正 `FigureCorrectionService`（`TryGetMarkdownAsync` で読み戻し `FigureMarkdown.TryReplaceImageWithCode` で置換）

→ 下流は**本文の綴りに依存する**。とくに人手補正は `![{figureId}]({imageUri})` の**完全一致**を目印に
しており（IADR-0154 決定 3）、**この形を壊すと補正が静かに空振りする**。

## 再現（着手前・陰性対照）

ローカルに pandoc が無いため、**稼働 k3s の conversion-service pod（pandoc 3.1.3）で実走**した。
Pod は再起動していない（`/tmp` への書き込みのみ）。

```console
$ kubectl exec -i -n microservices-platform conversion-service-696959d9bd-tzwfj \
    -c conversion-service -- sh -s < repro1120.sh
=== docx built: 10476 bytes ===
=== pandoc -f docx -t gfm --extract-media /tmp/repro-1120-a/conv-REPRO original.docx ===
# 四半期レポート

本文の段落である。

<img src="/tmp/repro-1120-a/conv-REPRO/media/rId20.png" alt="構成図" />

## まとめ

末尾の段落。
=== extracted media files ===
/tmp/repro-1120-a/conv-REPRO/media/rId20.png
=== /tmp reference count in body ===
1
```

**本文に一時パスが 1 件残る**（＝`NormalizationService` が末尾へ `![fig-1](storage://…)` を足すと図は 2 度出る）。

### pandoc が出す参照の形（同 pod で実測。regex の設計根拠）

| 入力 | 出力の形 |
| --- | --- |
| html / gfm | `![構成図](<mediaDir>/fig.png)` — **Markdown 画像形** |
| docx（属性つき） | `<img src="<mediaDir>/media/rId20.png" style="…"\nalt="…" />` — **HTML 形。属性が改行を挟む** |
| docx（図 2 枚） | `<img src="…/rId20.png" …>` と `<img src="…/rId23.png" …>` が**元の位置**に並ぶ |

→ **2 つの構文形**を扱う必要がある。HTML 形は**タグが改行をまたぐ**。

## 対象範囲

- 対象: `src/knowledge/backend/Services/ConversionService/**`（宣言ファイル領域）
- 対象外:
  - `IBodyConverter` / `BodyConversionResult` の**署名変更**（不要）
  - 人手補正の契約（IADR-0154 決定 3 の目印・置換単位）の変更
  - docx の図キャプションが本文に素の段落として残ること（pandoc の出力仕様。別件）
  - 抽出図の採番規則（ファイル名の序数順）そのもの

## 設計

### 決定 1: 一時パスの消去は**変換器の責務**（`PandocConversionService`）

`mediaDir` は `PandocConversionService` の内部事情であり、`finally` で消える。
**その事実を知っているのは変換器だけ**なので、本文から一時パスを消すのも変換器の責務とする。
`NormalizationService` へ「一時パスを掃除しろ」と持ち込むと、変換器を差し替えるたびに
掃除の実装が要る（IADR-0008 の 3 ポート分離を崩す）。

### 決定 2: 2 段の目印（`figure:<figureId>`）で位置を運ぶ

最終参照（`storage://…/assets/fig-1.png`）は**アップロード後にしか判らない**（`NormalizationService`）。
いっぽう位置（本文のどこか）は**変換直後にしか判らない**（`PandocConversionService`）。
両者を繋ぐため、変換器は一時パスを **`![fig-1](figure:fig-1)`** へ書き換えて返し、
`NormalizationService` がこれを最終の埋め込み（`ImageEmbed` / `CodeEmbed`）へ置換する。

- 形は **`FigureMarkdown` を単一情報源**にする（IADR-0154 決定 3 と同じ思想）。
  `PlaceholderUri` / `PlaceholderEmbed` / `TryReplacePlaceholder` を足す。
- 目印は `ImageEmbed` の形そのもの（`![id](uri)`）なので、**置換に失敗しても目印が読める**。

### 決定 3: 書き換えは「画像構文まるごと」を置き換える。**src 属性だけ差し替えない**

`<img src="figure:fig-1" style="…" />` を残すと、それは `FigureMarkdown` の目印と一致せず、
人手補正が空振りする（受け入れ基準 3）。よって **`<img …>` タグ全体**／
**`![…](…)` 全体**を `PlaceholderEmbed` へ置き換える。属性（`style` / `alt`）は落ちる。

### 決定 4: **一時パスは 1 件も残さない**（fail-closed の安全網）

構文を認識できなかった参照（`<embed>`・非画像媒体へのリンク等）が残ると、issue の事象が
そのまま再発する。よって構文置換のあとに **`mediaDir` 前置の残渣を走査**し、
図に写像できるものは `figure:<id>` へ、写像できないものは**空文字へ落とす**（警告ログ）。
`ExtractFigures` は画像でない拡張子を図として採らないため、**写像できない媒体は実在する**
（防御的実装ではない）。

### 決定 5: 原本由来の `figure:` 参照は落とす

原本が偶然（あるいは意図的に）`![fig-1](figure:fig-1)` を含むと、`NormalizationService` が
**無関係の図をそこへ差し込む**か、図が無ければ**解決できない参照が残る**。
構文走査の同じ 1 パスで `figure:` スキームの画像構文を落とす（`Regex.Replace` は自分の出力を
再走査しないため、このパスで見える `figure:` は**必ず原本由来**である）。
「曖昧なら受け取らない」（IADR-0154 決定 3 の `IsEmbeddable` と同じ判断）。

### 決定 6: 目印が無ければ**従来どおり末尾へ append**する

目印を持たない本文（縮退プレースホルダ・図を本文で参照しない原本・変換器の差し替え）でも
図は本文から参照できねばならない。append の綴りは**現行とバイト等価**に保つ
（`"\n\n" + embed + "\n"`）。これにより目印を含まないゴールデンは**動かない**。

### 決定 7: 同じ媒体を 2 度参照する原本は、目印も 2 つ置き**両方を置換する**

`TryReplacePlaceholder` は**全出現**を置換する（人手補正の `TryReplaceImageWithCode` は
IADR-0154 決定 3 のとおり**先頭 1 件のみ**で据え置く）。片方だけ置換すると
解決できない `figure:` 参照が残り、決定 4 の不変条件を自分で破る。

### 処理フロー

```mermaid
flowchart TD
  A[pandoc -t gfm --extract-media mediaDir] --> B[stdout: 本文（一時パス入り）]
  A --> C[mediaDir 配下の媒体ファイル]
  C --> D[ExtractFigures: path -> ExtractedFigure fig-N]
  B --> E[RewriteExtractedMediaReferences]
  D --> E
  E --> F[本文: ![fig-N]&#40;figure:fig-N&#41;・一時パス 0 件]
  F --> G[NormalizationService: 図ごとに置換]
  G -->|目印あり| H[本文中の元の位置へ ImageEmbed / CodeEmbed]
  G -->|目印なし| I[末尾へ append（現行どおり）]
```

## 受け入れ基準

- [ ] Given 図を含む docx / When 稼働クラスタで変換する / Then 保管された正規化本文に
      `/tmp/` で始まる参照が**含まれない**（生出力を PR に貼る）
- [ ] Given 図を含む原本 / When 変換する / Then 図が本文から**参照できる**（壊れたリンクを残さない）
- [ ] Given 人手補正 / When 図のコード化をやり直す / Then 置換が空振りしない
      （`FigureMarkdown.TryReplaceImageWithCode` の目印と一致している）
- [ ] Given ゴールデン / When 形を変える / Then 差分が PR に現れる（手で書き換えない＝`UPDATE_GOLDEN=1`）
- [ ] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が両ユニットで通る

## テスト方針

| ID | 内容 | 期待 | 置き場 |
| --- | --- | --- | --- |
| T-23 | **実 pandoc 出力（docx 形）の書き換え** — pod で採取した実出力を固定入力にし、`<img>` タグ全体が目印へ替わること | 一時パス 0 件・`![fig-1](figure:fig-1)` 1 件 | `Tests/Infrastructure/ExternalServices/` |
| T-24 | **Markdown 画像形の書き換え**（html / gfm 入力） | 同上 | 同上 |
| T-25 | **図に写像できない媒体参照は落ちる**（決定 4 の安全網） | 一時パス 0 件・目印も増えない | 同上 |
| T-26 | **原本由来の `figure:` 参照は落ちる**（決定 5） | 出力に `figure:` が残らない | 同上 |
| T-27 | **同一媒体の 2 度参照は目印も 2 つ**（決定 7） | 目印 2 件 | 同上 |
| T-28 | **媒体外の画像参照は触らない**（陽性対照の対） | 外部 URL の `<img>` がそのまま残る | 同上 |
| T-29 | **目印があれば本文中の元の位置へ埋め込む**（画像保持） | 末尾に append されない・目印は残らない | `Tests/Features/ConversionJobs/Normalize/` |
| T-30 | **目印があれば本文中の元の位置へ埋め込む**（コード化成功） | 同上 | 同上 |
| T-31 | **目印が無ければ末尾へ append**（決定 6・回帰） | 現行どおりの綴り | 同上 |
| T-32 | **人手補正が空振りしない**（受け入れ基準 3） | 目印置換後の本文で `TryReplaceImageWithCode` が true | 同上 |
| T-33 | **実 pandoc の端から端**（pandoc 導入環境のみ） | 一時パス 0 件・図 1 件 | `Tests/Infrastructure/ExternalServices/`（`Assert.SkipUnless`） |
| T-14〜T-18 | ゴールデン更新（`UPDATE_GOLDEN=1`）。`html-article` / `office-docx-report` の本文へ目印を入れ、**位置が本文中へ戻ること**を全文で固定する。`pdf-report` は目印を入れず**append 経路**を固定したまま残す | golden 差分が PR に出る | `Tests/Golden/` |

- 新規テストは #1063 の鏡写し規約に従い `Tests/<本体と同じ相対パス>/` へ置く。
- pandoc 実走を要するのは T-33 のみ（既存方針どおり**真の Skipped**）。T-23 は
  **pod で採取した実出力**を固定入力にするため、pandoc の無い CI でも実際の綴りを検査できる。

## 計画書との差異

- 差異: なし。ADR-0012 と `04_workflows/03_conversion-flow.md` は「画像参照を埋込」とだけ定め、
  **位置（本文中か末尾か）を規定していない**。よって位置の決定は実装判断であり IADR-0352 に残す。

## 未決事項

- 同一の図を 2 度参照する原本では、人手補正（IADR-0154 決定 3・先頭 1 件のみ置換）が
  1 つ目だけをコード化し 2 つ目は画像のまま残る。**IADR-0352 のフォローアップに記す**
  （本 issue の射程＝一時パスと二重化の解消を越える）。
- docx の図キャプションが本文へ素の段落として残る（pandoc の gfm 出力仕様）。別件。
