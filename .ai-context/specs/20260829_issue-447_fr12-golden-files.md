---
title: 作業仕様書 FR-12 正規化変換のゴールデンファイルテスト（#447 退行防止項目）
type: spec
status: done
related_ids:
  - FR-12
  - UC-06
  - SC-07
  - ADR-0012
  - ADR-0014
  - IADR-0008
  - IADR-0154
  - IADR-0296
  - IADR-0298
author: claude
created: 2026-08-29
updated: 2026-08-29
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-12)
  - planning:projects/microservices-platform/03_usecases/ (UC-06 文書を正規化変換する)
  - planning:projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md
related_specs:
  - ./20260703_FR-12_document-normalization-pipeline.md
  - ./20260810_issue-543_manual-correction-api.md
  - ../adr/IADR-0298_normalization-golden-files.md
---

# 仕様書: FR-12 正規化変換のゴールデンファイルテスト（#447 / 退行防止）

> **#447 は親 issue であり、本作業では閉じない。** 本作業が閉じるのはその中の 1 項目
> 「パイプライン各段の単体テスト＋**代表的文書形式のゴールデンファイルテスト（正規化結果の
> スナップショット）**」のうち、**ゴールデンファイルテストの部分だけ**である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-12**（原本を AI が扱いやすい正規化形式へ変換して管理する）
- ユースケース（UC）: **UC-06**（文書を正規化変換する）。縮退した図の人手補正は SC-07
- 関連 ADR: `ADR-0012`（変換パイプライン pandoc＋LLM・段階的コード化）/ `ADR-0014`（本文・資産は
  オブジェクトストレージ）
- 実装 IADR: `IADR-0008`（ポート分離＋deny-by-default 縮退＋決定的 `DocumentId`）/
  `IADR-0154`（図 1 つ 1 つの記録）/ `IADR-0296`（削除伝播が資産キーの形に依存する）/
  **`IADR-0298`（本作業の決定）**
- 現行の記述: `docs/functional/FR-12_document-normalization.md` §処理フロー、
  `docs/tests/FR-12_document-normalization.md`（T-01〜T-13）

## 目的・背景

正規化変換の**出力の形**（本文 Markdown の綴り・資産キー・返す件数）は、複数のサービスが
暗黙に依存している。にもかかわらず現行のテストは `Should().Contain("```mermaid")` のような
**部分一致**でしか見ておらず、**形が変わっても緑のまま通る**。

依存している側を実測した（§母集合 軸 2）。

| 依存先 | 何に依存しているか |
| --- | --- |
| 人手補正（`FigureMarkdown.TryReplaceImageWithCode`） | 本文中の `![figureId](uri)` の**文字列一致**。形が変わると置換が静かに空振りする |
| `DocumentObjectPurger`（DocumentService） | 資産キー `{id:N}/assets/{figureId}{ext}` の形。前方一致の一括削除ができないため台帳から逆引きする |
| `LlmGatewayDiagramCoder` への送信制御 | `NormalizationService` が `confidentiality` を渡すこと。渡さなくなっても**出力は変わらない**ため既存テストでは見えない |

ゴールデンファイル（入力 fixture → 正規化結果のスナップショット）で**出力の全体**を固定し、
形の変化を PR の diff に現す。

## 対象範囲

### 対象（本作業）

1. 正規化変換のゴールデンファイル器を `ConversionService.Worker.Tests` に置く
   （fixture・golden・更新手順）
2. 代表 4 形式（Markdown / HTML / Office(docx) / PDF）の case を置く
3. `docs/tests/FR-12_document-normalization.md` へテストケースを追記する
4. 決定を `IADR-0298` に記録する
5. **変異試験で検出力を実測する**（無変異ベースラインと対で取る）

### 対象外（理由と送り先）

| 対象外 | 理由 |
| --- | --- |
| **実 pandoc を走らせる変換のゴールデン化** | **この環境に pandoc が無い**（`which pandoc` が非 0）。§実測 参照。実原本を入力にした変換は元から `PandocConversionServiceTests` が `Assert.SkipUnless` で環境依存にしており、ゴールデンにすると**環境によって golden が生成されたりされなかったりする**——決定性が壊れる |
| **PandocConversionService の入力形式判定（`PandocInputFormat`）の固定** | `private static` であり、公開せずには外から呼べない。公開は本作業の射程を超える生産コード変更である。§やらなかったこと 参照 |
| `scripts/` の検査器・baseline | 並列トラックの宣言領域。**本作業は 1 バイトも触らない**（§検査器への影響） |
| 他サービス・フロントエンド・`deploy/` | 同上 |
| `NormalizationService` の実装変更 | 本作業は**固定するだけ**である。見つけた綻び（§気付いたが直さないもの）は記録に留める |

## 母集合（着手前の実測。`.claude/rules/traceability.repo.md` 規則 1〜10）

**走査時点は本仕様書を書く前**である（規則 8）。**本ファイル自身は数に入っていない。**
除外は `node_modules` / `obj` / `bin` / `.git` / `TestResults` / `coverage`（生成物）と
`src/ai-stock-trading`（別プロジェクトの submodule）**のみ**であり、**拡張子では絞っていない**
（規則 3）。行フィルタでの絞り込みもしていない（規則 4）。

### 軸 1: 「ゴールデン／スナップショット」の綴り（誤りの側から引く。規則 1・2）

リポジトリ全体（上記除外のみ）での行数。

| 綴り | 行数 | 含意 |
| --- | ---: | --- |
| `golden` | 6 | **既存の golden は 1 か所だけ**（`HybridSearchServiceTests` の T-44 ＋ `docs/tests/FR-03`）。**ファイル方式の golden はリポジトリに 1 つも無い** |
| `Golden` / `GOLDEN` | 0 / 0 | 綴りの衝突なし。新しい語彙である |
| `ゴールデン` | 2 | いずれも `.ai-context/superpowers/plans/` の旧計画（凍結記録・追随不可） |
| `snapshot` | 57 | 大半は EF の `*ModelSnapshot.cs`（生成コード）と `contract-schema-baseline.json`。**テストの意味での snapshot は無い** |
| `スナップショット` | 152 | **文書の版スナップショット**（FR-06）と契約 baseline の説明。**別概念である**——語をそのまま流用すると読み手が版管理と取り違える |
| `fixture` / `フィクスチャ` | 82 / 200 | 統合テストの器（`IntegrationTestFactory`）の意味で確立済み。**「入力データ」の意味では使われていない** |

**含意**: 「golden」は本リポジトリで**順位の固定**（T-44）に使われている語であり、意味は一致する。
一方 **`fixture` は既に「テストの器」の意味を持っている**ため、入力データの置き場を `Fixtures/`
と名付けると既存語彙と衝突する。本作業は **`Cases/`（入力）と `Expected/`（golden）** を使う。

### 軸 2: 正規化の出力の形に依存している箇所（`ImageEmbed` / `CodeEmbed` / `assets/`）

**24 行 / 13 ファイル。** 本番コードは 4 ファイルである。

| ファイル | 依存の形 |
| --- | --- |
| `Worker/Domain/FigureMarkdown.cs` | 埋め込み形の**単一情報源**（`![id](uri)` / ```` ```lang ```` ） |
| `Worker/Features/ConversionJobs/NormalizationService.cs:52,57` | 資産キーの組み立てと画像埋め込み |
| `DocumentService/Features/Documents/DocumentObjectPurger.cs` | **資産キーの形に依存**（`{id:N}/assets/{figureId}{ext}`）。削除伝播 |
| `Platform.Bff.Tests/BffTestFactory.cs:226` | 画面へ配る図 DTO の例（試験の器） |

テスト側 9 ファイルはいずれも**リテラルで形を書いている**（`"storage://normalized/assets/fig-1.png"`
等）ため、形が変わればそれらは落ちる。**落ちないのは `NormalizationService` の出力そのもの**である。

### 軸 3: pandoc への依存点

`pandoc` は **151 行 / 45 ファイル**に現れる。本番コードで pandoc を**実行する**のは
**`PandocConversionService.cs` の 1 ファイルだけ**である（`Program.cs` は DI 登録、
その他は文書・仕様書・IADR・i18n カタログ）。

**含意**: pandoc に依存する段は `IBodyConverter` の 1 ポートに閉じている。**接ぎ目は既にある**ので、
ゴールデンは「`IBodyConverter` の出力 → 正規化結果」を固定できる。

### 軸 4: 追随先（規則 9・10）

`FR-12_document-normalization` を引いている **14 行 / 10 ファイル**。

| 引いている側 | 追随の要否 |
| --- | --- |
| `docs/tests/FR-12_document-normalization.md` | **要**（本作業がケースを足す） |
| `scripts/test-spec-coverage-baseline.json` | §検査器への影響 で判定（**触らない**） |
| `docs/functional/FR-12_document-normalization.md` | **不要**。本作業は機能の記述を変えない（テストを足すだけ） |
| `.ai-context/specs/` 6 件・`.ai-context/adr/IADR-0008` | **不要**（確定済みの凍結記録。本文プロズは書き換えない） |
| `docs/data/document-and-version.md` | **不要**（版データの説明であり本作業と無関係） |

### 軸 5: 黙って除外したものは無い（規則 6）

除外は上記のとおり生成物と submodule だけである。**`src/ai-stock-trading` を除外した理由**は、
別プロジェクト（`AST/` 名前空間）の計画に属し、本 FR の射程外だからである。

## 実測（着手前に環境を確かめた）

| 確認 | コマンド | 結果 |
| --- | --- | --- |
| pandoc の有無 | `which pandoc` | **無い**（終了コード 1） |
| .NET SDK | `dotnet --version` | `10.0.400` |
| 既存のゴールデン器 | 軸 1 の走査 | **ファイル方式は 1 つも無い**（新設である） |
| 既存の正規化テスト | `Tests/NormalizationServiceTests.cs` | 4 件。すべて**部分一致**（`Should().Contain(...)`）。**スナップショット的なものは無い** |
| 変換段のテスト | `Tests/PandocConversionServiceTests.cs` | 3 件。うち 2 件は pandoc 導入環境でのみ走る（`Assert.SkipUnless`）。**この環境では 2 件が Skipped** |

## 設計

### D-1 どこで fake に差し替えるか

**`IBodyConverter` の境界で差し替える。** これは本作業が新設する接ぎ目ではなく、
`IADR-0008` が既に置いたポートである。

```
原本 ──[ IBodyConverter ]──▶ 本文 Markdown ＋ 抽出図 ──[ NormalizationService ]──▶ 正規化結果
        ▲ pandoc（外部プロセス）              ▲ ここから先が決定的
        └ この環境では実走できない            └ ゴールデンで固定する範囲
```

`IDiagramCoder`（LLM）と `IObjectStore`（オブジェクトストレージ）も同様に fake へ差し替える。
**3 つとも本番の実装では外部依存**であり、決定的な部分だけを残すには差し替えが要る。

### D-2 何を固定し、何を固定しないか（**この節が本作業の正直さの中心である**）

**固定する（golden が値として持つ）**

| # | 固定するもの | 壊れると何が起きるか |
| --- | --- | --- |
| G-1 | 正規化 Markdown の**全文**（本文＋コードブロック＋画像埋め込みの綴り・順序・空行） | 人手補正の置換が空振りする（`FigureMarkdown` の警告そのもの） |
| G-2 | `DocumentId`（決定的 GUID の**実際の値**） | 再変換で別 ID になり、文書管理が重複登録する |
| G-3 | `MarkdownUri` と**資産キーの全文**（`{id:N}/assets/{figureId}{ext}`） | 削除伝播（`DocumentObjectPurger`）が実体を取りこぼす |
| G-4 | 資産の contentType・**バイト長と SHA-256**（素通しの証拠） | 画像が壊れて保存されても気付かない |
| G-5 | `DiagramsCoded` / `DiagramsRetained` の件数 | 画面（SC-07）の表示と補正対象の抽出がずれる |
| G-6 | `Figures`（図 1 つ 1 つの記録。`IADR-0154`） | 縮退した図を後から引けなくなる |
| G-7 | **`IDiagramCoder` へ渡した `confidentiality`** | **LLM への送信制御が黙って無効化される**。出力に現れないので他のどのテストでも見えない |

**固定しない（golden の対象外。理由つき）**

| # | 固定しないもの | 理由 |
| --- | --- | --- |
| N-1 | **pandoc の変換結果そのもの** | この環境に pandoc が無い。入力 fixture は「変換器がこう出すであろう Markdown」を人が書いたものであり、**pandoc の実際の出力ではない** |
| N-2 | **docx / PDF / HTML のバイナリ解析** | 同上。原本ファイルは 1 バイトも読んでいない |
| N-3 | `PandocInputFormat` の contentType → 入力形式の対応 | `private static`。§やらなかったこと |
| N-4 | LLM の応答からのコード抽出 | `LlmGatewayDiagramCoderTests`（T-07 / T-12）が既に持つ。二重に作らない |
| N-5 | 保管の実体（MinIO/S3） | 製品未確定（機能仕様書 §スコープ外） |

🔴 **したがって「PDF のゴールデンテストを書いた」とは言えない。** 書いたのは
**「PDF 由来と宣言された変換器出力を正規化した結果」のゴールデン**である。`docs/tests/` の
記載も同じ言葉遣いに揃える。

### D-3 case の置き場と形

```
Tests/Golden/
  Cases/<name>.json      … 入力の宣言（RawDocumentFetched 相当 ＋ 図 ＋ 各図のコード化結果）
  Cases/<name>.body.md   … IBodyConverter が返す本文 Markdown（＝pandoc が出したとみなす本文）
  Expected/<name>.golden.md … 正規化結果のスナップショット
```

**本文を JSON から出したのは意図的である。** JSON へ埋めると `\n` の羅列になり、
**diff が読めなくなる**——ゴールデンの値打ちは「形の変化が PR の diff に現れること」なので、
読めない形で置くと目的を自分で潰す。

### D-4 golden の更新手順（手で書き換えない）

```bash
export PATH="$PATH:/root/.dotnet"
UPDATE_GOLDEN=1 dotnet test src/knowledge/backend/backend.slnx \
  --filter "FullyQualifiedName~NormalizationServiceTests"
```

**更新モードは書き込んだうえで、そのテストを失敗させる。** 「書いて緑」にすると、
`UPDATE_GOLDEN` が CI の環境に紛れ込んだとき**差分を無条件に飲み込んで緑になる**——
`PandocConversionServiceTests` のコメントが名指しした「走らなかったケースを Passed として報告する」と
同じ型の事故である。差分をレビューしたあと、変数を外して再実行すれば緑になる。

### D-5 fail-closed

- golden が無い → **失敗**（黙って作らない）
- case が 0 件 → **失敗**（走査が空振りしたまま緑にならない）
- `Expected/` に対応する case が無い golden（孤児）→ **失敗**（case を消したら golden も消す）

## テスト方針（`docs/tests/FR-12` への写像）

| # | 何を固定するか | ケース |
| --- | --- | --- |
| T-14 | Markdown 由来・図なし。本文が素通しであること・`DocumentId` の実値・`document.md` のキー | `markdown-plain` |
| T-15 | HTML 由来・縮退 1 件。画像埋め込みの綴りと資産キー（`.png`） | `html-article` |
| T-16 | Office(docx) 由来・コード化 1 件＋縮退 1 件。**両者が混ざったときの順序と空行**、`.jpg` への写像 | `office-docx-report` |
| T-17 | PDF 由来と宣言された変換器出力・縮退 2 件（`.svg` / `.bin`）。**機密区分が `IDiagramCoder` へ渡ること**（`restricted`） | `pdf-report` |
| T-18 | 器そのものの fail-closed（case 0 件・孤児 golden を検出する） | — |

## 変異試験（検出力の実測）

**無変異ベースラインと対で取る。** 「実データで緑」は検出力の証拠にならない
（`IADR-0293` が実測した型の事故）。

| # | 変異 | 期待 |
| --- | --- | --- |
| M-0 | 変異なし（ベースライン） | **全件緑** |
| M-1 | `FigureMarkdown.ImageEmbed` から figureId を落とす（`![]({uri})`） | KILL |
| M-2 | コードブロック前の空行を 1 つ減らす（`"\n\n```"` → `"\n```"`） | KILL |
| M-3 | `ExtensionFor` の `image/jpeg` を `.jpeg` へ変える | KILL |
| M-4 | `diagramCoder.CodeAsync` へ `confidentiality` ではなく `null` を渡す | KILL |
| M-5 | 資産キーの GUID 書式を `:N` から既定（ハイフンあり）へ変える | KILL |

実測結果は §変異試験の実測 に記す。

## 検査器への影響（`scripts/` を触らないための判定）

- **`check-test-spec-coverage.js`**: 「記載された対が baseline に無い → fail」。したがって
  **新しい `*Tests.cs` を作って `docs/tests/` へ名前を書くと落ちる**。
  → **新しいテストクラスを作らず、既存の `NormalizationServiceTests` へ足す。**
  器（ヘルパ）は `Tests/Golden/NormalizationGolden.cs` に置く——**`*Tests.cs` で終わらない**ので
  検査器のテストクラス検出に掛からず、baseline を動かさない。
  **これは検査器の回避ではない**: ヘルパは実際にテストクラスではなく、
  golden 対象のテストは `NormalizationServiceTests`（baseline に既に載っている対）に属する。
- **`check-trace-blocks.js`**: `docs/tests/FR-12` の追記は**表示テキストへ計画 ID を書かない**。
  ID は trace ブロックへ入れる。
- **`check-adr-numbering.js`**: `IADR-0298` を足すと **0298〜0300 が欠番になり落ちる**。
  この 3 番は並列トラックが予約済みであり、**統合時に埋まる**。§未決事項 1。

## 変異試験の実測（2026-08-29）

**すべて予定どおり落ちた。** 各変異のあと `git checkout --` で戻し、
`git status --porcelain` が空であることを確認している。件数は
`ConversionService.Worker.Tests`（全 81 件）である。

| # | 変異（実際に当てた差分） | 実測 |
| --- | --- | --- |
| M-0 | 変異なし（ベースライン） | **緑**。79 通過 / 2 skip（skip は pandoc 導入環境専用の 2 件で、本作業の前から同じ） |
| M-1 | `ImageEmbed` を `$"![{figureId}]({uri})"` → `$"![]({uri})"` | **KILLED** 4 件失敗 / 75 通過。golden 3 件（html / docx / pdf）＋ 既存 `Retains_diagram_as_image_when_coding_not_possible` |
| M-2 | `markdown.Append("\n\n```")` → `markdown.Append("\n```")` | **KILLED** 1 件失敗 / 78 通過。golden（docx）**だけ** |
| M-3 | `ExtensionFor` の `"image/jpeg" or "image/jpg" => ".jpg"` → `".jpeg"` | **KILLED** 1 件失敗 / 78 通過。golden（docx）**だけ** |
| M-4 | `diagramCoder.CodeAsync(figure, confidentiality, ct)` → `(figure, null, ct)` | **KILLED** 3 件失敗 / 76 通過。golden（html / docx / pdf）**だけ** |
| M-5 | 資産キーを `$"{documentId:N}/assets/..."` → `$"{documentId}/assets/..."` | **KILLED** 3 件失敗 / 76 通過。golden（html / docx / pdf）**だけ** |
| M-6 | `Cases/pdf-report.json` を退避（孤児 golden を作る） | **KILLED**。`Golden_case_set_is_closed` が失敗 |
| M-7 | `Expected/stray-case.golden.md` を追加（case の無い golden） | **KILLED**。同上（`BeEquivalentTo` が孤児を指摘） |

### バックエンド全体での実測（M-2 / M-4）

**「golden だけが落とした」は knowledge バックエンド全体でも成り立つ。** M-2 と M-4 を
`dotnet test src/knowledge/backend/backend.slnx`（12 アセンブリ・通過 1,160 件相当）へ当てた結果、
**落ちたのは `ConversionService.Worker.Tests` の golden だけ**で、他 11 アセンブリは全て緑だった。

すなわち本器が無ければ、**コードブロック前の空白の畳み方の変化**と
**LLM への機密区分の受け渡しの消失**は、リポジトリのどこにも映らない。

## 受け入れ基準（本作業で満たす条件）

- [x] 代表 4 形式の case について、正規化結果が golden と一致する
- [x] golden は環境変数で書き戻せ、**手で書き換える運用になっていない**
- [x] 更新モードは書き込んだうえで失敗し、**黙って差分を飲まない**（生成時に 4 件とも失敗することを実測）
- [x] case 0 件・孤児 golden で落ちる（fail-closed。M-6 / M-7）
- [x] 変異 5 件がすべて KILL され、無変異ベースラインが緑である
- [x] `docs/tests/FR-12_document-normalization.md` が**固定できたものとできなかったものを
      区別して**書いている
- [x] `dotnet test src/knowledge/backend/backend.slnx` が**全件**緑（12 アセンブリ・失敗 0）
- [x] `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` が通る
- [x] `node scripts/check-trace-blocks.js` / `check-doc-type-vocabulary.js` が通る
- [ ] 🔴 `node scripts/check-adr-numbering.js` は **IADR-0298〜0300 の欠番で落ちる**。
      本作業が作った欠番ではなく、並列トラックの予約分である（§未決事項 1）

## 気付いたが直さないもの（記録に留める）

1. **`NormalizationService` はコードブロックの綴りを `FigureMarkdown.CodeEmbed` から取っていない。**
   自前で ```` "\n\n```" + language + "\n" + code + "\n```\n" ```` を組み立てている。
   `FigureMarkdown` は「形は 1 箇所に閉じる」と宣言しているので、**宣言と実態がずれている**。
   出力は現状一致するため退行ではない。本作業は**現状の形を golden で固定するだけ**にする
   （直すと golden の値が変わり、「固定した」と「直した」が同じ diff に混ざる）。
2. **`PandocInputFormat` は `application/pdf` を知らない。** contentType の switch に無く、
   拡張子 `.pdf` も無いため、**既定の `markdown` に落ちる**——PDF を pandoc へ Markdown として
   食わせることになる。pandoc は PDF を入力に取れないため、実環境では E2（非 0 終了）へ倒れる。
   **本作業の射程外**（生産コードの変更＋実 pandoc が要る）。§未決事項 2。

## 未決事項

1. **`IADR-0298` の採番は 0298〜0300 の欠番を作る。** 並列トラックが予約済みであり、
   統合時に埋まる前提である。埋まらない場合は改番（先着尊重）が要る。
2. **PDF の入力形式判定**（上記 2）。実 pandoc を持つ環境での確認と、計画への環流要否の判断が要る。
3. 実 pandoc を使った変換段のゴールデン化。pandoc を持つ環境（CI コンテナ）でのみ走る形にするか、
   pandoc の版差で golden が揺れるかを実測してから決める。
