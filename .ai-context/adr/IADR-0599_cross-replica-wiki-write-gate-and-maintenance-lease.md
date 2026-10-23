---
title: IADR-0599 Wiki.js への書き込みの門はレプリカをまたいで効かせる（wiki-service の PostgreSQL の advisory lock・専用の非プール接続）。wiki は HPA の対象に残し、門の待ちと受け口の実行期限を延ばす。document-service の個人資料の定期処理は GraphService と同型のリースで単一実行にする
type: impl-adr
status: Accepted
related_ids: [FR-13, FR-19, FR-20, FR-22, UC-07, NFR-05, ADR-0011, ADR-0027, ADR-0096, IADR-0050, IADR-0299, IADR-0478, IADR-0557, IADR-0563, IADR-0596]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-13・FR-19・NFR-05）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（再試行・DLQ）
related_specs:
  - ../specs/20261023_2100_cross-replica-exclusion.md
---

# IADR-0599: レプリカをまたぐ排他 —— Wiki.js への書き込みの門と個人資料の定期処理（#2100）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2100。親 #2047 の指摘 M-3）

## 起点・関連

- 起点 issue: #2100（親 #2047）。既知: #1971（律速の件。レプリカ前提の矛盾は未記載）。
- 関連する計画書 ID: FR-13・UC-07（Wiki 同期）、FR-19・FR-20・FR-22（個人資料の定期処理）、NFR-05（可用性 99.9%）。計画 ADR: ADR-0011・ADR-0027・ADR-0096。いずれも変えない。
- 改める実装 ADR: [IADR-0563](./IADR-0563_wiki-js-write-gate.md)（残余リスク (a)「門はプロセスの中だけで効く。… 上げるときは本 IADR を改める」と決定 4「門の待ちの期限は Wiki.js 1 回と同じ」）。
- 前例として写す実装 ADR: [IADR-0299](./IADR-0299_knowledge-health-producer.md) 決定 3（GraphService の PostgreSQL のリース）。
- 前提を確かめた実装 ADR: [IADR-0050](./IADR-0050_hpa-pdb-scaling-scope.md)（HPA/PDB の対象＝要求処理系）・[IADR-0478](./IADR-0478_consumer-outbound-call-timeouts-under-wolverine-execution-timeout.md)（受け口の期限の起動時検査）・
  [IADR-0596](./IADR-0596_wiki-catalog-receiver-ordering.md)（版の印は 2 レプリカの並行を前提にしている）。
- 作業仕様書: [20261023_2100_cross-replica-exclusion](../specs/20261023_2100_cross-replica-exclusion.md)。

## コンテキストと課題

- helm の既定（`deploy/helm/microservices-platform/values.yaml` の `scaling`）で document と wiki は HPA（minReplicas 2・maxReplicas 4）の対象である。
  HPA の対象でなくても、ローリング更新（maxSurge）では新旧 2 pod が同時に生きる。
- **(A) wiki-service**: Wiki.js 2.5 の木の再構築は書き込みが並ぶと `pageTree` の主キーで衝突する（#1971・IADR-0557 の実測）。IADR-0563 の門（`WikiJsWriteGate`）は `SemaphoreSlim` で、
  プロセスの中しか抑えない。2 レプリカなら同期・撤去の書き込みが最大 2 本（4 リスナー）並び、#1971 の衝突が戻り得る。試験 `HpaPdbScalingTests` は wiki を HPA の対象に固定したまま、IADR-0563 の前提と矛盾して緑だった。
- **(B) document-service**: `PrivateNoteMaintenanceHostedService`（日次。退職者の資料の完全削除・90 日の削除・版の刈り取り・通知・同期履歴の削除）は、レプリカごとに独立に周期を回す。
  同じ周期に 2 本が並ぶと、同じ行を 2 本が読み・消し、通知の発火記録が確定する前に両方が「未発火」と読んで送り得る（発火記録で重複を止められるのは、確定の**後**に読んだ側だけである）。
- 計画の可用性要件（NFR-05: サービス可用性 99.9%）と IADR-0050（要求処理系は minReplicas 2 ＋ PDB）。wiki-service は Wiki の閲覧・検索（SC-04・UC-07）の要求も受ける。

## 検討した選択肢

### (A) Wiki.js への書き込み

1. **(a2) wiki を `scaling.services` から外し replicas 1 に固定する** —— キュー駆動の部分だけを見れば IADR-0050 の「ワーカーは対象外」に合うが、wiki-service は閲覧・検索の要求処理系でもあり、
   1 レプリカでは drain・再起動のたびに SC-04 が止まる（NFR-05・IADR-0050 が HPA/PDB へ入れた理由を失う）。**ローリング更新の maxSurge で新旧 2 pod が並ぶ窓は残る**（Recreate にすれば窓は消えるが、
   更新のたびに閲覧が止まる）。採らない。
2. **(a1) 門をレプリカをまたいで効かせる（PostgreSQL の advisory lock）** —— wiki-service は自分の PostgreSQL（`WikiDbContext`）を既に持ち、readiness も DB を見ている。
   GraphService・DataSourceService が同じ仕組みでレプリカ間の排他を張っている（IADR-0299・[IADR-0083](./IADR-0083_datasource-sync-single-writer-advisory-lock.md)）。閲覧の可用性を保ったまま、更新の窓も含めて塞げる。**採用。**
3. RabbitMQ の single active consumer —— 1 キューの中は 1 レプリカに寄るが、同期と撤去の 2 キューは別のレプリカで動き得るので木の再構築の並びは残る。単独では足りない。採らない。
4. Wolverine の永続化（リーダー選出つきのリスナー）—— wiki-service は Wolverine のメッセージ保存を持たない。新しい仕組みを足すことになる。採らない。

### (B) 個人資料の定期処理

- GraphService の `IKnowledgeHealthLeaseCoordinator`（`pg_try_advisory_lock`・取れなければ本周期を飛ばす）と同型の複製を置く。**新しい仕組みは作らない**（指示どおり）。抽象の `Platform.Shared` への格上げは IADR-0299 と同じ理由で射程の外。

## 決定

1. **Wiki.js への書き込みの門は、プロセスの中の枠（`SemaphoreSlim`。従前どおり）を取った後、レプリカをまたぐ枠（`IWikiJsReplicaWriteLock`）を取ってから Wiki.js を呼ぶ。**
   両方の取得が「門の待ち」で、呼び出しの後（成否・時間切れに関わらず）レプリカをまたぐ枠 → プロセスの中の枠の順で返す。レプリカをまたぐ枠を取れない（DB へ届かない等）ときは
   **Wiki.js を呼ばずに例外のまま投げる**（fail-closed。受け口の再試行・DLQ へ委ねる）。プロセスの中の枠を先に取るのは、1 レプリカから DB のロック待ちへ並ぶ受け口を上限の数に抑えるためである。
2. **本番のレプリカをまたぐ枠は wiki-service の PostgreSQL のセッションレベル advisory lock（`PostgresWikiJsReplicaWriteLock`）である。**
   - **接続の保持**: 取得ごとに専用の接続を 1 本開き、**プールしない（`Pooling=false`）**。ロックはその接続のセッションに紐づき、書き込みの間だけ保持する。
     プールすると、解放に失敗した接続がロックを持ったままプールに眠り得る。プールしなければ接続を閉じた時点でセッションが終わり、ロックも必ず消える（pod の停止も同じ）。
   - **キー**: 基点 `0x574A574700`（"WJWG" ＋ 枠の番号 1 バイト）。枠の数は `Wiki:MaxParallelWrites`（既定 1・1〜4。IADR-0563 決定 1）で、**全レプリカで合わせた数**になる。
     既定の 1 本は `pg_advisory_lock` で待つ（PostgreSQL のロック待ちは到着順に許可される）。2 本以上は全部を `pg_try_advisory_lock` で試し、取れなければ 100 ms おいて試し直す
     （1 本を選んで待つと、別の枠が空いても待ち続けるため）。
   - **待ちの上限**: 門の待ちの期限（決定 3）を連結した ct で切る。Npgsql はサーバへ取り消しを送り、待ちの文を止める。切ったら接続を閉じる（取り消しと許可が行き違って取れていても残らない）。
   - **破棄のべき等**: `pg_advisory_unlock`（5 秒の上限）の後に接続を閉じる。unlock の失敗はログだけ残す（閉じればロックは消える）。2 回目以降の破棄は何もしない。
   - DB のプロバイダがリレーショナルでない（単体試験の InMemory）ときだけ、プロセスの中の枠へ縮退する（`ProcessLocalWikiJsReplicaWriteLock`。GraphService のリースの選び方と同じ）。
3. **門の待ちの期限を Wiki.js 1 回の期限から切り離し、`Wiki:WriteGateWaitSeconds`（未設定なら Wiki.js 1 回の期限の 2 倍＝ 30 秒）にする。**（IADR-0563 決定 4 を改める）
   - 根拠: IADR-0563 の「前の書き込みは期限で必ず切られるので、上限 1 の待ちは Wiki.js 1 回を超えない」はプロセスが 1 つのときの論である。2 レプリカの最悪は、同じレプリカの枠の持ち主が
     他レプリカの書き込み 1 回を DB のロックで待ち、その後に自分で 1 回書く＝ 2 回分である（DB のロック待ちに並ぶのは各レプリカ 1 本までなので、DB の待ち自体は他レプリカの 1 回まで）。
   - 待ちが期限を超えたら従前どおり呼び出し先 `wiki-js-gate` の時間切れとして再試行へ委ねる（IADR-0478 の作法）。
4. **両受け口（`wiki-service.DocumentUpdated` / `wiki-service.DocumentDeleted`）の実行期限を `Wiki:HandlerTimeoutSeconds`（既定 90 秒）として方針（`HandlerExecutionTimeoutPolicy`）で与える。**
   最悪の所要時間（既定: 同期 20＋30＋15＝65 秒・撤去 30＋15＝45 秒）が実行期限に収まること、再試行の連鎖（4 × 90＋42＝402 秒）がブローカの `consumer_timeout`（1800 秒）に収まることを起動時に検査する。
5. **document-service の個人資料の定期処理は、GraphService と同型のリース（`IPrivateNoteMaintenanceLeaseCoordinator`・`pg_try_advisory_lock`・キー "DPNM" `0x44504E4D`）を周期ごとに試し、
   取れたレプリカだけが周期の本体を回す。** 取れない・一時障害の周期は本体を回さない（fail-safe）。リースは本体の間ずっと持ち、本体の成否に関わらず返す。
   接続は wiki と同じ理由でプールしない（GraphService の複製からの唯一の違い）。非リレーショナルでは常時取得へ縮退する。
6. **wiki は HPA の対象に残す。** `HpaPdbScalingTests` は「HPA の対象であること」と「本番の配線がレプリカをまたぐ排他を選ぶこと」を wiki と document について同じ試験で固定する。

## 理由

- 閲覧・検索の可用性（NFR-05・IADR-0050）を落とさずに、Wiki.js の書き込みを全レプリカで 1 本に保てる。ローリング更新の窓も同じ仕組みで塞がる。
- 律速は Wiki.js の CPU なので、全体の書き込みの速さは 1 レプリカのときと変わらない（速くも遅くもならない）。レプリカを増やしても同期は速くならない。
- 定期処理は既存の前例（IADR-0299）と同じ形で、試験の作法（`TryRunCycleAsync` を internal にして 1 周期を決定的に回す）もそのまま使える。

## 結果

- wiki-service の Wiki.js への書き込みは、レプリカの数に依らず `Wiki:MaxParallelWrites`（既定 1）本までになる。書き込み 1 回ごとに wiki-service の PostgreSQL へ接続を 1 本開閉する（1 件数秒の頻度）。
- 一括操作で 2 レプリカの 4 リスナーが同時に書きたいとき、待ちは最大で Wiki.js 2 回分（既定 30 秒）まで延び、受け口 1 通は最大 65 秒かかり得る（従前は 50 秒）。
- 個人資料の定期処理は、同じ時刻に回ろうとした周期のうち 1 つだけが本体を回す。

## 残余リスク

- (a) **リースは「同時に回らない」を保証するが、「1 日に 1 回」は保証しない。** 2 レプリカの 24 時間の拍は起動時刻で決まるので、時刻のずれた周期はそれぞれ回る（1 日に 2 回）。
  2 回目は直列なので、削除は既に消えた行を見ず、通知は 1 回目が確定した発火記録を見て送らない（[IADR-0215](./IADR-0215_notification-service-and-in-app-delivery.md) 決定 5-a）。GraphService の健全性の報告と同じ性質である。
- (b) **HPA が 3 レプリカ以上へ増やすと、門の待ちの最悪は既定の 30 秒（2 回分）を超え得る。** 超えた通は `wiki-js-gate` の時間切れとして再試行に回る（Wiki.js への書き込みは並ばないので衝突はしない）。
  wiki-service の CPU は律速でないので通常は 2 のまま。上げる運用をするときは `Wiki__WriteGateWaitSeconds` と `Wiki__HandlerTimeoutSeconds` を合わせて上げる（起動時検査が組み合わせを止める）。
- (c) 突き合わせで負けた受け口の合わせ直し（IADR-0596 決定 3）は、同じ受け口の中で本文 ＋ 門 ＋ Wiki.js をもう 1 回行う。この分は起動時検査に数えていない（IADR-0596 の時点から同じ）。
- (d) IADR-0563 残余リスク (b)（期限で切っても Wiki.js の中の処理は続く）は変わらない。切った直後に別のレプリカの書き込みが門を通ると、Wiki.js の中では並び得る。失敗した側は再試行される。
- (e) 本番の配線がリレーショナルなら PostgreSQL の枠を選ぶことは、`HpaPdbScalingTests` の配線の文面と単体の配線試験（InMemory では縮退すること）で抑える。リレーショナルなプロバイダで WikiService のホストを起こす試験は無い（起動時のマイグレーションが実 DB を要する）。

## 試験

- `WikiJsReplicaWriteLockTests`（結合・実 PostgreSQL）: 2 つのレプリカの門から同時に書いても直列（B が `pg_locks` の待ちに入ったことを観測してから、B が書いていないことを見る）・
  対照（プロセスの中の枠だけの門は 2 本重なる）・待ちは期限で切れてロックも接続も残らない／破棄はべき等・枠の数はレプリカをまたいで合わせた数。
- `PrivateNoteMaintenanceSingleRunTests`（結合・実 PostgreSQL）: 2 つのホストのうち本体を回すのは 1 つ・A の後は B が回せる・対照（リースを外すと 2 つとも入る）・本体が失敗してもリースは返る。
- `WikiJsWriteGateTests`・`WikiSyncTimeoutTests`・`PrivateNoteMaintenanceLeaseTests`（単体）・`HpaPdbScalingTests`（前提の固定）。
- 変異: 門がレプリカをまたぐ枠を取らない形・リースが常に取れる形の 2 つで、受け入れ基準 1・2 の結合試験が赤になることを確かめた（作業仕様書）。

## 関連

- #2100・#2047・#1971（IADR-0563）・#443（IADR-0299）・#197（IADR-0050）
