---
title: 可観測性・運用の再実装 — LLM 利用実績の用途別・モデル別計測と有効期間つき単価表・ナレッジ健全性指標
type: spec
status: in-progress
related_ids: [FR-10, NFR, UC-05, SC-10, ADR-0006, ADR-0044, IADR-0110, IADR-0212, IADR-0011, IADR-0164, IADR-0265]
author: Claude
created: 2026-08-23
updated: 2026-08-27
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
  - planning:projects/microservices-platform/07_adr/ADR-0044_llm-usage-metrics-and-pricing-table.md
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md
---

# 仕様書: #443 LLM 利用実績の計測と単価表・ナレッジ健全性指標

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/microservices-platform/`）を
> 一次情報とし、本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（利用状況・検索傾向・回答品質の可視化）／NFR（可観測性・費用）
- ユースケース（UC）: UC-05
- 画面（SC）: SC-10（運用ダッシュボード。**本 issue は API 側のみ**。画面側は #452 / #504）
- 関連 ADR: ADR-0006（可観測性スタック）／**ADR-0044**（LLM 利用実績の計測粒度と単価表。ADR-0006 の部分改定）／
  ADR-0022・ADR-0025（単価・トークナイザの前提）／ADR-0034 決定 2（件数表示の存在秘匿）
- 関連 IADR: IADR-0110（補完の終了理由カウンタ・属性軸）／IADR-0212（出力トークン Histogram）／
  IADR-0011（業務指標は DashboardService・技術/費用は可観測性スタック）／IADR-0164（月次手動確認の暫定統制）／
  IADR-0119（FR-17/18 の保留は 2026-08-07 に解除済み）
- 計画書リンク: `06_technical/05_observability-ops.md` §LLM 利用実績の計測粒度と単価表の扱い ／
  §ナレッジ健全性の指標（集計範囲・2026-08-02 確定）

### 着手条件の確認（IADR-0119 決定 6）

ナレッジ健全性指標は FR-17 / FR-18（文書間リンク・AI 提案）の指標を含む。**着手条件は原文で引き直した**
（IADR-0119 の判定表は pin 固定のスナップショットであり「今どうか」の答えではない、と同 IADR が自ら警告している）。

| 前提 ADR | 実測（`/home/user/project-planning` の隣接クローンで確認・2026-08-23） | 判定 |
| --- | --- | --- |
| ADR-0033 / 0034 / 0035 | いずれも `status: Accepted` | 充足 |
| FR-17 / FR-18 の保留 | IADR-0119 の 2026-08-07 追補で**解除済み** | 着手可 |
| FR-19 / FR-20 | 保留継続（本作業の射程外。個人資料の**除外**は扱うが、個人資料機能そのものは実装しない） | — |

## 目的・背景

ADR-0044 は「用途別・モデル別の LLM 利用実績」「有効期間つき単価表」「金額換算はゲートウェイ側」を確定させたが、
**トークン消費量の累計・金額換算・単価表はリポジトリに存在しない**（後述の実測）。
計画側の運用統制（月次の手動確認）は「費用の金額は 1 円も出せない」状態のまま置かれている。

## 着手前の実測（受け入れ基準の現状）

母集合は下記「母集合の引き方」の 4 軸で引いた。走査の結論のみ記す。

| ADR-0044 の決定 | 実測 | 判定 |
| --- | --- | --- |
| 決定 1: 用途別・モデル別の**属性軸** | `LlmCompletionMetrics` が `llm.purpose` / `llm.model` / `llm.provider` / `llm.confidentiality` を持つ | **実装済み**（IADR-0110） |
| 決定 1: **トークン消費量** | `llm.completion.output_tokens`（Histogram・**出力のみ**）が IADR-0212 で稼働。**入力トークンは計測点が無い**。**累計（費用の分子）を持つ計器も無い** | **一部**（出力の分布のみ） |
| 決定 1: **金額換算** | `src/` にも `deploy/` にも単価に相当する設定・定数が無い（軸 1・軸 2 の走査で 0 件） | **未実装** |
| 決定 2: **フォールバック発火回数**（用途別・モデル別） | `LlmCompletionMetrics.ResultFallback` が `llm.result=fallback` で計上済み（ADR-0038 決定 6 / IADR-0225） | **実装済み**。本作業では**触らない** |
| 決定 3: **有効期間つき単価表** | 存在しない | **未実装** |
| 決定 3: **期間外・該当なしの警告** | 存在しない | **未実装** |
| 決定 4: 可視化面は Grafana・BFF 契約に載せない | `llm-usage.json` が存在するが「これは費用ではない」と自ら明記している | **部分**（費用パネルが無い） |
| 計画 §ナレッジ健全性の指標（7 指標・全体集計・運用者限定・`private-note` 除外・件数のみ） | `DashboardService` は `UsageEvent`（search / answer）のみ。健全性の口が無い。`IngestTagMetrics`（DocumentService）が 7 指標目だけを Grafana 側で持つ | **未実装**（1/7 のみ別経路で存在） |

走査の証跡（コマンドと生の出力）は §母集合の引き方 に置く。

## 対象範囲

- **対象**:
  1. `LlmGateway`: 有効期間つき単価表（設定）・単価の解決・期間外/該当なしの警告・
     用途別×モデル別のトークン累計と金額換算メトリクス。
  2. `DashboardService`: ナレッジ健全性指標の集計 API（`private-note` 除外・運用者/管理者限定・件数のみ）。
  3. Grafana ダッシュボード（compose 経路 A ＋ k8s 経路 B の**両方**）の費用パネル追加と「費用は出ない」注記の是正。
  4. 仕様書（`docs/observability/` 新設・`docs/operations/` の状態記述の追随）と仮番号 IADR。
- **対象外**（理由つき）:
  - **#380**（Opus 5 の実運用値確認）。**同 issue は稼働環境での実測であり、本 issue は計測手段の実装である。**
    重なるのは「出力トークン」の 1 点だけで、**その計器（`llm.completion.output_tokens` Histogram）は
    IADR-0212 で既に実装済みであり、本作業は一切変更しない**。本作業が足すのは**累計カウンタ**
    （`llm.tokens.total`）で、用途は費用の分子である。**分布は #380（max_tokens の再調整）、
    累計は #443（費用）** と役割で分ける。二重実装にはならない。
  - **フォールバック発火回数の計測**（ADR-0044 決定 2）。**既に実装済み**のため触らない。
  - **SC-10 画面のナレッジ健全性節**（4 KPI・辺の型・フォールバック警告の**表示**）。IADR-0119 の
    「解除で発火した予告」表が引き受け先を **#504 / #452** と名指ししている。本作業は API までとする。
  - **辺の型ごとの内訳（型別の件数）**。画面が要る粒度であり、引き受け先は上と同じ。API は指標ごとの
    件数のみを返す。
  - **観測値の生産者側の配線**（GraphService / DocumentService から健全性の観測値を送る実装）。
    当該サービスは別担当が作業中であり、本 PR のファイル領域と交差する。API と受け口までを本作業とする。
  - **月次予算の上限アラート**（Alertmanager 未配備。計画 §LLM 費用の上限アラートと暫定の統制）。
  - **Knowledge.Contracts への DTO 追加**。契約スナップショット（`scripts/contract-schema-baseline.json`）の
    更新を伴い、他担当と衝突する。BFF へ載せる段（#452 / #504）で昇格させる。

## 設計

### 1. 単価表（有効期間つき）— `LlmGateway`

設定 `Llm:Pricing`（`ModelPricingOptions`）:

```jsonc
"Pricing": {
  "Currency": "USD",
  "Models": {
    "claude-sonnet-5": [
      { "EffectiveFrom": "2026-01-01T00:00:00Z", "EffectiveTo": "2026-09-01T00:00:00Z",
        "InputPerMillionTokens": 2.0, "OutputPerMillionTokens": 10.0 },
      { "EffectiveFrom": "2026-09-01T00:00:00Z", "InputPerMillionTokens": 3.0, "OutputPerMillionTokens": 15.0 }
    ]
  }
}
```

- **境界の規約**: `EffectiveFrom` は**含む**、`EffectiveTo` は**含まない**（半開区間 `[From, To)`）。
  隣接する区間を「終了日 = 次の開始日」で書いたとき、**切替時刻ちょうどに両方が該当する／どちらも該当しない**
  という穴を作らないためである。`EffectiveFrom` 省略 = 過去方向に無限、`EffectiveTo` 省略 = 未来方向に無限。
- **解決の結果は 3 値**（`Priced` / `OutOfEffectivePeriod` / `NoEntryForModel`）。
  **無音で 0 円にしない**（ADR-0044 決定 3）—— 該当しない場合は
  **警告ログ ＋ `llm.pricing.unpriced.total` カウンタ**を出し、**金額メトリクスは記録しない**
  （0 を積むと「安く済んだ」と読めてしまい、期限切れが費用の減少に化ける）。
- 区間が重なる設定は**起動時に落とす**（`IValidateOptions` + `ValidateOnStart`）。重なりを実行時に
  黙って先勝ちで解決すると、どちらの単価で計算したかが後から分からない。

### 2. トークン累計・金額換算メトリクス — `LlmGateway`

新クラス `LlmUsageMetrics`（Meter 名は既存と同じ `microservices-platform.llm-gateway`）。

| 計器 | 種別 | 単位 | 属性 |
| --- | --- | --- | --- |
| `llm.tokens.total` | `Counter<long>` | `{token}` | `llm.token_type`（`input`/`output`）＋ `llm.purpose` / `llm.model` / `llm.provider` / `llm.confidentiality` |
| `llm.cost.usd.total` | `Counter<double>` | `{USD}` | `llm.purpose` / `llm.model` / `llm.provider` / `llm.confidentiality` |
| `llm.pricing.unpriced.total` | `Counter<long>` | `{completion}` | `llm.pricing_status`（`out_of_period`/`no_entry`）＋ `llm.model` |

- 属性の値域は **IADR-0110 の規律をそのまま継承**する（`purpose` は設定で閉じ未定義は `other`、
  利用者識別子・プロンプト・本文は載せない）。正規化関数は `LlmCompletionMetrics` から**共有**する
  （`internal` へ公開し二重定義を作らない）。
- 記録するのは**送信が成立した呼び出しだけ**（`/complete` の成功経路と `/complete/stream` の
  `sawDone` 経路）。未送信にトークンは存在しない（IADR-0212 決定 3 と同じ判断）。

### 3. ナレッジ健全性指標 — `DashboardService`

- エンティティ `KnowledgeHealthObservation`（`Indicator` / `SubjectKey` / `DocScope` / `ObservedAt`）。
  **`SubjectKey` は不透明な識別子で、API から一切返さない**（文書名を出さないため）。
- 受け口 `POST /dashboard/knowledge-health/observations`: 指標 1 つ分の**スナップショット置換**
  （当該指標の既存行を削除して差し替える）。生産者は将来 GraphService / DocumentService。
- 閲覧 `GET /dashboard/knowledge-health`: **`platform-admin` / `platform-operator` のみ**（他は 403・無認証は 401）。
  **`DocScope == "private-note"` の行を集計から除外**し、**7 指標すべてを 0 埋めして件数だけ返す**。
  閲覧は `IAuditLogger` で監査ログに残す（計画「閲覧は監査ログに記録する」）。
- **集計範囲は全体**（閲覧者の権限で絞らない）。計画が定めた 3 条件（件数のみ・ロール限定・個人資料除外）は
  同時に満たすことが条件であり、個別に緩めない。
- 指標の語彙は閉じる（`KnowledgeHealthIndicators`）。未知の指標名は 400。

### 4. Grafana

`llm-usage.json` に費用パネル（用途別・モデル別の金額、単価未解決の警告）を足し、
「費用は表示しない」注記を現状に合わせて書き換える。**compose（`deploy/grafana/provisioning/`）と
k8s（`deploy/local/observability/grafana.yaml`）の両方**を同時に直す
（`check-grafana-provisioning-parity.js` が経路間の乖離で落ちる）。

## 受け入れ基準

- [ ] 単価表が有効期間つき設定として存在し、コード内定数を持たない
- [ ] 期間をまたぐ集計で正しい単価が適用される（**切替時刻ちょうど・前後**の境界テストがある）
- [ ] 期間外・該当なしは**警告**として表に出る（無音の 0 円にしない）
- [ ] トークン消費・金額が**用途別・モデル別**に出る
- [ ] 金額換算はゲートウェイ側で行い、Grafana のクエリに単価を書かない
- [ ] 健全性指標が `private-note` を除外して集計される
- [ ] 健全性指標は運用者・システム管理者以外は **403**（無認証は 401）
- [ ] 健全性指標の応答に**文書名・識別子が含まれない**（否定形テスト）
- [ ] メトリクス命名・ラベルの契約テスト（ダッシュボードが依存する系列名の変更を検知する）
- [ ] ダッシュボード整備（#287 相当の「ダッシュボード 0 件」を繰り返さない）

## テスト方針

各テストの直前コメントに起点 ID を書く（`check-test-traceability.js`）。

| # | 対象 | 内容 |
| --- | --- | --- |
| T-1〜T-5 | 単価解決 | 区間内／**切替時刻ちょうど（新単価側が当たる）**／切替直前 1 tick（旧単価）／期間外（両端の外）／モデル未登録 |
| T-6 | 金額計算 | 入力・出力を別単価で按分し百万トークン単位で換算 |
| T-7 | 設定検証 | 区間の重なりを起動時に落とす |
| T-8〜T-10 | メトリクス | 用途別・モデル別にトークンと金額が出る／単価未解決では**金額を記録せず**警告カウンタが増える／属性名の契約（系列名・ラベル名の固定） |
| T-11〜T-15 | 健全性指標 | `private-note` 除外／運用者 200・管理者 200・一般 403・無認証 401／応答に `SubjectKey` が現れない（否定形）／0 埋め／未知指標は 400 |

## 母集合の引き方（規則 1〜10 の適用）

**誤りの側**（「未実装である」と断定している記述）から引いた。正しい側（「実装済み」）で引くと、
本作業で**新たに誤りになる記述**は 1 件も捕まらない（規則 1・10）。

| 軸 | 検索語 | ヒット | 追随の要否 |
| --- | --- | --- | --- |
| 1 | `単価\|pricing\|Pricing` | 19 ファイル | 下表のとおり |
| 2 | `金額換算\|単価表` | **24 行 / 13 ファイル** | 同上 |
| 3 | `llm_completion\|llm\.completion` | 21 ファイル | 系列名の追随先（ダッシュボード・アラート・仕様書） |
| 4 | `doc_scope\|ナレッジ健全性` | 19 ファイル | 健全性指標の追随先 |

> **★ 軸 2 の数は数え直した値である。** 着手時に出力を目で数えて「20 行 / 16 ファイル」と書いたが、
> **生の出力を数え直すと 24 行 / 13 ファイルだった**（`grep -c` で per-file 集計）。
> **出力を目で数えた値は導出値であり、走査し直すのではなく数え直さなければ合わない**
> （規則 7「導出値は走査ではなく計算し直す」の同型の失敗）。以下の増分表はこの 24 を起点とする。

**拡張子で絞らず**（規則 3）、`node_modules` / `.git` / `obj` / `bin` / submodule（`src/ai-stock-trading`）のみ
パスで除外した。**行フィルタで絞っていない**（規則 4）。

### 追随する（本作業で直す）

| ファイル | 理由 |
| --- | --- |
| `deploy/grafana/provisioning/dashboards/llm-usage.json` | 「トークン消費量・金額換算・単価表はいずれも未実装」と本文に書いている |
| `deploy/local/observability/grafana.yaml` | 上の**同一文言の複写**（経路 B）。片方だけ直すとパリティ検査が落ちる |
| `docs/operations/llm-cost-monthly-review-runbook.md` | 「費用そのもの（金額）は見えない」 |
| `docs/operations/operations.md` | 「費用の金額は現状 1 円も出せない」 |
| `docs/observability/llm-usage-and-cost-metrics.md`（新設） | 新しい計器の仕様 |

### 追随しない（除外と理由）

| ファイル | 除外理由 |
| --- | --- |
| `.ai-context/adr/IADR-0110` / `IADR-0164` / `IADR-0212` | **凍結記録**。本文プロズを後から書き換えない（`CLAUDE.md` §目的）。後継は新 IADR（仮番号 IADR-0265）が引き受ける |
| `.ai-context/specs/*`（6 件） | 過去の作業仕様書。**当時の実測の記録**であり、後から書き換えると「いつ測ったか」が失われる |
| `docs/how-to/plan-id-range-history-annex.md` | ADR-0044 を**採番の履歴**として引くだけで、実装状況を述べていない |
| `docs/observability/llm-completion-metrics.md:131` | 「単価表の見積もりと突き合わせる」は**運用の読み方**であり、実装状況の断定ではない。新設の仕様書から相互に参照する |
| `src/.../CompletionRoutingEndpointTests.cs` | `pricing` は用途名の一部（テストデータ）であり無関係 |
| `docs/screens/SC-10_*` / `docs/tests/SC-10_*` / `src/knowledge/frontend/**` | **画面側は #452 / #504 の射程**（IADR-0119「解除で発火した予告」表）。本作業では触らない |
| `deploy/prometheus/alerts.yml` | 月次予算のしきい値が未確定（計画 §リスク・未決事項）。アラートは配線できない |

**自己参照の補正（規則 8）**: 軸 2 の走査は着手前に **20 行**を返した。**本仕様書は走査の後に作成した**
ため、この 20 行に自己参照は含まれていない。

**完了後に同じ語で引き直すと 84 行である**（走査がそのまま返す数）。**予測ではなく引き直した値である** ——
着手時点では「本仕様書と新 IADR の 2 件が増えて 22 行」と書いていたが、**これは二重に誤りだった**
（起点の 20 が数え違いであり、かつ「語は 1 ファイル 1 回」を暗に仮定していた）。引き算を見せる。

| 区分 | 行数 |
| --- | ---: |
| 着手前（実測を数え直した値） | 24 |
| **自己参照**: 本仕様書 18 ＋ 新 IADR 9 | **+27** |
| 新設の可観測性仕様書 | +7 |
| 追随した既存文書の増分: Runbook +2（1→3）／運用仕様書 +1（1→2） | +3 |
| ダッシュボードの増分: compose +2（1→3）／k8s +2（1→3）。**同一文言の 2 経路** | +4 |
| 機能仕様書 +1（0→1）／テスト仕様書 +2（0→2） | +3 |
| **実装コードとテスト**（新設の単価表・計器とその単体テスト） | +16 |
| **合計（実測）** | **84** |

**値はコミットで固定する。** 追試するときは同じ除外条件（`node_modules` / `.git` / `obj` / `bin` /
`src/ai-stock-trading`）で引くこと（`grep -rc ... | grep -v ':0$'` で per-file 集計してから合算する
—— **目で数えない**）。

## 計画書との差異

- 差異: **あり（軽微・環流不要）**。計画 §ナレッジ健全性の指標は**指標の集計主体を定めていない**。
  IADR-0011 の責務分担（業務指標は DashboardService）に従い DashboardService へ置いたが、
  **観測値の生産者は GraphService / DocumentService**であり、DB-per-service（ADR-0002）のため
  DashboardService から直接は数えられない。**受け口（観測値の報告 API）を設ける**ことで解決した。
  計画の決定には反しないため環流はしない（実装に閉じた判断として仮番号 IADR-0265 に記録する）。
- 差異: **あり**。計画 §運用コスト は通貨を明示していない。単価表の一次情報（Anthropic の公開単価）が
  USD であるため **USD で計上**し、円換算は行わない（為替レートの取得先という新しい依存を作らないため）。

## 未決事項

- 月次予算のしきい値（計画側で実測待ち）。本作業では**アラートを配線しない**。
- 陳腐化文書数のしきい値（計画側で未確定）。指標の**枠**だけを用意し、判定は生産者側に委ねる。

---

## ［2026-08-27 追記 / #1018］受け入れ基準チェックリストと実装の実態が乖離している

**§受け入れ基準のチェックボックスは 10 件すべて未チェックのままだが、実装はコードとして
着地済みである。** チェックが埋まらないまま残っているのは、**§未決事項が挙げるしきい値
（月次予算・陳腐化文書数）が計画側で未確定**であり、それに依存する項目を実測で埋められないためである。

- **本文のチェックボックスは書き換えない。** 凍結記録の本文プロズを後から書き換えない運用に従う
  （`.claude/rules/traceability.repo.md` §凍結の射程。`.ai-context/specs/` に許されるのは
  本ブロックのような `［YYYY-MM-DD 追記 / #NNN］` 書式の経過追記だけである）。
- **`status: in-progress` は据え置く。** 「実装は着地したが、しきい値未確定分が残る」という実態と
  一致するのはこの値であり、`done` へ進めると**未確定分が残っていることが読めなくなる。**
- しきい値が確定したら、その時点の作業の仕様書で受け入れ基準を引き直す。**本書のチェックボックスを
  後から埋めることはしない。**
