---
title: テスト仕様書 — FR-12 原本の正規化変換
type: test-spec
status: in-progress
created: 2026-07-03
updated: 2026-08-31
author: claude
---
<!-- trace:
ids: [FR-11, FR-12, UC-06, SC-07]
adrs: [ADR-0010, ADR-0012, ADR-0014]
iadrs: [IADR-0008, IADR-0104, IADR-0132, IADR-0162, IADR-0296, IADR-0298, IADR-0318]
specs: [20260703_FR-12_document-normalization-pipeline, 20260829_issue-447_fr12-golden-files, 20260831_issue-1097_pandoc-runtime-image-and-fail-closed]
issues: [#118, #379, #447, #506, #520, #525, #658, #1097]
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
| T-20 | **PDF の明示的な拒否** | PDF は pandoc の入力形式にならない。既定形式（`markdown`）へ落とさない | `UnsupportedSourceFormatException`。MIME・拡張子のどちらから判っても拒否する | 正規化変換: 例外 E5 |
| T-21 | 入力形式の写像 | MIME（不明なら拡張子）から pandoc 入力形式を決める | `docx` / `html` / `gfm` / `markdown` が期待どおり。PDF だけ例外 | 正規化変換: 本文変換 |
| T-22 | **実行時イメージの退行防止** | 実行時段の `apt-get install` 行に pandoc が居ること | Dockerfile の runtime 段に導入行がある。消すと落ちる | 実行時イメージへの pandoc 導入 |
| T-11 | 完了イベント | 変換後に `DocumentNormalized` が発行され後続へ連鎖する | Published = true、`MarkdownUri` 非空 | 正規化変換: 連鎖 / `RawDocumentFetchedConsumerTests` |
| T-12 | **画像保持（モデル拒否）** | `stopReason="refusal"`（送信は成立したがモデルが拒否）は本文が空で返るためフェンスも無いが、T-02 の「コード化不能」と混同せず拒否として記録する。縮退先（画像保持）は不変 | `Coded=false`、`Reason="llm-refused"`（`not-codeable` でない） | LLM 送信先切替・正規化変換 / `LlmGatewayDiagramCoderTests.Retains_with_refusal_reason_when_model_refuses` |

| T-13 | **契約の必須性** | `ConversionJobDto` の `diagramsCoded` / `diagramsRetained` / `hasCorrection` は C# が非 null（既定値つき）であり、応答本文には必ず出る。契約の `required` がこれと一致すること | `check-openapi-dto-drift` が違反 0。`required` から 1 つ外すと**落ちる**（変異 M1） | 正規化変換 / 応答スキーマの `required` を C# の非 null 性から起こす実装判断 / `scripts/check-openapi-dto-drift.js` |

| ID | 観点 | 内容 | 期待 | 起点 |
| --- | --- | --- | --- | --- |
| T-14 | **ゴールデン（Markdown 由来・図なし）** | 変換器出力をそのまま正規化したとき、本文が素通しであること・冪等 ID の**実値**・本文キーの全文を固定する | `Expected/markdown-plain.golden.md` と完全一致 | 正規化変換: 本文変換・冪等性 / `NormalizationServiceTests` |
| T-15 | **ゴールデン（HTML 由来・画像保持 1 件）** | 画像埋め込みの綴り（`![figureId](uri)`）と資産キー（`.png`）の全文、資産のバイト長・SHA-256 を固定する | `Expected/html-article.golden.md` と完全一致 | 正規化変換: 段階的コード化 / 人手補正が置換する目印 / 削除伝播が逆引きする鍵 |
| T-16 | **ゴールデン（Office(docx) 由来・コード化＋画像保持の混在）** | コードブロックと画像埋め込みが混ざったときの**順序と空行**、`image/jpeg` → `.jpg` の写像を固定する | `Expected/office-docx-report.golden.md` と完全一致 | 正規化変換: 基本フロー |
| T-17 | **ゴールデン（PDF 由来と宣言された変換器出力・画像保持 2 件）** | 未知の画像 MIME が `.bin` へ落ちること、および**機密区分が図コード化ポートへ渡ること**を固定する。後者は正規化結果に現れないため、他のどのテストでも見えない | `Expected/pdf-report.golden.md` と完全一致。`diagramCoderCalls` に `restricted` が並ぶ | 正規化変換: 機密制御 |
| T-18 | **器の fail-closed** | case が 0 件・case の無い golden（孤児）で落ちる。走査が空振りしたまま緑にならないこと | `Golden_case_set_is_closed` が失敗する | 退行防止の器そのものの見張り |

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
