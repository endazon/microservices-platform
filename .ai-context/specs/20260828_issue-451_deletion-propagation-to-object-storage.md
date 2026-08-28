---
title: 作業仕様書 — 削除をオブジェクトストレージの実体まで伝播させる（ADR-0057 決定 1 の①）
type: spec
status: done
related_ids:
  - FR-06
  - FR-12
  - FR-19
  - FR-21
  - UC-03
  - UC-11
  - SC-19
  - ADR-0014
  - ADR-0015
  - ADR-0054
  - ADR-0057
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "ADR-0057（削除の伝播範囲。決定 1: 削除は本文の実体と索引まで及ぶ／決定 4: SC-19 固定文言の暫定手段）"
  - "ADR-0054 §結果（doc_scope を遡及付与しない方針。本作業の資産 URI と同型）"
related_adrs:
  - IADR-0296
  - IADR-0008
  - IADR-0024
  - IADR-0270
  - IADR-0290
issue: "#451"
---

# 作業仕様書: 削除をオブジェクトストレージの実体まで伝播させる — #451

## 起点

計画 **ADR-0057**（Accepted）決定 1〜2 は、完全削除の伝播範囲を
「**①オブジェクトストレージの本文・資産 ②ベクトルストアのチャンク・埋め込み
③監査・法務目的の残余を置かない**」へ格上げし、受け入れ基準として
「①オブジェクトストレージに当該文書の本文・資産が残っていない」ことを求めている。

**②は先行 PR（`20260828_issue-1016_delete-propagation.md`）が実装済みで、①だけが残っていた。**
同仕様書は残件をこう記録している ——
「①オブジェクトストレージの実体削除は本 PR に含まれない（`IObjectStorageClient` に削除 API が
無く、API 追加から要る）」。**本作業がその①を実装する。**

## 着手前の実測（欠陥の確認）

| # | 実測した事実 | 出典（実ファイル） |
| --- | --- | --- |
| 1 | `IObjectStorageClient` に削除の口が無い（6 メンバ: `PutText` / `PutBytes` / `GetText` / `GetBytes` / `CanResolve` / `CreatePresignedGetUrl`） | `Platform.Shared.Infrastructure/Foundation/Ports/Storage/IObjectStorageClient.cs` |
| 2 | 削除の入口 3 つはいずれも DB 行を消してイベントを出すだけ | `DocumentEndpoints.cs`（`write.MapDelete("/{id:guid}")`）／`PrivateNoteEndpoints.cs`（`g.MapPost("/purge")`）／`PrivateNoteMaintenanceService.PurgeExpiredAsync` |
| 3 | 🔴 `ObjectStorageOptions.EnableVersioning` の**既定が `true`** で、起動時に `VersionStatus.Enabled` を掛ける。よって**素の `DeleteObject` は delete marker を書くだけ**で全過去版が残る | `ObjectStorageOptions.cs:30` / `S3ObjectStorageClient.EnsureBucketAsync` |
| 4 | 🔴 `DocumentNormalized.AssetUris` が **DocumentService に永続化されていない**（`DocumentNormalizedConsumer` が `CreateNormalized` / `ApplyNormalized` へ渡していない。`Document` に資産の欄が無い） | `DocumentNormalizedConsumer.cs:50,60` / `Document.cs` |
| 5 | 台帳から辿れる本文の参照は `Document.MarkdownUri` と `DocumentVersion.MarkdownUri` の 2 つ | `Document.cs` / `DocumentVersion.cs` |
| 6 | オブジェクトキーの体系が**経路ごとに違う**（`documents/{id:D}/body.md` ／ `{id:N}/document.md` ／ `{id:N}/assets/{figureId}{ext}` ／ `{sourceId}/{fetchId}/raw{ext}`）。**前方一致の一括削除は成立しない** | `DocumentBodyIntake.StorageKey` / `NormalizationService.cs:52,65` / `DataSourceSyncService.cs:100` |
| 7 | `deploy/docker-compose.yml` の `document-service` に `*objectstorage-env` が無い。helm は `services.document.objectStorage: true` を持つ（`values.yaml:206`） | `docker-compose.yml:249-264` / `values.yaml:206` |

### 参照カウントは要るか（実測して判断した）

**要らない。ただし理由は「1 文書 1 オブジェクト」ではなく「削除対象の鍵空間が文書 ID で分割されている」ことである。**

- `Document.MarkdownUri` が採り得る値は `documents/{id:D}/body.md`（本文直接受け入れ・Obsidian 同期）と
  `{id:N}/document.md`（変換経路）の 2 系統で、**どちらも文書 ID を鍵に含む**。
  2 つの文書が同じ本文オブジェクトを指すことは構造上起こらない。
- `AssetUris` は `{id:N}/assets/{figureId}{ext}`。同じく文書 ID で分割されている。
- 🔴 **唯一、共有が起こり得るのは `Document.OriginalUri` である。** これは API 要求
  （`req.OriginalUri`）からしか入らず、取り込み経路（`CreateNormalized`）は設定しない。
  値が `storage://` を指す場合、その実体は DataSourceService が
  `{sourceId}/{fetchId}/raw{ext}` で書いた**別サービス所有の原本**であり得る。
  **よって `OriginalUri` は削除対象から外す**（下記「対象外」）。
  外すことが、参照カウントを持たずに済ませる条件そのものである。

## 対象範囲

- **対象**: `IObjectStorageClient` への削除 API 追加（全バージョン削除）／実装 3 種の追随／
  `Document.AssetUris` の永続化＋マイグレーション／削除の入口 3 つへの結線／
  compose の配備ドリフト是正／変異試験／文書の追随。
- **対象外**:
  - `Document.OriginalUri` の実体削除（上記のとおり別サービス所有であり得る）。
  - 再正規化で参照されなくなった旧資産の掃除（`ApplyNormalized` は `AssetUris` を差し替えるため、
    落ちた資産は台帳から消えて残る）。**ADR-0057 は削除操作の伝播範囲を定めた裁定であり、
    再変換時の孤児掃除は射程外。** フォローアップとして IADR に残す。
  - `DocumentVersion` への資産欄の追加（IADR-0290 が版応答から本文参照を落とした直後であり、
    「その版の資産」と読まれる欄を版スナップショットへ足さない）。
  - Docker を要する経路の実走（本環境に Docker / k3s が無い。`Knowledge.IntegrationTests` は
    `DockerRequired.SkipUnlessAvailable()` で skip される）。

## 設計

### 1. ポートに削除を足す

```csharp
// storage://<bucket>/<key> が指すオブジェクトを、**全バージョン**削除する。
Task DeleteAsync(string uri, CancellationToken ct = default);
```

**戻り値を持たせない。** 呼び出し側が要るのは「消えたか／消えなかったか」だけであり、
版数は S3 実装のログが持つ。失敗は例外で伝える（下記の fail-closed がそれに乗る）。

`S3ObjectStorageClient` の実装（SDK API は `AWSSDK.S3 4.0.100.2` を**リフレクションで実測**して確定した）:

- `ListVersionsAsync(ListVersionsRequest{BucketName, Prefix=key}, ct)` で版を列挙する。
  - **`Prefix` は前方一致なので `v.Key == key` で厳密に絞る**（`body.md` と `body.md.bak` を巻き込まない）。
  - **`IsTruncated` が真の間 `KeyMarker` / `VersionIdMarker` で辿る**（1 応答は既定 1000 件上限）。
  - `.NET SDK は delete marker も `Versions` に混ぜて返す`（`S3ObjectVersion.IsDeleteMarker`。
    `ListVersionsResponse` に `DeleteMarkers` プロパティが**存在しない**ことを実測で確認）。
    **delete marker も版として消す** —— 残すとオブジェクトが「削除済みの状態」として残り続ける。
- 各版を `DeleteObjectAsync(new DeleteObjectRequest{BucketName, Key, VersionId = v.VersionId}, ct)` で消す。
- **列挙が 0 件でも 1 回は素の削除を撃つ**（バージョニング無効のバケット・未バージョン化オブジェクトの取りこぼし防止）。

`NullObjectStorageClient` は 🔴 **`Put*` と同じ作法（警告して成功）**にする。
例外にすると、ストレージ未構成の開発環境で完全削除が 500 になる。

### 2. `AssetUris` を永続化する

`Document.AssetUris`（`List<string>`）を追加し、`Attributes` / `Tags` と同じ jsonb 変換器で永続化する。
`CreateNormalized` / `ApplyNormalized` が受け取り、`DocumentNormalizedConsumer` が `ev.AssetUris` を渡す。
マイグレーション `AddDocumentAssetUris`（`Documents.AssetUris` jsonb・既定 `[]`）。

🔴 **既存文書には資産 URI が入らない**（遡及付与しない）。`doc_scope` が実データ 0 件・遡及付与しない
方針（計画 ADR-0054 §結果）と同型である。**「全部消える」とは書かない。**

### 3. 削除の入口 3 つへ結線する（台帳から逆引きする）

`DocumentObjectPurger`（新設・scoped）が台帳を集めて消す。集める先は 3 つ:

1. `Document.MarkdownUri`
2. `Document.AssetUris` の全要素
3. **全 `DocumentVersion.MarkdownUri`**（版スナップショット。過去に別のキーを指していた本文を取りこぼさない）

`storage://` 以外（http 等）は対象外にする（`StorageUri.IsStorageUri` で選別）。重複は畳む。

#### 判断 A: DB 行の削除とオブジェクト削除の**順序** → **オブジェクトを先に消す**

2 つの壊れ方を比べた。

| 壊れ方 | 何が起きるか | 検知可能性 | 回復可能性 |
| --- | --- | --- | --- |
| **DB 行を先に消し、オブジェクト削除が失敗** | 台帳が消え、実体だけが残る。**その実体を指す値がもうどこにも無い** | 🔴 **不能**。誰も参照を持たないので、棚卸しでも見つからない | 🔴 **不能**（永久の不可視な残留） |
| **オブジェクトを先に消し、DB コミットが失敗** | 文書行は残り、本文が引けない | ✅ 画面が縮退表示になる／再削除で解消 | ✅ 可能（オブジェクト削除は冪等） |

**「消したのに残っている」ほうが悪い。** SC-19 は「いかなる方法でも復元できません」と言い切る画面であり、
不可視な残留はその宣言を**嘘にしたうえで、嘘であることを誰にも観測させない**。
よって **オブジェクト削除 → DB 行削除 → イベント発行** の順に固定する。

#### 判断 B: 失敗時の扱い → **fail-closed（対話操作）／文書ごとに隔離（定期処理）**

- **対話操作（FR-06 削除・FR-19 完全削除）**: オブジェクト削除が失敗したら**例外をそのまま通す**。
  `SaveChangesAsync` の**手前**で落ちるので DB 行は残り、利用者は 5xx を受ける。
  **「成功した」と告げないことが要点**である。再実行すれば台帳がまだ在るので同じ削除をやり直せる。
- **定期処理（90 日自動物理削除）**: **文書ごとに隔離**する。1 件の失敗で周期全体を止めない
  （止めると、無関係な資料の期限超過が積み上がる）。失敗した文書は行を残し、
  **次周期で再試行する**（`PurgeAt <= now` の条件は満たしたままなので自然に再入する）。

### 4. 配備ドリフトの是正

`deploy/docker-compose.yml` の `document-service` へ `*objectstorage-env` を足す。
`datasource-service` / `graph-service` と同じ形（`depends_on` に minio を足さない）に揃える ——
未構成でも縮退クライアントで起動する設計であり、起動順の強制は不要である。

### 5. SC-19 の但し書き

§「SC-19 の但し書きの扱い」に分けて記す（結論と理由）。

## 母集合の引き方（規則 9・10）と結果

**着手前に、誤りの側の語で全ファイルを走査した。拡張子で絞っていない。軸は 4 本引いた。**

| 軸 | コマンド | 生ヒット |
| --- | --- | --- |
| 1 | `git grep -n -I -P "(?<!I)ADR-0057" -- . ':!src/ai-stock-trading'` | 31 行 / 15 ファイル |
| 2 | `git grep -n -I -E "削除 ?API｜実体を消｜復元できません" -- . ':!src/ai-stock-trading'` | 14 ファイル |
| 3 | `git grep -n -I "AssetUris" -- . ':!src/ai-stock-trading'` | 23 行 |
| 4 | `git grep -n -I -E "オブジェクトストレージ"` を `残｜未｜届｜消｜削除` で絞り込み | 26 行 |

🔴 **軸 1 は当初 `ADR-0057` で引いたが、`IADR-0057`（ユニット依存方向の機械検査＝無関係）を
大量に巻き込んでいた。** PCRE の後読みで打ち分けてから数え直した。
**別系統の ID が前方一致で混ざるのは、この採番体系（`ADR` / `IADR`）に固有の落とし穴である。**

### 追随すると判断したもの

| # | 場所 | 何が誤りになるか | 対応 |
| --- | --- | --- | --- |
| 1 | `docs/data/private-note.md:66` | 「🔴 オブジェクトストレージ上の本文実体は残る（ストレージポートに削除が無い。既知の残件）」 | **書き換える**（live な権威文書） |
| 2 | `docs/screens/SC-19_private-notes.md:74-79` | 「削除が本文の実体まで及ぶ配線は未配備であり（本文の実体を消す経路がまだ無い）」 | **書き換える**（但し書きの**根拠**が変わる。文言そのものは残す。下記§参照） |
| 3 | `docs/tests/SC-19_private-notes-screen.md:54` | 「①には実体削除が未配備であることの但し書きを含む」 | **書き換える**（同上） |
| 4 | `docs/functional/FR-06_document-crud-versioning.md:112` | 「本文（Markdown 本体）のオブジェクトストレージ実保存は未実装（現状 URI 参照のみ）」 | **書き換える**。FR-21 の本文直接受け入れ経路が実保存しており、**着手前から既に誤り**だった（本作業が削除を足したことで「読める場所に無い」ではなく「消す対象がある」と読まれるため、放置すると誤りが増幅する） |
| 5 | `deploy/docker-compose.yml` | document-service に objectstorage-env が無い | **足す**（上記 4） |

### 追随しないと判断したもの（除外理由つき）

| 場所 | 除外理由 |
| --- | --- |
| `.ai-context/specs/20260828_issue-1016_delete-propagation.md:69` ほか | **凍結記録**。`.claude/rules/traceability.repo.md`「凍結の射程」により、`.ai-context/specs/` は `［YYYY-MM-DD 追記 / #NNN］` 書式の経過追記が可。**本件は「当時の残件宣言」であり、当時の事実として正しい。** 後続で解消したことは本仕様書と IADR-0296 が持つため、追記もしない（記録の重複は片方が腐る） |
| `.ai-context/specs/20260828_issue-451c_sc19-sc20-screens.md:167-180` | 同上（凍結記録・当時の判断の記録） |
| `.ai-context/adr/IADR-0264:99` / `IADR-0281:199` | **凍結記録（`Accepted` の本文）**。いずれも「格納した本文が残る」「ストレージ依存が要る」の文脈で、削除 API の有無を主張していない |
| `docs/how-to/plan-id-range-history-annex.md:34` | ADR-0057 の**要旨を引いた表**。裁定内容の引用であり、実装状態を主張していない |
| `docs/how-to/session-handoff.md:171` | 裁定 3 本の**採番の記録**。実装状態を主張していない |
| `docs/functional/FR-12_document-normalization.md` / `docs/tests/FR-12_*` の `AssetUris` | 発行側（ConversionService）の契約の記述。**本作業は購読側の永続化を足しただけで、発行側の契約は変えていない** |
| `docs/api/openapi.yaml:1270`（「実体を消さず status を `disabled`」） | DataSource の無効化の説明。別件 |
| `src/knowledge/.../GraphService/.../DocumentDeletedConsumer.cs:22`（「EF の一括削除 API」） | 語が同じだけの別物（`ExecuteDelete`） |
| `docs/functional/FR-01_data-source-catalog.md` の「実オブジェクトストレージ未接続」 | **dev 環境の縮退の説明**であり、削除の可否を主張していない |
| フロント i18n カタログ（`locales/{ja,en}/messages.po`）と `PrivateNotesPage.test.tsx` | **固定文言を変えない**判断（下記§）のため、追随不要 |

### 規則 10 の引き直し（**是正後の語で引き直して初めて出たもの**）

🔴 **是正前の語（「削除 API が無い」「残る」）では捕まらない母集合がある。** 実装を終えてから
「この変更で**新たに**誤りになる自分の記述」を軸を変えて引き直したところ、**3 件出た**。

| 軸 | コマンド | 出たもの | 対応 |
| --- | --- | --- | --- |
| 5 | `git grep -l -I "MarkdownUri" -- docs/` | `docs/data/document-and-version.md` が `Document` の**属性表・ER 図・NULL 非許容 JSON の注記**を持つ。`AssetUris` を足した以上、**表が実体と食い違う** | **書き換える**（live な権威文書）＋削除の順序の節を追加 |
| 6 | `git grep -n -I "IObjectStorageClient" -- docs/ scripts/ deploy/ .ai-context/adr/` | `IADR-0024` がポートの**メンバを 6 個で列挙**している（削除を含まない） | **凍結記録。本文は書き換えず、日付つき追記で現行の正（IADR-0296）を指す** |
| 6 | 同上 | `IADR-0270` が「🔴 完全削除後も MinIO 上の本文オブジェクトは残る」と**現在形で主張**し、フォローアップ 3 に「ストレージ実体の削除手段」を残している | **同上**（主張が偽になったため、追記で解消を明示する） |

`docs/tech/composability-classification.md` と `docs/tech/composable-component-guide.md` も
ポートを挙げるが、**メンバを列挙していない**ため誤りにならない（除外）。

**この 3 件は、着手前の 4 軸（誤りの側の語で引いた軸）には 1 件も現れていない。**
是正前の語で引いた母集合は、是正が生む誤りを構造的に含まない —— 規則 10 が言っているのは
この非対称性である。

## SC-19 の但し書きの扱い（ADR-0057 決定 4）

**結論: 文言はそのまま残す。ただし「なぜ残すか」の根拠を差し替え、文書側をそれに追随させる。**

計画 ADR-0057 決定 4 は、決定 1 が未実装である間の暫定手段として
「①の文言を出さない」か「但し書きを添える」の 2 択を実装側へ委ねており、
実装は**但し書き**（「（削除の反映には時間がかかる場合があります。）」）を選んでいた。

- **当初の根拠は消えた。** 「本文の実体を消す経路がまだ無い」は本作業で解消した。
  SC-19 が扱う**個人資料は変換経路を通らないため資産を持たず**（`AssetUris` を書くのは
  `DocumentNormalizedConsumer` のみ。個人資料は `Document.Create` ＋ Obsidian 同期で作られる）、
  §2 の限界（既存文書の資産が辿れない）は **SC-19 の射程には掛からない**。
- **別の根拠が残っている。** ②索引・③グラフの掃除は `DocumentDeleted` を介した**非同期**である
  （先行 PR の実装）。応答を返した時点では、検索・グラフ側に短時間残り得る。
  「削除の反映には時間がかかる場合があります」は**この事実の記述としてなお真**である。
- したがって**文言は据え置く**（利用者に見える面を変えない・i18n カタログを動かさない・
  計画の固定文言と衝突させない）。**変えるのは「なぜ置いているか」を説明している文書のほうである。**

🔴 **「実装したから外す」と機械的にやらない**という指示に対する答えがこれである ——
**外す条件（①の未配備）は消えたが、置いておく別の条件（②③の非同期性）が立っている。**

## 受け入れ基準

- [x] `IObjectStorageClient.DeleteAsync` があり、実装 5 種（本番 2・テストダブル 3）が追随している
- [x] S3 実装が**全バージョン**（delete marker を含む）を消し、ページングを辿る
- [x] `NullObjectStorageClient` は警告して成功する（500 にしない）
- [x] `Document.AssetUris` が永続化され、マイグレーションがある
- [x] 削除の入口 3 つが、台帳（本文 URI ＋ 全版スナップショット URI ＋ 資産 URI）を消す
- [x] オブジェクト削除が失敗したら DB 行が残る（対話操作）／文書ごとに隔離される（定期処理）
- [x] compose の `document-service` に objectstorage-env がある
- [x] **変異 4 種＋ページングの 5 種すべてが KILL される**

## テスト方針

**「実データで緑」は検出力の証拠にならない。変異試験で示す。** 器は 2 層に分ける。

- **ポート境界（`DocumentService.Tests`）**: `RecordingObjectStorageClient` に `Deleted` の記録と
  失敗注入を足し、3 つの入口が**何を消したか**を直接見る。Docker 非依存。
- **S3 実装（`Platform.Shared.Infrastructure.Tests`）**: `AmazonS3Client` の
  `ListVersionsAsync` / `DeleteObjectAsync` が **`virtual`** であること（実測）を使い、
  派生クラスで差し替える。**モックライブラリを持たない同プロジェクトの作法に沿う**
  （既存 `ObjectStorageBootstrapHostedServiceTests` と同じく実 I/O を起こさない）。
- MinIO 実体でのラウンドトリップは `Knowledge.IntegrationTests` へ足すが、
  🔴 **本環境に Docker が無いため skip される。緑とは書かない。**

## 計画書との差異

- 差異: あり。**ADR-0057 の受け入れ基準①「オブジェクトストレージに当該文書の本文・資産が残っていない」は、
  本作業の後も「本作業より前に取り込まれた文書の図表資産」については満たせない**
  （`AssetUris` を遡及付与しないため）。ADR-0054 §結果（`doc_scope` を遡及付与しない）と同型の受容であり、
  IADR-0296 決定 4 に明記する。本文（`MarkdownUri`）は既存文書でも消える。

## 未決事項

- 再正規化で参照から落ちた旧資産の掃除（孤児）。IADR-0296 フォローアップ 1。
- `Document.AssetUris` を `DocumentDto` / openapi へ出すか。現状は出さない（契約を広げない）。
