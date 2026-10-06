---
title: IADR-0503 DocumentUpdated の再発行（再索引の手段）は DocumentService の管理者だけの口が通常の発行の門で 1 ページずつ行い、量の制御・再開・DLQ の監視は駆動スクリプトが持つ
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-06, UC-04, NFR-09, ADR-0013, ADR-0016, ADR-0027, ADR-0127, IADR-0313, IADR-0314, IADR-0455, IADR-0484, IADR-0497]
author: claude
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0013_embedding-model.md（フォローアップ「再索引運用（モデル更新時）の手順整備」）
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md
related_specs:
  - ../specs/20261006_1762_republish-document-updated.md
---

# IADR-0503: DocumentUpdated の再発行は DocumentService の管理者だけの口と駆動スクリプトで行う（#1762）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-06
- 決定者: claude（#1762）

## 起点・関連

- 起点 issue: **#1762**（経路B の Qdrant が全コレクション 0 件・文書 22,564 件。運用仕様書の再索引の手順 2「全文書に `DocumentUpdated` を再発行する」を行う手段がリポジトリに無い）
- 計画: ADR-0013（フォローアップ「再索引運用の手順整備」）・ADR-0016（Voyage が既定の埋め込み先）・ADR-0027（Wolverine）・ADR-0127（高機密文書は語彙索引だけ）
- 先例: [IADR-0455](./IADR-0455_publish-gate-all-paths-withdrawal-and-private-note-scope.md)（`DocumentUpdated` は門を通す）・
  [IADR-0484](./IADR-0484_ast-stale-copies-enumeration-rules.md)（管理者だけの運用の口を `AdminOnly` で積む）・
  [IADR-0314](./IADR-0314_wolverine-outbound-routing-and-queue-binding.md)（発行側の経路宣言）・[IADR-0313](./IADR-0313_deterministic-local-embedding-for-search-gate.md)（決定的ローカル埋め込み）
- 作業仕様書: [`20261006_1762_republish-document-updated.md`](../specs/20261006_1762_republish-document-updated.md)（§調べたこと f-1〜f-12・§原因の分析）

## コンテキストと課題

射影（Qdrant の索引・Wiki.js・グラフ）は台帳（DocumentService）の写しであり、作り直す手段は `DocumentUpdated` の再発行である。
運用仕様書はモデル移行・ペイロード項目の追加・語彙索引の導入・復旧の整合確認の 4 か所でこの手順を求めているが、
**それを行う手段（スクリプト・管理 API・ジョブ）が無かった**。稼働の経路B では 22,564 件に対して索引が 0 件であり、PoC の受け入れが進まない。

決めること:

1. どこで発行するか（台帳を持つ側か、射影の側か、ブローカへの直接投入か）。
2. 稼働クラスタで安全に流すための量の制御・進捗・中断と再開・DLQ の扱いを、どこが持つか。
3. 認可（誰が呼べるか）。
4. 発行の中身を通常の経路とどう揃えるか（門・共有先・タグの表示名・本文指紋）。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A. DocumentService の管理者だけの口 ＋ 駆動スクリプト** | 口が 1 ページを選んで通常の門で発行し、続きのカーソルを返す。スクリプトがページを回し、量・再開・DLQ を見る | **採用**。台帳を持つ唯一のサービスが、通常の経路と**同じ関数**でイベントを組む。fan-out で 3 つの射影が同じ手順で揃う。口は状態を持たず（カーソルは要求に載る）、失敗しても同じカーソルで呼び直せば冪等 |
| B. 取り込み側の「DocumentService から作り直す」ジョブ | 取り込みが台帳を全件読み、自分で取り込む | 却下。台帳を全件読む口が新たに要る（gRPC の読み取り面は利用者の可視性で絞る設計で、全件の口は無い）。`DocumentUpdated` に相当する値（共有先・タグの表示名・本文指紋・`hasBody`・原本の所在）を組み立てる 2 つ目の場所ができる（IADR-0153 決定 2・IADR-0447 が 1 か所に保った解決点が割れる）。Wiki・グラフは作り直せない |
| C. Wolverine へメッセージを直接差し込む | 管理 UI の publish、または取り込みのキューへの直接送信 | 却下。イベントの 12 項目を手で組み立てることになり、個人資料の露出の門（IADR-0455）を通らない。キューへの直接送信は fan-out の外で、pipeline.json の queue 上書き（IADR-0239 決定 4）とも結合する。22,564 件を手で送る手段にならない |
| D. SQL でアウトボックスの表へ行を入れる | 耐久アウトボックスの表に未送信として行を作る | 却下。**本サービスの発行は耐久アウトボックスを持たない**（`IMessageBus.PublishAsync` の直接発行。作業仕様書 f-1）。表を作るのはメッセージングの構成変更であり、全発行経路の挙動が変わる。射程を超える |

## 決定

### 決定 1: 発行は DocumentService の口 `POST /documents/republish-updated` が、既存の門を通して行う

- 発行は `DocumentEndpoints.PublishUpdatedIfIndexableAsync`（属性を変えない経路の門）を 1 件ずつ呼ぶ。
  中身は通常の経路と同じ関数（`PublishUpdatedAsync`）で載る —— 共有先は `ResolveSharedWithAsync`、タグは表示名、本文指紋・`hasBody`・原本の所在は台帳の値。
  **台帳を書き換えない**（版も更新時刻も動かない。イベントは台帳の更新時刻を運ぶ —— 取り込みは `ev.UpdatedAt` を索引へ写すので、再索引のたびに更新時刻が今にならない）。
- 個人資料も対象に入れ、門が決める（3 トグルとも OFF なら発行しない＝`skippedByGate`）。門の判定を口の中で写さない —— dry-run が数えるために、
  門に属性だけを取る形（`PassesPublishGate(IReadOnlyDictionary)`）を足し、`Document` を取る形はそれへ委ねる（述語は 1 つ）。
- fan-out のまま出す（取り込み・Wiki 同期・グラフ同期の 3 つが受ける）。Wiki 同期は `published` / `normalized` の組織文書を Wiki.js へ upsert し、
  グラフ同期は順序ガード（同じ更新時刻）で何もしない。**射影ごとに選んで出す口は作らない**（案 C の欠点をそのまま持つため）。

### 決定 2: 口は「1 ページを選んで発行し、続きのカーソルを返す」だけを持つ（状態を持たない）

- 要求: `dryRun`（**必須**。省略は 400）・`limit`（既定 100・1〜500）・`cursor`・`createdBefore`・`ids`（500 件まで）・`attributes`（完全一致・AND）。
- 並び: **作成時刻昇順・同時刻は ID 昇順**のキーセット（`GET /documents/page` の `DocumentPageCursor` を再利用）。並びのキーが不変なので、
  走査の間ずっと在った文書はちょうど 1 回ずつ選ばれる。途中で作られた文書は末尾に現れ、`createdBefore` を開始時刻に固定すれば選ばれない
  （作成の経路で発行済み）。更新時刻では並べない（途中で更新された文書が前へ移って読み飛ばされる）。
- 台帳は絞り込みに要る列（ID・作成時刻・属性・本文の所在の有無）だけを投影して読み、選んだページの文書だけを本体ごと読み直す。
  `createdBefore` は UTC へ寄せて SQL で絞る（Npgsql は offset 0 の `DateTimeOffset` しか timestamptz へ書けない）。
- `dryRun=true` は発行せず、カーソルより後ろの全件の内訳を返す: 機密区分（`ConfidentialityLevels.FromAttributes`。欠落・未知は `restricted`。
  `public` + `internal` が埋め込みへ進む件数＝費用の見積もりの母数）・本文の所在が無い件数（取り込みは何もしない）・門で止まる件数。

### 決定 3: 認可は `AdminOnly`（`write` 群に置き、口の側で積む）。BFF には出さない

- 1 回の呼び出しで最大 500 件の発行を起こし、埋め込みの費用と取り込み・Wiki 同期の負荷を生む破壊的に近い運用操作である。
  `write` 群は運用者と機械の書き手（AST の KB 書き込み・`platform-operator`）にも開いているので、口の側で `AdminOnly` を積む（IADR-0484 と同じ形）。
- メッシュ内部の口であり、BFF の面には出さない（画面から呼ぶ用途が無い。運用者は port-forward で叩く）。駆動スクリプトは `abac-seeder`
  （`platform-admin` を持つ client_credentials。seed 系の投入器と同じ）で名乗る。

### 決定 4: 量の制御・進捗・中断と再開・DLQ の監視は駆動スクリプト `scripts/republish-document-updated.js` が持つ

- 明示の指定（`--live`）が無ければ何もしない（#1550）。状態ファイル（カーソル・`createdBefore`・絞り込み・累計）を**ページごとに**書き、`--resume` で続ける。
  絞り込みが状態と違う新規の走査は拒む（カーソルは絞り込みの集合の中の位置である）。
- 量: ページの大きさ（既定 50）・間隔（既定 2 秒）・1 回の実行のページ数の上限。RabbitMQ の取り込みのキュー（`ingestion-service.DocumentUpdated`。
  `messages` は ready ＋ unacked）が上限（既定 200）以下になるまで次のページを出さない（`kubectl exec deploy/rabbitmq -- rabbitmqctl list_queues`）。
- **カナリア**（既定 10 件）: 最初のページだけ小さく出し、取り込みのキューが空になるまで待って、DLQ（`wolverine-dead-letter-queue`）が増えていたら止まる。
  以後もページごとに DLQ の増加（既定 20 件まで）を見る。埋め込み先の無い構成（Voyage の鍵が無い）で流すと全件が再試行の後 DLQ へ行くため（作業仕様書 §原因の分析）、
  その前提の欠けを最初の 10 件で止めるための仕組みである。キューを読めなければ黙って監視なしに倒さず、`--no-queue-watch` の明示を求める。
- 失敗: 同じカーソルで間隔を倍にして呼び直し、連続 3 回で状態を残して止まる。
- **DLQ のメッセージは再投入しない**（古い状態の写しであり、原因を直した後の再発行が今の台帳から作り直す）。DLQ は全サービスで共有なので、中身を確かめずに purge しない。

## 結果

- 運用仕様書の再索引の手順 2 が実行できるようになる（4 か所の導線を同じ手段へ向ける）。
- 再発行は冪等（取り込みは文書単位で全コレクションから削除してから決定的なチャンク ID で書く）なので、同じ範囲を何度流しても索引は同じ形に収束する。
- 口は状態を持たないので、DocumentService の再起動・複数レプリカに依らない。

## 残余リスク（受け入れたもの）

- **発行は耐久アウトボックスを持たない。** 口が 200 を返しても、ブローカへ送り出す前に DocumentService が落ちればそのページの一部は失われる。
  確かめ方は Qdrant の `points_count` と取り込みのログであり、足りなければ同じ絞り込みで再走する（冪等）。アウトボックスの導入は別の判断である。
- **経路B の RabbitMQ は PVC を持たない。** 走査の途中でブローカが作り直されるとキューの中身が消える。状態ファイルのカーソルは「発行した」位置であり「索引された」位置ではない。
- **同じ文書の 2 つの `DocumentUpdated` が並行に処理される**と、削除と書き込みが交錯し得る。再発行は台帳の今の状態を運ぶので、通常の更新と交錯しても最後に処理された方の形に収束する（同じ状態なら同じ点）。
- 22,564 件規模では、ページごとに台帳の投影（4 列）を全件読む。1 ページあたり 1 回であり、`GET /documents/page` の全件読みより軽い。

## 試験

- `DocumentService.Tests/Features/Documents/Republish/RepublishDocumentUpdatedEndpointTests`（認可・ページとカーソル・途中の作成・絞り込み・入力の検証・dry-run・中身の同一性・台帳を書き換えない・門）。
- `scripts/scripts.repo.test.js` の `#1762` 節（引数解析・状態ファイル・連続失敗・キューの深さ・DLQ の増加・要約）。
- 変異試験の結果は作業仕様書 §変異試験。

## 関連

- 運用仕様書「埋め込みプロバイダの設定・ゼロ保持・再索引」（手順 2 の使い方）
