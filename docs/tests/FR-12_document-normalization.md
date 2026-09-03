---
title: テスト仕様書 — FR-12 原本の正規化変換
type: test-spec
status: in-progress
created: 2026-07-03
updated: 2026-09-03
author: claude
---
<!-- trace:
ids: [FR-11, FR-12, UC-06, SC-07]
adrs: [ADR-0010, ADR-0012, ADR-0014, ADR-0070]
iadrs: [IADR-0008, IADR-0104, IADR-0132, IADR-0162, IADR-0296, IADR-0298, IADR-0320, IADR-0351, IADR-0362]
specs: [20260703_FR-12_document-normalization-pipeline, 20260829_issue-447_fr12-golden-files, 20260831_issue-1097_pandoc-runtime-image-and-fail-closed, 20260903_issue-1120_extract-media-path-rewrite, 20260903_issue-1192_pdf-text-layer-extraction]
issues: [#118, #379, #447, #506, #520, #525, #658, #1097, #1120, #1192]
-->

# テスト仕様書: 原本の正規化変換

## 対象

`src/knowledge/backend/Services/ConversionService/Tests`

## テストケース（受け入れ基準・フローの写像）

| ID | 観点 | 内容 | 期待 | 起点 |
| --- | --- | --- | --- | --- |
| T-01 | 図コード化成功 | コード化成功時、本文へコードブロックを埋込み画像資産は作らない | `DiagramsCoded=1`、`AssetUris` 空、本文に ```` ```mermaid ```` | FR-12 基本フロー / `NormalizationServiceTests` |
| T-02 | 画像保持（不能） | コード化不能時、画像を保存し本文へ参照を埋込む | `DiagramsRetained=1`、`AssetUris` 1件、本文に `![fig-1](` | 正規化変換: 段階的コード化 |
| T-03 | 画像保持（送信拒否） | `Sent=false`（機密区分で送信拒否）は画像保持へ縮退する | `DiagramsRetained=1`、`AssetUris` 1件 | 正規化変換: 機密制御 / 変換パイプライン・LLM ゲートウェイの決定 |
| T-04 | 冪等 DocumentId | `SourceId`＋原本パスから決定的に導出され、再変換で一致する | `r1.DocumentId == r2.DocumentId == DeterministicGuid.ForDocument(...)` | 正規化変換: 冪等性 |
| T-05 | 送信制御委譲 | `/complete` に `confidentiality`＋`purpose="diagram-coding"` を渡す | リクエスト本文に両フィールドが含まれる | LLM ゲートウェイの決定 / `LlmGatewayDiagramCoderTests` |
| T-06 | 縮退（呼び出し失敗） | LLM 呼び出しが例外／非200でも例外送出せず画像保持へ縮退する | `Coded=false`、`Reason` に失敗理由 | 正規化変換: 例外 E3 |
| T-07 | コード抽出 | ```` ```mermaid ```` / ```` ```plantuml ```` のフェンスから言語とコードを抽出する | `Coded=true`、`Language`/`Code` 一致 | 正規化変換: 基本フロー |
| T-08 | 決定的 Guid | 同一入力で同一 Guid、異なる入力で異なる Guid（RFC4122 v5 相当） | 期待どおり | 正規化変換: 冪等性 / `DeterministicGuidTests` |
| T-09 | pandoc 変換 | pandoc 導入環境でローカル Markdown 原本を実変換し本文を返す | 本文に原本タイトルが出現、図0件 | 正規化変換: 本文変換 / `PandocConversionServiceTests` |
| T-10 | **縮退は既定で起きない**（fail-closed） | pandoc 未導入／原本を読み出せないとき、既定は例外である | `BodyConversionUnavailableException`。**プレースホルダ本文を返さない** | 正規化変換: 例外 E1 |
| T-10b | 縮退（明示的に許可したとき） | `Conversion:AllowDegradedBodyConversion=true` のときだけプレースホルダ本文（図0件） | 本文にファイル名が出現、`Figures` 空 | 正規化変換: 例外 E1 |
| T-19 | **原本の取り寄せ** | オブジェクトストレージ上の原本を取得して pandoc に食わせる | 本文に原本の中身が出現し、プレースホルダの綴りを含まない。取得が 1 回起きる | 正規化変換: 本文変換 |
| T-20 | **PDF は拒否せず抽出器へ振り分ける**（2026-09-03 に反転） | PDF は pandoc の入力形式にならないが、**例外にせず**テキスト層の抽出器の担当とする（`PandocInputFormat` が `null`）。既定形式（`markdown`）へ落とさないことは不変 | `UnsupportedSourceFormatException` を**投げない**。MIME・拡張子のどちらから判っても `null` | 正規化変換: 処理フロー 3（振り分け） / `PandocConversionServiceTests` |
| T-20b | **PDF 以外の未対応形式は引き続き拒否**（T-20 の陽性対照） | 計画の対応形式表に無い未知の MIME ＋未知の拡張子は既定へ落とさず拒否する。「PDF で投げない」が「何も投げない」に退化していないことの証拠 | `UnsupportedSourceFormatException`（`.xls` / 拡張子なし / `image/png`） | 正規化変換: 例外 E5 |
| T-21 | 入力形式の写像 | MIME（不明なら拡張子）から pandoc 入力形式を決める | `docx` / `html` / `gfm` / `markdown`（`.txt` は明示）が期待どおり。PDF は `null` | 正規化変換: 本文変換 |
| T-22 | **実行時イメージの退行防止** | 実行時段の `apt-get install` 行に pandoc が居ること | Dockerfile の runtime 段に導入行がある。消すと落ちる | 実行時イメージへの pandoc 導入 |
| T-23 | **抽出媒体の参照書き換え（HTML 形）** | 図抽出が本文へ書き込む一時ディレクトリの絶対パスを、図の目印へ書き換える。docx 由来は属性が改行をまたぐ `<img>` である | 一時パス 0 件・`<img>` が残らない・図の**元の位置**に目印が入る | 正規化変換: 本文変換 / 抽出図の位置 |
| T-24 | **抽出媒体の参照書き換え（Markdown 画像形）** | html / gfm 由来は `![alt](パス)` の形で出る | 一時パス 0 件・元の位置に目印 | 同上 |
| T-25 | **図に写像できない媒体参照は落とす** | 画像でない拡張子は図として採らないため、その参照には対応する図が無い | 一時パス 0 件・目印も作らない | 抽出図の位置: 縮退 |
| T-25b | **構文を認識できなかった参照の安全網** | 画像構文でない形（`<embed>` 等）で一時パスが出ても残さない | 一時パス 0 件 | 抽出図の位置: fail-closed |
| T-26 | **原本由来の目印は落とす** | 原本が目印の綴りを含んでいたら受け取らない | 出力に目印スキームが残らない | 抽出図の位置: 曖昧なら受け取らない |
| T-27 | **同一媒体の 2 度参照** | 参照 2 件に対して目印も 2 件置き、両方が置換される | 目印 2 件・置換後に残らない | 抽出図の位置 |
| T-27b | **参照と図はパスで対応付ける** | 採番は媒体ファイル名の序数順であり、本文の出現順とは限らない | 各参照がそのパスから起こした図の目印になる | 同上 |
| T-28 | **媒体外の参照は触らない**（陽性対照の対） | 外部 URL・原本内の相対パスはそのまま残す。これが無いと「一時パス 0 件」は「画像参照を全部消した」でも達成できる | 外部 URL の `<img>` と相対パスの `![]()` が不変 | 同上 |
| T-29 | **画像保持の図を目印の位置へ埋め込む** | 目印を最終の画像参照へ置換する（末尾へ append し直さない） | 本文中の元の位置に画像参照・目印は残らない | 正規化変換: 段階的コード化 / 抽出図の位置 |
| T-30 | **コード化した図を目印の位置へ埋め込む** | 同上（コードブロックとして） | 本文中の元の位置にコードブロック | 同上 |
| T-31 | **目印が無ければ末尾へ append**（回帰） | 目印を持たない本文では従来どおり末尾へ足す。綴りは従前とバイト等価 | append の全文一致 | 抽出図の位置: 縮退 |
| T-32 | **人手補正が空振りしない** | 位置を本文中へ戻しても、埋め込みの綴りは人手補正が置換する目印のままである | `TryReplaceImageWithCode` が true を返し、コードブロックへ替わる | 人手補正の目印 / 抽出図の位置 |
| T-33 | **実 pandoc の端から端**（pandoc 導入環境のみ） | 図を含む原本を実変換し、一時パスが残らず図が 1 度だけ出ること | 一時パス 0 件・`conv-` 0 件・目印 1 件 | 正規化変換: 本文変換 / 抽出図の位置 |
| T-34 | **振り分け（PDF → 抽出器）** | `IBodyConverter` の合成器が PDF を抽出器へ、それ以外を pandoc へ渡す。外部プロセスの有無に依存しないよう縮退プレースホルダの綴りで「どちらが走ったか」を見る | PDF は「から pdftotext で抽出します」、docx / html / md / txt は「から pandoc で変換します」。未知の形式は取り寄せる前に `UnsupportedSourceFormatException` | 正規化変換: 処理フロー 3 / `FormatRoutingBodyConverterTests` |
| T-35 | **テキスト層なしの判定（純関数）** | 抽出結果が空白のみ（空・改行・改頁 `\f`・タブ）なら本文なし。可視の文字が 1 つでもあれば本文あり | `ToBody` が `(""、true)` ／ `(本文、false)`。整形は改行正規化・行末空白除去・空行の畳み込みだけ | 正規化変換: 例外 E6 / `PdfTextLayerConverterTests` |
| T-36 | **テキスト層あり PDF の抽出**（pdftotext 導入環境のみ・陽性） | 実行時に生成した最小 PDF（Helvetica のテキスト）から本文を取り出す。図は抽出しない | 本文に描いた文字列が出現・`BodyAbsent = false`・図 0 件・プレースホルダではない | 正規化変換: 処理フロー 3 |
| T-37 | **テキスト層なし PDF は本文なしで完了**（pdftotext 導入環境のみ・陰性） | 描画だけの PDF（スキャン相当）は**例外にならず** `BodyAbsent = true` で返る | 例外なし・本文空・図 0 件 | 正規化変換: 例外 E6 |
| T-38 | **本文があるのに作れない失敗は従来どおり**（pdftotext 導入環境のみ） | 壊れた PDF は `pdftotext` が非 0 終了する → 例外（再試行 → デッドレター）。原本未解決も既定は例外 | `InvalidOperationException` ／ `BodyConversionUnavailableException` | 正規化変換: 例外 E1 / E2 |
| T-39 | **抽出器不在は fail-closed**（pdftotext 未導入環境のみ） | pdftotext が無いとき既定は例外。縮退は `AllowDegradedBodyConversion=true` のときだけで、縮退は「本文なし」ではない | `BodyConversionUnavailableException` ／ 明示許可時はプレースホルダ＋ `BodyAbsent = false` | 正規化変換: 例外 E1 |
| T-40 | **実行時イメージの退行防止（poppler-utils）** | 実行時段の `apt-get install` 行に `poppler-utils` が居ること | Dockerfile の runtime 段に導入行がある。消すと落ちる | 実行時イメージへの抽出器導入 |
| T-41 | **本文なしはジョブの成功として記録される** | コンシューマは `BodyAbsent = true` の正規化結果を `succeeded` で確定し、発行口へも同じ値を渡す | `status = succeeded`・`bodyAbsent = true`・`deadLettered = false`・`error = null`。本文ありでは `bodyAbsent = false`（陽性対照） | 正規化変換: 例外 E6 / `RawDocumentFetchedConsumerJobTests` |
| T-42 | **読み取りモデルの標識** | `bodyAbsent` は succeeded の内訳として保存され、処理を再開したら落ちる | 成功直後 true → 再受信で processing ＋ false | `ConversionJobStoreTests` |
| T-43 | **発行イベントへの写像** | `DocumentNormalized.BodyAbsent` へ写る（既定 false なので true を渡して見る） | `ev.BodyAbsent == true` | `MassTransitDocumentNormalizedPublisherTests` |
| T-11 | 完了イベント | 変換後に `DocumentNormalized` が発行され後続へ連鎖する | Published = true、`MarkdownUri` 非空 | 正規化変換: 連鎖 / `RawDocumentFetchedConsumerTests` |
| T-12 | **画像保持（モデル拒否）** | `stopReason="refusal"`（送信は成立したがモデルが拒否）は本文が空で返るためフェンスも無いが、T-02 の「コード化不能」と混同せず拒否として記録する。縮退先（画像保持）は不変 | `Coded=false`、`Reason="llm-refused"`（`not-codeable` でない） | LLM 送信先切替・正規化変換 / `LlmGatewayDiagramCoderTests.Retains_with_refusal_reason_when_model_refuses` |

| T-13 | **契約の必須性** | `ConversionJobDto` の `diagramsCoded` / `diagramsRetained` / `hasCorrection` は C# が非 null（既定値つき）であり、応答本文には必ず出る。契約の `required` がこれと一致すること | `check-openapi-dto-drift` が違反 0。`required` から 1 つ外すと**落ちる**（変異 M1） | 正規化変換 / 応答スキーマの `required` を C# の非 null 性から起こす実装判断 / `scripts/check-openapi-dto-drift.js` |

| ID | 観点 | 内容 | 期待 | 起点 |
| --- | --- | --- | --- | --- |
| T-14 | **ゴールデン（Markdown 由来・図なし）** | 変換器出力をそのまま正規化したとき、本文が素通しであること・冪等 ID の**実値**・本文キーの全文を固定する | `Expected/markdown-plain.golden.md` と完全一致 | 正規化変換: 本文変換・冪等性 / `NormalizationServiceTests` |
| T-15 | **ゴールデン（HTML 由来・画像保持 1 件）** | 画像埋め込みの綴り（`![figureId](uri)`）と資産キー（`.png`）の全文、資産のバイト長・SHA-256 を固定する | `Expected/html-article.golden.md` と完全一致 | 正規化変換: 段階的コード化 / 人手補正が置換する目印 / 削除伝播が逆引きする鍵 |
| T-16 | **ゴールデン（Office(docx) 由来・コード化＋画像保持の混在）** | コードブロックと画像埋め込みが混ざったときの**順序と空行**、`image/jpeg` → `.jpg` の写像を固定する | `Expected/office-docx-report.golden.md` と完全一致 | 正規化変換: 基本フロー |
| T-17 | **ゴールデン（PDF 由来と宣言された変換器出力・画像保持 2 件）** | 未知の画像 MIME が `.bin` へ落ちること、および**機密区分が図コード化ポートへ渡ること**を固定する。後者は正規化結果に現れないため、他のどのテストでも見えない | `Expected/pdf-report.golden.md` と完全一致。`diagramCoderCalls` に `restricted` が並ぶ | 正規化変換: 機密制御 |
| T-17b | **ゴールデン（テキスト層あり PDF 由来と宣言された抽出器出力・図なし）** | プレーンテキスト相当の本文が素通しで保管され、`bodyAbsent` が立たないことを固定する | `Expected/pdf-text-layer.golden.md` と完全一致（`bodyAbsent : false`） | 正規化変換: 処理フロー 3 |
| T-17c | **ゴールデン（テキスト層なし PDF 由来と宣言された抽出器出力・空）** | 本文なしで完了し（`bodyAbsent : true`）、**空の `document.md`** が保管され、図も資産も作らないことを固定する | `Expected/pdf-no-text-layer.golden.md` と完全一致（`markdownLength : 0`） | 正規化変換: 例外 E6 |
| T-18 | **器の fail-closed** | case が 0 件・case の無い golden（孤児）で落ちる。走査が空振りしたまま緑にならないこと | `Golden_case_set_is_closed` が失敗する（PDF の 2 case も名指しで要る） | 退行防止の器そのものの見張り |

## 補足

- 外部依存（pandoc / LLM Gateway / オブジェクトストレージ）はフェイク／インメモリ実装で差し替える
  （`FakeBodyConverter` / `FakeDiagramCoder` / `RecordingObjectStore`）。
- `PandocConversionServiceTests` は pandoc の導入有無が環境依存のため、前提を満たさないケースは
  **真の Skipped**（`Assert.Skip*`）にする。**ソフトスキップ（`if (cond) return;`）にしない** ——
  走らなかったケースが Passed として報告され、実行実績が無いのに緑に見えるためである。
  T-09 / T-19 は pandoc 未導入環境で skip され、T-10 / T-10b は pandoc 導入環境で skip される
  （どちらの環境でも「両方が走った」ことにはならない。**skip 件数を実行結果に必ず出すこと**）。
- T-22 は Dockerfile を読む静的なテストであり、**焼いたイメージに pandoc が実在するか**は見ない。
  実在するかは配備側の readiness（`pandoc` チェック）が見る。この 2 段を混同しない。
- **T-13 は C# のテストではなく検査器で持つ**（`scripts.repo.test.js` が CI から起動する）。
  契約と C# の一致は**個々の実行時挙動ではなく静的な突合**で確かめるのが確実であり、
  同型の事故はいずれも実行時テストでは捕まっていない。
- **ゴールデンファイル（T-14〜T-18）は `Tests/Golden/` に置く。** 入力は `Cases/<name>.json`
  （宣言）＋ `Cases/<name>.body.md`（変換器が出したとみなす本文）、期待値は
  `Expected/<name>.golden.md` である。golden は**手で書き換えない** ——
  `UPDATE_GOLDEN=1 dotnet test ... --filter "FullyQualifiedName~NormalizationServiceTests"` で
  書き戻し、差分をレビューしてからコミットする。**更新モードは書き戻したうえでテストを失敗させる**
  （変数が CI の環境へ紛れ込んだときに差分を無条件で飲み込んで緑になるのを防ぐ）。
- 🔴 **ゴールデンで pandoc は実走していない。** 入力は「変換器がこう出すであろう Markdown」を
  人が書いたものであり、**docx / PDF / HTML の原本は 1 バイトも読んでいない**。
  したがって T-17 は「PDF のゴールデンテスト」ではなく、**「PDF 由来と宣言された変換器出力を
  正規化した結果」のゴールデン**である。原本の解析と入力形式の判定（`PandocInputFormat`）は
  **固定していない**。
- **ゴールデンが固定するもの**: 正規化 Markdown の全文 / 冪等 ID の実値 / 本文キー・資産キーの全文 /
  資産の contentType・バイト長・SHA-256 / コード化・保持の件数 / 図 1 つ 1 つの記録 /
  図コード化ポートへ渡した機密区分。**固定しないもの**: pandoc の変換結果・原本のバイナリ解析・
  入力形式の判定・LLM 応答からのコード抽出（T-07 / T-12 が持つ）・実ストレージの挙動。
- **検出力は変異試験で実測してある**（変異 5 件がすべて KILL、無変異ベースラインは緑）。
  うち 3 件（空白の畳み方・機密区分の受け渡し・資産キーの GUID 書式）は
  **バックエンド全体でゴールデンだけが落とした**。
- Vision 画像入力に対する結合試験は別タスクで扱う。

> **［2026-08-31 追記］実 pandoc 変換・実オブジェクトストレージは「別タスク」ではなくなった。**
> 実行時イメージへ pandoc を導入したうえで、**稼働クラスタで docx / HTML / PDF の実原本を変換して
> 実測した**（docx は実図抽出 1 件つき。PDF は明示的な拒否）。自動テストはこの経路を持たない
> —— 実オブジェクトストレージと実 pandoc の両方を要し、いずれもコンテナ実行時依存だからである。
> **T-19 は差し替えたストレージで「取り寄せてから変換する」形だけを固定する。**

> **［2026-09-03 追記］図抽出が本文へ書き込む一時パスと、図の二重化を解消した。**
> 図抽出は本文中の画像参照を一時ディレクトリの絶対パスへ書き換えるが、そのディレクトリは
> 変換直後に消える —— 従前は**壊れた参照が保管物に残ったまま、同じ図が末尾にも足されて
> 二重に出ていた**。変換器が参照を目印へ書き換え、正規化側が本文中の元の位置で最終参照へ
> 置換する形に改めた（T-23〜T-33）。
>
> - **T-23〜T-28 の入力は実 pandoc の出力である。** 稼働クラスタの変換サービス pod で
>   実変換して採取した綴りをそのまま固定入力にしてある。「変換器がこう出すであろう」と
>   想像して書くと、**属性が改行をまたぐ `<img>`** のような実際の形を取りこぼす。
>   ゴールデン（T-14〜T-18）の「pandoc は実走させない」方針は変えていない。
> - **T-33 だけが実 pandoc を要する**（未導入環境では真の Skipped）。書き換えそのものの
>   検査は T-23〜T-28 が pandoc 無しで行うため、CI でも実際の綴りを検査できる。
> - **T-28 は陰性結論（一時パス 0 件）に対する陽性対照である。** これが無いと
>   「画像参照を全部消す」実装でも受け入れ基準を満たしてしまう。
> - ゴールデンは**目印を含む case（`html-article` / `office-docx-report`）と含まない case
>   （`pdf-report`）の両方**を持つ。前者は図が本文中の元の位置へ入ること、後者は目印が
>   無いときに末尾へ append する経路を固定する。

> **［2026-09-03 追記］PDF はテキスト層の抽出器へ振り分け、テキスト層が無ければ「本文なしで完了」にした。**
> T-20 は「PDF の明示的な拒否」から「PDF は拒否せず抽出器へ」へ**反転**した（T-20b がその陽性対照）。
> 追加した T-34〜T-43 と T-17b / T-17c の作法:
>
> - **PDF の原本はテスト実行時に生成する**（`MinimalPdf`。ASCII のみ・xref 計算済み）。追跡下に
>   バイナリを置かない前提（NUL バイト検査）を守るためで、生成器そのものは検証対象ではない。
> - **実 `pdftotext` を要するケース（T-36〜T-38）は真の Skipped**。未導入環境でしか走らないケース
>   （T-39）と対になっており、どちらの環境でも「両方が走った」ことにはならない。**在／不在の判定は
>   終了コードではなく版の行で行う**（poppler は `-v` で 0、同名の xpdf 版は 99 を返す。開発機の
>   xpdf 版で終了コードを見ると全ケースが Skipped になり、実行実績が無いのに緑に見えた）。
> - **空判定（T-35）は純関数**で、pdftotext 無しで走る。変異試験（空判定を「常に本文あり」へ変える）で
>   T-35 の 5 件と T-37 が落ちることを確かめた。
> - **振り分け（T-34）は縮退プレースホルダの綴りで観測する**。外部プロセスの有無に依存しないよう、
>   縮退を明示許可して原本を解決できないストレージを渡すと、両変換器は必ず自分のプレースホルダを返す。
> - ゴールデンは `bodyAbsent` を `## result` に描く（既存 4 件は `false` の 1 行が増えた）。
>   `pdf-no-text-layer` は `.body.md` が空であり、空の `document.md` が保管されることを固定する。
>   「pandoc / pdftotext は実走させない」方針は変えていない。
