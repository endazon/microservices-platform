---
title: IADR-0320 pandoc を実行時イメージへ入れ、本文変換の縮退を fail-closed にする
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-12, UC-06, SC-07, NFR, ADR-0012, ADR-0014, ADR-0027, ADR-0053, IADR-0008, IADR-0137, IADR-0154, IADR-0298]
author: claude
created: 2026-08-31
updated: 2026-08-31
---

# IADR-0320: pandoc を実行時イメージへ入れ、本文変換の縮退を fail-closed にする

- 状態: Accepted
- 日付: 2026-08-31
- 決定者: claude（実装担当）
- 起票: #1097（親 #447）

## 文脈

`ConversionService` の実行時イメージが pandoc を持っていない。`PandocConversionService` は
`Process.Start("pandoc")` で pandoc を**外部プロセスとして起動する**実装であり、pandoc が無いときは
プレースホルダ本文（図 0 件）を返して**成功する**設計だった（「dev 環境での動作保証」）。

その縮退が**配備した実物でずっと起きていた。**

```console
# develop 561e9ade / 稼働 k3s v1.35.4+k3s1（2026-08-31 実測）
$ grep -c -i pandoc src/knowledge/backend/Services/ConversionService/Dockerfile
0
$ kubectl -n microservices-platform exec deploy/conversion-service -c conversion-service \
    -- sh -c "which pandoc || echo NOPANDOC"
NOPANDOC
```

FR-12 の主要素（本文の Markdown 化）が 1 度も実行されておらず、SC-07 の変換ジョブ画面には
**成功として並ぶ** —— 「変換した」と「変換したふりをした」が画面から区別できない。
`#972` / `#992` が潰した「200 ＋ 空を正常応答に見せる」穴と同型である。

### 着手時に判明した第 2 の穴（pandoc を入れるだけでは直らない）

`ResolveLocalSource` は `file` スキームとローカルパスしか解決しない。ところが
`DataSourceSyncService` が発行する `RawDocumentFetched.StorageUri` は
`IObjectStorageClient.PutBytesAsync` の戻り値、すなわち **オブジェクトストレージの参照**である
（稼働クラスタの `conversion-service` は `ObjectStorage__Endpoint=http://minio:9000` を持つ）。
`IsFile` が false なので `null` が返り、**pandoc があっても縮退したままになる。**

### 既存テストは何を見ていたか

- `NormalizationServiceTests` / golden（`IADR-0298`）は `IBodyConverter` を**差し替えて**測っており、
  pandoc の実物には一度も当たっていない（IADR-0298 決定 2 が明記）。
- `PandocConversionServiceTests` の実 pandoc ケースは `Assert.SkipUnless` で skip される。
- したがって**全テストが緑でも、pandoc の有無について何も分からない。**

## 決定

### 決定 1: 実行時イメージへ pandoc を入れる。取得元は base image の APT ミラーに限る

`Dockerfile` の runtime 段（`mcr.microsoft.com/dotnet/aspnet:10.0` = Ubuntu 24.04 noble）で
`apt-get install -y --no-install-recommends pandoc` する。版は noble の **pandoc 3.1.3**。

**取得元をベースイメージに設定済みの APT ミラーに限る。** GitHub リリース資産や任意 URL から
バイナリを引かない —— 配布元が増えるほど「どこから来たか」が追えなくなる
（08_data-egress-policy が製品の実行時について定める禁止の、ビルド時への適用）。

イメージサイズは増える（pandoc は静的リンクの Haskell バイナリで `/usr/bin/pandoc` 単体が約 190MB）。
**ラチェットは置かない**（増分は PR 本文に数として残す）。

### 決定 2: 縮退の可否を構成で決め、既定は fail-closed

`Conversion:AllowDegradedBodyConversion`（`ConversionOptions`。既定 `false`）。

- `false`（既定）: pandoc 不在・原本が読み出せない → `BodyConversionUnavailableException`。
  再試行 → デッドレターへ委ねる。**ADR-0012 の「本文変換の恒久失敗は再試行し、継続失敗は
  デッドレターキューへ送り管理者に通知する」に一致する。**
- `true`: 従来どおりプレースホルダ本文（図 0 件）。

🔴 **縮退そのものは消さない。** 単体テストは pandoc の無い CI・開発機で走る必要があり、
消すと「テストが赤くなるだけで実害の検知にはならない」（issue #1097 の補足）。
直すべきは「配備した実物が縮退したまま成功を返す」ことである。

**配備（helm / compose）はこの値を注入しない。** 注入する面があると「dev だけ縮退」を
本番側から覆せてしまう。開発機は環境変数 `Conversion__AllowDegradedBodyConversion=true` で明示する。

### 決定 3: オブジェクトストレージ上の原本を取り寄せる

`IObjectStorageClient.CanResolve` が真なら `GetBytesAsync` で一時ファイルへ落として pandoc に食わせ、
変換後に消す。`file` スキーム／ローカルパスは従来どおり直接渡す（一時ファイルを作らない）。
どちらでも解決できなければ決定 2 の分岐（既定は例外）へ落ちる。

**`IBodyConverter` の契約は変えていない**（`ConvertAsync(storageUri, contentType, ct)` のまま）。
`IADR-0008` が置いた 3 ポートの境界はそのままであり、golden（`IADR-0298`）も無改修で通る。

### 決定 4: PDF は既定形式へ落とさず、明示的に拒否する

`PandocInputFormat` は PDF の MIME も拡張子 `.pdf` も知らず、**既定の `markdown` へ落ちていた**。
`FileSystemConnector` は `.pdf` を列挙対象に含むので、実 pandoc を入れた瞬間に
`pandoc -f markdown foo.pdf` が非 0 終了し、原因の判らない失敗が再試行 4 回のあとデッドレターへ倒れる。

**pandoc は PDF を出力にはできるが入力には取れない。** よって `UnsupportedSourceFormatException`
を投げ、`RawDocumentFetchedConsumer` は**再送出せず** `FailAsync(..., deadLettered: true)` で
恒久失敗として記録する。

**「デッドレターへ流す」と「判る形で拒否する」のうち後者を採った理由**:
再試行しても結果は変わらない決定的な拒否であり、毒メッセージとして DLQ に溜める価値が無い。
変換ジョブ画面（SC-07）には理由つきの `failed` として並び、`POST /retry` で再変換できる。
`DeadLettered = true` は「この失敗の後に自動再試行は起きない」の意（`IADR-0137` / ADR-0053 決定 2）
であり、この経路でも真である。

🔴 **PDF の本文をどう取るかは決めていない。** `FileSystemConnector` が `.pdf` を列挙する以上、
「取り込めるが変換できない」状態が残る。**計画側の裁定事項**として planning へ環流する
（poppler 等の別経路を足すのは ADR-0012（本文は pandoc）の射程外である）。

### 決定 5: pandoc の存在を readiness に載せる（fail-closed のときだけ）

`PandocHealthCheck` を `ready` タグで登録する。pandoc を持たないイメージを配ると **Pod が Ready に
ならない**ので、配る側が気づく。

これが**「無い状態を検知できる」ことの実物側の担保**である。従前 pandoc の欠落は
どこにも現れなかった（変換は縮退して成功し、probe も緑だった）。
`AllowDegradedBodyConversion=true` の開発機では登録しない —— そこでは縮退が正常な振る舞いである。

### 決定 6: 退行防止にリポジトリ横断の検査器は足さない（1 回目だから）

「実行時イメージに必要な外部ツールが入っていない」は**本件が 1 回目**である。
`#908` / `#957`・`#1025`・`#452` はいずれも「イメージが焼かれていない／配備に出ていない」であり、
**イメージの中身の欠落ではない**。したがって `.claude/rules` の
「検査器・規約の追加は同型の事故が 2 回起きたら」に従い、`scripts/` へ新しい検査器を足さない。

代わりに射程内で閉じる 2 つを置く。

1. `PandocConversionServiceTests.Dockerfile_installs_pandoc_into_the_runtime_stage`
   —— 自分の Dockerfile を読み、runtime 段の `apt-get install` 行に pandoc が居ることを見る。
   **新しいテストクラスを作らない**ので `check-test-spec-coverage` の baseline 更新は要らない
   （`IADR-0298` 決定 6 と同じ理由）。
2. 決定 5 の readiness —— 「書いてあるか」ではなく「焼いたイメージに実在するか」を配備側で見る。

**2 回目が起きたら**（別サービスで同型の欠落）、`scripts/` に「Dockerfile が宣言する実行時依存 ×
実イメージ」の突合を置くこと。本 ADR はその判断を先送りしたのであって、不要と決めたのではない。

## 実走させて初めて見えた別件（本 ADR の射程外）

`--extract-media` は本文中の画像参照を**一時ディレクトリの絶対パス**へ書き換える。その本文がそのまま
保管されるため、変換直後に消える `/tmp/conv-XXXXXXXX/media/...` への壊れた参照が正規化 Markdown に残る
（図そのものは末尾に `![fig-1](storage://...)` として正しく付くので、**同じ図が 2 度出て片方が壊れている**）。

直すには「抽出図を本文のどこへ差し込むか」を決め直す必要があり、`IADR-0154`（人手補正の目印）と
`IADR-0298`（本文の綴りをゴールデンで固定）に関わる設計判断である。**別 issue へ切る。**
従前は pandoc が 1 度も走っていなかったため、この綻びは観測すらされていなかった。

## 影響

| 面 | 影響 |
| --- | --- |
| イメージ | +約 190MB（実測は PR 本文）。起動時間への影響は無い（pandoc は変換時にのみ起動する） |
| 配備 | `deploy/` の変更なし。イメージの中身は Dockerfile が決め、helm はイメージ参照のみ |
| NuGet | 追加なし（`scripts/backend-library-baseline.json` は不変） |
| 契約 | `IBodyConverter` の署名は不変。golden（IADR-0298）は無改修で通る |
| 既存テスト | `PandocConversionServiceTests` の「縮退が正常」という固定を「既定は失敗」へ改めた |

## 代替案と却下理由

| 案 | 却下理由 |
| --- | --- |
| Dockerfile に pandoc を足すだけ | **直らない。** 原本が常にオブジェクトストレージ参照なので縮退し続ける（決定 3） |
| 縮退を消す | pandoc の無い CI・開発機でテストが赤くなるだけで、実害（配備物の縮退）は検知できない |
| 縮退の判定を `ASPNETCORE_ENVIRONMENT` で行う | 環境名は配備ごとに増える。**縮退という危険な振る舞いの可否は 1 個の明示的な設定で持つ** |
| PDF を poppler 等で別経路に回す | ADR-0012 は「本文は pandoc」と定める。計画外の機能追加であり、計画へ環流して裁定を仰ぐ |
| `scripts/` に横断検査器を足す | 同型の事故が 1 回目のため（決定 6） |

## 関連

- 計画: ADR-0012（変換パイプライン）、ADR-0014（オブジェクトストレージ）、ADR-0053（デッドレター）
- 実装: IADR-0008（3 ポート分離と deny-by-default）、IADR-0137（デッドレター標識）、
  IADR-0154（図の記録）、IADR-0298（正規化ゴールデン。pandoc は実走させない）
- 作業仕様書: `20260831_issue-1097_pandoc-runtime-image-and-fail-closed.md`
