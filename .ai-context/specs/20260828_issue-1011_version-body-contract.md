---
title: 作業仕様書 — 版応答が「版ごとの本文」を約束しない形へ契約を揃える（#1011）
type: spec
status: in-progress
related_ids:
  - FR-06
  - FR-19
  - FR-21
  - UC-03
  - SC-03
  - ADR-0014
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "FR-06［2026-08-23 明確化］「バージョン管理」の射程は版の作成・一覧・取得まで。版の復元は含めない（利用者裁定 2026-08-23 / 環流 planning#473・planning#478 でマージ済み）"
related_adrs:
  - IADR-0290
  - IADR-0024
  - IADR-0264
issue: "#1011"
---

# 作業仕様書: 版応答の本文契約を事実へ揃える — #1011

## 起点

`GET /documents/{id}/versions/{version}`（および BFF の `GET /bff/documents/{id}/versions/{version}`）は
版のメタデータを返す。応答に `markdownUri` が入っているが、その URI を引くと**現行版の本文**が返る。
版履歴は「変更履歴を追跡できる」ものとして提示されているのに本文だけは常に最新であり、
**応答が 200 で、それらしい URI が入っているため呼び出し側から区別できない**。

## 1. 機序の追認（走査で確認した。記憶では挙げない）

| # | 主張 | 実測（ファイル:行） |
| --- | --- | --- |
| 1 | `DocumentVersion.MarkdownUri` は文書の**現行 URI をそのまま写す** | `Domain/DocumentVersion.cs:33` = `MarkdownUri = doc.MarkdownUri`（`Capture` の中。版ごとに違う値を作る経路が無い） |
| 2 | 本文のオブジェクトキーは**文書 ID だけから決まる固定キー**で、再投入は同じキーを上書きする | `Domain/DocumentBodyIntake.cs:30` = `documents/{documentId:D}/body.md`。呼び出し点は 4 つ（`DocumentEndpoints.cs:120`（作成）/ `:302`（本文 PUT）/ `ObsidianSyncEndpoints.cs:109` / `:253`）で、**いずれも版番号を鍵に含めない** |
| 3 | オブジェクトストレージのクライアントが返す URI に**バージョン ID が含まれない** | `S3ObjectStorageClient.cs:30,48` は `StorageUri.Build(bucket, key)` を返すだけ。`StorageUri.Build` は `storage://<bucket>/<key>` を組むのみで versionId を持つ余地が無い（`Foundation/Ports/Storage/StorageUri.cs`）。`PutObjectAsync` の応答 `VersionId` は読んでいない（`grep VersionId` は 0 件）。取得側 `GetTextAsync` / `GetBytesAsync` も versionId を渡さない |

**結論**: 3 段のどこにも「版 → その時点の本文」を復元する情報が無い。`markdownUri` は
**常に現行版の本文**を指す。issue の機序は 3 点とも追認できた。

### 1.2 「バケットのバージョニングが履歴を持つ」の成否

`DocumentBodyIntake.cs:28-29` のコメントは、固定キー上書きを **「バケットのバージョニングが履歴を
持つ（ADR-0014）」** という前提で正当化している。実測すると:

- `deploy/` 配下に MinIO のバージョニング設定は **0 件**（`grep -rn -i "versioning" deploy/ | wc -l` = 0）。
- ただし**アプリ側は起動時に有効化を試みる**。`ObjectStorageOptions.EnableVersioning` の既定は `true`、
  `EnsureBucketOnStartup` も `true` で、`S3ObjectStorageClient.EnsureBucketAsync` が
  `PutBucketVersioningAsync(Enabled)` を呼ぶ（`S3ObjectStorageClient.cs:99-107`）。
- **したがって「配備されていない」と言い切るのは正確でない。**「マニフェストに設定は無い／アプリの
  ブートストラップが有効化を試みる」が事実である。

**そして決定的なのは、有効かどうかに関わらず版ごとの本文は引けないという点である** ——
機序 3 のとおり参照 URI が versionId を持たず、読み取り経路も versionId を渡さないため、
**過去のオブジェクト版を指す手段がどこにも無い**。コメントが書いている「履歴を持つから固定キー
上書きでよい」は、**文書の版履歴に対する正当化としては成立していない**（オブジェクトの世代が
ストレージ内部に積まれることと、版応答から過去本文を引けることは別である）。

## 2. 採る案 — 案 C（契約の側を事実に合わせる）

案 A（キーの版化）・案 B（バケットのバージョニング＋版 ID 保持）は**採らない**。
planning#473 の裁定（2026-08-23）が

> FR-06 の「バージョン管理」に版の復元は含めない。射程は**版の作成・一覧・取得まで**である。
> 「版ごとの本文非保持」は現状の記録であり、**是正を求めるものではない**

と明示しており、実装すればスコープの無断拡大になる。**版行そのものの削除もしない**
（版の作成・一覧・取得は FR-06 の射程内で、現に要求されている）。

### 2.1 `markdownUri` を「落とす」か「改める」か —— 呼び出し側の実測で決める

| 面 | 実測 | 判定 |
| --- | --- | --- |
| SPA（画面） | `sc03-document/components/DocumentDetailPage.tsx` の `VersionTable` が描くのは **版・変更メモ・作成日時の 3 列だけ**（`:262-291`）。`markdownUri` を読む行は無い | 使っていない |
| SPA（取得層） | `sc03-document/api/useDocumentQueries.ts` は `useBffDocumentVersions` の結果を素通しする（`select: okArray`）。フィールドを触らない | 使っていない |
| BFF | `Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs:72,103` は `DocumentVersionDto` を丸ごと deserialize して丸ごと返す。`.MarkdownUri` を読む行は無い | 使っていない |
| バックエンドのテスト | `grep -rn -i markdownuri` を版テスト（`DocumentEndpointVersioningTests` / `DocumentVersioningTests` / `TagIdentityTests` / `ObsidianSyncProtocolTests` / `Platform.Bff.Tests`）へ当てて **0 件**。`BffTestFactory.StubVersions` も `MarkdownUri` を設定していない | 使っていない |
| フロントのテスト | 版フィクスチャに `markdownUri` を置いている行は無い（`DocumentDetailPage.test.tsx` / `adminFlow.test.tsx`） | 使っていない |

**呼び出し側は 1 つも読んでいない。** よって **落とす**（版応答の契約から `markdownUri` を外す）。

「現行版の本文を指す」と説明文で断る案（改める側）を採らないのは、**説明文は呼び出し地点に現れない**
ためである。`markdownUri` という名前を版スナップショットの中に残す限り、
「この版の本文の URI」という読み方が既定のまま残り、issue が問題にした
「**呼び出し側から区別できない**」がそのまま残る。読み手が 0 の値を消すほうが安い。

### 2.2 変える面・変えない面

| 面 | 変える | 理由 |
| --- | --- | --- |
| `Knowledge.Contracts.Dtos.DocumentVersionDto.MarkdownUri` | **削除** | 契約の正体。ここを残すと openapi との突合（`check-openapi-dto-drift.js`）が通らない |
| `DocumentEndpoints.ToVersionDto` | `MarkdownUri` の写しを削除 | 応答生成点 |
| `docs/api/openapi.yaml` の `DocumentVersionDto` / 版取得の description | プロパティ削除・説明を事実へ | 公開契約 |
| orval 生成物 | 再生成 | CI が再生成差分を検査する |
| DB 列 `DocumentVersions.MarkdownUri` と `DocumentVersion.MarkdownUri`（ドメイン） | **残す**（コメントのみ是正） | 落とすとマイグレーションが要る。**削除は裁定が求めていない**（「是正を求めるものではない」）うえ、`DocumentDbContextModelSnapshot.cs` は並行作業と衝突しやすい。列は「スナップショット時点の文書の本文 URI」という記録として意味を保つ |
| 版の復元端点 | **作らない** | 裁定が「含めない」と明示 |

## 3. 同型の記述の走査（母集合）

`.claude/rules/traceability.md`「是正・追随の母集合の取り方」に従い、**誤りの側の語**で、
**拡張子で絞らず**、**パス除外のみ**で、**軸を 5 本**引いた。走査は本仕様書を書く前に実施した
（規則 8。本仕様書自身が語を含むため、以後の再走査では本ファイルが 1 件増える）。

走査は**本仕様書を書いた後**に数え直しており、**本ファイル自身が検索語を含む**。
規則 8 に従い「そのまま返る数 → 自己参照を引く → 正味」の引き算を見せる。

| 軸 | 検索語 | そのまま返る数 | 自己参照 | 正味 |
| --- | --- | --- | --- | --- |
| 1 | `バージョニング` | 41 | 9 | **32** |
| 2 | `過去版\|版ごと\|旧版\|前の版\|以前の版` | 63 | 9 | **54** |
| 3 | `markdownuri`（-i、`docs/` と `.ai-context/` のみ） | 131 | 24 | **107** |
| 4 | `版時点の本文\|版の本文\|本文 URI\|本文の URI\|本文URI` | 24 | 10 | **14** |
| 5 | `履歴を持つ\|履歴が残る\|履歴は保持\|履歴を残す\|履歴を保持` | 18 | 7 | **11** |

このうち **「版ごとに本文が残る」と読める記述**は 18 件（重複を除いた対象単位）。内訳:

### 3.1 是正する（本作業の領域内）— 10 件

| # | 対象 | 誤りの形 |
| --- | --- | --- |
| 1 | `Services/DocumentService/Domain/DocumentBodyIntake.cs:28-29` | 「バケットのバージョニングが履歴を持つ」を固定キー上書きの正当化に使う |
| 2 | `Services/DocumentService/Features/ObsidianSync/ObsidianSyncEndpoints.cs:245-246` | 同じ文言（同型） |
| 3 | `Services/DocumentService/Domain/DocumentVersion.cs:3-4` | 「本文 URI …を ID＋版番号で再構成する」 |
| 4 | `Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs:82-85` | `markdownUri` を返す前提の注記（削除後は事実と食い違う） |
| 5 | `docs/api/openapi.yaml`（版取得の description） | 同上 |
| 6 | `docs/api/openapi.yaml`（`DocumentVersionDto.markdownUri`） | プロパティそのもの |
| 7 | `docs/data/document-and-version.md:39` | 「任意時点の…本文 URI…を再構成できる」 |
| 8 | `docs/data/document-and-version.md:79` | 列の説明「版時点の本文 URI」 |
| 9 | `docs/functional/FR-06_document-crud-versioning.md:39` | 出力に `DocumentVersionDto(… MarkdownUri …)` |
| 10 | `docs/screens/SC-03_document-detail.md:146` | `DocumentVersionDto = { … markdownUri?, … }` |

（`docs/tests/FR-06_document-crud-versioning.md` は誤りではないが、本作業で足すテストの行を追記する。）

### 3.2 是正しない — 除外とその理由（規則 6。黙って落とさない）

| # | 対象 | 除外理由 |
| --- | --- | --- |
| 11 | `Platform.Shared.Infrastructure/Foundation/Ports/Storage/ObjectStorageOptions.cs:26,29` | **並行作業の領域**（触ってはならない）。かつ**誤りではない** —— オブジェクトの世代を積むこと自体は事実 |
| 12 | `Platform.Shared.Infrastructure/.../S3ObjectStorageClient.cs:101` | 同上 |
| 13 | `Platform.Shared.Infrastructure/.../ObjectStorageBootstrapHostedService.cs:7` | 同上 |
| 14 | `Tests/Knowledge.IntegrationTests/Storage/ObjectStorageRoundTripTests.cs:15,78` | **並行作業の領域**（触ってはならない）。「バージョニングで履歴は保持される」は**オブジェクト層の話としては成立**し、版応答の契約を約束していない |
| 15 | `.ai-context/adr/IADR-0270_...:126` | **凍結記録**（`.ai-context/adr/` の本文プロズは後から書き換えない）。事実は IADR-0290 に記録し、そちらから参照する |
| 16 | `.ai-context/adr/IADR-0024_...:56-57,84,86,93` | 同上。かつ**オブジェクト層の決定としては現に有効**（覆さない） |
| 17 | `.ai-context/specs/20260707_FR-06_object-storage-minio.md` | 凍結記録（作業仕様書）。決定を変える追記は要らない |
| 18 | `.ai-context/specs/20260627_FR-06_document-versioning-metadata.md:91` | 同上 |

**11〜14 は申し送りとして報告する**（同型だが領域外。`.ai-context/adr/` からは参照できる）。

## 4. 契約の後方互換（何を許容するか）

`DocumentVersionDto.MarkdownUri` の削除は `check-contract-schema.js` の判定で**破壊的**（削除）である。
`scripts/contract-breaking-allowlist.json` へ承認エントリを 1 件書き、`--update` で
`contract-schema-baseline.json` の `$acceptedBreakingChanges` へ移す。

- **許容するもの**: `Knowledge.Contracts.Dtos.DocumentVersionDto.MarkdownUri` の**削除**、ただ 1 件。
- **なぜ安全か**: openapi 上 `required` に入っておらず `nullable: true` である。読んでいる呼び出し側が
  **リポジトリ内に 0 件**（§2.1 の実測）。生成 TS 型では省略可フィールドが 1 つ減るだけで、
  参照が無いため型エラーにならない。
- **なぜ非破壊へ逃げないか**: 「値を null で返す」形にすると、契約に「この版の本文の URI」という
  名前だけが残り、**issue が問題にした読み違えが温存される**。

## 5. 受け入れ基準

- [ ] 版応答（サービス／BFF とも）に `markdownUri` が**含まれない**
- [ ] 版の作成・一覧・取得は従来どおり動く（版行を消していない）
- [ ] 復元端点を足していない
- [ ] `DocumentBodyIntake.cs` のコメントが「バージョニングが履歴を持つ」を前提にしていない
- [ ] 同型の記述（§3.1 の 10 件）が是正されている
- [ ] 版応答が版ごとの本文を約束しないことをテストが固定し、**変異試験で落ちる**
- [ ] 契約 baseline に許容の記録が残っている

## 6. テスト方針と変異試験

**新規テスト（`DocumentEndpointVersioningTests`）**:

1. `版応答は本文の参照を含まない` — 本文つきで作成 → 本文を差し替え（版 2）→
   `GET /documents/{id}/versions/1` と `/versions` の**生 JSON** に `markdownUri` が出ないことを見る。
   併せて `GET /documents/{id}`（現行版）には出ることを対照として見る
   （**「JSON から消えた」ことを測っているのであって、DTO の型を測っているのではない**）。
2. `版ごとの本文は保持されない` — 本文を 2 回投入し、**オブジェクトキーが両方とも同一**であること
   （`DocumentBodyIntake.StorageKey` が版に依らない）と、格納先に**最後の本文しか無い**ことを見る。
   §1 の機序 2 を実行で固定する。

**変異試験**（両方向の生出力を報告に貼る）:

- M1: `ToVersionDto` に `MarkdownUri = v.MarkdownUri` を戻す（＋DTO のプロパティを戻す）→ 1 が落ちること
- M2: 変異を戻す → 緑に戻ること

## 7. 計画書との差異

- 差異: なし。planning#473 の裁定（射程は作成・一覧・取得まで／版ごとの本文非保持は是正対象外）に沿う。
  **本作業は要求の追加でも削除でもなく、契約の記述を事実へ揃えるものである。**

## 8. 未決事項・申し送り

- §3.2 の 11〜14（platform 側の共有ストレージ実装と統合テストのコメント）は領域外のため触っていない。
  **誤りではない**が、`ADR-0014` / `IADR-0024` の「バージョニングで履歴を保持する」が
  **文書の版履歴を保証するものではない**ことは IADR-0290 に記録した。
- `DocumentVersions.MarkdownUri` 列は残す。将来これを落とすならマイグレーションが要る（本作業では扱わない）。
