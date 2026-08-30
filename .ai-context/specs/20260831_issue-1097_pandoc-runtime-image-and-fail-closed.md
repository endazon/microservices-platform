---
title: 作業仕様書 — ConversionService の実行時イメージへ pandoc を入れ、本文変換の縮退を fail-closed にする（#1097）
type: spec
status: done
related_ids:
  - FR-01
  - FR-12
  - UC-06
  - SC-07
  - NFR
  - ADR-0012
  - ADR-0014
  - ADR-0027
  - IADR-0008
  - IADR-0137
  - IADR-0154
  - IADR-0298
  - IADR-0316
author: claude
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - 20260703_FR-12_document-normalization-pipeline.md
  - 20260829_issue-447_fr12-golden-files.md
issue: "#1097"
---

# 作業仕様書 — pandoc を実行時イメージへ入れ、縮退を fail-closed にする（#1097）

## 目的と射程

`ConversionService` の実行時イメージに pandoc が無く、`PandocConversionService` が
**プレースホルダ本文（図 0 件）を返して「成功」する**。FR-12 の主要素（本文の Markdown 化）が
配備した状態では 1 度も実行されていない。

射程は `src/knowledge/backend/Services/ConversionService/**` と、その正本となる `docs/` の記述。

## 着手前の実測（2026-08-31・`develop` 561e9ade・稼働 k3s v1.35.4+k3s1）

```console
$ grep -c -i pandoc src/knowledge/backend/Services/ConversionService/Dockerfile
0
$ kubectl -n microservices-platform exec deploy/conversion-service -c conversion-service \
    -- sh -c "which pandoc || echo NOPANDOC"
NOPANDOC
$ grep -rni "pandoc" deploy/
（0 件）
$ nerdctl --namespace k8s.io run mcr.microsoft.com/dotnet/aspnet:10.0 sh -c "grep PRETTY /etc/os-release"
PRETTY_NAME="Ubuntu 24.04.4 LTS"
```

### 問 1: 本当に pandoc が要るのか → **要る**

`PandocConversionService.RunPandocAsync` が `new ProcessStartInfo("pandoc")` で**外部プロセスとして
起動している**（`-f <fmt> -t gfm --extract-media <dir> <src>`）。`CheckPandocAsync` も
`pandoc --version` を起動する。**pandoc を前提にした記述ではなく、pandoc を実行するコードである。**
代替の変換ライブラリは入っていない（`Directory.Packages.props` に markdown / docx / html 変換の
パッケージは無く、`IBodyConverter` の本番実装は `PandocConversionService` ただ 1 つ）。

### 問 2: 無い状態で何が起きているか → **無言の縮退**（warning ログ 1 行のみ）

`ConvertAsync` は `CheckPandocAsync` が false なら `Placeholder(storageUri)` を返す。
本文は `# <name>` ＋ 「本文は <uri> から pandoc で変換します。」、図 0 件。例外は出ないので
`RawDocumentFetchedConsumer` は成功として `DocumentNormalized` を発行し、
`ConversionJob.Status = succeeded` になる。SC-07 の画面では**成功として並ぶ**。

### 問 2b: 🔴 pandoc を入れるだけでは直らない（着手前に判明した第 2 の穴）

`ResolveLocalSource` は `file://` とローカルパスしか解決しない。ところが
`DataSourceSyncService` が発行する `RawDocumentFetched.StorageUri` は
`IObjectStorageClient.PutBytesAsync` の戻り値、すなわち `storage://<bucket>/<key>` である
（稼働クラスタの `conversion-service` は `ObjectStorage__Endpoint=http://minio:9000` で
実クライアントを構成済み）。`storage://` は `uri.IsFile == false` で null を返し、
**pandoc があってもプレースホルダへ落ちる。**

したがって受け入れ基準「プレースホルダ本文が返らない」は Dockerfile だけでは満たせない。
**原本を storage スキームから取得する経路**が要る。

### 問 3: golden テストは何を見ていたか → **pandoc に一度も当たっていない**

`NormalizationGolden.RenderAsync` は `ScriptedBodyConverter`（`IBodyConverter` の差し替え）を
使っており、入力は「変換器がこう出すであろう Markdown」を人が書いた `Cases/<name>.body.md` である。
IADR-0298 決定 2 が「pandoc は実走させない」と明記している。したがって golden が緑であることは
pandoc の有無について何も語らない。`PandocConversionServiceTests` の 3 件のうち
2 件は `Assert.SkipUnless(PandocAvailable())` で **skip されている**（pandoc 未導入の CI・開発機）。

### 問 4: 代替の変換経路は在るか → **無い**

`IBodyConverter` の実装は `PandocConversionService` のみ（本番実装 1 件・テスト用スクリプト実装 2 件）。
PDF テキスト抽出器（poppler 等）も入っていない。

### 併せて直す綻び: PDF

`PandocInputFormat` は PDF の MIME を知らず、拡張子 `.pdf` も既定の `markdown` へ落ちる。
`FileSystemConnector` は `.pdf` を**列挙対象に含む**ため、実 pandoc を入れた瞬間に
`pandoc -f markdown foo.pdf` が走り、非 0 終了 → 再試行 4 回 → デッドレターへ倒れる。

## 母集合の引き直し（[[IADR-0141]] 決定 1 / `traceability.repo.md` 規則 9・10）

issue 本文の反映先リストは使わず、着手時に自分で引いた。**拡張子で絞らず、追跡下の全ファイルから
パス除外だけで引いた**（`git ls-files | grep -v '^src/ai-stock-trading' | xargs grep -lni ...`）。

| 軸 | 検索語（誤りの側から） | 件数 | 摘要 |
| --- | --- | --- | --- |
| 1 | `pandoc` | 54 | 全体像 |
| 2 | `プレースホルダ｜デグレード｜placeholder` | 66 | 大半は UI の入力プレースホルダ。変換に関わるのは 4 件 |
| 3 | `ローカル解決｜原本フェッチ｜ローカルパスのみ` | 7 | 「file スキームのみ対応」の宣言 |
| 4 | `\.pdf｜application/pdf` | 7 | PDF を扱うと宣言している箇所 |

### 直す（live な権威文書・コード）

- `src/knowledge/backend/Services/ConversionService/Dockerfile`
- `src/.../ConversionService/Infrastructure/ExternalServices/PandocConversionService.cs`
- `src/.../ConversionService/Infrastructure/Configuration/ConversionOptions.cs`（新規）
- `src/.../ConversionService/Domain/Ports/IBodyConverter.cs`（例外型の追加）
- `src/.../ConversionService/Features/ConversionJobs/Normalize/RawDocumentFetchedConsumer.cs`
- `src/.../ConversionService/Program.cs`
- `src/.../ConversionService/Tests/PandocConversionServiceTests.cs`
- `docs/functional/FR-12_document-normalization.md`（例外フロー E1・スコープ外）
- `docs/tests/FR-12_document-normalization.md`（T-09/T-10・制約）
- `docs/functional/FR-01_data-source-catalog.md`（実装状況表の「変換（pandoc）」行）
- `.ai-context/adr/IADR-0316_*`（新規・実装 ADR）

### 除外したもの（理由つき）

| 対象 | 除外理由 |
| --- | --- |
| `.ai-context/adr/IADR-0002/0008/0042/0231/0281/0298` | **凍結記録**。本文プロズを後から書き換えない（`CLAUDE.md`「主従」）。差分は新規 IADR-0316 が持つ |
| `.ai-context/specs/*`（10 件） | 同上（確定済み作業仕様書） |
| `.ai-context/superpowers/*`（2 件） | 同上。経過追記も不可（`traceability.repo.md`） |
| `CHANGELOG.md` | 自動生成（`CLAUDE.md`「補助成果物の自動生成」）。手で書き足さない |
| `scripts/test-spec-coverage-baseline.json` | 新規テストクラスを作らないため更新不要（IADR-0298 決定 6 と同じ理由） |
| `docs/tech/system-architecture.md`・`docs/data/conversion-job.md`・`docs/screens/SC-07_conversion-jobs.md` | 「本文は pandoc」という記述であり、本変更後も正しい |
| `docs/tech/composability-classification.md`・`docs/tech/composable-component-guide.md` | 分類表としての pandoc 言及。変わらない |
| `docs/tests/SC-07_conversion-jobs.md`・`docs/tests/FR-01_data-source-catalog.md` | 画面/コネクタ側のテスト仕様。本変更で偽にならない |
| フロントエンド（`ConversionJobsPage*`・`locales/*`） | 表示文言。変換の実行系に触れない |
| `deploy/helm/**`・`deploy/docker-compose.yml` | 🔴 引き直したが**変更不要**。イメージの中身は Dockerfile が決め、helm はイメージ参照のみ。既定値が fail-closed のため新しい環境変数の注入も要らない（`values.yaml` へ足すと「dev だけ縮退」を配備側で覆せる面が増えるので足さない） |
| `scripts/backend-library-baseline.json` | NuGet を 1 つも足さないため変更なし |
| `src/knowledge/backend/Services/DataSourceService/**` | `.pdf` の列挙可否は FR-01 側の裁定であり、本 issue の射程外。計画へ環流する |

## 決定（詳細は IADR-0316）

1. **Dockerfile の runtime 段で `apt-get install --no-install-recommends pandoc`**。取得元は
   **ベースイメージ（Ubuntu 24.04 noble）に設定済みの APT ミラー**であり、外部 CDN・GitHub リリース・
   任意の URL からは取らない（08_data-egress-policy の精神に合わせ、取得元を仕様書へ明記する）。
   版は Ubuntu noble の pandoc 3.1.3。
2. **縮退の可否を構成で決め、既定は fail-closed**。`Conversion:AllowDegradedBodyConversion`
   （既定 `false`）。false のとき pandoc 不在・原本未解決は**例外**（再試行 → デッドレター。ADR-0012
   の「本文変換の恒久失敗は再試行し、継続失敗はデッドレターへ」に一致）。
   縮退そのものは**消さない**（true で従来どおりプレースホルダ）。
3. **オブジェクトストレージ上の原本を取得する**。`IObjectStorageClient.GetBytesAsync` で
   一時ファイルへ落として pandoc に食わせる。`file` スキーム／ローカルパスは従来どおり直接使う。
4. **PDF は明示的に拒否する**。`UnsupportedSourceFormatException` を投げ、
   コンシューマは**再送出せずに** `FailAsync(..., deadLettered: true)` で恒久失敗として記録する
   （＝デッドレターへ流さず、SC-07 に理由の判る `failed` として出す）。
5. **readiness に pandoc を載せる**（fail-closed のときだけ）。入っていない実行時イメージは
   **Ready にならない**——「無い状態を検知できる」ことの実物側の担保。

## 受け入れ基準（issue #1097 の写像）

| # | 基準 | 検証 |
| --- | --- | --- |
| A-1 | 実行時イメージで `which pandoc` がパスを返す | `nerdctl run` |
| A-2 | 稼働クラスタで `pandoc --version` が版を出す | `kubectl exec` |
| A-3 | docx / HTML の原本でプレースホルダ本文が返らない | クラスタで実変換し MinIO の本文を読む |
| A-4 | PDF が `markdown` へ落ちない | 単体テスト ＋ クラスタ実測 |
| A-5 | pandoc 不在で dev 以外は縮退しない | 単体テスト（既定 fail-closed） |
| A-6 | `dotnet build` / `dotnet test` が通る | 検証手順 |
| A-7 | イメージサイズの増分を記録する | `nerdctl images` の前後 |

## 退行防止（「同型の事故 2 回目か」の判断）

「実行時イメージに必要な外部ツールが入っていない」は**本件が 1 回目**である
（#908/#957・#1025・#452 はいずれも「イメージが焼かれていない／配備に出ていない」であり、
イメージの中身の欠落ではない）。したがって `.claude/rules` の
「検査器・規約の追加は同型の事故が 2 回起きたら」に従い、**リポジトリ横断の新しい検査器スクリプトは
作らない**。代わりに射程内で閉じる 2 つを置く。

1. `PandocConversionServiceTests` に **Dockerfile を読んで pandoc の導入行を確かめる `[Fact]`**
   （新クラスを作らないので `test-spec-coverage` の baseline 更新は不要）
2. readiness ヘルスチェック（決定 5）——配備した実物の側で検知する

## 実測の結果（2026-08-31・稼働 k3s）

| # | 結果 |
| --- | --- |
| A-1 | `/usr/bin/pandoc` / `pandoc 3.1.3` |
| A-2 | 稼働 Pod で `pandoc 3.1.3`（`Features: -server +lua`） |
| A-3 | docx（10796 バイト）→ 205 文字・**図 1 件抽出**／HTML → 258 文字。どちらもプレースホルダではない |
| A-4 | PDF は `failed` / `deadLettered=true` / `attempts=1`（無意味な再試行をしない） |
| A-5 | pandoc を退避すると readiness が 503、変換は `BodyConversionUnavailableException` で失敗（縮退しない）。戻すと 4 回目の再試行で成功 |
| A-6 | knowledge backend: 1223 通過 / 44 skip / 0 失敗 |
| A-7 | 302.6MB → 504.1MB（圧縮 116.8MB → 155.5MB） |

## 🔴 実走させて初めて見えた別件（本 PR の射程外・追随 issue を起こす）

pandoc の `--extract-media` は本文中の画像参照を**一時ディレクトリの絶対パスへ書き換える**。
その本文がそのまま正規化 Markdown として保管されるため、変換直後に消える
`/tmp/conv-XXXXXXXX/media/rId20.png` への**壊れた参照が本文に残る**（実測）。
図そのものは末尾に `![fig-1](storage://...)` として正しく付く。つまり**同じ図が 2 度出て、片方は壊れている**。

直すには「抽出した図を本文のどの位置へ差し込むか」を決め直す必要があり、それは
`IADR-0154`（人手補正の目印）と `IADR-0298`（本文の綴りをゴールデンで固定）に関わる**設計判断**である。
本 issue（pandoc が入っていない）の射程を越えるので、**別 issue に切って記録する。**

従前は pandoc が 1 度も走っていなかったため、この綻びは**存在すら観測されていなかった**。

## 検証手順

```
dotnet build src/knowledge/backend/backend.slnx && dotnet test src/knowledge/backend/backend.slnx
dotnet format src/knowledge/backend/backend.slnx --verify-no-changes
node scripts/check-deploy-manifests.js && node scripts/check-image-mapping.js
node scripts/check-commit-messages.js && node scripts/check-trace-blocks.js
node scripts/check-doc-links.js && node scripts/check-doc-updated.js
node scripts/gen-knowledge-graph.js --check
node scripts/check-backend-libraries.js && node scripts/check-test-traceability.js
```
