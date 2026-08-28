---
title: IADR-0298 正規化変換のゴールデンは変換器境界で固定し、pandoc は実走させない
type: impl-adr
status: Accepted
related_ids: [FR-12, UC-06, SC-07, ADR-0012, ADR-0014, IADR-0008, IADR-0154, IADR-0293, IADR-0296]
author: claude
created: 2026-08-29
updated: 2026-08-29
---

# IADR-0298: 正規化変換のゴールデンファイル

- 状態: Accepted
- 日付: 2026-08-29
- 決定者: claude（実装担当）
- 起票: #447（親 issue。本 IADR が閉じるのはその中の「ゴールデンファイルテスト」1 項目である）

## 文脈

正規化変換の**出力の形**（本文 Markdown の綴り・資産キー・返す件数）には、他のサービスが
**文字列一致で**依存している。にもかかわらず既存のテストは部分一致でしか見ていない。

| 依存している側 | 依存の形 | 形が変わると |
| --- | --- | --- |
| 人手補正（`FigureMarkdown.TryReplaceImageWithCode`） | 本文中の `![figureId](uri)` の文字列一致 | 置換が静かに空振りする。**本文は変わらないのに補正は保存済み**という壊れ方をする |
| `DocumentObjectPurger`（DocumentService。IADR-0296） | 資産キー `{id:N}/assets/{figureId}{ext}` | 削除がオブジェクトの実体を取りこぼす。前方一致の一括削除ができないため台帳から逆引きしており、**キーの形が契約になっている** |
| LLM ゲートウェイの送信制御 | `NormalizationService` が `confidentiality` を `IDiagramCoder` へ渡すこと | **正規化結果に一切現れない**ため、渡さなくなっても全テストが緑のままである |

既存の `NormalizationServiceTests` 4 件はいずれも `Should().Contain(...)` の部分一致であり、
**上の 3 つのどれも捕まえない**（§検出力の実測で 5 件の変異を当てて確かめた）。

## 決定

`ConversionService.Worker.Tests` にゴールデンファイル器を置き、代表的文書形式の正規化結果を
スナップショットで固定する。器は `Tests/Golden/NormalizationGolden.cs`、資材は
`Tests/Golden/Cases/`（入力）と `Tests/Golden/Expected/`（golden）である。

### 決定 1: 差し替えは `IADR-0008` が置いた 3 ポートの境界で行う

`IBodyConverter`（pandoc）・`IDiagramCoder`（LLM）・`IObjectStore`（オブジェクトストレージ）は
すべて外部依存であり、**接ぎ目は既にある**。本器は新しい接ぎ目を作らない。

固定するのは **「変換器出力 Markdown ＋ 図 → 正規化結果」の決定的な部分**である。

### 決定 2: 🔴 pandoc は実走させない。固定できたものとできなかったものを分けて書く

**この環境に pandoc は無い**（`which pandoc` が非 0）。仮に有る環境でも、pandoc の版差で
出力が動くため golden にすると**環境によって赤くなる**。既存の `PandocConversionServiceTests` が
`Assert.SkipUnless` で環境依存にしているのと同じ理由である。

したがって入力（`Cases/<name>.body.md`）は「変換器がこう出すであろう Markdown」を人が書いたもので
あり、**pandoc の実際の出力ではない**。原本（docx / PDF / HTML のバイナリ）は 1 バイトも読んでいない。

| | 固定する | 固定しない |
| --- | --- | --- |
| 本文 | 正規化 Markdown の**全文**（綴り・順序・空行） | pandoc の変換結果そのもの |
| ID | 決定的 `DocumentId` の**実値** | — |
| 保管 | 本文キー・資産キーの**全文**、資産の contentType・バイト長・SHA-256 | 実ストレージ（MinIO/S3）の挙動 |
| 件数 | `DiagramsCoded` / `DiagramsRetained` | — |
| 図 | 図 1 つ 1 つの記録（`IADR-0154`） | LLM 応答からのコード抽出（既存テストが持つ） |
| 送信制御 | **`IDiagramCoder` へ渡した機密区分** | ゲートウェイ側の判定（既存テストが持つ） |
| 形式判定 | — | `PandocConversionService.PandocInputFormat`（`private static`） |

🔴 **したがって「PDF のゴールデンテストがある」とは言えない。** あるのは
**「PDF 由来と宣言された変換器出力を正規化した結果」のゴールデン**である。テスト仕様書・
器のコメント・本 IADR で同じ言葉遣いに揃えてある —— **「統制を定めた」と「統制が働いている」を
読み分けられない書き方をしない**という規約の、テストへの適用である。

### 決定 3: 本文は JSON へ埋めず、`.body.md` に分ける

case 1 件は `<name>.json`（宣言）と `<name>.body.md`（変換器出力の本文）の 2 ファイルである。
本文を JSON へ埋めると `\n` の羅列になり、**diff が読めなくなる** ——
ゴールデンの値打ちは「形の変化が PR の diff に現れること」なので、読めない形で置くと
目的を自分で潰す。

golden の側も同じ理由で**行指向のテキスト**にした。本文は
`--8<-- markdown begin` / `--8<-- markdown end` の間へ**逐語で**置き、
それ以外（ID・キー・件数・図・機密区分）は `key: value` と番号つきの 1 行で並べる。

### 決定 4: golden の更新は環境変数で書き戻す。**更新モードは書いたうえで失敗する**

```bash
UPDATE_GOLDEN=1 dotnet test src/knowledge/backend/backend.slnx \
  --filter "FullyQualifiedName~NormalizationServiceTests"
```

**「書いて緑」にしない。** 変数が CI の環境へ紛れ込んだとき、**差分を無条件に飲み込んで緑になる**
——`PandocConversionServiceTests` のコメントが名指しした「走らなかったケースを Passed として
報告する」と同じ型の事故である。書き戻したあと変数を外して再実行すれば緑になる。

**手で書き換える運用にはしない。** 手書きの golden は「実装の出力」ではなく「人が思っている
出力」を固定してしまう。

### 決定 5: fail-closed（3 点）

- golden が無い → **失敗**（黙って作らない）。初回だけ「何であれ現在の出力」が正になるのを防ぐ
- case が 0 件 → **失敗**。走査が空振りしたまま緑にならない
- case の無い golden（孤児）→ **失敗**。case を消したら golden も消す

### 決定 6: 新しいテストクラスを作らず、既存の `NormalizationServiceTests` へ足す

`check-test-spec-coverage.js` は「テスト仕様書に記載された（仕様書 × クラス）の対が baseline に
無い → fail」であり、**新しい `*Tests.cs` を作って仕様書へ名前を書くと `scripts/` の baseline
更新が要る**。本作業は並列トラックと領域が重なるため `scripts/` を触らない方針であり、
かつ golden の対象は正規化オーケストレータそのもの＝`NormalizationServiceTests` に属する
テストである。器（ヘルパ）は `*Tests.cs` で終わらない別ファイルへ置く。

## 検出力の実測（変異試験・2026-08-29）

**無変異ベースラインと対で取った。** 「実データで緑」は検出力の証拠にならない（`IADR-0293` が
実測した型）。各変異のあと `git checkout --` で戻し、`git status --porcelain` が空であることを
確認している。

| # | 変異 | 結果（`ConversionService.Worker.Tests` 81 件） |
| --- | --- | --- |
| M-0 | 変異なし（ベースライン） | **緑**（79 通過 / 2 skip。skip は pandoc 導入環境専用の 2 件） |
| M-1 | `FigureMarkdown.ImageEmbed` から figureId を落とす | **KILLED** 4 件失敗（golden 3 ＋ 既存 1） |
| M-2 | コードブロック前の空行を 1 つ減らす | **KILLED** 1 件失敗（**golden だけ**） |
| M-3 | `ExtensionFor` の `image/jpeg` を `.jpeg` へ | **KILLED** 1 件失敗（**golden だけ**） |
| M-4 | `IDiagramCoder` へ `confidentiality` ではなく `null` を渡す | **KILLED** 3 件失敗（**golden だけ**） |
| M-5 | 資産キーの GUID 書式を `:N` から既定（ハイフンあり）へ | **KILLED** 3 件失敗（**golden だけ**） |
| M-6 | case を 1 件消す（孤児 golden） | **KILLED**（fail-closed の見張り） |
| M-7 | case の無い golden を 1 件足す | **KILLED**（同上） |

**M-2 と M-4 は knowledge バックエンド全体（12 アセンブリ・1,160 件）でも実測した** ——
どちらも**落ちたのは golden だけ**であり、他の 11 アセンブリはすべて緑だった。
すなわち本器が無ければ、**空白の畳み方の変化と送信制御の無効化はどこにも映らない**。

## 影響

- テスト件数が 76 → 81 件になる（Theory 4 ＋ fail-closed の Fact 1）。
- 資材 12 ファイル（case 4 × 2 ＋ golden 4）が追跡下に入る。**golden は生成物だが、
  差分を PR に載せることが目的**なのでコミットする（`orval` 生成物・i18n カタログと同じ扱い）。
- **`scripts/` は 1 バイトも触らない**（決定 6）。

## 残件・既知の限界

1. **実 pandoc を通した変換のゴールデン化**は射程外である（決定 2）。pandoc を持つ環境でのみ
   走る形にするか、版差で golden が揺れるかを実測してから決める。
2. **`PandocInputFormat` は `application/pdf` を知らない。** contentType の switch にも拡張子の
   switch にも無く、**既定の `markdown` に落ちる**。pandoc は PDF を入力に取れないため、
   実環境では変換が非 0 終了しデッドレターへ倒れる。本作業は生産コードを変えないため
   **直していない**。確認と環流要否の判断は別作業へ残す。
3. **`NormalizationService` はコードブロックの綴りを `FigureMarkdown.CodeEmbed` から取っていない**
   （自前で組み立てている）。`FigureMarkdown` は「形は 1 箇所に閉じる」と宣言しているので
   宣言と実態がずれているが、**出力は一致する**ため退行ではない。golden は現状の形を固定した
   ——直すと「固定した」と「直した」が同じ diff に混ざる。
4. **採番**: 本 IADR は 0298〜0300 の欠番の上に載る。3 番は並列トラックが予約済みであり、
   統合時に埋まる前提である。埋まらない場合は先着尊重で改番する。
