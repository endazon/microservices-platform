---
title: IADR-0394 MeterListener の probe は Meter の「インスタンス」で購読を絞り、直列化は多層防御に留める
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-10
  - FR-11
  - ADR-0006
  - ADR-0044
  - ADR-0076
  - IADR-0110
  - IADR-0378
  - IADR-0389
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md (OTel/Prometheus への統一計装・Accepted)
  - planning:projects/microservices-platform/07_adr/ADR-0044_llm-usage-metrics-and-pricing-table.md (用途別・モデル別の費用計測・決定 1・3)
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md (決定 4・合成監視を費用へ計上しない)
---

# IADR-0394: probe の購読は Meter インスタンスで絞る（#1275）

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 起点 issue: [#1275](https://github.com/endazon/microservices-platform/issues/1275)（`bug`）。
  利用者の実測報告（2026-09-05）。
- 仕様書: `.ai-context/specs/20260905_issue-1275_meterlistener-probe-scoping.md`。
- [IADR-0110](./IADR-0110_llm-completion-stop-reason-metrics.md) 決定 7（`MeterListener` による回帰固定）と
  そこで作られた直列化コレクションの**加入規則を言い直す**。決定 1〜7 は覆さない。
- [IADR-0378](./IADR-0378_synthetic-traffic-marker-and-exclusion.md)（#1203）が足した不在の表明が、
  この欠陥を初めて観測可能にした。**#1203 が穴を作ったのではない。**

## コンテキストと課題

`MeterListener` は **Meter 名でプロセス全体の測定を購読する**。`LlmUsageMetrics.MeterName` は
`LlmCompletionMetrics.MeterName` と同じ定数（`microservices-platform.llm-gateway`）であり、
production が共有している。したがって Meter 名だけで絞る probe は、**同じ Meter へ発行する
別のテストクラスの測定を拾う**。

[IADR-0110](./IADR-0110_llm-completion-stop-reason-metrics.md)（#395）はこの危険を認識し、
`CompletionEndpointCollection` で「補完エンドポイントを叩くテストクラス」を直列化していた。
**加入規則が危険の範囲より狭かった** —— `LlmUsageMetricsTests` はエンドポイントを叩かないが
`RecordUsage` で同じ Meter へ `llm.tokens.total` を発行しており、加入していなかった。

### 🔴 「非決定的だから再現できない」ではなかった

issue の時点では develop で 3 回連続緑・CI も緑だった。**並列度を上げると 5/5 で再現する。**

```
./LlmGateway.Tests.exe -class ...LlmSyntheticUsageExclusionTests -class ...LlmUsageMetricsTests \
  -parallel all -maxThreads unlimited
→ PostCompleteStream_WhenSynthetic_... [FAIL]（5 回中 5 回）
```

混入した測定の値（`llm.tokens.total` = 1000 / 500 / 1000000、`llm.cost.total` = 0.0105 / 0.00018）は
`LlmUsageMetricsTests` が `RecordUsage` へ渡した値そのものであり、**発行元を値で確定できた**。

### 🔴 issue が書いた前提の 1 つは誤りだった

issue は「`MeterListener` は Meter 名でしか絞れず、Meter 名は production の定数なので、
**インスタンスでは区別できない**」としていた。**これは誤りである。**
`InstrumentPublished` が受け取る `Instrument` は `Meter` プロパティで Meter オブジェクトを返し、
`IMeterFactory` は**容器ごとに別の Meter インスタンス**を作る。

本リポジトリには先例が 3 件あり、いずれもインスタンス（または Scope）で絞っている ——
`AskStreamFirstTokenMetricsTests`（`ReferenceEquals`）、`QdrantCjkNgramSearchTests` /
`QdrantFullTextIndexObservabilityTests`（`instrument.Meter.Scope == factory`）。
**すでに解かれていた問題を、別の場所で解き直していなかっただけである。**

## 検討した選択肢

1. **Meter インスタンスで購読を絞る（採用）** — 自分の DI 容器から解決した Meter と
   同一インスタンスの計器だけを購読する。production は 1 行も変えない。先例が 3 件ある。
2. **測定が運ぶタグで絞る** — probe がタグを捨てているのを直し、テストが自分で決めた識別子
   （用途など）を持つ測定だけ集める。**採らない**（主軸としては）。理由は §理由。
3. **production へテスト専用の Meter 名注入口を作る** — 過剰な抽象化であり計画外。
   テストの都合が production の公開面に出る。
4. **直列化コレクションの加入規則だけ広げる** — 加入し忘れが静かに起きる（本 issue がその実例）。
   同一アセンブリ内でしか効かず、**probe 側の欠陥はそのまま残る**。単独では採らない。

## 決定

1. **`MeterListener` の probe は Meter の「インスタンス」で購読を絞る。**
   `ReferenceEquals(instrument.Meter, meter)`。`meter` は probe 自身が
   **そのテストの容器の `IMeterFactory`** から `Create(MeterName)` で引く
   （production の計器を先に解決してから引く —— 存在しない計器は `InstrumentPublished` に
   載らず、**購読しているつもりで何も見ていない**状態になる）。
2. **タグは捨てずに保持する。** 絞り込みの主軸ではないが、混入が起きたときに
   「どの用途・どのモデルの発行か」が失敗メッセージから読め、回帰試験がタグで検証できる。
3. **production へテスト専用の Meter 名注入口は作らない。** 本 PR の production 差分は **0 行**である。
4. **直列化コレクションの加入規則を危険の範囲へ言い直す（多層防御）。**
   `CompletionEndpointCollection` → **`SharedMeterCollection`**（`Name` は `llm-shared-meter`）へ改名し、
   規則を「**共有 Meter へ発行するクラス**」とする。`LlmUsageMetricsTests` を加える。
   **名前が規則を語るようにする** —— 「補完エンドポイント」という名前が、
   エンドポイントを叩かない発行者を加入対象から外して見せたことが本欠陥の一因である。
5. **同型の欠陥を他ユニットでも直す。** `KnowledgeHealthReportMetricsTests`（GraphService）は
   production の Meter 名で絞って `BeEmpty()` を表明しており、`KnowledgeHealthProducerTests` が
   同じ計器を別容器から発行する。**同じ型であり、まだ観測されていないだけである。**
   決定 1 を適用する。

## 理由

- **決定 1 が決定 2（タグ）より強いのは、条件が要らないからである。** タグで絞る案は
  「他クラスの発行が必ず別のタグを持つ」ことに依存する。本経路の `llm.purpose` は
  **設定で値域を閉じている**（[IADR-0110](./IADR-0110_llm-completion-stop-reason-metrics.md) 決定 2）ため、
  テストが好きな識別子を名乗るには**ルーティング設定へ用途を足す**必要があり、
  足さなければ `other` へ丸められて**識別子として機能しない**。
  インスタンス絞りは値域の設計に依存せず、**将来 production が属性を変えても壊れない**。
- **決定 4 が「多層防御」なのは、直列化が構造的な保証ではないからである。** 加入は人が書く属性で
  あり、書き忘れは静かに起きる。実測でも、**probe 側の是正だけで**（`[Collection]` を外した状態で）
  再現手順が 5/5 緑になった。直列化は主たる防護ではない。
- **決定 3 は「テストのために production を曲げない」原則である。** Meter 名は
  ダッシュボードとアラート式が読む契約（`LlmUsageMetricsTests` の T-19 が固定している）であり、
  そこへテスト用の分岐を足すと、契約の読み手が増える。

## 検証

- **再現（是正前）**: 上記の並列度引き上げで **5 回中 5 回** `PostCompleteStream_WhenSynthetic_...` が失敗。
- **是正後**: 同じ手順で **5 回中 5 回** 緑。さらに `[Collection]` を外して
  **probe の絞り込みだけ**の状態でも **5 回中 5 回** 緑（直列化に依存していないことの分離実験）。
- **回帰試験（新規 1 件）** `UsageProbe_IgnoresSameNamedMeterFromAnotherContainer`:
  別容器の `IMeterFactory` から**同じ Meter 名・同じ計器名**で `999_999` を発行し、拾わないことを固定する。
  **陽性対照を同じ試験の中に置く** —— 同じ probe が自分のアプリの発行は拾う。
  よって「拾わない」は購読が死んでいるからではない。
- **変異試験**: `ReferenceEquals(instrument.Meter, meter)` を
  `instrument.Meter.Name == LlmUsageMetrics.MeterName` に戻すと、この回帰試験が
  **`Value = 999999.0` を拾って落ちる**（実測）。スケジューリングに依存せず単独で落ちる。
- **陰性対照（絞り込みが「何も拾わない」に退化していないこと）**:
  `PostComplete_WhenNotSynthetic_RecordsUsageTokens` の `Contain` が常設の対照である。
- **GraphService 側は「再現していない」。** 同じ並列度引き上げを
  `KnowledgeHealthReportMetricsTests` × `KnowledgeHealthProducerTests` に当てたが、
  **3 回とも緑**だった（発行の窓が短く、重ならない）。
  🔴 **したがって「直った」ではなく「同型の欠陥として直した」である。**
  ただし**機構は決定的に示した** —— 同型の回帰試験
  `同じMeter名でも別インスタンスの発行は拾わない` を足し、変異（名前で絞る版に戻す）で
  **`999999` を拾って落ちる**ことを実測した。名前で絞る probe が外の発行を拾うこと自体は疑いが無い。
- `dotnet test .../LlmGateway.Tests.csproj` を連続 3 回、いずれも 248/248 緑。
  `GraphService.Tests` は 387/387 緑。

## 結果

- 良い影響: 共有 Meter に対する**不在の表明が書けるようになった**。従前は
  「存在を表明するだけ」なら混入しても落ちなかったが、`NotContain` / `BeEmpty` は落ちる。
  費用計測（ADR-0044）と合成監視の除外（ADR-0076 決定 4）はどちらも**不在が本体**であり、
  この土台なしには固定できない。
- 悪い影響 / トレードオフ:
  - probe が DI 容器を要求するようになり、器の受け渡しが 1 段増える。
  - **すべての probe を機械検査する検査器は置かない**（規約の追加は同型 2 回目から。本件は
    LlmGateway と GraphService の 2 箇所だが、**同じ PR での同時発見は「2 回」ではない**と読み、
    記録に留める）。同型が独立して再発したら検査器を作る。
- フォローアップ:
  1. Meter 名が一意な probe（`{name}.test-{guid}` 方式）とインスタンス方式の 2 系統が併存している。
     **どちらも安全であり、統一する動機は無い。**
  2. 直列化コレクションの加入は依然として人が書く。**加入漏れの機械検査は無い。**

## 関連

- Supersedes: なし（[IADR-0110](./IADR-0110_llm-completion-stop-reason-metrics.md) 決定 7 の運用を補う）
- Superseded by: なし
- 関連要求 / UC: NFR（可観測性）、FR-10（LLM 利用実績）、FR-11（送信可否の統制）
- 関連 IADR: [IADR-0110](./IADR-0110_llm-completion-stop-reason-metrics.md)（直列化コレクションの出所）、
  [IADR-0378](./IADR-0378_synthetic-traffic-marker-and-exclusion.md)（不在の表明を足した変更）、
  [IADR-0389](./IADR-0389_knowledge-health-producers-and-observation-dimension.md)（GraphService 側の同型の計器）
