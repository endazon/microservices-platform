---
title: IADR-0351 --extract-media の一時パスは変換器が目印へ書き換え、図は本文中の元の位置へ戻す
type: impl-adr
status: Accepted
related_ids:
  - FR-12
  - UC-06
  - SC-07
  - ADR-0012
  - IADR-0008
  - IADR-0154
  - IADR-0298
  - IADR-0320
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md
  - planning:projects/microservices-platform/04_workflows/03_conversion-flow.md
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
---

# IADR-0351: `--extract-media` の一時パスは変換器が目印へ書き換え、図は本文中の元の位置へ戻す

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: claude（実装）
- 実装 issue: **#1120**（出所は [IADR-0320](./IADR-0320_pandoc-runtime-image-and-fail-closed-body-conversion.md) §「実走させて初めて見えた別件」）
- 作業仕様書: [20260903_issue-1120](../specs/20260903_issue-1120_extract-media-path-rewrite.md)

## 文脈

pandoc の `--extract-media` は**本文中の画像参照を一時ディレクトリの絶対パスへ書き換える**。
`PandocConversionService` はその標準出力をそのまま `BodyConversionResult.Markdown` として返し、
`NormalizationService` はそれを土台に**図を末尾へ append** して保管していた。結果:

1. 変換直後に `ConvertAsync` の `finally` が消す `/tmp/conv-XXXXXXXX/media/…` への
   **壊れた参照が保管物に残る**（保管された時点で既に存在しない）
2. 同じ図が**本文中（壊れた一時パス）と末尾（正しい `storage://`）の 2 度**現れる
3. 図が**元の位置から末尾へ寄る**ため「どの段落の図か」が失われる

保管された本文は**索引本文（チャンク化・埋め込み）・Wiki ページ・BFF の本文取得・人手補正**が
そのまま読む。壊れた `<img>` タグはその全部に入る。

**#1097 で pandoc を実行時イメージへ入れて初めて観測された。** 従前は pandoc が 1 度も
実走していなかったため、この綻びはどの記録にも現れていない。

### 実測（2026-09-03・稼働 k3s の conversion-service pod / pandoc 3.1.3）

pandoc が出す参照は**2 つの構文形**がある（どちらも pod で実変換して採取した）。

| 入力 | 出力の形 |
| --- | --- |
| html / gfm | `![構成図](<mediaDir>/fig.png)` — Markdown 画像形 |
| docx（属性つき） | `<img src="<mediaDir>/media/rId20.png" style="…"⏎alt="…" />` — **HTML 形。属性が改行をまたぐ** |

「変換器がこう出すであろう」と想像して書くと、**改行をまたぐ `<img>`** のような実際の形を取りこぼす。

## 決定

### 決定 1: 一時パスの消去は**変換器の責務**である

`mediaDir` は `PandocConversionService` の内部事情であり、`finally` で消える。
**その事実を知っているのは変換器だけ**なので、本文から一時パスを消すのも変換器の責務とする。
`NormalizationService` へ「一時パスを掃除しろ」と持ち込むと、変換器を差し替えるたびに
掃除の実装が要る（[IADR-0008](./IADR-0008_conversion-ports-deny-by-default-and-idempotent-id.md) の 3 ポート分離を崩す）。`IBodyConverter` の署名は変えない。

### 決定 2: 位置は **2 段の目印（`figure:<figureId>`）**で運ぶ

最終参照（`storage://…/assets/fig-1.png`）は**アップロード後にしか判らない**（`NormalizationService`）。
いっぽう位置（本文のどこか）は**変換直後にしか判らない**（`PandocConversionService`）。
両者を繋ぐため、変換器は一時パスを **`![fig-1](figure:fig-1)`** へ書き換えて返し、
`NormalizationService` がこれを最終の埋め込みへ置換する。

これに伴い `ExtractFigures` を `ExtractMedia` へ改め、**書き出し元のパスを図と一緒に返す**。
従前はパスを捨てていたため、本文中の参照（同じパスを指す）と図の対応が付かなかった。

目印の綴りは **`FigureMarkdown` を単一情報源**にする（`PlaceholderScheme` / `PlaceholderUri` /
`PlaceholderEmbed` / `TryReplacePlaceholder`）。[IADR-0154](./IADR-0154_manual-figure-correction-phase1.md) 決定 3 が埋め込み形を 1 箇所へ閉じたのと
同じ理由である —— 置く側と置換する側が別々に形を書くと、片方だけ変えたときに**静かに空振りする**。

**目印の形は `ImageEmbed` そのもの（`![id](uri)`）にしてある。** 置換に失敗して本文へ残っても
「どの図の目印か」が読める。

### 決定 3: 書き換えは「画像構文まるごと」を置き換える。**`src` 属性だけ差し替えない**

`<img src="figure:fig-1" style="…" />` を残すと、それは `FigureMarkdown` の目印と一致せず、
最終的に本文へ残る綴りも `![fig-1](storage://…)` にならない。すると**人手補正
（`TryReplaceImageWithCode`）が空振りする** —— 補正だけ保存されて本文に出ない、という
[IADR-0154](./IADR-0154_manual-figure-correction-phase1.md) 決定 3 が名指しした「壊れたと分かりにくい失敗」に戻る。

よって `<img …>` タグ全体／`![…](…)` 全体を目印へ置き換える。**属性（`style` / `alt`）は落ちる。**

### 決定 4: **一時パスは 1 件も残さない**（fail-closed の安全網）

構文置換のあとに `mediaDir` 前置の**残渣を走査**し、図に写像できるものは `figure:<id>` へ、
写像できないものは**空文字へ落とす**（警告ログ）。

これは起こり得ないケースへの防御ではない —— `ExtractMedia` は**画像でない拡張子を図として採らない**
ため、写像できない媒体参照は実在する。加えて `<embed>` のように画像構文でない形も pandoc は出し得る。
**不変条件を構文の網羅性に賭けない**（賭けると、網羅の漏れがそのまま issue の再発になる）。

### 決定 5: 原本由来の `figure:` 参照は落とす

原本が `![fig-1](figure:fig-1)` を含むと、`NormalizationService` が**無関係の図をそこへ差し込む**か、
図が無ければ**解決できない参照が残る**。構文走査の同じ 1 パスで落とす —— `Regex.Replace` は
自分の出力を再走査しないため、**このパスで見える `figure:` は必ず原本由来**である。

「曖昧なら受け取らない」（[IADR-0154](./IADR-0154_manual-figure-correction-phase1.md) 決定 3 の `IsEmbeddable` と同じ判断）。

### 決定 6: 目印が無ければ**従来どおり末尾へ append** する

目印を持たない本文（縮退プレースホルダ・図を本文で参照しない原本・変換器の差し替え）でも、
図は本文から参照できねばならない。**図がまったく参照できなくなるほうが悪い。**

append の綴りは**従前とバイト等価**（`"\n\n" + embed + "\n"`）に保つ。
したがって目印を含まないゴールデンは動かない（`markdown-plain` / `pdf-report`）。

### 決定 7: 同じ媒体を 2 度参照する原本は、目印も 2 つ置き**両方を置換する**

`TryReplacePlaceholder` は**全出現**を置換する。片方だけ置換すると、解決できない
`figure:` 参照が本文へ残る —— 本 ADR が直している事故がまさにそれである。

（人手補正の `TryReplaceImageWithCode` が**先頭 1 件のみ**なのは [IADR-0154](./IADR-0154_manual-figure-correction-phase1.md) 決定 3 の別判断であり、
本 ADR では変えていない。フォローアップ参照。）

### 決定 8: ゴールデンは**目印を含む case と含まない case の両方**を持つ

[IADR-0298](./IADR-0298_normalization-golden-files.md) 決定 2「pandoc は実走させない」は変えない。`Cases/<name>.body.md` は
「変換器がこう出すであろう Markdown」であり、**変換器が目印を出すようになった以上、
その case も目印を含むべき**である。

- 目印あり: `html-article`（画像保持）・`office-docx-report`（コード化＋画像保持の混在）
  → 図が**本文中の元の位置**へ入ることを全文で固定する
- 目印なし: `pdf-report` → **末尾へ append する経路**（決定 6）を固定したまま残す

ゴールデンは `UPDATE_GOLDEN=1` で書き戻した（手で書き換えていない。[IADR-0298](./IADR-0298_normalization-golden-files.md) 決定 4）。

## 根拠 / 代替案

| 案 | 却下理由 |
| --- | --- |
| **壊れた参照だけ落とす**（図は従来どおり末尾） | 差分は小さいが、**図の位置情報は失われたまま**。issue が挙げた 3 番目の害（どの段落の図か判らない）が残る |
| `--extract-media=.` ＋ 作業ディレクトリ指定で**相対パスにする** | **壊れていることは変わらない**（解決先が無いのは同じ）。見た目が短くなるだけである |
| `src` 属性だけを一時パスから最終 URI へ差し替える | 人手補正の目印（`![id](uri)`）と一致しない（決定 3）。**#1120 を直しながら [IADR-0154](./IADR-0154_manual-figure-correction-phase1.md) を壊す** |
| 掃除を `NormalizationService` に置く | `mediaDir` は変換器の内部事情。3 ポート分離が崩れる（決定 1） |
| `IBodyConverter` に「図の位置」を構造として持たせる（オフセット等） | 契約が太る。本文は文字列として下流を流れるので、**目印を本文へ書く方が経路全体で一貫する** |
| 安全網（決定 4）を置かない | 不変条件を構文の網羅性に賭けることになる。**写像できない媒体は実在する** |

## 影響

| 面 | 影響 |
| --- | --- |
| 契約 | **変更なし**（`IBodyConverter` / `BodyConversionResult` の署名は不変） |
| 保管物 | 正規化本文の**綴りが変わる**（図が本文中の元の位置へ入る）。`DocumentId`・資産キーは不変 |
| 人手補正 | **不変**（`![figureId](imageUri)` の目印は保たれる。T-32 が固定） |
| 既存の保管物 | **遡及して直さない。** 再変換（`retry` / 手動同期）で新しい綴りへ入れ替わる |
| ゴールデン | 3 件が動いた（`html-article` / `office-docx-report` の本文、`pdf-report` は説明行のみ） |
| NuGet | 追加なし |

## 実測（2026-09-03・稼働 k3s の conversion-service pod）

pod には .NET 10 ランタイムと pandoc 3.1.3 がある。**本 PR のコンパイル済みコード**
（`PandocConversionService` ＋ `NormalizationService`）を pod 内で走らせ、保管される本文を採取した
（**Pod は再起動していない**。ストレージと LLM だけをインメモリ／常時縮退へ差し替えている）。

修正前（同 pod・pandoc 生出力。`NormalizationService` はこれの末尾へ図を append していた）:

```console
<img src="/tmp/repro-1120-a/conv-REPRO/media/rId20.png" alt="構成図" />
=== /tmp reference count in body ===
1
```

修正後（保管される `document.md` そのもの）:

```console
# 四半期レポート

本文の段落である。**強調**と`コード`を含む。

- 箇条書き 1
- 箇条書き 2

![fig-1](storage://knowledge-normalized/93a74f2ec59951dabc5b707e80dd9788/assets/fig-1.png)

構成図

## まとめ

pandoc が実際に走ったかどうかは、この見出しが出るかで判る。

=== 計測 ===
/tmp 参照の件数    : 0
'conv-' の出現件数 : 0
'figure:' の残り   : 0
図 fig-1 の参照件数 : 1
```

## フォローアップ

- **同一の図を 2 度参照する原本**では、人手補正（[IADR-0154](./IADR-0154_manual-figure-correction-phase1.md) 決定 3・先頭 1 件のみ置換）が
  1 つ目だけをコード化し、2 つ目は画像のまま残る。本 ADR の決定 7 で目印は両方置かれるように
  なったため、**この状態は今後実際に作れる**。補正の置換単位を見直すかは別 issue とする。
- docx の図キャプションが本文へ素の段落として残る（pandoc の gfm 出力仕様）。本 ADR の射程外。
- 既存の保管物に残る壊れた参照は再変換でしか直らない。**一括再変換の要否は運用判断**である。

## 関連

- 計画: ADR-0012（変換パイプライン。**図の位置は規定していない**ため本 ADR が決める）、
  `04_workflows/03_conversion-flow.md`（「画像参照を埋込」）
- 実装: [IADR-0008](./IADR-0008_conversion-ports-deny-by-default-and-idempotent-id.md)（3 ポート分離）、[IADR-0154](./IADR-0154_manual-figure-correction-phase1.md)（人手補正の目印）、
  [IADR-0298](./IADR-0298_normalization-golden-files.md)（正規化ゴールデン）、[IADR-0320](./IADR-0320_pandoc-runtime-image-and-fail-closed-body-conversion.md)（pandoc の実行時イメージ導入と fail-closed）
