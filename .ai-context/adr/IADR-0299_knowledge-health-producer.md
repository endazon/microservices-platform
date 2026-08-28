---
title: IADR-0299 ナレッジ健全性は「算出できる 1 指標」だけを生産し、受け口は内部 API へ移す —— 残り 6 指標は理由を指標ごとに分けて記録する
type: impl-adr
status: Accepted
related_ids:
  - FR-10
  - FR-17
  - FR-18
  - FR-19
  - UC-05
  - SC-10
  - ADR-0002
  - ADR-0006
  - ADR-0033
  - ADR-0034
  - ADR-0054
  - IADR-0011
  - IADR-0026
  - IADR-0083
  - IADR-0119
  - IADR-0128
  - IADR-0153
  - IADR-0215
  - IADR-0242
  - IADR-0265
  - IADR-0270
  - IADR-0281
author: claude
created: 2026-08-29
updated: 2026-08-29
plan_refs:
  - "06_technical/05_observability-ops.md §ナレッジ健全性の指標（集計範囲・2026-08-02 確定）— 7 指標と 4 規則。陳腐化文書数のしきい値は未確定と明記"
  - "ADR-0033 決定 3・6・9（辺の型のフォールバック・差分更新・型別の使用件数）"
  - "ADR-0034 決定 2（件数の変動から個人資料の存在が推測される経路を塞ぐ）"
  - "ADR-0054 §結果（doc_scope は既存文書へ遡及付与しない）"
---

# IADR-0299: ナレッジ健全性の観測値の生産者

- 状態: Accepted
- 日付: 2026-08-29
- 決定者: 実装（#443）

## 起点・関連

- 関連する計画書 ID: FR-10 / FR-17 / FR-18 / FR-19 / UC-05 / SC-10 / ADR-0002 / ADR-0006 / ADR-0033 / ADR-0034 / ADR-0054
- 関連する実装仕様書: `.ai-context/specs/20260829_issue-443_knowledge-health-producer.md`
- 先行: `IADR-0265`（受け口・集計・統制。**§結果 フォローアップ 1 が生産者側を未着手として残していた**）

## コンテキストと課題

IADR-0265 が `POST /dashboard/knowledge-health/observations`（受け口）と `GET /dashboard/knowledge-health`
（集計・ロール限定）を実装した。**しかし本番コードから送っている経路が 1 本も無かった** ——
実測（2026-08-29。`grep -rn "knowledge-health"`）で、呼び出しは**テストの 8 か所だけ**であった。
`POST /dashboard/events`（UsageEvent）も同型で、発火側が本番コードに 1 本も無い。

**受け口があることと、指標が測れていることは別である。** 現状は 7 指標すべてが恒久的に 0 件で返り、
画面が無いおかげで誤読が表に出ていないだけであった。

論点は 3 つある。

1. **7 指標のうち何を生産できるのか。**「配線するだけ」で済むものと、そうでないものの区別。
2. **生産者は利用者 JWT を持たない。** 受け口は `RequireAuthorization()` を持つが、
   定期処理には載せる資格情報が無い（`client_credentials` の実装はリポジトリ本体に 1 行も無い）。
3. **画面に出さない根拠が成立していない指標が 2 つある。** IADR-0281 と IADR-0153 決定 5 は
   「Grafana で観測できれば成立する」を根拠に SC-10 へ出さないと決めたが、**そのパネルが存在しなかった**。

## 検討した選択肢

### 送出の経路

| 案 | 評価 |
| --- | --- |
| **HTTP（採用）** | 受け口の契約は**全量スナップショット置換**である。同期呼び出しなら置換が 1 通ずつ順に適用される |
| メッセージング（Wolverine） | ❌ **順序保証が無く部分配信もある。** 置換を 2 通流すと**古い方の集合が最終状態として残る**。「集合の差し替え」を非同期の粒度に載せられない |
| DashboardService が直接数える | ❌ DB-per-service（ADR-0002）。`graph_documents` / `edges` は GraphService の DB にある |

### 排他制御

| 案 | 評価 |
| --- | --- |
| **PostgreSQL advisory lock（採用）** | IADR-0083 が定期同期で採った形と同じ。セッション終了で自動解放されるため、pod crash でロックが残らない |
| 何も置かない | ❌ 全量置換のため、**片方の DELETE がもう片方の INSERT 済み行を消す**。次周期でも同じ競合が起き得るので**自然回復しない** |
| 「今は replicas: 1 だから不要」 | ❌ **ローリング更新の maxSurge で新旧 2 pod が同時に生きる。** steady state のレプリカ数は根拠にならない |

### 抽象の置き場所

| 案 | 評価 |
| --- | --- |
| **GraphService 内に複製（採用）** | サービス間の直接参照は禁止（`Shared.Contracts` の契約と HTTP のみ）。DataSourceService の `ISyncLeaseCoordinator` は**参照できない** |
| `Platform.Shared.Infrastructure` へ汎用リースを新設 | ❌ 2 件目の利用者が現れた段階での一般化であり、本作業の射程を超える（計画外の抽象化） |
| 契約 DTO を `Knowledge.Contracts` へ昇格 | ❌ IADR-0265 が「**BFF へ載せる段で昇格させる**」と決めており、その段は別トラックが持つ。契約スナップショット（`check-contract-schema.js`）の基準を本作業で動かさない |

### 受け口の認可

| 案 | 評価 |
| --- | --- |
| **`/internal/...` ＋ 無認可（採用）** | `NotificationIngressEndpoints`（`/internal/notifications`）の先例。**同じ制約に対して既に採った形**である |
| `client_credentials` を新規に実装 | ❌ 実装が本体に 1 行も無い。認可基盤の新設は #443 の射程を大きく超える |
| `RequireAuthorization()` のまま据え置く | ❌ **観測値が永久に届かない。** しかも送出は fail-open なので、届かないことが 401 として表に出ない |

## 決定

### 決定 1 — 生産するのは `orphan-documents` だけとし、残り 6 指標は**理由を指標ごとに分けて**記録する

「7 指標のうち 1 指標を実装した」ではなく、**残りが実装されない理由は 6 通りある**。
まとめて「未着手」と書くと、解ける順序も解き方も分からなくなる。

| 指標 | 扱い | 理由（**これが本決定の実体である**） |
| --- | --- | --- |
| `orphan-documents` | **生産する** | `graph_documents` × `edges` が同一 DbContext にあり、EF の 1 クエリで算出できる |
| `stale-documents` | **生産しない** | 判定材料（`GraphDocument.UpdatedAt`）は揃うが、**しきい値が計画側で未確定**である。実装が数値を決めると未確定が既成事実になる。**計画へ裁定を依頼した** |
| `unresolved-links` | **生産しない** | 解決失敗は `LinkEdgeSynchronizer` がログへ出して**捨てている**。永続化が未設計であり、**曖昧一致（複数ヒット）を未解決に含めるかも未定義**である（決定 5 に設計を残した） |
| `unsummarized-clusters` | **生産できない** | クラスタリング・要約の実装が**リポジトリ全体で 0 件**。`get_cluster_summary` は McpServer の**公開禁止**リストに名前があるだけで実体が無い。構造的に生産不可能である |
| `edge-type-usage` | **生産しない** | 件数は引けるが、観測値モデルが「指標 1 つ＝件数 1 つ」であり**型別の内訳を表現できない**。IADR-0265 が先送り済みで、モデルの改定が要る |
| `undefined-type-fallbacks` | **生産済み（宛先が別）** | `EdgeTypeFallbackMetrics` が既に数えている。宛先は観測値ではなく OTel カウンタである（IADR-0281） |
| `ingest-unknown-tags` | **生産済み（宛先が別）** | `IngestTagMetrics` が既に数えている（IADR-0153 決定 5）。**この 1 指標だけ 0 が正常である** |

🔴 **「7 指標のうち 1 件しか生産者が無い」ことを、画面の設計より先に効かせる。** 節を作れば
残り 6 指標は「0 件」として描かれるが、その 0 は**「問題が無い」ではなく「測っていない」**である。
受け口が「0 件の指標も欠落させない」と決めているのは**測っている前提**の話であり、
未計測を 0 と描くのは真逆の誤読を作る。よって **SC-10 の画面には出さない**（決定 6）。

### 決定 2 — 送出は HTTP。**空でも送る**

`GraphService.Domain.Ports.IKnowledgeHealthReporter` ← `HttpKnowledgeHealthReporter`
（named client ＋ fail-open ＋ 5 秒タイムアウト。`HttpPrivateNoteNotifier` と同型）。

接続先は `Services:DashboardService`、既定 `http://dashboard-service:8080`。
**compose・helm のいずれでも Service 名とポートが一致するため上書きの env は要らない**
（`notification-service` と同じ形）。🔴 chart のキー `dashboard` を変えると Service 名が動き、
**fail-open のため 502 にすらならず静かに報告が止まる**。

🔴 **孤立が 0 件でも報告を送る。** 受け口は当該指標の全行を落としてから差し替えるため、
「0 件だから送らない」と最適化すると**前回の件数が恒久的に残る**（解消したのに数字が減らない）。

### 決定 3 — 排他リースを取ったレプリカだけが報告する

`IKnowledgeHealthLeaseCoordinator` ←
`PostgresKnowledgeHealthLeaseCoordinator`（`pg_try_advisory_lock`。キー `0x474B4850` = "GKHP"）／
非リレーショナル（InMemory）は `NoOpKnowledgeHealthLeaseCoordinator`。
取得できない周期は**収集もせずスキップ**する（fail-safe）。

**DataSourceService の同型実装を参照せず複製した**（上の §抽象の置き場所）。
advisory lock のキーは DataSourceService の "DSPS" と別値にしてある —— DB が分かれているため
衝突し得ないが、**同じ値だと「同じロックを取っている」と読み違えられる**。

### 決定 4 — 受け口を `/internal/knowledge-health/observations` へ移し、認可を外す

| 項目 | 変更前 | 変更後 |
| --- | --- | --- |
| パス | `POST /dashboard/knowledge-health/observations` | `POST /internal/knowledge-health/observations` |
| 認可 | `RequireAuthorization()` | 無し |
| OpenAPI | 記述対象 | `ExcludeFromDescription()` |

**閲覧側 `GET /dashboard/knowledge-health` のロール限定は一切変えない**（規則 2 は全体集計を許す
唯一の統制点である）。受け口を無認証にしたことと閲覧側は独立であり、**両方を同じ変更で動かしたので
両方をテストで固定した**。

🔴 **統制を定めたことと、統制が働いていることを書き分ける。**

| | 内容 | 本環境で測れるか |
| --- | --- | --- |
| **定めた統制** | 第一防御 = mesh の STRICT mTLS（IADR-0026）。多層防御 = ネットワーク分離（内部サービスは host 非公開・Service は既定 ClusterIP・NetworkPolicy 既定拒否） | — |
| **測れているもの** | ① compose が `dashboard-service` を host 公開しない ② Helm の Service テンプレートに `type:` が現れない（`NetworkIsolationTests`） | **測れる** |
| **測れていないもの** | **mTLS が実際に遮断していること** | 🔴 **測れない。** Docker / k3s が無く、`NetworkIsolationTests` が見ているのはマニフェストの字面だけである |

**残余リスク（受容）**: 同一ネットワーク内からは無認証で観測値を差し替えられる。受容の根拠は
**作れるのが「指標名と不透明な鍵の集合」だけ**であり、**受け口が書き込み専用で既存の観測値を
読み出さない**こと（読み出しは認証 ＋ ロール限定の GET だけ）である。`/internal/notifications` が
同じ形の残余リスクを既に受容している。

### 決定 5 — `unresolved-links` は**実装せず、永続化の設計だけ**を残す

いま解決失敗は `LinkEdgeSynchronizer.ResolveTargetsAsync` が
`LogInformation`（不在）／`LogWarning`（曖昧）で出して**捨てている**。カウンタも表も無い。
配線だけでは生産できず、**設計の決定が先に要る**。着手する者のために論点を列挙する。

1. **何を数えるか（未定義）**: 不在（0 件ヒット）だけか、**曖昧一致（複数ヒット）も含めるか**。
   計画の定義は「リンク先を**特定できない**辺」であり、曖昧一致も字義には当てはまるが、
   **原因も打ち手も違う**（不在＝取り込み漏れ／曖昧＝タイトルの重複）。**分けて数えるべきである。**
2. **粒度（未定義）**: 「解決できないリンクの**本数**」か「**相異なるリンク先名の数**」か。
   本文に同じリンクが 10 回現れたら 10 か 1 か。`IngestTagMetrics`（種類）と
   `EdgeTypeFallbackMetrics`（回数）が同じ表の中で別の粒度を採っており、**既定は無い**。
3. **保持先**: 観測値（`subjectKey` = リンク先名）を送るなら、**リンク先名は本文由来の自由文である**。
   受け口は `subjectKey` を応答に出さないが、**DashboardService の DB には残る**。
   個人資料の本文由来の文字列が越境することの是非を先に決めること。
4. **除去の契機**: リンクは辺にならないため、**解決したことを知る契機が無い**。
   全量スナップショット置換の性質上、**再抽出のたびに全リンク先を数え直す**設計にしないと
   件数が減らない（`orphan-documents` は行の再走査なのでこの問題を持たない）。

**上記のいずれも実装が独断で決めてよいものではない**（1 と 2 は指標の意味そのものである）。
計画へ裁定を依頼する。

### 決定 6 — SC-10 の画面には出さない。**不在固定のテストは残し、理由を足す**

出さない理由は 3 つで、**いずれも単独で十分**である。

1. 決定 1 の「7 指標のうち 1 件しか生産者が無い」——残り 6 の 0 が「問題なし」と読める。
2. 画面へ載せるには BFF の口が要るが、当該領域は別トラックが持つ。
3. IADR-0119 の「節ごと着手保留」は解けていない。

`OperationsDashboardPage.test.tsx` の否定 8 語は**すべて残す**。ただし
**理由の記述は追随させた** —— 従前のコメントは 3 だけを理由にしており、保留が解けたときに
7 行すべてを置く読み方を誘発する。1 を日付つきの追記で足した。

### 決定 7 — 2 指標の Grafana パネルを新設する（**他の IADR の前提の穴埋め**）

IADR-0281 と IADR-0153 決定 5 が「画面に出さない」根拠にした
**「Grafana で観測できれば成立する」が、実際には成立していなかった**。
実測（`grep -rn "edge_type_fallback\|unknown_tag" deploy/` → **0 件**、
ダッシュボードは 2 本のみ）。`Platform Overview` へ 2 枚足し、
**compose 経路と k8s 経路の両方**へ同内容で置いた（`check-grafana-provisioning-parity.js` が一致を検査する）。

- `graph_edge_type_fallback_total`（by `graph_fallback_layer`）
- `ingest_unknown_tag_total`（by `ingest_source`）

**凍結記録である 2 つの IADR は書き換えない。** 直したのは**前提の側**である。

## 理由

- **受け口があることと測れていることを混同しない。** #443 の受け入れは「受け口と統制」で閉じていたが、
  **生産者が居ない指標は恒久的に 0 である**。0 は最も誤読されやすい値であり、
  「画面が無いから表に出ていない」だけの状態を残さない。
- **指標ごとに理由が違うことを、そのまま記録に残す。** 「未着手 6 件」と丸めると、
  しきい値の裁定待ち（外部要因）と、機能そのものが無いもの（構造的不能）と、
  既に生産済みで宛先だけが違うものが同じ棚に入る。**解ける順序が失われる。**
- **未確定を実装が決めない。** `stale-documents` のしきい値と `unresolved-links` の数え方は、
  どちらも**指標の意味そのもの**である。実装が値を置けば、それが既成事実として計画へ逆流する。

## 結果

- 良い影響:
  - 7 指標のうち **1 件**（`orphan-documents`）が実データで動く。**受け口ができて以来はじめて**である。
  - 2 指標（`undefined-type-fallbacks` / `ingest-unknown-tags`）が **Grafana で実際に見られる**。
    2 つの IADR が置いた前提が、遅れて成立した。
  - 受け口の認可が、**実際の呼び出し元の性質と噛み合った**（従前は誰も通れなかった）。
- 悪い影響・トレードオフ:
  - 受け口が無認証になった。残余リスクは決定 4 に受容として記録した。
    **mTLS が実際に遮断していることは本環境では測れていない。**
  - 送出は fail-open であり、**届かなかったことはエラーログにしか現れない**
    （計器を置いていない —— `PrivateNoteNotificationMetrics` に相当するものは本作業で作らなかった。
    1 指標・1 時間周期では、ログで足りる規模である）。
  - GraphService が DashboardService へ**同期の依存**を持った。fail-open なので可用性は落ちないが、
    依存の向き（可変ユニット内・サービス間 HTTP）が 1 本増えた。
  - `ISyncLeaseCoordinator` と同型のコードが**リポジトリ内で 2 つ目**になった。
    3 つ目が現れたら `Platform.Shared` への一般化を検討すること（**2 回目では動かさない** ——
    検査器・規約の追加と同じ「同型の事故が 2 回起きたら」の作法に合わせる）。
- フォローアップ:
  1. **`stale-documents` のしきい値の裁定**（計画へ起票）。解ければ生産は配線のみで済む。
  2. **`unresolved-links` の数え方と保持先の裁定**（決定 5 の 4 論点）。
  3. 🔴 **個人資料からの辺が組織文書の孤立判定を変える。** 本作業は計画の字義どおり
     **辺の相手のスコープを問わない**実装にした。結果として、組織文書が個人資料からリンクされると
     孤立件数が 1 減り、**件数の変動から個人資料の存在が推測され得る** ——
     計画が個人資料を除外した理由の 1 つ（ADR-0034 決定 2 と同じ「推測経路を塞ぐ」）と衝突する。
     **辺の相手のスコープで孤立判定を変えるのは指標の意味の変更であり、実装が決めない。** 計画へ起票する。
  4. `edge-type-usage` は観測値モデルの改定（指標 1 つに内訳を持たせる）が要る。
  5. `POST /dashboard/events`（UsageEvent）の発火側は依然として本番コードに 1 本も無い。
     **本作業の射程外だが、同じ形の穴である**（IADR-0011 §結果 のフォローアップが残ったまま）。

## 関連

- Supersedes: なし
- Superseded by: なし
