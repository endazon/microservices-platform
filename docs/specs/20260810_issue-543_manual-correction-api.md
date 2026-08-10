---
title: 作業仕様書 — 人手補正の本文取得 API と補正投稿 API を新設する（Phase 1 = 図のコード化。#543）
type: work-spec
status: fixed
related_ids:
  - FR-12
  - UC-06
  - SC-07
  - ADR-0012
  - IADR-0029
  - IADR-0042
  - IADR-0043
  - IADR-0122
  - IADR-0127
  - IADR-0128
  - IADR-0137
  - IADR-0154
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../adr/IADR-0042_conversion-job-read-model.md"
  - "../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md"
  - "../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md"
  - "../adr/IADR-0137_conversion-dead-letter-marker.md"
  - "../screens/SC-07_conversion-jobs.md"
---

# 作業仕様書: 人手補正の本文取得 API と補正投稿 API（#543）

## 起点となる計画書（トレーサビリティ）

- **FR-12**（文書正規化）／**UC-06**（文書を正規化変換する。代替フロー「変換結果を管理者が補正して再登録する」）／**SC-07**（変換ジョブ画面）
- 計画の確定: `05_screens/01_screens.md` §SC-07「人手補正の契約とその範囲」（**確定・2026-08-05**。利用者裁定〔質問票 第12回 Q12・Q20 および派生 Q31・Q33〕）
- 関連 issue: **#545**（IADR-0042 の表題と実体のずれ。**本 issue の着手で解消する**）
- 環流元: planning#198 / planning#200

## 射程

**射程内**:

1. 画像保持へ縮退した図を**ジョブ単位で識別できるようにする**（現在どこにも記録が無い。後述 母集合 軸 3）
2. **本文取得 API**（Phase 1 の「本文」＝図コードと元の図画像）
3. **補正投稿 API**（PlantUML / Mermaid のコード片を受け取り、取り込みへ再投入する）
4. **補正版を正とする**ための retry 側のゲート（補正の破棄に明示確認を要求する）
5. `ConversionJobDto` への標識（「補正あり」・図のコード化件数）
6. **後継 IADR（IADR-0154）** と [[IADR-0042]] への日付つき追記（#545 の回答どおり）

**射程外**（理由つき）:

| 除外するもの | 理由 |
| --- | --- |
| **SC-07 画面の 2 ペイン編集 UI** | 本 issue の表題は「**API を新設する**」であり、画面は別作業である。**別 issue を起票する**（[[IADR-0116]] 規約 1: 1 issue = 1 PR） |
| **Phase 2（変換結果 Markdown 全体の編集）** | 計画が明示的に繰り延べている（`01_screens.md:330`） |
| **`src/ai-stock-trading`** | 別プロジェクトの submodule。変更しない |
| 変換パイプライン（pandoc / LLM）の挙動そのもの | 縮退の判定条件は [[ADR-0012]] の確定であり、本件は縮退**後**の救済手段を足すだけである |

## 母集合（[[IADR-0141]] 決定 1・走査基準は #648 §7 で改めた「語ではなくファイルから引く」）

**issue 本文の表は転記していない。以下はすべて自分で引いた実測である。**

### 引き方

```console
$ grep -rl --exclude-dir={.git,node_modules,ai-stock-trading,bin,obj,dist,coverage} \
    -E 'SC-07|FR-12|UC-06|ConversionJob|/bff/conversion|人手補正|再変換|conversion-job' .
```

→ **237 ファイル**（`planning/` 含む）。**拡張子で絞らず**（規則 3）、**2 段目の行フィルタを掛けず**（規則 4）、
資源に関係するファイルを選んで**全文を読む**（#648 §7）。関係するのは実装 12・テスト 8・文書 14。

### 軸 1: 変換ジョブの口（全数。ワーカー上）

`.../ConversionService.Worker/Foundation/Endpoints/ConversionJobEndpoints.cs`

| 行 | 口 | 用途 |
| --- | --- | --- |
| 16 | `MapGroup("/jobs")` | 群 |
| 19 | `GET /` | 一覧（`?status=`） |
| 24 | `GET /{id:guid}` | 個別 |
| 36 | `POST /{id:guid}/retry` | 再変換 |

**3 本のみ。**［#545 の実測（`de55761`）および planning の実測（2026-08-05）と一致する。］

### 軸 2: BFF 側の口（全数）

`Knowledge.Bff.Endpoints/ConversionBffEndpoints.cs` → `BffConversionJobList` / `BffConversionJobGet` / `BffConversionJobRetry` の **3 本**。
認可は照会が admin ＋ operator、`retry` が `AdminOnly` の AND 合成（[[IADR-0128]] 決定 1）。

### ★ 軸 3: 補正の対象（画像保持へ縮退した図）はどこに記録されているか

**結論: どこにも記録されていない。** これが本 issue の実際の難所である。

```console
$ grep -rn 'MarkSucceeded\|NormalizationResult\|Retained\|AssetUris' \
    src/knowledge/backend/Services/ConversionService/src/ --include=*.cs
```

| 場所 | 縮退した図の情報 |
| --- | --- |
| `NormalizationService.cs:44-58` | `figure.FigureId` ごとに画像を保存し `![figureId](uri)` を本文へ埋め込む |
| `NormalizationService.cs:64` | `NormalizationResult(documentId, markdownUri, assetUris, coded, retained)` で**返している** |
| `RawDocumentFetchedConsumer.cs:56` | `jobs.SucceedAsync(fetchId, result.DocumentId, result.MarkdownUri, ct)` —— **`assetUris` / `coded` / `retained` を渡していない** |
| `RawDocumentFetchedConsumer.cs:59-61` | **ログ行にだけ出力**して捨てている |
| `ConversionJob.cs:65` | `MarkSucceeded(documentId, markdownUri)` —— 図の情報を持つ引数が無い |
| `ConversionJobDbContext.cs` | `DbSet<ConversionJob>` **1 つだけ**。図のテーブルは無い |

→ **「どの図がコード化に失敗して画像で残っているか」を問い合わせる手段が無い。**
**補正投稿 API を足す前に、補正対象を記録するところから要る。**

### ★ 軸 4: 縮退した図を持つジョブは `failed` ではなく `succeeded` である

**これは本件の設計を左右する。** UC-06 は縮退を**例外フローの中の正常な収束**として定めている:

> 本文変換・資産保存の恒久失敗は再試行し、継続失敗はデッドレターへ送る。
> **図コード化（LLM）の失敗は画像保持へ縮退し、後日の人手補正・再登録でコード化する**
> （`03_usecases/01_usecases.md:167`）

実装もそのとおりで、`NormalizationService` は縮退を例外にせず本文を完成させ、
`RawDocumentFetchedConsumer` は `DocumentNormalized` を発行して `SucceedAsync` する。
**ジョブの状態は `succeeded` である。**

いっぽう既存コードは**人手補正 ≡ 再変換 ≡ 失敗ジョブ限定**と読める書き方をしている:

| 箇所 | 記述 | #543 後 |
| --- | --- | --- |
| `ConversionJob.cs:88` | 「**UC-06: 人手補正は失敗ジョブに限る。**失敗以外は再変換不可」 | **誤りになる**（正しくは「**再変換**は失敗ジョブに限る」） |
| `ConversionJobEndpoints.cs:31` | 「FR-12 例外フロー / UC-06: **人手補正。**失敗ジョブのみ再変換する」 | **同上** |
| [[IADR-0042]] 表題 | 「状況照会・**人手補正 API**」 | **#545 のとおり後継 IADR で実体を定める** |

**「人手補正」という語が、実装では「再変換」の別名として使われている。**
Phase 1 の人手補正は**別の操作**（成功ジョブの図を後から直す）であり、**同じ語で 2 つを指したまま実装すると混ざる。**

### 軸 5: hi-fi モックアップが要求する表示（`planning .../mockups/hi-fi/sc-07.html:420-430`）

| 行 | 表示 | 契約から導出できるか |
| --- | --- | --- |
| `#J-9812` 状態「**✕ 図コード化失敗**」備考「画像保持へ縮退済み」 | **できない**（縮退件数が DTO に無い） |
| `#J-9805` 備考「**Mermaid 2図** `補正あり`」 | **できない**（コード化件数・補正標識が DTO に無い） |
| 人手補正パネル「図コード（編集可）／元の図（画像保持中）」 | **できない**（図の一覧・画像を返す口が無い） |

**「✕ 図コード化失敗」は状態の 5 値目ではない。** `01_screens.md:317` が
「**ジョブ状態モデルは 4 値である**」と確定しており、デッドレターを `failed` の内訳として
独立した真偽値にした [[IADR-0137]] 決定 1 と**同じ扱い**にする。
[[IADR-0127]]「**状態表示は契約から導出できる値だけで作る**」に従い、**導出元を DTO へ載せる。**

### 軸 6: 機械クライアント —— **誰がこの口を呼ぶか**

```console
$ grep -rn --exclude-dir={bin,obj,coverage} -E '"/jobs|/bff/conversion' src/ \
    --include=*.cs --include=*.ts --include=*.tsx
```

→ 本番の呼び出し元は `ConversionBffEndpoints.cs`（BFF）と
`knowledge/frontend/src/features/sc07-conversions/useConversionJobs.ts`（生成フック経由）のみ。
残りはテスト（`ConversionJobEndpointTests` / `BffConversionEndpointTests`）とスタブ（`BffTestFactory.cs`）。
**`src/ai-stock-trading` からの呼び出しは 0 件**（除外せず走査して確認）。

### 軸 7: **誰がこの応答を読むか**（クライアントの解析層。#640 で引き漏らした軸）

- 生成クライアント（orval）: `platform/frontend/src/foundation/api/generated/conversion/*` —— **`pnpm run codegen` の再生成が要る**
- 409 の本文は `ApiError.body` へ載る（#640 で `parseProblemDetails` が `message` を読むよう直した）。
  **`corrections_would_be_lost` の 409 も同じ経路で画面まで届く。**

### 軸 8: 追随が要る文書・生成物（規則 8 —— この変更で新たに誤りになる自分の記述）

| 追随先 | 何が誤りになるか |
| --- | --- |
| `docs/screens/SC-07_conversion-jobs.md` | §hi-fi 対応表 #10 / #12「人手補正 → **しない。契約の不在**」・§実装しない要素の理由 (a)・§未決事項 1 |
| `docs/functional/FR-12_document-normalization.md` | 縮退した図の扱い（**E3 の説明自体は誤りにならなかった**が、縮退した図が記録・補正できるようになった旨と、埋め込み形が置換の目印になった旨の追記が要る） |
| `docs/tests/SC-07_conversion-jobs.md` | テスト仕様の追加。**`docs/tests/FR-12_*` は追加不要だった**——同書の T-02 / T-03 / T-06 / T-12 は縮退の**判定**を写像しており、本 issue はその判定を変えていない |
| `docs/data/conversion-job.md` | **図テーブルの追加**（データ仕様書） |
| `docs/adr/IADR-0042_*` | **日付つき追記**（表題の「人手補正 API」は設計時点の想定であり、実体は後継 IADR が定める） |
| `docs/api/openapi.yaml` ＋ `docs/api/BFF_bff-surface.md` | `/bff/conversion/jobs/**` の新設 |
| `src/platform/frontend/src/foundation/api/generated/**` | **orval 再生成（コミットする）** |
| `scripts/contract-schema-baseline.json` | `contract-schema` ジョブが検査する |
| `BffTestFactory.cs` | 後段スタブへ図の応答を足す |
| `feedback/20260805_sc05-07-admin-contract-gaps.md` | **解消の追記** |
| `ConversionJob.cs:88` / `ConversionJobEndpoints.cs:31` のコメント | **軸 4 のとおり誤りになる。書き換える** |

**i18n・カバレッジ床は画面を作らないため本 issue では動かない**（画面の別 issue 側で扱う）。

## 判断（実測で決めたもの）

### 判断 1: #545 の問いは「**後継 IADR を起こす**」で答える（表題の是正ではない）

[[IADR-0042]] 決定 3 が列挙する口は `GET /jobs`・`GET /jobs/{id}`・`POST /jobs/{id}/retry` の 3 本であり、
**同 ADR は当時の決定を正しく記述している。** 表題の「人手補正 API」は設計時点の想定である。

表題だけ直すと「**人手補正 API はどこで決まったのか**」が宙に浮く。Phase 1 は
[[IADR-0042]] が想定していた口とは**範囲の違う別の決定**である（図のコード化に限る・補正版を正とする・
マージを採らない）。よって **[[IADR-0154]] を後継として起こし、[[IADR-0042]] へは日付つき追記**を残す。
（#545 に同趣旨の回答が 2026-08-05 付で入っており、本 issue の着手をもって #545 を閉じる。）

### 判断 2: 図は **`ConversionJobFigure` として永続化**する（本文 Markdown からの再抽出にしない）

代替案（本文 Markdown を読み直して `![figureId](uri)` を正規表現で拾う）は採らない:

- 本文はオブジェクトストレージにあり、**一覧表示のたびに全ジョブ分の Markdown を読む**ことになる
- コード化に**成功**した図（```mermaid ブロック）と縮退した図を、本文の形だけから確実に区別できない
  —— 利用者が原本に書いた Mermaid ブロックと**見分けが付かない**
- 補正の有無（「補正あり」標識）は本文からは分からない

**ジョブの子テーブルに持つ。** [[IADR-0043]]（変換ジョブの永続化）が敷いた EF + マイグレーションの
形をそのまま踏襲する。

### 判断 3: 画像は **BFF がオブジェクトストレージから読んで返す**（ワーカーに配信させない）

先例がある —— `DocumentBffEndpoints.cs:61` の `GET /bff/documents/{id}/content` は
BFF が `IObjectStorageClient` で本文を解決して返している（`:224-228`）。**同じ形を採る。**

ワーカー側へ画像配信を足さないのは [[IADR-0029]]（ワーカーは最小 HTTP サーフェス）に従うためである。
**ワーカーが返すのは図のメタデータ（`imageUri` を含む）だけ**で、バイト列は BFF が解決する。

### 判断 4: 補正版を正とするゲートは **API 側で強制する**（確認ダイアログだけに頼らない）

`ConversionJobEndpoints.cs:33` は既に
「**UI 制御だけに頼らず API 側でも状態を強制し**、処理中の二重発行・成功済みの不要な再処理を防ぐ」
と書いている（レビュー #172 指摘対応）。**補正の破棄も同じ扱いにする。**

> `POST /jobs/{id}/retry` は、そのジョブに補正があるとき **409 `corrections_would_be_lost`**
> （本文に補正件数を載せる）を返す。破棄してよい場合だけ `?discardCorrections=true` を付けて再送する。

画面の確認ダイアログはこの 409 を受けて出す（#640 の `usageCount` つき 409 と同じ経路。軸 7）。
**確認をダイアログだけに置くと、生成クライアントや別経路の呼び出しが素通りする。**

### 判断 5: 補正の反映は **図ブロックの置換**であり、pandoc からのやり直しではない

`NormalizationService.cs:56` が埋め込む形は `![{figureId}]({uri})` で**決定的**である。
補正投稿はこの 1 行を ```` ```{language}\n{code}\n``` ```` へ置換して Markdown を保存し直し、
`DocumentNormalized` を**再発行**する（`DocumentId` は決定的なので文書は同一。取り込みへ再投入される）。

**原本からのやり直し（＝ retry）にしない**のは、やり直すと LLM が再び失敗して縮退へ戻り、
**補正が消える**ためである。計画の「マージは採らない」（`01_screens.md:334`）と同じ理由である。

### 判断 6: 認可は **人手補正の 3 本すべて `AdminOnly`**

計画: 「**閲覧は管理者・運用者。再変換の実行と人手補正は管理者限定**」（`01_screens.md:314`）。

**図の一覧・画像の取得も「人手補正」の一部**である（2 ペインを開く操作そのもの）。
群の既定を残して個々の端点へ `AdminOnly` を積む **AND 合成**（[[IADR-0128]] 決定 1）で揃える。
サービス側にも同じ制約を置く多層防御（[[IADR-0044]]）——ただしワーカーは認可を課さない方針
（[[IADR-0029]] / [[IADR-0128]] 決定 3）なので、**ネットワーク分離の回帰ガード
（`NetworkIsolationTests`）が引き続き代償統制**である。ここは**変えない**。

## 実装方針

### 契約（`Knowledge.Contracts`）

```csharp
// 図 1 つ分。Phase 1 の補正対象。
public record ConversionFigureDto(
    string FigureId, bool Coded, string? Language, string? Code,
    string? ImageUri, string? ImageContentType, string? Caption,
    bool Corrected, DateTimeOffset? CorrectedAt);

// 補正投稿の要求。
public record FigureCorrectionRequest(string Language, string Code);
```

`ConversionJobDto` へは **末尾に既定値つきで**追加する（[[IADR-0122]] 決定 2）:
`DiagramsCoded = 0` / `DiagramsRetained = 0` / `HasCorrection = false`。

### 口

| 層 | 口 | 認可 |
| --- | --- | --- |
| ワーカー | `GET /jobs/{id}/figures` | 課さない（[[IADR-0029]]） |
| ワーカー | `POST /jobs/{id}/figures/{figureId}/correction` | 同上 |
| ワーカー | `POST /jobs/{id}/retry`（**改修**: `?discardCorrections=`） | 同上 |
| BFF | `GET /bff/conversion/jobs/{id}/figures` | **AdminOnly** |
| BFF | `GET /bff/conversion/jobs/{id}/figures/{figureId}/image` | **AdminOnly**（バイト列は BFF が解決） |
| BFF | `POST /bff/conversion/jobs/{id}/figures/{figureId}/correction` | **AdminOnly** |

### 状態と応答

| 事象 | 応答 |
| --- | --- |
| 未知のジョブ / 図 | 404 |
| コード化済みの図へ補正投稿 | 409 `figure_not_correctable`（Phase 1 は縮退した図に限る） |
| `processing` のジョブへ補正投稿 | 409 `job_busy`（同一ジョブの直列化。`01_screens.md:319`） |
| 補正のあるジョブへ `retry`（確認なし） | **409 `corrections_would_be_lost`** ＋ 補正件数 |
| 補正のあるジョブへ `retry?discardCorrections=true` | 202（補正を消して再変換） |

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 縮退した図がジョブから引ける | `ConversionJobStoreTests`: 縮退 2・コード化 1 の変換後に `figures` が 3 件・`Coded` の内訳が一致 |
| 2 | 本文取得 API が 2 ペイン分を返す | `ConversionJobEndpointTests`: `GET /jobs/{id}/figures` が `imageUri` と `code` を返す |
| 3 | 補正投稿で本文が置換され再発行される | `RawDocumentFetchedConsumerJobTests` 相当: 保存された Markdown から `![id](uri)` が消えコードブロックになる・`DocumentNormalized` が再発行される |
| 4 | コード化済みの図は補正できない | 409 `figure_not_correctable` |
| 5 | 補正のあるジョブの retry は止まる | 409 `corrections_would_be_lost` |
| 6 | 確認つき retry は通り、補正が消える | 202 かつ以後 `HasCorrection == false` |
| 7 | 認可（多層防御） | `BffConversionEndpointTests`: 3 本とも operator は 403・admin は 200/202 |
| 8 | 契約の後方互換 | 既存 `ConversionJobDto` の位置を変えていない（[[IADR-0122]] 決定 2） |

**変異検査（両方向）**: `AdminOnly` を外すと 7 が落ちること、
`corrections_would_be_lost` の判定を外すと 5 が落ちることを**実測してから**確定させる。

## 実装中に決めたこと（仕様書からの差分）

### 1. `NormalizationResult.Figures` に既定値を付けない

`[]` を既定にすれば呼び出し側 3 箇所（すべてテスト）を直さずに済んだが、**付けなかった。**
本 issue が直している事故は、まさに「**渡さなくてもコンパイルが通り、黙って落ちる**」形である。
既定値を付けると同じ事故が再発する。テストの 3 箇所は**件数と図の内訳が一致するよう明示的に**書いた。

### 2. EF が新規行を「既存行の更新」と解釈して落ちた

`ConversionJobFigure.Id` をエンティティ側で採番していたところ、EF が Guid 主キーを
**ストア生成**と見なし、値が入った新規行を UPDATE しようとして
`DbUpdateConcurrencyException: Attempted to update or delete an entity that does not exist in the store`
になった。`ConversionJob.Id` と同じく **`ValueGeneratedNever()`** を置いて解決した。

**マイグレーションは作り直した** —— `ValueGeneratedNever` はモデルスナップショットに出るため、
先に生成したマイグレーションでは snapshot が `ValueGeneratedOnAdd()` のまま食い違っていた。
（`dotnet ef migrations remove` は DB へ接続しようとして失敗するので、
ファイルを消して snapshot を `git checkout` で戻してから再生成した。）

### 3. ★ BFF の `retry` が 409 の本文を捨てていた（**#640 と同型・3 度目の発見**）

新しい 409（`corrections_would_be_lost` ＋ `correctedFigures`）を足そうとして気づいた:

```csharp
var resp = await client.PostAsync($"/jobs/{id}/retry", content: null, ct);
return Results.StatusCode((int)resp.StatusCode);   // ← 本文が消える
```

**#640 では解析層（`ApiError.parseProblemDetails`）を直したが、中継層はここに残っていた。**
`docs/api/openapi.yaml` と `docs/api/BFF_bff-surface.md` は**その挙動を仕様として明記していた**
（「409 に本文は無い。BFF は `Results.StatusCode` でステータスのみを中継する」）。
[[IADR-0040]] の透過中継へ改め、**両文書も直した**（規則 8）。

**変異試験**: 中継を `Results.StatusCode` へ戻すと `Retry_Passes409Body_ThroughVerbatim` **のみ**が
落ちる（Failed 1 / Passed 21）ことを実測した。**テストが本当にこの欠陥を捕まえている。**

### 4. ★ 再発行する `DocumentNormalized` が ABAC 属性を落とすところだった

最初に書いた実装は `Attributes: new Dictionary<string, string>()` / `Tags: []` で再発行していた。
**取り込み側は属性から機密区分を読む**ため、これは**文書の可視範囲を変える**欠陥である。
`GetSourceEventAsync` を足し、原本イベントから復元して載せるよう直した
（回帰テスト `Correction_Republishes_WithAbacAttributesPreserved`）。

**契約 DTO を見ているだけでは気づけなかった** —— `ConversionJobDto` は
「原本イベント再構成用の列は DTO に含めない」設計であり、**属性は DTO に無い**。
軸を「誰がこの応答を読むか」から**「このイベントを誰が読むか」**へ広げて初めて出てきた。

### 5. 「人手補正」が「再変換」の別名として使われていた

母集合 軸 4 のとおり、`ConversionJob.cs:88` と `ConversionJobEndpoints.cs:31` が
「人手補正は失敗ジョブに限る」と書いていた。**再変換の説明としては正しいが、人手補正の説明としては誤り**である。
`docs/data/conversion-job.md:117` にも同じ記述があった。**3 箇所とも実態へ揃えた**（[[IADR-0154]] 決定 5）。

### 6. テスト間の状態漏れ

`BffTestFactory` は `IClassFixture` で共有されるため、足した `ConversionConflictBody` /
`LastConversionPath` をテストのコンストラクタで戻さないと、**後続テストの retry 応答へ本文が漏れる**。
既存の `ConversionStatusCode` / `ConversionThrows` と並べて戻すようにした。

### 7. ★ 補正ゲートの到達経路（レビュー 1 巡目の 🟡）

レビューが「`corrections_would_be_lost` は通常操作では到達しない」と指摘した。**実測して確かめた。**

```
補正済みの succeeded ジョブ → POST /jobs/{id}/retry → 409 not_retryable（補正ゲートまで届かない）
```

（`Retry_CorrectionsGate_IsReachable_ThroughPublicApiOnly` で固定した。）

**指摘は正しい。** `retry` は直上で `failed` 以外を弾き、補正は `succeeded` のジョブへ入るためである。

**ただし「到達しない」わけではない。** 発火するのは
**「一度成功して図が記録されたジョブが、その後の変換で失敗した」**場合である
——図は `MarkFailed` で消えないので補正が残り、そこへ `retry` が来る。稀だが実在する経路であり、
**そのときこそ補正が黙って消えてはならない**ので分岐は残す。

**分岐を消さないための注記をコードへ置いた。** 書いておかないと、次に読む人が
「到達しない分岐」と判断して外しかねない（レビューがまさにその読み方をしかけた）。

### 8. CodeQL: 利用者由来の値をログへ出していた（3 件）

`figureId`（経路パラメータ）と `request.Language`（要求本文）が `logger` へ直接渡っていた。
**改行を残すと偽のログ行を注入できる**（Log entries created from user input）。

`RawDocumentFetchedConsumer.SummarizeError` が変換失敗メッセージを 1 行へ丸めているのと**同じ趣旨**であり、
`ForLog()` で**制御文字を落として長さを切る**。**ログ本文だけの措置**で、保存する値は変えていない。
回帰テスト `Correction_WithControlCharsInFigureId_DoesNotBreak` を置いた。

### 9. BFF の画像取得が図一覧を毎回引く（レビュー 1 巡目の 🟡・**変更しない**）

`GET /figures/{figureId}/image` は 1 枚のために `GET /jobs/{id}/figures` を丸ごと取る。
**指摘のとおりだが、変更しない。** 単一図取得の口をワーカーへ足すと [[IADR-0029]]
（ワーカーは最小 HTTP サーフェス）に反する。SC-07 の想定図数（数枚）では実害が無く、
レビュー自身も「妥当な範囲のトレードオフ」と評している。**Phase 2 で図数が増えるなら見直す。**

## 申し送り

- **SC-07 画面（2 ペイン編集・「補正あり」標識・確認ダイアログ）は別 issue** で起票する。
- **#545 は本 issue の着手で解消**する（後継 IADR = [[IADR-0154]]）。
- Phase 2（Markdown 全体の編集）は計画が繰り延べており、**本 issue では扱わない**。
- **Phase 2 で 1 ジョブの図数が増えるなら、BFF の画像取得を見直す**（§9）。
- `01_screens.md:289,298` が「**人手補正（Phase 2）の導入時に SC-06 手動同期の分類を再確認する**」と
  予告している。**Phase 1 では補正が retry で消えるゲートを API に置くため実害は無い**が、
  **手動同期がトリガする再変換にも同じゲートが及ぶかは未確定のまま**である。
  → **計画へ環流する**（`/plan-feedback`）。
