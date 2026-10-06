---
title: 作業仕様書 — 全文書（または条件で絞った文書）へ DocumentUpdated を安全に再発行する管理の口と駆動スクリプト（#1762）
type: spec
status: done
related_ids: [FR-02, FR-06, UC-04, NFR-09, ADR-0013, ADR-0016, ADR-0027, ADR-0127, IADR-0503, IADR-0314, IADR-0313, IADR-0455, IADR-0484, IADR-0497]
author: claude
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0013_embedding-model.md（フォローアップ「再索引運用（モデル更新時）の手順整備」）
  - planning:projects/microservices-platform/07_adr/ADR-0027（メッセージング。Wolverine）
issue: "#1762"
---

# 作業仕様書 — DocumentUpdated の再発行（再索引の手段）（#1762）

> 本仕様書は実装着手前に作成した（着手 2026-10-06）。判断の記録は **IADR-0503** に置く。
> 計画は project-planning `c3ad458` を読んだ。基点は MSP `origin/develop` `34ee800e`。
> **稼働クラスタには一切触れていない。** 稼働での実行は PoC セッションが行う（§PoC の操作者向けの手順）。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-02**（取り込んだ文書をパース・チャンク化・埋め込み生成し、検索インデックスへ登録する。UC-04）・
  **FR-06**（文書管理。口は DocumentService に置く）。ブランチ名は FR-02 を起点に取った（目的は索引の再構築）。
- 計画 ADR: **ADR-0013**（フォローアップ「再索引運用（モデル更新時）の手順整備」）・**ADR-0016**（埋め込みプロバイダ）・
  **ADR-0027**（Wolverine）・**ADR-0127**（高機密文書は語彙索引だけ）。
- 関連 IADR: IADR-0314（発行側の経路宣言。#992）・IADR-0313（決定的ローカル埋め込み）・IADR-0455（発行の門）・
  IADR-0484（管理者だけの読み取りの口の先例）・IADR-0497（語彙索引）。
- 起点 issue: **#1762**。
- 採番: `origin/develop` `34ee800e` の IADR の最大は 0502。本件は **IADR-0503**。

## 射程と、触らないもの

- **触る**: DocumentService に管理者だけの口 `POST /documents/republish-updated` を 1 本（操作フォルダ `Features/Documents/Republish/`）、
  合成点（`DocumentEndpoints`）への 1 行、駆動スクリプト `scripts/republish-document-updated.js`、`scripts/live-scripts.json`・
  `scripts/README.md`・`scripts/scripts.repo.test.js`（非検査器の宣言と純関数の試験）、試験（DocumentService）、
  文書（運用仕様書の再索引の節・`docs/api/openapi.yaml`・テスト仕様書 FR-06）、IADR（新 IADR-0503・索引）。
- **触らない**: 取り込みの受け口（冪等性は読んで確かめるだけ）、発行のアダプタと門（既存の門をそのまま通す）、
  メッセージングの既定（再試行・DLQ）、BFF（口は BFF へ出さない。メッシュ内部・port-forward で叩く）、
  Helm の値（Voyage の鍵の配線は本 PR の外。§原因の分析 の「直す先」）、稼働クラスタ（何も実行しない）。

## 調べたこと（現状の事実。`34ee800e`）

| # | 事実 | 出典 |
| --- | --- | --- |
| f-1 | `DocumentUpdated` の発行は `IDocumentUpdatedPublisher`（Wolverine の `IMessageBus.PublishAsync`）。**耐久アウトボックスは無い**（`UseDurableOutbox` 等の設定が無く、保存 → 発行の順に別々に行う）。経路は `DocumentEndpoints` の 2 つの門（`PublishUpdatedIfIndexableAsync` / `…OrWithdrawingAsync`）からだけ通す（`PublishGateCoverageTests`） | `Features/Documents/DocumentEndpoints.cs`・`Infrastructure/Messaging/WolverineDocumentUpdatedPublisher.cs`・`Program.cs` |
| f-2 | 発行は型名の fan-out exchange `DocumentUpdated` へ出る（`RoutePlatformEvent`）。購読は ingestion（`ingestion-service.DocumentUpdated`）・wiki・graph の 3 つ | `WolverineExtensions.cs`・`deploy/helm/.../files/pipeline.json` |
| f-3 | 取り込みは冒頭で露出の門 → `MarkdownUri` が null なら何もしない → 当該文書の点を**全コレクションから削除** → 決定的なチャンク ID（`ChunkId.Derive(documentId, index)`）で upsert。**再発行は冪等**（同じ状態なら同じ点が上書きされる） | `IngestionService/Features/Ingestion/Ingest/DocumentUpdatedConsumer.cs` |
| f-4 | 再試行は 2s/10s/30s の 3 回（試行 4 回）→ RabbitMQ の `wolverine-dead-letter-queue`（Wolverine 6.24 の既定名。全サービスで共有） | `WolverineExtensions.UsePlatformMessagingDefaults`・Wolverine.RabbitMQ の文字列 |
| f-5 | 埋め込みの一時障害（`Retryable=true`）は例外 → 再試行 → DLQ。**Voyage の鍵が未設定のとき、プロバイダは `InvalidOperationException` を投げ、ゲートウェイはそれを `Retryable=true` に畳む** | `LlmGateway/Infrastructure/ExternalServices/VoyageEmbeddingProvider.cs:22`・`Features/Embeddings/Embed/EmbedUseCase.cs:76-93` |
| f-6 | **Helm（`values.yaml`・`deploy/local/values-local.yaml`）は `Embedding__Voyage__ApiKey` をどこにも配線していない**（`llmgateway.extraEnv` は `Llm__ApiKey` だけ）。compose だけが `VOYAGE_API_KEY` を渡す。`git log -S'Embedding__Voyage__ApiKey' -- deploy scripts` は compose の 2 件だけ | `deploy/local/values-local.yaml:84-98`・`deploy/docker-compose.yml:727` |
| f-7 | AST の KB 書き込みは `POST /documents` に本文つきで保存し、機密区分の既定は `internal`（埋め込みへ進む区分） | `src/ai-stock-trading/.../KnowledgeBaseWriterSink.cs`・`KnowledgeModels.cs:30` |
| f-8 | 経路B の RabbitMQ は **PVC を持たない**（Deployment に volume が無い）。キューと DLQ の中身はブローカの Pod の作り直しで消える。Qdrant・Postgres は PVC で永続 | `deploy/local/infra/rabbitmq.yaml`・`deploy/local/infra-persistence/` |
| f-9 | 発行側の経路宣言は #992（2026-08-30）まで本番コードに無く、それ以前の発行は `No routes can be determined` で捨てられていた | `WolverineExtensions.RoutePlatformEvent` の注記 |
| f-10 | `scripts/` に `DocumentUpdated` を出すものは無い。運用仕様書の再索引の手順 2 は手段を持たない（issue の指摘どおり） | `grep -rn DocumentUpdated scripts/` |
| f-11 | `abac-seeder` のサービスアカウントは `platform-admin` を持つ（seed 系の投入器が使う client_credentials） | `deploy/keycloak/microservices-platform-realm.json` |
| f-12 | Wiki 同期は `published` / `normalized` の組織文書を Wiki.js へ upsert する（再発行で Wiki.js への書き込みが起きる）。グラフ同期は `UpdatedAt` の順序ガードで同じイベントを何もせず終える | `WikiService/.../DocumentSyncConsumer.cs`・`GraphService/.../GraphDocumentSyncConsumer.cs` |

## 原因の分析（依頼 2。コードと構成から。稼働での確認は PoC）

**最も可能性が高い: 一度も索引されていない。** 22,564 件の大半は AST の KB 書き込み（f-7。`internal`）であり、
`internal` の文書は Voyage（ティア B）へ埋め込みに行く。経路B は Voyage の鍵を配線していない（f-6）ので、
プロバイダは例外 → ゲートウェイは `Retryable=true` → 取り込みは `EmbeddingTransientException` を投げ → 4 回試して
`wolverine-dead-letter-queue` へ（f-4・f-5）。取り込みは**削除してから書く**（f-3）ので、点は 1 つも残らない。
DLQ の中身は RabbitMQ の作り直しで消える（f-8）ため、今は DLQ にも残っていない可能性が高い。

- 3 つのコレクションがすべて 0 件であることと整合する: ruri は既定で無効（埋め込み先にならない）、語彙索引（2026-10-05 導入）は
  高機密文書の行き先で、AST の文書（`internal`）も seed 文書（`public`）も行かない。高機密の既存文書も再発行されるまで入らない（運用仕様書の 10-05 の注記）。
- `verify-oidc-edge-flow.sh` の注記と IADR-0313 の動機（「`Embedding__Voyage__ApiKey` がどこにも配線されておらず … 索引に 1 点も入らない」）が同じ事象を記録している。
- **副次の要因（同じ結果を生む別の穴。今回の 0 件の主因ではない）**: ①#992 以前（〜2026-08-30）の発行は経路が無く捨てられた（f-9）。
  ②発行は耐久アウトボックスを持たない（f-1）ので、保存後・送出前に DocumentService が落ちた発行は失われる。
  ③取り込みのキューが一度も宣言されていない間（取り込みサービスの初回起動前）の発行は、束ねるキューが無い exchange で捨てられる。
- **「失われた」（Qdrant の中身が消えた）可能性は低い**: PVC は永続で（issue の観測）、取り込みにはコレクションを消す経路が無い
  （`QdrantBootstrapHostedService` は作成と索引だけ）。

**確かめていないこと（PoC が下のコマンドで確かめる）**:

```console
# a. ゲートウェイに Voyage の鍵が配線されているか（無ければ上の筋が確定する）
kubectl -n microservices-platform get deploy llmgateway-service -o jsonpath='{range .spec.template.spec.containers[0].env[*]}{.name}{"\n"}{end}' | grep -i voyage
# b. 取り込み・ゲートウェイのログに一時障害の痕跡があるか
kubectl -n microservices-platform logs deploy/ingestion-service --since=24h | grep -c 'transient embedding failure'
kubectl -n microservices-platform logs deploy/llmgateway-service --since=24h | grep -c 'Voyage AI の API キーが未設定'
# c. DLQ と取り込みのキューの深さ
kubectl -n platform-infra exec deploy/rabbitmq -- rabbitmqctl list_queues -q name messages | grep -E 'wolverine-dead-letter-queue|ingestion-service\.DocumentUpdated'
# d. RabbitMQ の Pod がいつ作られたか（DLQ が消えた時点の目安）
kubectl -n platform-infra get pod -l app=rabbitmq -o jsonpath='{.items[0].metadata.creationTimestamp}'
# e. 文書の機密区分の内訳（下の口の dry-run が返す）
node scripts/republish-document-updated.js --live --dry-run
```

**直す先（本 PR の外。PoC が選ぶ）**: 再発行の前に `internal` / `public` の埋め込み先を用意しないと、再発行は DLQ を
22,564 件ぶん埋めるだけになる。選択肢は (i) Voyage の鍵を Secret で `Embedding__Voyage__ApiKey` へ配線する（課金が発生する。
ゼロ保持の認定は未了＝#1740）、(ii) 使い捨ての検証スタックに限り `LOCALEMBED=1`（決定的ローカル埋め込み。意味的な近さは無い）。
駆動スクリプトの**カナリア**（最初の小ページの後に DLQ の増加を見て止まる）は、この前提が欠けたまま流すことを止めるために置く。

## 設計（正は IADR-0503）

### 口: `POST /documents/republish-updated`（DocumentService・管理者だけ）

- 認可: `write` 群（admin / operator）に置き、口の側で `AdminOnly` を積む（AST の古い写しの列挙と同じ形。運用者・機械の書き手は 403）。
  BFF には出さない（メッシュ内部。port-forward で叩く）。
- 要求（JSON）: `dryRun`（**必須**。省略は 400 —— 黙って発行しない）・`limit`（既定 100・1〜500 に丸める）・`cursor`
  （前ページの `nextCursor`。不透明。不正は 400）・`createdBefore`（作成時刻の上限。この時刻**より前**に作られた文書だけ）・
  `ids`（文書 ID の集合。最大 500。超えたら 400）・`attributes`（属性の完全一致。AND。空のキー・空の値は 400）。
- 並びとカーソル: `GET /documents/page` と同じ**作成時刻昇順・同時刻は ID 昇順**のキーセット（`DocumentPageCursor` を再利用）。
  並びのキーが不変なので、走査の間ずっと在った文書はちょうど 1 回ずつ選ばれる。途中で作られた文書は末尾に現れ、
  `createdBefore` を走査の開始時刻に固定すれば選ばれない（それらは作成の経路で既に発行されている）。削除はカーソルを動かさない。
- 個人資料も対象に入れ、**発行の門をそのまま通す**（3 トグルとも OFF の資料は発行しない＝`skippedByGate` に数える）。
  門の外の発行を作らない（`PublishGateCoverageTests`）。
- `dryRun=true`: 何も発行しない。カーソルより後ろの全件（`remaining`）について、機密区分の内訳（`ConfidentialityLevels.FromAttributes`。
  欠落・未知は `restricted` に倒す —— 取り込みの語彙索引の判定と同じ入力）・本文の所在が無い件数（取り込みが何もしない）・門で止まる件数を返す。
- `dryRun=false`: カーソルより後ろの先頭 `limit` 件を選び、1 件ずつ門を通して発行する。続きがあれば `nextCursor`、無ければ null。
- 応答: `dryRun`・`matched`（カーソルに依らない絞り込みの全件）・`remaining`（この呼び出しの前に残っていた件数）・
  `selected`（このページの件数。dry-run は `remaining`）・`published`・`skippedByGate`・`withoutBody`・`byConfidentiality`・`nextCursor`。
- 台帳の読み方: 絞り込みに要る列だけを投影して読み（ID・作成時刻・属性・本文の所在の有無）、メモリで絞る（属性は jsonb の値変換で SQL へ訳せない。
  `GET /documents/page` と同じ）。ページの文書だけを本体ごと読み直す（22,564 件を毎回本体ごと読まない）。
  `createdBefore` と `ids` は SQL 側で絞る。`createdBefore` は UTC へ寄せて渡す（Npgsql は offset 0 の `DateTimeOffset` しか書けない）。
- 発行は既存の門の中の `PublishUpdatedAsync`（共有先の解決・タグの表示名・本文指紋を同じ関数で載せる）。**内容は通常の経路と 1 バイトも違わない。**
- 監査: ページごとに情報ログ 1 行（主体・件数・門で止めた件数・次のカーソルの有無）。

### 駆動: `scripts/republish-document-updated.js`

- `--live` が無ければ何もせず exit 3（#1550。`--help` だけは指定なしで動く）。稼働クラスタへ port-forward（document-service 18094・keycloak 18095）、
  トークンは `abac-seeder`（platform-admin）の client_credentials（`seed-abac-policies.js` の資格情報解決を再利用）。60 秒で取り直す。
- `--dry-run`: 口の dry-run を 1 回呼び、件数・機密区分の内訳・埋め込みへ進む件数（`public` + `internal`。費用の見積もりの母数）を出して終わる。
- 本走: 状態ファイル（既定 `./republish-document-updated.state.json`）に `cursor`・`createdBefore`・絞り込み・累計を**ページごとに**書く。
  `--resume` で状態ファイルから続ける（絞り込みが状態と違えば拒否）。新規の走査は状態ファイルが在れば拒否（`--resume` か削除を促す）。
- 量の制御: `--page-size`（既定 50）・`--sleep-ms`（既定 2000）・`--max-pages`（1 回の実行の上限）。
  キューの監視（既定で有効。`--no-queue-watch` で切る）: `kubectl exec deploy/rabbitmq -- rabbitmqctl list_queues -q name messages` で
  取り込みのキュー（`ingestion-service.DocumentUpdated`）の深さが `--max-queue-depth`（既定 200）以下になるまで次のページを出さない。
- カナリア（既定 10 件。`--canary 0` で切る）: 最初のページだけ件数を絞り、取り込みのキューが空になるまで待ち（`--drain-timeout-ms` 既定 300000）、
  DLQ（`wolverine-dead-letter-queue`）が増えていたら**止まる**（埋め込み先が無いまま流すと全件が DLQ へ行く —— §原因の分析）。
  以後もページごとに DLQ の増加が `--max-dlq-growth`（既定 20）を超えたら止まる。
- 失敗: HTTP の失敗・到達不能は同じカーソルで間隔を倍にして再試行し、`--max-consecutive-failures`（既定 3）回続いたら状態を残して exit 1。
- 終了時に進捗（累計の発行件数・門で止めた件数・本文なし件数）と、確かめ方（Qdrant の `points_count`）・DLQ の扱いを出す。

## 選択肢の比較（要約。正は IADR-0503）

| 案 | 採否 | 理由 |
| --- | --- | --- |
| A. DocumentService の管理者だけの口 ＋ 駆動スクリプト | **採用** | 台帳（正本）を持つ唯一のサービスが、通常の発行の門とアダプタで**同じイベント**を出す。fan-out で全射影（Qdrant・Wiki・グラフ）が同じ手順で揃う。ページ・カーソル・dry-run を口が持つので、量の制御と再開はスクリプトに閉じる |
| B. 取り込み側の「DocumentService から作り直す」ジョブ | 却下 | 取り込みが台帳を全件読む口（gRPC 読み取り面は利用者の可視性で絞る）と、`DocumentUpdated` を組み立てる 2 つ目の場所（共有先・タグ表示名・本文指紋の解決）が要る。射影が Qdrant だけに閉じ、Wiki・グラフの再構築の手段が別に要る |
| C. Wolverine へメッセージを直接差し込む（キューへ送信・管理 UI の publish） | 却下 | イベントの中身（共有先・タグの表示名・本文指紋・`hasBody` 等 12 項目）を手で組み立てることになり、発行の門（個人資料の露出）を通らない。管理 UI からの手打ちは 22,564 件に使えない |
| D. SQL でアウトボックスの表へ行を入れる | 却下 | **耐久アウトボックスの表は存在しない**（f-1）。作るならメッセージングの構成変更（全発行経路の挙動が変わる）で、本件の射程を超える |

## 母集合（規則 9・10。2026-10-06 `34ee800e` 時点）

- 「再発行」「再索引」の手段を述べる文書を `grep -rn "再発行\|再索引" docs/ .ai-context/adr` で引いた。運用仕様書の
  §埋め込みプロバイダの設定・ゼロ保持・再索引 の手順 2、§ペイロード項目を増やしたときの再索引、§語彙索引の導入後の注記、
  §復旧の整合確認（「`DocumentUpdated` 再発行で再構築」）の 4 か所が手段を名指ししていない。**4 か所とも手段への導線を足す**（本文の他の主張は変えない）。
- DLQ の名前: 運用仕様書は `*_error`（MassTransit の命名）と書くが、取り込みは Wolverine へ移っており DLQ は `wolverine-dead-letter-queue`（f-4）。
  **取り込みの DLQ についての 1 か所だけ**直す（MassTransit が残る他の段の記述は正しいので触らない）。
- 非検査器の宣言: `scripts/scripts.repo.test.js` の `NOT_CHECKERS`（投入器・実行器の列挙）へ足す。稼働へ当たる入口の一覧 `scripts/live-scripts.json` と
  `scripts/README.md`「稼働クラスタへ当たる scripts」へ足す（閉包の試験が要求する）。

## 受け入れ基準 → 試験

| AC | 内容 | 試験 |
| --- | --- | --- |
| AC-1 | 管理者だけが呼べる。運用者・AST の書き手・一般利用者は 403。口は `AdminOnly` を積む | `RepublishDocumentUpdatedEndpointTests.管理者だけが呼べ…` / `口はAdminOnlyを積む` |
| AC-2 | ページを辿ると、カーソルより後ろの文書がちょうど 1 回ずつ選ばれる（重複も抜けも無い）。走査の途中で作られた文書は `createdBefore` で選ばれない | `ページを辿ると全件がちょうど1回ずつ発行され…`・`走査の途中で作られた文書は…` |
| AC-3 | 絞り込み（ids・属性・createdBefore）が効く。不正な入力（dryRun 省略・空のキー・ids 超過・不正なカーソル）は 400 | `絞り込みは…`・`不正な要求は400…` |
| AC-4 | dry-run は何も発行せず、内訳（機密区分・本文なし・門で止まる件数）を返す | `dry-runは何も発行せず…` |
| AC-5 | 発行の中身は通常の経路と同じ（共有先・タグの表示名・本文指紋・hasBody 等） | `発行する中身は通常の経路と同じ…` |
| AC-6 | 3 トグルとも OFF の個人資料は発行せず `skippedByGate` に数え、カーソルは進む | `門で止まる個人資料は…` |
| AC-7 | 駆動スクリプトの純関数: 引数解析・状態ファイルの再開と不一致の拒否・連続失敗で止まる・キューの深さの解析・DLQ の増加の判定 | `scripts.repo.test.js` の `#1762` 節 |
| AC-8 | （独立監査 R1・Y1・Y2）DLQ で止まると状態のカーソルを確かめた位置へ戻し、purge の有無に依らず `--resume` で取りこぼさない。`--resume` は DLQ の基準を取り直す。走査の終わりにキューが空になるまで待って DLQ を確かめてから完了を出す。停止条件（カナリアの許容 0・DLQ の停止・深さの待ち）を純関数で固定する。0 件は待たずに終わる | `scripts.repo.test.js` の `#1762 監査` 節（純関数 5 本・駆動器全体 5 本） |
| AC-9 | （独立監査 Y4・Y5）空の `ids` は 400。dry-run も発行も、主体・操作者・理由をログへ残し、札の制御文字を潰す。発行する実行は `--reason` 必須 | `不正な要求は400…`・`dryRunも発行も_認証済みの主体と操作者と理由をログへ残し…`（T-78・T-81）・`#1762 監査 Y5` |

## 残るもの（受け入れたもの）

- 発行は耐久アウトボックスを持たない（f-1）。口が 200 を返しても、ブローカへ届く前に DocumentService が落ちればそのページの一部は失われる。
  **確かめ方は Qdrant の `points_count` と取り込みのログ**であり、足りなければ同じ絞り込みで再走（冪等）する。
- RabbitMQ に PVC が無い（f-8）ので、走査の途中でブローカが作り直されるとキューの中身が消える。状態ファイルのカーソルは「発行した」位置であり
  「索引された」位置ではない。疑わしいときは `--resume` ではなく新規の走査（状態ファイルを消す）でやり直す。
- 再発行は fan-out であり、Wiki 同期（`published` / `normalized` の組織文書を Wiki.js へ upsert）とグラフ同期（順序ガードで no-op）も動く（f-12）。
- 埋め込みの費用: `public` / `internal` の文書は 1 チャンクにつき埋め込み 1 回。dry-run の内訳から見積もる（単価は契約の価格表を見る）。
- （独立監査 Y3）口と `GET /documents/page` はページごとに台帳を全件読む（1 ページ O(N)・1 走査 O(N²)。22,564 件・ページ 50 で約 453 回 ≈ 1,020 万行）。
  キーセットを SQL へ移し `(CreatedAt, Id)` の索引を張る直しは **#1765** へ切り出した（Postgres の uuid と .NET の Guid の並びを両側で揃える注意つき）。
- 確かめた位置はキューが空になった時点でしか進まないので、止まったときに戻る幅が大きいと再開で重ねて発行する件数（費用）が増える（取りこぼしより重複を選んだ）。

## 試験の対応（実装後）

| 試験クラス | 本数 | 何を固定するか |
| --- | --- | --- |
| `DocumentService.Tests/Features/Documents/Republish/RepublishDocumentUpdatedEndpointTests` | 11 | AC-1〜AC-6・AC-9（テスト仕様書 FR-06 の T-76〜T-81） |
| `scripts/scripts.repo.test.js`（`#1762` 節） | 18 | AC-7・AC-8・AC-9 |

## 変異試験の結果（2026-10-06。scratch `msp1762-mut/`〔作業ツリーの写し〕の `mut.py`。1 変異ずつ当て、.NET は `RepublishDocumentUpdated` の試験、スクリプトは `#1762` 節を走らせ、戻す。**8 件すべて検出**）

| # | 変異 | 落ちた試験 |
| --- | --- | --- |
| M-1 | 口の `AdminOnly` を外す | `管理者だけが呼べ…`・`口はAdminOnlyを積む`（2） |
| M-2 | カーソルの比較を「以上」にする（前ページ末尾を再選択） | `ページを辿ると全件がちょうど1回ずつ…`・`走査の途中で作られた文書は…`（2） |
| M-3 | `nextCursor` を前ページの先頭から作る | `ページを辿ると全件がちょうど1回ずつ…`（1） |
| M-4 | dry-run でも発行する | `dry-runは何も発行せず…`（1） |
| M-5 | `dryRun` の省略を `false` として受ける | `不正な要求は400…`（1） |
| M-6 | 門を通さず数えるだけにする（門で止まる個人資料も発行） | `門で止まる個人資料は…`（1） |
| M-7 | スクリプト: 連続失敗の判定を「以上」から「超え」にする | `#1762: 連続失敗が上限に達したら止まる…`（1） |
| M-8 | スクリプト: 状態の絞り込みの突き合わせを外す | `#1762: 状態ファイルの絞り込みが違えば同じ走査とみなさず…`（1） |

## 独立監査（NO-GO）の指摘の是正（2026-10-06・同じ PR）

| 指摘 | 是正 | 試験 |
| --- | --- | --- |
| 🔴 R1 DLQ で止まったあとの `--resume` が、基準を持ち越して直後に止まり続けるか、DLQ を purge していると DLQ へ行った文書を黙って飛ばして「完了」と言う（カーソルを DLQ の確認より前に進めて保存していた） | 状態に**確かめた位置**（`confirmedCursor` / `confirmedDone`。DLQ の確認を通り、かつ取り込みのキューが空だった時点）を持つ。DLQ の増加・キューの待ちの超過・連続失敗・Ctrl-C で止まるときは cursor を確かめた位置へ戻して保存し、カナリアもやり直す。`--resume` は DLQ の基準を今の深さへ取り直し、前の基準と今の深さを表示する。状態ファイルの版を 2 へ上げた | 純関数（`confirmChecked`・`rollbackToConfirmed`・`rebaselineDlq`）と、駆動器全体を偽の口と kubectl のスタブ（失敗が後から DLQ に現れる時間差を模す）で走らせる 2 本（purge あり／なし） |
| 🟡 Y1 走査の終わりに DLQ を確かめない | 最後のページ（と `--max-pages` の区切り）の後、キューが空になるまで待って（上限 `--drain-timeout-ms`）同じ判定をかけ、増えていれば確かめた位置へ戻して exit 1。通ってから「完了」 | 駆動器全体 2 本（最後のページの失敗で止まる／失敗なしで完了） |
| 🟡 Y2 停止条件が試験されていない（S1・S2・S8 が生き残った） | `dlqVerdict` / `queueWaitPlan` を切り出して試験 | 純関数 2 本 |
| 🟡 Y4 `ids: []` を全件として扱う | 400（鍵 `ids`）。openapi に `minItems: 1` | `不正な要求は400…`（T-78） |
| 🟡 Y5 dry-run を記録しない・操作者と理由が残らない | dry-run もログへ 1 行。要求に任意の札 `requestedBy`（100 文字）・`reason`（500 文字）を足し、`LogSanitizer` で制御文字を潰して主体と一緒に出す。スクリプトは `--operator`（既定 `USER`）・`--reason` から埋め、**発行する実行（再開を含む）は `--reason` 必須**（`--live` の判定より後で見る —— 素の実行は exit 3 のまま） | `dryRunも発行も_認証済みの主体と操作者と理由をログへ残し…`（T-81）・`#1762 監査 Y5` |
| 🟢 AI レビュー: dry-run が残り全件を件数ごとに列挙し直す／0 件でもカナリアの待ちをする | 内訳を 1 回の走査で数える（`RepublishSelection.Summarize`。`CountByConfidentiality` は吸収して消した）。確かめていない発行が無く、このページも何も発行しなかったら待たずに進め、終わりの確認も要らない | 既存の dry-run の試験（T-79）・駆動器全体 1 本（0 件・他の配信でキューが空でない・待ちの上限 1ms で exit 0） |
| 🟡 Y3 1 ページ O(N)・1 走査 O(N²) | **本 PR では直さない。#1765 へ切り出した**（キーセットを SQL へ・`(CreatedAt, Id)` の索引・Postgres の uuid と .NET の Guid の並びの注意。22,564 件・ページ 50 で約 453 回 ≈ 1,020 万行） | — |

**変異試験（是正分。scratch `msp1763-fix-mut/` の `mut.py`。1 変異ずつ当てて `node scripts/scripts.test.js` 全体または DocumentService の `Republish` の試験を走らせ、戻す。13 件すべて検出）**:

| # | 変異 | 最初に落ちた試験 |
| --- | --- | --- |
| S1 | カナリアの許容を `--max-dlq-growth` にする | `#1762 監査 Y2: DLQ の判定…` |
| S2 | DLQ の停止が発火しない（`stop: false`） | `#1762 監査 Y2: DLQ の判定…` |
| S8 | 深さの待ちを外す（`before: null`） | `#1762 監査 Y2: キューの待ち方…` |
| R1a | 止まるときに確かめた位置へ戻さない（`rollbackToConfirmed` が恒等） | `#1762 監査 R1: 確かめた位置は…` |
| R1b | `--resume` で DLQ の基準を取り直さない | `#1762 監査 R1: DLQ を purge せずに再開しても…`（exit 1） |
| R1c | キューに残りがあっても確かめた位置を進める | `#1762 監査 R1: 確かめた位置は…` |
| R1d | DLQ の停止で状態を戻さずに止まる（旧形） | `#1762 監査 R1: DLQ で止まると確かめた位置へ戻して保存し…` |
| Y1 | 走査の終わりの確認を外す | `#1762 監査 Y1: 最後のページの失敗は…`（exit 0 で完了と言う） |
| AI | 何も発行しなかったページでも確認を待つ | `#1762 AI レビュー: 絞り込みが 0 件なら…`（exit 1） |
| Y5 | `--reason` を必須にしない | `#1762 監査 Y5: --operator と --reason…` |
| Y4 | 空の `ids` の検証を外す | `不正な要求は400…`（200 が返る） |
| Y5a | dry-run を記録しない | `dryRunも発行も_認証済みの主体と…` |
| Y5b | 理由の札を潰さずにログへ出す | `dryRunも発行も_認証済みの主体と…` |

（Y5c〔発行の行に操作者を載せない〕も同じ試験で落ちる。）

## 検証（2026-10-06。`origin/develop` `34ee800e` 基点）

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/knowledge/backend/backend.slnx --no-incremental` | 成功。警告は既存の CS0618（`Knowledge.IntegrationTests` の `QdrantBuilder()`）だけ |
| `dotnet build src/platform/backend/backend.slnx` | 警告 0・エラー 0 |
| `dotnet test .../DocumentService.Tests.csproj` | 988 件すべて成功（新規 10 件を含む）。監査の是正後 989 件すべて成功（T-81 の 1 件を足した） |
| `dotnet format <knowledge / platform の slnx> --verify-no-changes` | 両方 exit 0 |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 928 件成功（`#1762` の 8 件・#1550 の閉包と README の網羅を含む）。監査の是正後 938 件成功（`#1762` 18 件） |
| `check-trace-blocks` / `check-adr-numbering` / `gen-knowledge-graph --check` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-test-traceability` / `check-doc-links` / `check-unit-dependencies` / `check-bff-authz-docs` / `check-openapi-dto-drift` / `check-contract-schema` / `check-event-topology` / `check-backend-libraries` / `check-reading-budget` | すべて exit 0 |
| `check-test-spec-coverage` | `--update` で床を上げた（FR-06 × `RepublishDocumentUpdatedEndpointTests`）後 exit 0 |
| `check-doc-updated --base origin/develop` | exit 0 |

## PoC の操作者向けの手順（要約。正は運用仕様書の再索引の節）

1. 上の確認 a〜d を実行し、埋め込み先が用意されているか確かめる（無ければ再発行しない）。
2. `node scripts/republish-document-updated.js --live --dry-run --operator <名前>` で件数と内訳を見る（dry-run も口のログに残る）。
3. `node scripts/republish-document-updated.js --live --operator <名前> --reason "<理由>"`（`--reason` は必須。カナリア 10 件 → 取り込みのキューが空になるまで待つ →
   DLQ が増えていなければ続ける → 走査の終わりにキューが空になるまで待って DLQ を確かめてから「完了」）。
4. 止まったら（Ctrl-C・連続失敗・キューの待ちの超過・DLQ の増加）、状態のカーソルは**確かめた位置**へ戻してある。表示された理由を直してから
   `node scripts/republish-document-updated.js --live --resume --operator <名前> --reason "<直した原因>"`（量の指定を変えていたら同じものを渡す）。
   再開は DLQ の基準を今の深さへ取り直し（前の基準 → 今の深さを表示）、カナリアからやり直し、確かめていないページ（DLQ へ行った文書を含む）をもう一度発行する。
   DLQ を purge してから再開しても取りこぼさない。
5. Qdrant の `points_count` が増えることを確かめる。

［2026-10-06 追記 / #1764］f-6・§原因の分析の「経路B は Voyage の鍵を配線していない」は本仕様書の時点の観測である。#1764（IADR-0504）で
`Embedding__Voyage__ApiKey` は Secret `llm-provider-credentials` のキー `voyage-api-key` から optional で渡す配線になった（値は Vault へ入れるまで空。
手順は `docs/operations/voyage-embedding-key-runbook.md`）。運用仕様書の再発行の節の同じ文も同 PR で改めた。
