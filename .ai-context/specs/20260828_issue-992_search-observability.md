---
title: 作業仕様書 — 統合スタックで検索が実際に効くことを観測可能にする（#992・案 1）
type: spec
status: done
related_ids:
  - FR-02
  - FR-03
  - FR-05
  - FR-21
  - SC-01
  - SC-02
  - UC-01
  - UC-03
  - ADR-0014
  - ADR-0015
  - ADR-0016
  - IADR-0133
  - IADR-0252
  - IADR-0255
  - IADR-0256
  - IADR-0264
  - IADR-0284
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "ADR-0016（埋め込みのモデル別コレクションと越境判定・fail-closed）"
  - "ADR-0015（正規化本文はオブジェクトストレージへ置く）"
  - "ADR-0014（オブジェクトストレージのバケット・キー設計）"
issue: "#992"
---

# 作業仕様書: 検索が実際に効くことを観測可能にする（#992・案 1）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-03（横断検索・ハイブリッド）/ FR-02（取り込み・埋め込み）/ FR-21（本文の直接受け入れ）/ FR-05（ABAC）
- ユースケース（UC）: UC-01（検索・質問する）/ UC-03（文書を登録・編集する）
- 画面（SC）: SC-01（横断検索）/ SC-02（検索結果）
- 計画 ADR: ADR-0014 / ADR-0015（本文の置き場）/ ADR-0016（埋め込みの越境判定）

## 何を作るか（選定済みの案）

issue #992 は 3 案を挙げ、**着手前に決めること**としていた。**利用者計画で案 1 が選定済み**である。

> 案 1: **索引可能な文書を投入する経路を用意する**（`MarkdownUri` を持つ文書を seed する。
> `ABACSEED` と同じ「使い捨てスタックでだけ opt-in」の形にできる）

案 2（埋め込みを CI で供給する）・案 3（`POST /bff/search` の縮退を区別可能にする）は**本作業の射程外**である。
案 3 は #1010 の周辺で別に扱う。案 2 は本作業の実測（§3）で**必要性が確定した**ので、申し送りに書く。

## 1. 着手前の実測（issue 本文の前提を確かめる）

issue #992 は 2026-08-22 に書かれ、その時点の実装を根拠にしている。**まず実装を読み直した。**

### 1.1 FR-21 の本文直接受け入れ経路は **既に在る**（issue 本文の記述は古い）

issue 本文は「`CreateDocumentRequest` の項目は `Title` / `OriginalUri` / `ContentType` /
`Attributes` / `Tags` のみで、`MarkdownUri` を受け取らない」と書く。**DocumentService については誤りである。**

| 実測点 | ファイル | 事実 |
| --- | --- | --- |
| DocumentService の登録要求 | `DocumentEndpoints.cs:456-465` | `CreateDocumentRequest` は**末尾に `string? Body = null` を持つ**（FR-21・[[IADR-0264]]） |
| 本文の格納 | 同 `:108-127` | `Body` があれば `storage.PutTextAsync` → `Document.CreateWithBody(... bodyUri ...)`。**`MarkdownUri` が入る** |
| 既存文書への投入 | 同 `:273-315` | `PUT /documents/{id}/body`（所有者ベース。拒否は 404・[[IADR-0277]]） |
| 上限・型 | `DocumentBodyIntake.cs` | 1 MB（UTF-8 バイト）・`text/markdown; charset=utf-8` |

**したがって「索引可能な文書を投入する経路」は新設不要で、既存の FR-21 経路をそのまま seed に使える。**
`CreateDocumentRequest` へ `MarkdownUri` を足す案は**採らない**（[[IADR-0264]] が「新しい欄を作ると
取り込みの分岐が 2 本になる」として明示的に退けている）。

### 1.2 BFF 側は `Body` を運ばない（issue 本文が正しいのはこちら）

`Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs:321-326` の `DocumentCreateRequest` は
`Title` / `OriginalUri` / `ContentType` / `Attributes` / `Tags` の 5 項目で、**`Body` を持たない**。
BFF は受けた `req` をそのまま `/documents` へ転送するため、**BFF 経由で作った文書の本文は落ちる。**

**本作業では BFF 契約を変えない。** seed は使い捨てスタックの初期投入であり、
`ABACSEED` が AuthorizationService の管理 API を直接叩くのと同じ流儀で、**DocumentService を直接叩く**。
BFF に `Body` を通すのは SC-05 の画面要件が出てから決めるべき別の判断であり、
`docs/api/openapi.yaml` と orval 生成物を巻き込む（本作業の射程外）。

### 1.3 取り込みの早期 return は `MarkdownUri` だけを見る

`IngestionService/Composable/Steps/DocumentUpdatedConsumer.cs:32-36`:

```csharp
if (ev.MarkdownUri is null) { logger.LogWarning("... skipping ingestion", ...); return; }
```

**`MarkdownUri` が入りさえすれば早期 return は通過する。** ここが案 1 の買うものである。

### 1.4 🔴 DocumentService に **オブジェクトストレージが配線されていない**（新規に見つけた欠陥）

`deploy/helm/microservices-platform/values.yaml` の `services.document` には
**`objectStorage: true` が無い**（`datasource` / `conversion` / `ingestion` / `wiki` / `graph` には在る）。
`deployment.yaml:70-85` は `$svc.objectStorage` が真のときだけ `ObjectStorage__*` を描画するので、
DocumentService は `NullObjectStorageClient` へ縮退する（`Program.cs:51` の `AddPlatformObjectStorage`）。

縮退クライアントは **`storage://<bucket>/documents/<id>/body.md` という決定的な URI を返すだけで、
本文を永続化しない**（`NullObjectStorageClient.PutTextAsync`）。結果:

- `MarkdownUri` は入る（早期 return は通過する）
- しかし IngestionService（`objectStorage: true` を持つ）が `GetTextAsync` で読みにいくと
  **オブジェクトが存在せず失敗する**

**FR-21 は配備のどの環境でも本文を落としている。** seed の前提として、ここを直す（§4.1）。

### 1.5 🔴 埋め込みが無いと**索引に 1 チャンクも入らない**（案 1 単独では「ヒット」に届かない）

parent の指示「埋め込みが無くてもハイブリッド検索の全文側でヒットし得るかを実測せよ」への答えである。
**問い側と索引側で答えが違う。**

| 側 | 実測 | 結論 |
| --- | --- | --- |
| **問い（検索）** | `HybridSearchService.cs:102-108` —— 空ベクトルなら `WarnEmbeddingUnavailable` を出し、**ベクトル側を空にして全文側だけで続ける**（#995 / [[IADR-0256]]） | **全文だけでもヒットし得る** |
| **索引（取り込み）** | `DocumentUpdatedConsumer.cs:62-82` —— `Embedded=false` なら `continue`（恒久拒否）または `EmbeddingTransientException`（一時障害）。**どちらでも `UpsertChunkAsync` に到達しない** | **索引に何も入らない** |

`Embedding__Voyage__ApiKey` は `deploy/local/values-local.yaml` にも `k8s-local-up.sh` にも無い
（配線されているのは `Llm__ApiKey`＝Anthropic だけ）。よって統合スタックでは:

- `confidentiality=public` の文書 → 越境判定はティアB（voyage）を選ぶ → `VoyageEmbeddingProvider` が
  **API キー未設定で例外** → `/embed` は `Embedded=false, Retryable=true` → 取り込みは例外を投げ、
  **リトライののち DLQ**（索引されない）
- 区分が未指定・`confidential` 以上 → 許容ティアは A のみ、ティアA は `Enabled=false`（既定）→
  **fail-closed で送信拒否** → `Retryable=false` → チャンクは skip（索引されない）

**したがって「全文側へ判定を倒す」ことはできない。**「全文側だけでも当たる」のは
**索引に点が在るとき**の話であり、点を作る側が fail-closed で止まっているからである。

**この fail-closed は緩めない。** 機密区分 × ティアの越境マトリクス（`EmbeddingEgress`）は
セキュリティ上の既定値であり、「CI だから」で開けない（issue #992 の受け入れ観点・parent の指示）。
埋め込みの供給は**案 2 の裁定事項**として残す（§7）。

### 1.6 RetrievalService が読むコレクションは 1 本

`RetrievalService` の `appsettings.json` は `Qdrant:CollectionName = knowledge_chunks_voyage_3_5`
（`QdrantVectorStore.cs:17-19` が単一コレクションを読む）。取り込みはモデル別コレクションへ書くため、
**ティアA（`knowledge_chunks_ruri_v3`）へ索引しても検索からは見えない。**
「ティアA の stub を足せば済む」わけではないことを、案 2 の裁定材料として記録しておく。

## 2. 是正・追随の母集合（規約 9・10）

**誤りの側の文字列で全ファイルを走査した**（規則 1・3・4。パス除外のみ、拡張子で絞らない）。

```
grep -rn "初期投入経路|索引が空|索引そのものが空" .        （submodule・node_modules を除く）
grep -rln "MarkdownUri" --include=*.md --include=*.sh --include=*.js --include=*.yml .
```

軸 2 本の結果（生の出力に対して判断した。`head` で切っていない）:

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `scripts/verify-oidc-edge-flow.sh`（4 箇所: 46 / 284 / 291 / 320 行） | **直す** | live なコードのコメント。「文書の初期投入経路が無い」「索引そのものが空」は本作業で偽になる |
| `.github/workflows/integration-stack.yml:98` | 直さない | ABACSEED についての記述で、内容は正しい |
| `.ai-context/specs/20260822_issue-466_*.md` / `20260822_issue-972_*.md` / `20260823_issue-995_*.md` | **直さない** | 確定済みの作業仕様書＝凍結記録。当時の実測として正しい（`.claude/rules/traceability.repo.md`「凍結の射程」） |
| `.ai-context/adr/IADR-0252` / `IADR-0255` | **直さない** | 同上。決定を覆すものではないので後継 ID の併記も不要 |
| `.ai-context/superpowers/plans/2026-06-26-P0-foundation.md` | 直さない | 凍結記録（追記も不可） |

**導出値は走査ではなく計算し直した**（規則 10）: `verify-oidc-edge-flow.sh` の `TOTAL` は
段の追加に合わせて**加算式へ書き換える**（固定値を書き換えるのではなく、モードごとに足す）。

## 3. 設計

### 3.1 seed の投入経路

`ABACSEED=1` と同型にする（[[IADR-0133]] の方式をそのまま踏襲する）。

- **単一情報源はリポジトリ内の JSON**: `deploy/local/search-seed/documents.json`
- **投入は API 経由**（`POST /documents`。直 DB 書き込みをしない）
- **冪等**: 同じタイトルの文書が既に在れば作らない
- **認証**: Keycloak の直接付与（client `bff`・管理者ユーザー）。**資格情報の解決は
  `seed-abac-policies.js` の関数を `require` して再利用する** —— realm ファイルからパスワードと
  client_secret を引く作法（#933 / #984 の再発防止）を 2 か所に写さないため
- **接続先**: `kubectl port-forward svc/document-service`（自分で張り、終了時に片付ける）
- `--dry-run` で副作用なしに投入予定を見せる

**属性**は `confidentiality=public` / `department=engineering` にする。abac-seed のポリシーは
`confidentiality` だけを文書条件に持つため、`clearance` を持つ全利用者に見え、
**属性を 1 つも持たない `poc-operator` には見えない**（負の対照が成立する）。
**タグは付けない** —— 辞書に無いタグは 400 になる（SC-05 / #635。`verify-oidc-edge-flow.sh` が実測済み）。

### 3.2 `k8s-local-up.sh` の opt-in

`SEARCHSEED=1` のときだけ `node scripts/seed-search-documents.js` を呼ぶ。
既定（未設定）は**1 バイトも足さない**（ABACSEED と同じ fail-safe）。best-effort（失敗で up を止めない）。

### 3.3 判定の強化（`verify-oidc-edge-flow.sh`）

**2 つの opt-in に分ける。**「今日 CI で緑にできる判定」と「埋め込みの供給を待つ判定」は
達成条件が違うので、1 つのフラグに混ぜると**後者のせいで前者が走らなくなる**。

| フラグ | 段 | 判定 | 今日 CI で緑か |
| --- | --- | --- | --- |
| `SEARCH_SEEDED=1` | S1 | seed 文書が一覧に見え、**`markdownUri` を持つ**（＝取り込みの早期 return を通過する形になっている） | **緑にできる**（§4.1 の是正後） |
| 〃 | S2 | 負の対照: 属性を持たない利用者の検索が **0 件**（全開放の検出） | 緑にできる |
| `SEARCH_HITS=1` | S3 | seed 文書の語で検索して **実際にヒットする** | **緑にできない**（§1.5。案 2 待ち） |

- `SEARCH_HITS=1` は `SEARCH_SEEDED=1` を含意する（前提を確かめずに結論だけ測らない）
- **「空であること」を PASS の根拠にしない。** S1 は「在ること」、S3 は「当たること」を測る。
  S2 だけが 0 件を期待するが、これは**負の対照**であり、S1 が非空であることと対で意味を持つ
- 段番号は `STEPS` から動的に採る（既存の固定ラベルは 1 バイトも変えない）。
  `TOTAL` はモードごとの加算式にする（§2 の規則 10）

### 3.4 CI の配線

`.github/workflows/integration-stack.yml`:

1. up の step へ `SEARCHSEED=1` を足す
2. **投入を確定させる step を足す**（`node scripts/seed-search-documents.js`）——
   up の中の投入は best-effort なので、readiness の後に**終了コードを見る形で再実行する**
   （ABAC 投入とまったく同じ理由・同じ形）
3. `ABAC_POSITIVE=1` の gate へ **`SEARCH_SEEDED=1` を足す**
4. **`SEARCH_HITS=1` は足さない。** §1.5 のとおり今日は原理的に落ちる。
   落ちると `report-failure` が毎晩 issue を起こし、**他の退行が埋もれる。**
   「走らせないと保証にならない」ことは承知のうえで、**案 2 の裁定が出るまで保留する**（§7）

## 4. 変更点

### 4.1 `deploy/helm/microservices-platform/values.yaml`（§1.4 の是正）

`services.document` へ `objectStorage: true` を足す。1 行。

- 本番でも正しい（FR-21・ADR-0015 は本文をオブジェクトストレージへ置くと定めている）
- `minio-credentials` Secret は同 namespace に既に在る（`k8s-local-up.sh:152` が作る。ESO 経路も同じ名前）
- バケットの作成は ConversionService の bootstrap が担う（DocumentService は作らない。`Program.cs:47-51`）

### 4.2 `deploy/local/search-seed/documents.json`（新規）

seed する文書（タイトル・属性・本文・**検索の合言葉**）を宣言的に持つ。

### 4.3 `scripts/seed-search-documents.js`（新規）

§3.1 の投入器。純粋関数（`selectMissingDocuments` / `buildCreateRequest` / `seedProbeTerm`）を
export して単体試験できる形にする。

### 4.4 `scripts/k8s-local-up.sh` / `scripts/verify-oidc-edge-flow.sh`

§3.2・§3.3。

### 4.5 テスト

- `scripts/k8s-local-up.test.js`: `SEARCHSEED` の opt-in トークン（`seed-search-documents.js`）を
  `OPTIN_TOKENS` へ追加する。**既定オフで不在・opt-in で単独検出力を持つ**ことが自動で課される
- `scripts/scripts.repo.test.js`: 投入器の純粋関数と、`verify-oidc-edge-flow.sh` の
  `TOTAL` 加算式・新段の結線を固定する

## 5. 変異試験

**この作業環境には docker / k3s が無く、統合スタックを起こせない。** #466 の先例と同じく、
`EDGE_URL` / `KC_URL` を受けるスタブ HTTP サーバを scratchpad に立てて計測する
（**スタブはコミットしない**。検査対象ではない）。結果は §6。

**実クラスタでの実走は CI に委ねる。** 本仕様書はスタブでの実測しか主張しない。

## 6. 実測（変異試験）

［2026-08-28 追記 / #992］実装後に実測した。着手前は本節を空にしてあった ——
先に数字を書くと**手順どおり追試しても再現しない数**が残るためである（規約 8）。

スタブは `EDGE_URL`（18801）と `KC_URL`（18802）を 1 プロセスで受ける Node の HTTP サーバで、
`scratchpad` に置き**コミットしない**。スクリプトは JWT の署名を検証せず payload を base64
デコードするだけなので、偽の JWT で利用者を打ち分けられる（`preferred_username` で
`developer` と `poc-operator` を分岐させた）。

### 6.1 基準（変異なし）—— 🔴 門が誤発火しないこと

| モード | EXIT | 出力 |
| --- | ---: | --- |
| 既定 | **0** | `結果: PASS 16 / FAIL 0（段 11/11）` |
| `ABAC_POSITIVE=1` | **0** | `結果: PASS 22 / FAIL 0（段 17/17）` |
| `SEARCH_SEEDED=1` | **0** | `結果: PASS 18 / FAIL 0（段 13/13）` |
| `ABAC_POSITIVE=1 SEARCH_SEEDED=1` | **0** | `結果: PASS 24 / FAIL 0（段 19/19）` |
| `ABAC_POSITIVE=1 SEARCH_HITS=1` | **0** | `結果: PASS 25 / FAIL 0（段 20/20）` |

**既定と `ABAC_POSITIVE=1` の値は #466 の実測と一致している**（PASS 16 / 段 11、PASS 22 / 段 17）——
既存の段に手を入れていないことの傍証である。
`SEARCH_HITS=1` は `SEARCH_SEEDED=1` を含意するので段が 19 → 20 へ 1 本だけ増える（設計どおり）。

### 6.2 変異と結果（すべて `ABAC_POSITIVE=1 SEARCH_HITS=1` で実施）

| # | 変異 | 着弾確認 | EXIT | 検出した判定 |
| --- | --- | --- | ---: | --- |
| M1 | seed 文書が一覧に**現れない** | スタブ側 | **1** | S1「seed 文書（…）が一覧に無い」 |
| M2 | seed は在るが **`markdownUri` が null**（§1.4 の欠陥そのもの） | スタブ側 | **1** | S1「seed 文書は在るが markdownUri を持たない（取り込みの早期 return で捨てられる）」 |
| M3 | 検索が **0 件**を 200 で返す（索引が空・埋め込み不能の形） | スタブ側 | **1** | S3「seed 文書（…）がヒットしない（0 件）。索引に入っていない疑い」 |
| M4 | 検索が**別の文書**を 1 件返す（非空だが seed を含まない） | スタブ側 | **1** | S3「ヒット 1 件だが seed 文書（…）を含まない」 |
| M5 | 属性を持たない利用者の検索が **seed を返す**（ABAC 全開放） | スタブ側 | **1** | S2「200・1 件（属性が無いのに検索できている＝全開放）」 |
| M6 | 段 S1 を**丸ごと削除**（32 行削除 / 0 行追加） | 実装直後の版との `diff` で削除のみを確認 | **1** | 門「実行した段が 19 本で、宣言（TOTAL=20）と一致しません」 |
| M7 | `TOTAL` の加算を 1 つ落とす（`SEARCH_SEEDED` の +2 → +1） | `diff` が 2 行（1 行の置換）であることを確認 | **1** | 門「実行した段が 20 本で、宣言（TOTAL=19）と一致しません」 |

M1〜M5 はいずれも `PASS 24 / FAIL 1` で、**FAIL は狙った 1 件だけ**である
（他の判定を巻き込んでいない＝検出が特定の段に効いている）。
各変異のあと復旧し、`diff` が 0 に戻ることを確認した（M6・M7 はスクリプト本体の変異なので
バックアップとの `diff -q` が `same` を返すことまで確認した）。

🔴 **M3 が本 issue の中心である。** #991 の判定（200 ＋ 形）では M3 は素通りしていた ——
`{"results":[],"totalHits":0,"elapsedMs":1}` は契約どおりの形だからである。

### 6.3 単体試験・機械検査（実測）

| 実行 | 結果 |
| --- | --- |
| `node scripts/k8s-local-up.test.js` | `✓ 97 tests passed` |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | `✓ 625 tests passed`（#992 の新規 10 件を含む） |
| `bash -n scripts/verify-oidc-edge-flow.sh` | 構文 OK |
| `node scripts/check-action-versions.js` | `✓ Actions のバージョンに退行なし` |

**`scripts.test.js` は IADR 採番の欠番（0282 / 0283 が並列トラックの予約）で停止するため、
一時的な placeholder を置いて全件を走らせ、確認後に削除した**（placeholder はコミットしていない）。
この欠番は本 PR 単独の問題ではなく、3 本が develop へ着地した時点で解消する。

`node scripts/check-deploy-manifests.js` は **helm / kubectl / kubeconform がこの環境に無く
SKIP ではなく失敗**する（設計どおり。`DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS=1` は立てていない）。
**chart の実レンダリング検証は CI に委ねる。**

## 7. 申し送り（本作業で閉じないもの）

1. 🔴 **案 2（埋め込みの供給）の裁定が要る。** §1.5・§1.6 のとおり、
   **`SEARCH_HITS=1` は埋め込みが供給されるまで原理的に緑にならない。**
   検討材料: 検索は `knowledge_chunks_voyage_3_5` しか読まない（§1.6）ので、
   ティアA の stub を足すだけでは届かない。**ティアB の宛先をクラスタ内の決定的な stub へ向ける**
   （越境マトリクスは 1 バイトも触らず、宛先 URL だけを使い捨てスタックで差し替える）形なら
   fail-closed を緩めずに済むが、**これは設計判断であり裁定が要る。**
2. **案 3（`POST /bff/search` の縮退の区別）は未着手。** #992 のコメントが推す分割に従い、別 issue で扱う。
3. **BFF の `DocumentCreateRequest` に `Body` が無い**（§1.2）。SC-05 の画面要件が出たときに決める。
4. **§1.4 の欠陥は本 PR で直すが、既存環境のデータは直らない。** `NullObjectStorageClient` の
   縮退で作られた文書の `MarkdownUri` は**実体を持たない**。再投入が要る（使い捨てスタックでは不要）。
