---
title: 作業仕様書 — ナレッジ健全性の観測値の生産者（孤立文書）と、宛先の欠けていた 2 指標の可視化
type: spec
status: done
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
author: claude
created: 2026-08-29
updated: 2026-08-29
plan_refs:
  - "06_technical/05_observability-ops.md §ナレッジ健全性の指標（集計範囲・2026-08-02 確定）— 7 指標の定義と 4 規則。陳腐化文書数のしきい値は未確定と明記"
related_adrs:
  - IADR-0299
  - IADR-0265
  - IADR-0281
  - IADR-0153
  - IADR-0119
issue: "#443"
---

# 作業仕様書: ナレッジ健全性の観測値の生産者（#443 の生産者分）

## 起点

計画 `06_technical/05_observability-ops.md` §ナレッジ健全性の指標が **7 指標**とその集計・表示の
4 規則を定めている。受け口（`DashboardService`）と集計・統制は **IADR-0265**（#443）で実装済みだが、
**同 ADR §結果 フォローアップ 1 が「観測値の生産者側の配線」を未着手として残していた**。本作業は
その 1 本目を通す。

## 実測（着手前。私が自分で走査した結果）

### 走査 1 — 受け口を呼ぶ本番コードの有無

```
grep -rn "knowledge-health" .   （node_modules / obj / bin / submodule を除く）
```

**本番コードのヒットは受け口の定義 1 か所のみ**であった。呼び出しは
`DashboardService/Tests/KnowledgeHealthEndpointTests.cs` の 8 か所（すべてテスト）である。
`POST /dashboard/events`（UsageEvent）も同様で、**発火側は本番コードに 1 本も無い**
（ヒットはテストと「本作業では触らない」旨のコメントのみ）。**受け口はあるが誰も送っていない。**

### 走査 2 — 7 指標それぞれの実現可能性

| 指標 | 実測 | 本作業での扱い |
| --- | --- | --- |
| `orphan-documents` | `graph_documents` と `edges` が GraphService の同一 DbContext にある。孤立は「端点に自分を含む辺が 1 本も無い」で、EF の 1 クエリ（`WHERE NOT EXISTS`）で引ける | **実装する** |
| `stale-documents` | `GraphDocument.UpdatedAt` があり判定材料は揃う。**しきい値が計画側で未確定**（`05_observability-ops.md` §同節が「しきい値は未確定であり、引き続き検討する」と明記） | **実装しない。**計画へ裁定を依頼する |
| `unresolved-links` | `LinkEdgeSynchronizer.ResolveTargetsAsync` は不在を `LogInformation`・曖昧を `LogWarning` で出して**捨てているだけ**。表も列もカウンタも無い | **実装しない。**永続化の設計を IADR-0299 に残す |
| `unsummarized-clusters` | クラスタリング・要約の実装はリポジトリ全体で **0 件**（`get_cluster_summary` は McpServer の**公開禁止**リストに名前があるだけで実体が無い） | **対象外。**構造的に生産不可能 |
| `edge-type-usage` | 件数自体は `ix_edges_type` で引けるが、観測値モデルが「指標 1 つ＝件数 1 つ」であり**型別の内訳を表現できない** | **対象外**（IADR-0265 が先送り済み） |
| `undefined-type-fallbacks` | **既に生産されている**（`EdgeTypeFallbackMetrics` → `graph.edge_type_fallback.total`）。宛先が観測値ではなく OTel カウンタ | **可視化で解決する**（下記 §副産物） |
| `ingest-unknown-tags` | 同上（`IngestTagMetrics` → `ingest.unknown_tag.total`）。**この 1 指標だけ 0 が正常** | 同上 |

### 走査 3 — 副産物として見つかった実欠陥（本作業で直す）

上の 2 指標を SC-10 の画面へ出さない根拠は、IADR-0281 と IADR-0153（決定 5）が揃って書いている
**「裁定が求めたのは観測できることであり、Grafana で観測できれば成立する」**である。

```
grep -rn "edge_type_fallback\|unknown_tag" deploy/     → 0 件
ls deploy/grafana/provisioning/dashboards/*.json       → 2 本（overview / llm-usage）のみ
```

**両カウンタを描くパネルは 1 枚も存在しない。根拠が成立していない。** パネルの追加は実環境を
要さないため本作業で直す。

### 走査 4 — 追随が要る文書の母集合（規則 9: 記憶で挙げず、誤りの側の文字列で走査してから挙げる）

誤りになる側の文字列 `生産者`・`/dashboard/knowledge-health/observations`・`Grafana で観測できれば成立`
で全文書を走査した。結果と除外理由は次のとおり。

| ヒット | 追随 | 理由 |
| --- | --- | --- |
| `docs/functional/FR-10_dashboard.md`（2 か所 ＋ API 表） | **する** | live な権威文書。受け口のパスと認可が変わり、生産者の有無も変わる |
| `docs/tests/FR-10_dashboard.md` | **する** | 受け口のパスを含むテスト表 |
| `docs/tests/SC-10_operations-dashboard.md` | **する** | 「実装しない」の理由に 2 本目が加わる |
| `src/knowledge/backend/Services/DashboardService/**`（受け口・DTO・Domain・Tests） | **する** | 実装対象そのもの |
| `.ai-context/specs/20260823_issue-443_*.md` ほか specs 4 件 | **しない** | 凍結記録。確定済みの作業仕様書は本文プロズを後から書き換えない |
| `.ai-context/adr/IADR-0265 / IADR-0281 / IADR-0153` | **しない** | 凍結記録。フォローアップの消化は**新しい IADR（0299）が引き受ける**のが本リポジトリの作法 |
| `src/platform/backend/Services/AuthorizationService/Tests/AccessScopeContractTests.cs` | **しない** | 「生産者」の語が別文脈（ABAC 分岐の段階計画）で当たっただけ |
| `docs/screens/SC-10_operations-dashboard.md` | **する**（注記のみ） | 画面に出さない判断の根拠が 1 本増える |

**導出値は走査ではなく計算し直した**（規則 10）—— 「7 指標のうち生産者があるのは何件か」は
本作業の後で **1 件**（`orphan-documents`）である。「Grafana で描けている健全性指標の件数」は
本作業の後で **2 件**である。

## 対象範囲

- 対象:
  1. `orphan-documents` の生産者（GraphService の定期処理 → HTTP 送出）
  2. 受け口の認可方式の是正（`/internal/...` へ移設）
  3. Grafana パネル 2 枚の追加（compose 経路と k8s 経路の両方）
  4. 上記に伴う文書追随と、SC-10 画面側の不在固定テストの理由追記
- 対象外:
  - 残り 6 指標の生産者（上表の理由）
  - SC-10 画面への表示（後述 §画面に出すかの判断）
  - `POST /dashboard/events`（UsageEvent）の発火側配線 —— 本 issue の射程外であり、
    健全性指標とは別系統である
  - `docs/api/openapi.yaml`（内部 API は OpenAPI に載せない方針であり、そもそも変更が要らない）

## 設計

### 1. 送出はメッセージングではなく HTTP である

受け口の契約は **指標 1 つ分のスナップショット置換**（当該指標の全行 DELETE → INSERT）である。
メッセージングは順序保証が無く部分配信もあり得るため、**集合を丸ごと差し替える操作には使えない**
（2 通の置換が入れ替わると、古い方の集合が最終状態として残る）。よって HTTP の同期呼び出しとする。

雛形は `HttpPrivateNoteNotifier`（named client ＋ fail-open ＋ ポート越しの注入）を写す。

- ポート: `GraphService.Domain.Ports.IKnowledgeHealthReporter`
- アダプタ: `GraphService.Infrastructure.ExternalServices.HttpKnowledgeHealthReporter`
- 接続先: `Services:DashboardService`（コード既定 `http://dashboard-service:8080`）。
  **compose・helm とも Service 名が `dashboard-service`・ポート 8080 で一致するため上書き不要**である
  （`HttpPrivateNoteNotifier` → `notification-service` と同じ形）。
- タイムアウトは既定の 100 秒ではなく 5 秒。定期処理を 100 秒止める理由が無い。
- **fail-open**: 呼び出し元のキャンセル以外はすべて握り、エラーログへ落とす。
  指標の送出失敗でグラフの購読ホストを落とさない。

### 2. 定期実行と排他リース

`PrivateNoteMaintenanceHostedService`（BackgroundService ＋ PeriodicTimer ＋ 初回は 1 周期後）と
`DataSourceSyncHostedService`（リースのゲート ＋ `TryRunCycleAsync` を internal にして決定的に検証）
の 2 つを合わせて写す。

🔴 **排他リースを省かない。** 受け口が全量スナップショット置換であるため、2 レプリカが同時に走ると
**片方の DELETE がもう片方の INSERT 済み行を消し、恒久的に過少な件数が残る**（次の周期でも同じ競合が
起き得るので自然回復しない）。`graph` の steady state は `replicas: 1` だが、
**ローリング更新の maxSurge では新旧 2 pod が同時に生きる**。リースはその窓を塞ぐ。

- ポート: `IKnowledgeHealthLeaseCoordinator`（`ISyncLeaseCoordinator` と同型）
- 実装: `PostgresAdvisoryLockLeaseCoordinator`（`pg_try_advisory_lock`）/ 非リレーショナルは NoOp
- **DataSourceService の実装を参照せず複製する** —— サービス間の直接参照は禁止であり
  （`Shared.Contracts` ＋ HTTP のみ）、`Platform.Shared` へ上げるのは本作業の射程を超える
- 周期: 1 時間。指標は運用の棚卸しに使うものであり分単位の鮮度を要さない

### 3. 孤立の判定と個人資料の扱い

```csharp
db.Documents.Where(d => !db.Edges.Any(
    e => e.SourceDocumentId == d.DocumentId || e.TargetDocumentId == d.DocumentId))
```

- **両端点を見る。** 対称型は書き込み時に (min, max) へ正規化されるため、Source だけを見ると
  取りこぼす。「どの文書からも参照されず、どの文書も参照していない」の字義でもある。
- **個人資料は生産者側で落とさず、`docScope` を添えて送る。** 除外を強制するのは受け手であり
  （IADR-0265 の設計。件数だけを受け取ると除外の有無を受け手が確かめられない）、
  生産者の責務は**スコープを正しく添えること**である。
- 🔴 **スコープの判定は集合帰属で書く**（`GraphDocumentScope.IsPrivateNote`）。
  「organization でない」と否定で書くと、`doc_scope` を持たない既存文書（実データの大多数）が
  一斉に個人資料と見なされ、**受け手で全部除外されて孤立文書数が 0 になる**。
- 送る `subjectKey` は文書 ID（不透明な鍵。受け口は応答に出さない）。

### 4. 認可 —— 受け口を `/internal/...` へ移す

受け口は現在 `.RequireAuthorization()`（認証済み）を持つが、**生産者は利用者 JWT を持たない定期処理**
である。`client_credentials` の実装はリポジトリ本体に 1 行も無い。

利用者裁定により **`NotificationIngressEndpoints` の先例に倣う**:

| 項目 | 変更前 | 変更後 |
| --- | --- | --- |
| パス | `POST /dashboard/knowledge-health/observations` | `POST /internal/knowledge-health/observations` |
| 認可 | `RequireAuthorization()` | 無し（メッシュ内部限定） |
| OpenAPI | 記述対象 | `ExcludeFromDescription()` |

閲覧側 `GET /dashboard/knowledge-health` の**ロール限定は一切変えない**（規則 2 の唯一の統制点）。

🔴 **統制と、統制が働いていることは書き分ける。** 第一防御は mesh の STRICT mTLS、多層防御として
ネットワーク分離（内部サービスの host 非公開・Service は ClusterIP）である。
**mTLS が実際に遮断していることは本環境では測れない** —— `NetworkIsolationTests` が測っているのは
Helm の Service が `ClusterIP` であることと compose が host 公開していないことだけである。
残余リスクは IADR-0299 に受容として記録する。

### 5. Grafana パネル

`deploy/grafana/provisioning/dashboards/microservices-platform-overview.json` へ 2 枚足し、
**k8s 経路の inline（`deploy/local/observability/grafana.yaml`）へ同内容を写す**
（`check-grafana-provisioning-parity.js` が両経路の一致を検査する）。

- Prometheus 上の名前は OTLP の変換後（`.` → `_`）である:
  `graph_edge_type_fallback_total` / `ingest_unknown_tag_total`
- 属性も同様に `graph_fallback_layer` / `ingest_source`
- **両パネルとも 0 が正常**である旨をパネルの説明に書く（llm-usage の
  「単価を解決できなかった呼び出し（MUST be 0）」と同じ作法）

### 6. 画面（SC-10）に出すかの判断 —— 出さない

**出さない。** 理由は 3 つで、いずれも単独で十分である。

1. 7 指標のうち生産者があるのは **1 件だけ**である。節を作れば残り 6 指標は
   **「0 件」として描かれる**が、その 0 は「問題が無い」ではなく「測っていない」である。
   受け口の設計が「0 件の指標も欠落させない」（消えたのか 0 なのかを区別させる）と決めているのは
   **測っている前提**での話であり、未計測を 0 と描くのは真逆の誤読を作る。
2. 画面へ載せるには BFF の口が要るが、`src/knowledge/backend/Bff/**` は並行トラックの領域である。
3. IADR-0119 の「節ごと着手保留」は解けていない。

よって `OperationsDashboardPage.test.tsx` の**不在固定（8 語の否定）はすべて残す**。
**ただし理由の記述は追随させる** —— 現状のコメントは「要求が保留だから」だけを理由にしており、
保留が解けたときに 7 行すべてを置く読み方を誘発する。日付つきの追記で 1 の理由を足す。

## 受け入れ基準

- [ ] `orphan-documents` の観測値が定期的に受け口へ送られる（本番コードから）
- [ ] 孤立の判定が両端点を見る（片側だけを見ない）
- [ ] 個人資料には `docScope` が添えられ、受け手の除外が効く
- [ ] `doc_scope` を持たない文書は巻き添えで除外されない（陽性対照）
- [ ] リースを取得できない周期は収集も送出も行わない
- [ ] 孤立が 0 件でも報告を送る（スナップショット置換のため。送らないと過去の件数が残り続ける）
- [ ] 受け口が落ちていても例外を投げない（fail-open）
- [ ] 受け口が `/internal/...` にあり、無認証で 202、未知の指標名で 400 を返す
- [ ] 閲覧側 `GET /dashboard/knowledge-health` のロール限定が変わっていない
- [ ] Grafana の 2 パネルが compose・k8s の両経路に同内容で存在する

## テスト方針

受け入れ基準を xUnit の `[Fact]` へ写像し、**変異試験で検出力を実測する**（最低 5 変異＋無変異の
ベースライン対照）。変異は「①個人資料にスコープを添えない ②スコープ判定を否定形で書く
③リースを取らない ④送出しない ⑤孤立の判定を反転する」とし、全 KILL を確かめる。
ベースライン対照を置くのは、**永久に赤いテストが 1 本あると全変異が KILLED に見える**ためである。

## 検証の結果（実走した記録）

### 変異試験（6 変異・全 KILL。無変異対照つき）

| # | 変異 | 適用先 | 結果 | 落ちたテスト |
| --- | --- | --- | --- | --- |
| — | **無変異（対照）** | — | **緑** | 生産者 17 / 受け口 14 が全通過 |
| M1 | 個人資料にスコープを添えない（`docScope` を常に null） | 収集 | **KILLED** | 2 件（スコープ付与・綴りの大小） |
| M2 | スコープ判定を否定形で書く（「組織文書でない ⇒ 個人資料」） | 収集 | **KILLED** | 1 件（**陽性対照**。`scope: null` のケースだけが落ち、`organization` のケースは落ちない —— 対照が狙いどおり効いている） |
| M3 | 排他リースのゲートを外す | ワーカー | **KILLED** | 2 件（リース否認時に送ってしまう／解放しない） |
| M4 | 0 件なら送らない（「最適化」） | 収集 | **KILLED** | 1 件（空のスナップショットを送る） |
| M5 | 孤立の判定を反転する | 収集 | **KILLED** | 7 件 |
| M6 | 受け口の移設を巻き戻す（`RequireAuthorization()` を戻す） | 受け口 | **KILLED** | 1 件（終端メタデータ） |

🔴 **無変異対照は飾りではない。** 永久に赤いテストが 1 本でもあると、全変異が KILLED に見える。

### 🔴 変異試験で踏んだ落とし穴（次に回す者への申し送り）

**復帰に `shutil.copy2` を使うと mtime まで戻り、MSBuild が再コンパイルを飛ばす。**
実際に、復帰後の全件実行が **M5 と M6 の失敗をそのまま再現した**（＝古いバイナリを走らせていた）。
**復帰後は必ず `touch` してから再ビルドすること。** これを見落とすと、
「変異を戻したのにまだ赤い」を「テストが壊れた」と誤診する。

### 緑にできなかったもの（2 件。いずれも本作業の外の理由）

1. **`node scripts/scripts.test.js` は `IADR-0298 が欠番` で落ちる。**（作業当時。**統合時に 0298 が埋まり解消した**）。
   0298 は**並行トラックが確保済み**であり、本作業は指示どおり 0299 を使った。
   一時的に 0298 のプレースホルダを置いて実行したところ **655 件すべて通過**したので、
   **落ちているのは採番の欠番 1 点だけ**である（プレースホルダは削除済み）。
   並行トラックがマージされれば解消する。
2. **`node scripts/check-deploy-manifests.js`** は `helm` / `kubectl` / `kubeconform` が
   PATH に無いため実行できない（本作業以前からの環境制約）。
   **`DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS=1` は立てていない。**

### 測れていないもの（**緑と書かない**）

- **mTLS が実際に遮断していること。** Docker / k3s が無く、既存の機械検査が見ているのは
  マニフェストの字面（host 公開の有無・Service の `type:`）だけである。
- **Grafana が新しいパネルを受理すること。** provisioning のパリティ検査は
  「compose と k8s が同内容であること」しか見ない（検査器自身がその限界を明記している）。
  **配備時に画面で確認すること。**
- **実 PostgreSQL での advisory lock の挙動。** 単体テストは InMemory であり、
  リースは NoOp とスタブでしか回していない（DataSourceService の同型実装と同じ限界）。

## 計画書との差異

- 差異: **あり**。
  1. 陳腐化文書数のしきい値が未確定のため、当該指標を実装しない（計画へ裁定依頼）。
  2. 解決できないリンク数は永続化の設計が未定義（曖昧一致を未解決に含めるかを含む）のため実装しない。
  3. 未要約クラスタ数は前提となる機能が未実装のため構造的に生産できない。
  4. 辺の型ごとの使用件数は観測値モデルが内訳を表現できない（IADR-0265 が先送り済み）。

## 未決事項

- 🔴 **個人資料からの辺が組織文書の孤立判定を変える。** 本作業は計画の字義（「どの文書からも
  参照されず、どの文書も参照していない」）どおり**辺の相手のスコープを問わない**実装にした。
  この結果、組織文書 D が個人資料からリンクされると孤立件数が 1 減り、
  **件数の変動から個人資料の存在が推測され得る** —— 計画が個人資料を除外した理由の 1 つ
  （ADR-0034 決定 2 と同じ「件数からの推測経路を塞ぐ」）と衝突する。
  **実装側で解釈を決めない**（辺の相手のスコープで孤立判定を変えるのは指標の意味の変更である）。
  計画へ裁定を依頼する。
- 陳腐化文書数のしきい値（同上・裁定依頼）。
