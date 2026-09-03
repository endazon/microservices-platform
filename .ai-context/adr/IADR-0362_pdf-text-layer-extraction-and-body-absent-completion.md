---
title: IADR-0362 PDF の本文は pandoc の外の pdftotext で取り出し、テキスト層の無い PDF は failed ではなく「本文なしで完了」にする
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-12, UC-06, SC-07, ADR-0012, ADR-0070, IADR-0008, IADR-0122, IADR-0137, IADR-0298, IADR-0320, IADR-0351]
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - "07_adr/ADR-0070_pdf-body-extraction-and-ingest-format-set.md（Accepted・2026-09-03。決定 1・2・3・5）"
  - "06_technical/09_datasource-connectors.md §対応形式（取り込み形式の集合の正本）"
  - "05_screens/01_screens.md §SC-07（状態モデルは 4 値）"
---

# IADR-0362: PDF の本文は pandoc の外の pdftotext で取り出し、テキスト層の無い PDF は failed ではなく「本文なしで完了」にする

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: claude（実装担当）
- 起票: #1192（planning#509 の環流 → planning#521 で ADR-0070 が Accepted）
- 作業仕様書: `20260903_issue-1192_pdf-text-layer-extraction.md`

## 文脈

`IADR-0320`（Superseded in part by 本 IADR）決定 4 は「PDF は pandoc の入力形式にならないので
`UnsupportedSourceFormatException` で明示的に拒否する」とし、同 §代替案は「PDF を poppler 等で別経路に
回すのは ADR-0012（本文は pandoc）の射程外＝計画外の機能追加」として退けた。
**ADR-0070 決定 2 はその判断を覆した** —— 「FR-12 が要求しているのは正規化であり、pandoc は括弧書きで
示された手段である。手段が 1 つの形式に届かないときに別の手段を足すことは、要求の追加ではなく手段の補完である」。

着手時（`develop` `45853885`）の実装は PDF を 1 件残らず `failed` ＋ `deadLettered=true` にしていた
（`RawDocumentFetchedConsumer:61-77`）。ADR-0070 決定 3 が禁じた状態そのものである。

`.ai-context/` の確定済み記録は書き換えない。**本 IADR が `IADR-0320` を旧として引き、覆った範囲
（決定 4 の PDF 拒止と §代替案の「計画外」判定）を明記する。** 同 IADR の決定 1〜3・5・6（pandoc の
同梱・fail-closed・原本の取り寄せ・readiness・検査器を足さない）は**そのまま有効**であり、本 IADR は
それらと同じ線で抽出器を足す。

### 母集合（作業仕様書 §母集合 の要約）

- 形式の判定点は `PandocConversionService.PandocInputFormat` の 1 箇所（MIME → 拡張子の 2 段。PDF だけ例外、
  それ以外の未知は既定 `markdown`）。
- 失敗の分類は 3 経路（`BodyConversionUnavailableException` / 非 0 終了 / `UnsupportedSourceFormatException`）。
- ジョブ状態は 4 値で、`DeadLettered` / `DiagramsRetained` / `HasCorrection` が「5 値目ではない内訳」として
  末尾に既定値つきで載っている（`IADR-0137` 決定 1・5）。
- 「PDF は拒否する」と述べる文書は `docs/functional/FR-01*` `FR-12*` / `docs/tests/FR-12*` `UC-06*` の 4 件
  （誤りの側の文字列 `\bpdf\b` で走査。陽性対照 `pandoc` 13 件）。

## 決定

### 決定 1: 抽出器は poppler-utils の `pdftotext` を外部プロセスとして起動する。NuGet は足さない

ADR-0070 決定 2 は「poppler の pdftotext 相当。具体の実装は実装側に委ねる」とした。
**pandoc と同じ型（`Process.Start`）で `pdftotext -enc UTF-8 -nopgbrk <src> -` を起動する**
（`PdfTextLayerConverter`）。取得元はベースイメージ（Ubuntu 24.04 noble）の APT ミラー、版は
noble の poppler-utils 24.02.0。`IADR-0320` 決定 1 と同じ線であり、外部 CDN や任意 URL からは取らない。

| 案 | 却下理由 |
| --- | --- |
| PdfPig（NuGet。Apache-2.0） | ライブラリを 1 つ足し、`scripts/backend-library-baseline.json` の対象が増える。ADR-0070 が名指ししたのは pdftotext であり、pandoc と同じ外部プロセスの型に揃えるほうが運用（readiness・版の問い合わせ）も同型になる |
| PDFium / iText | ライセンス（AGPL）または P/Invoke の運用負担。本件の要求（テキスト層の抽出）に対して過剰 |
| OCR を同時に入れる | ADR-0070 案 4 が「別の意思決定」として留保した |

抽出はコンテナ内でローカル完結し、外部送信を行わない（03_conversion-flow §補足 の線は不変）。
**図は抽出しない**（`Figures` は空）。PDF 内画像の図抽出（`pdfimages`）は計画に無い。

### 決定 2: 振り分けは `IBodyConverter` の合成器 `FormatRoutingBodyConverter` が行う

PDF → `PdfTextLayerConverter`、それ以外 → `PandocConversionService`。DI では合成器だけを `IBodyConverter`
として登録し、両変換器は具象で登録する。**`NormalizationService` は `IBodyConverter` しか知らない** ——
`IADR-0008` の 3 ポートの境界は不変で、ゴールデン（`IADR-0298`）の差し替え点もそのままである。

原本の解決（オブジェクトストレージからの取り寄せ。`IADR-0320` 決定 3）は両変換器が同じ経路を使うため、
`RawSourceResolver` へ切り出した。挙動は変えていない。

### 決定 3: 形式の判定は `PandocInputFormat` の 1 箇所。PDF は `null`、表に無い未知の形式は例外

写像表を 2 箇所へ持たない。`PandocInputFormat` は

- pandoc が読む形式 → `-f` 値
- **PDF → `null`**（pandoc の担当ではない。合成器が抽出器へ振り分ける）
- **計画の対応形式表に無い未知の形式（未知 MIME ＋未知拡張子）→ `UnsupportedSourceFormatException`**

を返す。🔴 **3 つ目は従前の既定 `markdown` をやめる判断である。** ADR-0070 決定 5 は「変換側の既定
（未知の形式を markdown として読む）を計画の要求とはしない。この既定に頼ると、対応していない形式が
静かに壊れた本文になる」と述べた。#1192 の受け入れ基準が「PDF 以外の未対応形式では引き続き投げる（陽性対照）」
を求めているのも同じ趣旨であり、従前は**投げる形式が PDF しか無かった**ため「引き続き」が成立しなかった。
`.txt` / `text/plain` は計画の表どおり `markdown` へ明示的に写す。表に無い既存の写像（rtf / epub / rst /
latex / org）は取り込み側が列挙しないため実害が無く、**本 IADR では増減させない**（増減は計画側の裁定）。

`UnsupportedSourceFormatException` の扱い（再送出せず `FailAsync(deadLettered: true)`）は
`IADR-0320` 決定 4 のまま。**PDF がそこへ来なくなっただけ**である。

### 決定 4: 「テキスト層なし」は抽出結果が空白のみ。判定は純関数

`PdfTextLayerConverter.ToBody(raw)` が改行を正規化し（`\f` も改行へ）、`IsNullOrWhiteSpace` なら
`(空, BodyAbsent=true)`、そうでなければ行末空白の除去と 3 連以上の空行の畳み込みだけを行って本文とする。
Markdown の記法はエスケープしない（プレーンテキストは Markdown としてそのまま読める。PDF の本文品質は
原本に依存する —— ADR-0070 §結果）。**品質の判断はしない**（可視の文字が 1 つでもあれば本文あり）。

純関数にしたのは、pdftotext を実走できない環境でも空判定を試験するためである。変異試験
（空判定を「常に本文あり」へ変える）で純関数のテスト 5 件と実 pdftotext の陰性ケース 1 件が落ちることを確かめた。

### 決定 5: 本文なしは `succeeded` の内訳 `BodyAbsent`（真偽値）で運ぶ。状態値は 4 値のまま

ADR-0070 決定 3「状態モデルの 5 番目の値を新設しない」に従い、`DeadLettered`（`IADR-0137` 決定 1）と
同型の標識を **`IADR-0122` 決定 2 の非破壊（末尾・既定値つき）**で足す。

| 層 | 追加 |
| --- | --- |
| 変換器 | `BodyConversionResult.BodyAbsent`（init・既定 false） |
| 正規化 | `NormalizationResult.BodyAbsent`（末尾・既定 false） |
| 読み取りモデル | `ConversionJob.BodyAbsent`（列。`AddBodyAbsentMarker`・既定 false）。処理再開・失敗・再変換受付で false へ戻す |
| 契約 | `ConversionJobDto.BodyAbsent`（末尾・既定 false。openapi は `required`） |
| 後続 | `DocumentNormalized.BodyAbsent`（末尾・既定 false）。`IDocumentNormalizedPublisher.PublishNormalizedAsync(..., bodyAbsent, ct)` |
| 画面 | `isBodyAbsent(job)` → `StatusBadge tone="warning"`「本文なしで完了」＋備考の理由文。`isRetryable` は `failed` のみのまま |

**理由の運び方**は真偽値 1 つで足りる。現時点の唯一の原因は「テキスト層が無い」であり、理由文は画面が契約の
真偽値から導く固定文（Lingui）である。理由が増える（OCR 導入後に「OCR も空」等）ときは、そのときに文字列の
項目を足せばよい（末尾・既定 null で非破壊）。

### 決定 6: 本文なしでも `document.md` は保管する（内容は空）

`MarkdownUri` を null にすると `DocumentNormalized` / `ConversionJobDto` の契約が破壊的に変わり、
`DocumentNormalizedConsumer`（本文指紋の計算）と `IngestionService`（`MarkdownUri is null` の早期 return）の
挙動も変わる。空の本文は「本文が無い」の正直な表現であり、作った文（プレースホルダ）を索引に載せない。
IngestionService はチャンク 0 件で通る。**本文なしの文書をメタデータで検索に載せる（ADR-0070 決定 4）は
#1193 の射程**であり、本 IADR は `DocumentNormalized.BodyAbsent` を渡すところまでである。

### 決定 7: fail-closed は「本文があるのに作れない」場合に限って維持する。readiness に pdftotext を載せる

| 事象 | 扱い |
| --- | --- |
| pdftotext が実行時イメージに無い | 既定は `BodyConversionUnavailableException`（`AllowDegradedBodyConversion=true` のときだけプレースホルダ。`IADR-0320` 決定 2 と同じ 1 個の設定） |
| 原本を読み出せない | 同上 |
| pdftotext が非 0 終了（壊れた PDF・暗号化） | `InvalidOperationException` → 再試行 → デッドレター（UC-06 例外フロー） |
| 抽出結果が空白のみ | **失敗ではない**（決定 4・5） |

`PdfToTextHealthCheck` を `ready` タグで登録する（fail-closed のときだけ。`IADR-0320` 決定 5 と同型）。
抽出器を持たないイメージを配ると Pod が Ready にならない。

🔴 **在／不在の判定は終了コードではなく版の行で行う。** poppler の `pdftotext -v` は 0 で終わるが、
開発機（Git for Windows）の同名の xpdf 版は 99 で終わる（実測）。終了コードで見ると開発機で
「不在」と判定され、実 pdftotext を要するテストが**全件 Skipped になり、実行実績が無いのに緑に見えた**。

### 決定 8: 退行防止は射程内で閉じる。横断の検査器は足さない

`IADR-0320` 決定 6 と同じ判断（「実行時イメージに必要な外部ツールが入っていない」は、pandoc に続く
同型の 2 件目ではなく**同じ 1 件の続き**であり、事故としては起きていない）。射程内に

1. `PdfTextLayerConverterTests.Dockerfile_installs_poppler_utils_into_the_runtime_stage`
2. 決定 7 の readiness

を置く。ゴールデン（`IADR-0298`）には `pdf-text-layer` / `pdf-no-text-layer` の 2 case を足し、器は
`bodyAbsent` を `## result` に描く（既存 4 件は `false` の 1 行が増えた。`UPDATE_GOLDEN=1` で書き戻した）。

## 実測（2026-09-03・稼働 k3s）

→ PR 本文（イメージサイズの before/after・`pdftotext -v`・テキスト層あり／なしの PDF の変換結果の生出力）。
本節は PR 作成後に転記しない（凍結記録は本文プロズを後から書き換えない）。

## 影響

| 面 | 影響 |
| --- | --- |
| イメージ | poppler-utils の分だけ増える（実測は PR 本文）。起動時間への影響は無い（変換時にのみ起動） |
| 配備 | `deploy/` の変更なし。イメージの中身は Dockerfile が決める |
| NuGet | 追加なし（`scripts/backend-library-baseline.json` 不変） |
| 契約 | `ConversionJobDto` / `DocumentNormalized` に末尾・既定値つきで 1 項目ずつ（`check-contract-schema` の baseline を更新。破壊的変更 0） |
| 既存テスト | `PandocConversionServiceTests` の「PDF を拒否する」固定を「PDF は null・未知は拒否」へ改めた。ゴールデン 4 件に 1 行ずつ |
| 宣言領域外 | `Tests/Knowledge.IntegrationTests/Fixtures/RawDocumentFetchedEdge.cs`（ポート変更への追随。挙動不変） |

## 代替案と却下理由

| 案 | 却下理由 |
| --- | --- |
| 5 値目の状態 `completed_without_body` | ADR-0070 決定 3 が「新設しない」と明記。SC-07 は 4 値の閉じた集合 |
| `failed` のまま理由を付ける | 再変換の対象として並び、何度やっても結果が変わらないジョブが溜まる（ADR-0070 決定 3 の理由そのもの） |
| `MarkdownUri = null` で「本文なし」を表す | 契約の破壊的変更。後続 2 サービスの早期 return / 指紋計算が変わる（決定 6） |
| 抽出器を `PandocConversionService` の中で呼ぶ | 「PDF は pandoc の外」（ADR-0070 の表題）に反し、1 クラスが 2 つの外部プロセスを持つ。合成器のほうが差し替え点が明確 |
| 未知の形式は従前どおり `markdown` へ落とす | ADR-0070 決定 5 が頼らないと述べ、受け入れ基準の陽性対照が立たない（決定 3） |

## 関連

- 計画: ADR-0070（本 IADR の裁定。ADR-0012 §決定 を部分改定）、ADR-0012
- 実装: `IADR-0320`（**決定 4 の PDF 拒止と §代替案の「計画外」判定を本 IADR が覆す。決定 1〜3・5・6 は有効**）、
  `IADR-0008`（3 ポートは不変）、`IADR-0122` 決定 2（非破壊の追加）、`IADR-0137` 決定 1（内訳の標識）、
  `IADR-0298`（ゴールデン）、`IADR-0351`（目印。PDF は図を持たないため末尾 append も起きない）
- 後続: #1193（ADR-0070 決定 4。`DocumentNormalized.BodyAbsent` を受け、メタデータで検索に載せる）
- 作業仕様書: `20260903_issue-1192_pdf-text-layer-extraction.md`
