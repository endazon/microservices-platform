---
title: IADR-0290 版応答から本文の参照を落とす（版ごとの本文は保持しないという事実へ契約を揃える）
type: impl-adr
status: Accepted
related_ids:
  - FR-06
  - FR-19
  - FR-21
  - UC-03
  - SC-03
  - ADR-0014
  - IADR-0024
  - IADR-0264
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "FR-06［2026-08-23 明確化］: 「バージョン管理」の射程は版の作成・一覧・取得まで。版の復元は含めない（利用者裁定 2026-08-23 / 環流 planning#473）"
---

# IADR-0290 版応答から本文の参照を落とす

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: claude（実装エージェント）／裁定は planning#473（2026-08-23）

## 起点・関連

- 起点 issue: #1011
- 関連する計画書 ID: FR-06 / FR-19 / UC-03 / SC-03 / ADR-0014
- 関連する実装 ADR: [[IADR-0024]]（MinIO のバケット/キー設計・バージョニング）、[[IADR-0264]]（本文の置き場は `MarkdownUri`）
- 関連する実装仕様書: `.ai-context/specs/20260828_issue-1011_version-body-contract.md`

## コンテキストと課題

`GET /documents/{id}/versions/{version}`（BFF は `/bff/documents/{id}/versions/{version}`）は
版のメタデータを返す。応答の `markdownUri` を引くと**現行版の本文**が返る。版履歴は
「変更履歴を追跡できる」ものとして提示されているのに本文だけは常に最新であり、
**応答が 200 で、それらしい URI が入っているため呼び出し側から区別できない。**

機序は 3 段で、いずれも走査で追認した。

1. `DocumentVersion.Capture` が `MarkdownUri = doc.MarkdownUri` と**現行 URI をそのまま写す**
   （版ごとに違う値になる経路が無い）。
2. 本文のオブジェクトキーは `documents/{documentId:D}/body.md` で**文書 ID だけから決まり**、
   再投入は同じキーを上書きする（呼び出し点 4 か所とも版番号を鍵に含めない）。
3. 参照 URI は `storage://<bucket>/<key>` で**versionId を持たない**。書き込み時の
   `PutObjectResponse.VersionId` は読まれず、読み取り経路（`GetTextAsync` / `GetBytesAsync`）も
   versionId を受けない。

### 成立していなかった前提

`DocumentBodyIntake.StorageKey` のコメントは、固定キー上書きを
**「バケットのバージョニングが履歴を持つ（`ADR-0014`）」**という前提で正当化していた。実測すると:

- `deploy/` 配下にバケットのバージョニング設定は **0 件**である。
- ただし**アプリ側は起動時に有効化を試みる**（`ObjectStorageOptions.EnableVersioning` 既定 `true`、
  `S3ObjectStorageClient.EnsureBucketAsync` が `PutBucketVersioningAsync(Enabled)` を呼ぶ）。
  したがって「配備されていない」と言い切るのは正確でない。
- **有効かどうかに関わらず、版ごとの本文は引けない。** 機序 3 のとおり参照 URI が versionId を
  持たないため、過去のオブジェクト版を指す値が**どこにも存在しない**。

**オブジェクトの世代がストレージ内部に積まれること**と、**版応答から過去本文を引けること**は別である。
[[IADR-0024]] の決定（バージョニング有効＋同一キー上書き）はオブジェクト層の決定としては現に有効であり、
本 IADR はそれを覆さない。**覆すのは「その決定が文書の版履歴の本文も保証する」という読み方だけ**である。

## 検討した選択肢

| 案 | 内容 | 判定 |
| --- | --- | --- |
| A | オブジェクトキーを版化する（`documents/{id}/v{n}/body.md`） | **不採用** |
| B | バケットのバージョニングを配備し、版ごとに versionId を保持して URI へ載せる | **不採用** |
| C | **契約の側を事実に合わせ、版応答から本文の参照を落とす** | **採用** |

A・B を採らないのは、**planning#473 の裁定（2026-08-23）が射程を確定させている**ためである。

> FR-06 の「バージョン管理」に版の復元は含めない。射程は**版の作成・一覧・取得まで**である。
> 「版ごとの本文非保持」は**現状の記録であり、是正を求めるものではない**（復元を要求していないため整合している）。

版ごとに本文を保持する実装は、要求されていない能力を足すスコープの拡大になる。
**版の復元端点も作らない。版行そのものも消さない**（作成・一覧・取得は射程内で、現に要求されている）。

### C の中の二択 —— 「落とす」か「現行版を指すと明示する」か

呼び出し側を実測して決めた。

| 面 | 実測 |
| --- | --- |
| SPA（画面） | `VersionTable` が描くのは版・変更メモ・作成日時の 3 列だけ。`markdownUri` を読む行が無い |
| SPA（取得層） | `useDocumentQueries` は結果を素通しし、フィールドを触らない |
| BFF | `DocumentBffEndpoints` は DTO を丸ごと deserialize して丸ごと返す。`.MarkdownUri` を読む行が無い |
| テスト（前後端とも） | 版応答の `markdownUri` を読む／置くコードは 0 件（`BffTestFactory.StubVersions` も設定していない） |

**読み手が 1 つも無い。** 説明文で「現行版を指す」と断る案は、**説明文が呼び出し地点に現れない**ため
「版スナップショットの中の `markdownUri`」という名前の既定の読み方を打ち消せず、
issue が問題にした「呼び出し側から区別できない」がそのまま残る。よって**落とす**。

## 決定

1. **`Knowledge.Contracts.Dtos.DocumentVersionDto` から `MarkdownUri` を削除する。**
   `DocumentEndpoints.ToVersionDto` も写しをやめる。`docs/api/openapi.yaml` の
   `DocumentVersionDto` からプロパティを外し、版取得の説明を事実へ改める（orval 生成物を再生成）。
2. **DB 列 `DocumentVersions.MarkdownUri` とドメインの `DocumentVersion.MarkdownUri` は残す。**
   落とすにはマイグレーションが要り、裁定は是正を求めていない。列の意味は
   「スナップショット時点で**文書が指していた**本文 URI」であり、キーが文書 ID で固定である以上
   **現行版の本文と同じ値になる**。**外へ出さない**ことをコメントで固定する。
3. **「バケットのバージョニングが履歴を持つ」を根拠にした記述を、コードから撤去する。**
   代わりに「キーは文書 ID で固定・上書き／URI は versionId を持たない／ゆえに版ごとの本文は
   保持しない／それは計画と整合する」を書く。
4. **契約の破壊的変更として明示的に承認する。** `contract-breaking-allowlist.json` へ
   `memberRemoved:Knowledge.Contracts.Dtos.DocumentVersionDto.MarkdownUri` を理由つきで書き、
   `--update` で `contract-schema-baseline.json` の `$acceptedBreakingChanges` へ移す。

## 理由

- 版応答が返さない値は**読み違えようがない**。issue の核心は「区別できない」ことであり、
  値を残したまま説明を足す形では核心が残る。
- 破壊の実害が測って 0 である（読み手 0・`required` でない・`nullable`）。
- 事実の側（キー設計・URI 形式）を変えないため、**裁定の射程を一歩も越えない**。

## 結果

- 良い影響: 版応答から「その版の本文らしい URI」が消え、呼び出し側が誤って現行本文を
  過去版として扱う経路が閉じる。契約・コード・文書の記述が事実と一致する。
- 悪い影響・トレードオフ: 契約の破壊的変更を 1 件抱える（承認済み・記録済み）。
  将来 A/B を採るなら、この削除を巻き戻すのではなく**新しい形で足す**ことになる。
- フォローアップ:
  - `Platform.Shared.Infrastructure` の `ObjectStorageOptions` / `S3ObjectStorageClient` /
    `ObjectStorageBootstrapHostedService` と `Knowledge.IntegrationTests/Storage/ObjectStorageRoundTripTests.cs`
    にも「バージョニングで履歴を保持する」旨の記述がある。**オブジェクト層の話としては成立している**ので
    誤りではないが、**文書の版履歴を保証するものではない**。本作業では領域外のため触っていない。
  - [[IADR-0270]] の 126 行目と [[IADR-0024]] は凍結記録のため書き換えない。
    「バージョニングが履歴を持つ」を**文書の版履歴の根拠として引かない**ことは本 IADR が正である。
  - `DocumentVersions.MarkdownUri` 列を将来落とすならマイグレーションが要る（未着手）。

## 関連

- Supersedes: なし
- Superseded by: なし
