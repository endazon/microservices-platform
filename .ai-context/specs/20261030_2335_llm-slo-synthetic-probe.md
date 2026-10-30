---
title: 作業仕様書 — SLO 評価用に LLM を呼ぶ 60 分の合成プローブを置き、60 秒のプローブとは要求ごとに区別する（#2335）
type: spec
status: done
related_ids: [NFR-02, NFR-21, FR-04, UC-01, ADR-0079, ADR-0076, ADR-0044, IADR-0378, IADR-0354, IADR-0469, IADR-0654]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2335"
---

# 作業仕様書 — LLM を呼ぶ 60 分の合成プローブ（#2335）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `6a340d26`）。親 #2130（子 B。子 A＝#2334 は判定窓。マージ済み）。

## 計画（確定済み）

- **ADR-0079 決定 1**: 合成監視は 2 段にする。
  - 常時トラフィックの生成は 60 秒間隔で、LLM を呼ばない（検索までは走る）。
  - SLO 評価用は 60 分間隔で、LLM を呼ぶ（NFR-02 の初回トークン p95 に標本を供給する）。
- **ADR-0079 決定 2**: LLM を呼ぶ合成を認める。絶対額のしきい値は置かず、費用の上限は 60 分という間隔で固定する（月 720 回。概算 月約 4,400 円）。合成分は `llm_usage_synthetic_excluded_total` で切り分ける。
- **ADR-0079 決定 3**: 判定窓（#2334 で `[6h]`・`for: 30m` 済み）。検知の遅れの上限は 8 時間。
- **ADR-0076 決定 4**: 合成は標識を持ち、ADR-0044 の LLM 費用計測と SC-10 の集計から除外する。除外できない構成では配備しない。

## 現状（develop で確認）

- 標識は「主体」である。BFF（外周）は検証済み JWT の `azp` が `SyntheticMonitoring:Subjects` に在れば合成と判定し、内周へ `X-Synthetic-Traffic: 1` を運ぶ。内周はヘッダだけを見る。
- AiAnalysisService は、合成の要求では LLM を呼ばずに縮退する（`done(Sent=false)` と縮退の文）。許可は `SyntheticMonitoring:AllowLlmEgress` だけで、**サービス全体**に効く。立てると 60 秒のプローブも LLM を呼ぶので、helm は立つと描画を止める。
- 🔴 **初回トークンの計器が、LLM の出していないトークンでも記録される**（実測。コードで確かめた）。端点 `AskStream` は最初の `AskTokenEvent` で `RecordFirstToken` を呼ぶ。`AskTokenEvent` は次の 3 つの縮退でも出る。
  - (a) 閲覧できる文書が無い（`NoAccessAnswer.StreamEvents()` が中立文言を 1 トークンで流す）
  - (b) 合成の LLM 抑止（`done(Sent=false)` の縮退の文をトークンとして流す）
  - (c) ゲートウェイの送信拒否・不調の理由の文

  60 秒のプローブは (a) か (b) を通るので、**60 秒ごとに「速い初回トークン」を積む**。60 分のプローブが本物の標本を 1 時間に 1 つ積んでも、p95 はこの偽の標本（60 倍）に埋もれる。**区別の要件は計器にも及ぶ。**

## 方針（IADR-0654 に記録する）

1. **SLO 評価用の主体を分ける。**
   - Keycloak の別のクライアント `synthetic-monitor-slo` を用意する（ロールなし）。
   - BFF の構成 `SyntheticMonitoring:LlmSubjects` に、その主体を置く。
   - 合成の判定は従前どおり `Subjects` で行う（SLO の主体も `Subjects` に入れる）。LLM の許可は `LlmSubjects` で判定する。
   - 判定は外周で JWT から行い、外から偽装できない（受信ヘッダは見ない）。
2. **内周へは、ヘッダの値で区別を運ぶ。**
   - SLO の主体は `X-Synthetic-Traffic: slo`、それ以外の合成は従前どおり `1` を運ぶ。
   - 内周の合成の判定（値が空でないこと）は変えない。LlmGateway の費用の除外は、どちらの値でも効く。
3. **AiAnalysisService は、要求ごとに LLM を許す。**
   - 合成の要求のうち、ヘッダの値が `slo` のものだけ LLM を呼ぶ。
   - サービス全体の `AllowLlmEgress` は互換のために残すが、helm は引き続き立つと描画を止める。
4. **初回トークンの計器は、LLM が出したトークンでだけ記録する。**
   - `AskTokenEvent` に出所（`FromModel`、既定 true）を足し、(a)(b)(c) と拒否の注記・空本文の注記は false にする。
   - 端点は `FromModel` の最初のトークンだけを記録する。
   - 60 秒のプローブと、閲覧できる文書が無い実利用は、標本を積まなくなる。
5. **helm**: `syntheticMonitor.slo`（既定無効）を足す。描画するものは次のとおり。
   - 別の Deployment `synthetic-monitor-slo`。probe.js を共用し、間隔 3600 秒、経路は `/bff/analysis/ask/stream` だけ、自分のクライアント・Secret を使う。
   - BFF への `SyntheticMonitoring__LlmSubjects__0`。
   - 3 サービスへの `SyntheticMonitoring__Subjects__1`。
   - Keycloak への egress。

   描画で止める条件は次のとおり。
   - `slo.enabled` なのに `syntheticMonitor.enabled` が偽（除外の標識が無い）。
   - SLO のクライアントが常時側と同じ（区別が無い）。
   - 間隔が 3600 秒より短い（費用の上限が崩れる）。
   - SLO の経路に `/bff/analysis/ask/stream` 以外がある。
   - 60 秒側の Deployment と aianalysis に LLM の許可が出ない（従前の AllowLlmEgress の検査を保つ）。

   試験の型は、Deployment ではなく描画の検査（`helm-synthetic-monitor.test.js`）で固定する。
6. **realm**: クライアント `synthetic-monitor-slo` を宣言する（ロールなし・`client_credentials`。開発用の secret は従前のクライアントと同じ作法で、本番は config から供給する）。
7. **秘密情報**: Vault `secret/msp/synthetic-monitor-slo-oidc` → ExternalSecret → Secret `synthetic-monitor-slo-oidc`。
   - ExternalSecret の雛形と、SC-22 の管理項目の一覧（`deploy/bootstrap/sc22-secret-items.json`）、ローカルの bootstrap の鍵の作成を足す。
   - 実値は書かない。
8. **ライブのクラスタには触れない**（k3s・Vault・Keycloak）。配備の前に要る手順は PR の「PoC 配備への影響」に書く。
   - 前提の 1 つとして、**SLO の主体が読める文書が 1 つ以上要る**（ABAC。無いと「閲覧できる文書が無い」で LLM まで届かない）。これは配備の手順に書く。

## 母集合（規則 9・10）

- **規則 9**:
  - `git grep -n "AllowLlmEgress\|IsSyntheticInternalRequest\|HeaderValue\|RecordFirstToken\|new AskTokenEvent\|SyntheticMonitoring__Subjects\|synthetic-monitor"` で、追跡下（submodule と凍結記録を除く）を走査する。
  - 結果は実装中の追記に書く。
- **規則 10**: 次の記述が誤りになる。
  - 「LLM を呼ぶ 60 分側は未配備・別の配備単位」の注記（helm の template・values・probe.js・README・alerts.yml・operations）。
  - `RagOrchestrator` の「評価対象は本 PR では生まれない。裁定待ち」。
  - `AllowLlmEgress` の説明（唯一の許可という前提）。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 🔴 60 秒のプローブ（ヘッダ `1`）は LLM を呼ばない（輸送が 1 度も呼ばれない） | AiAnalysisService の合成の試験 | 緑（従前の振る舞いを固定する。陽性対照は下の `slo`） |
| SLO の要求（ヘッダ `slo`）は LLM を呼び、`AllowLlmEgress` が偽でも呼ぶ | 同 | 赤 |
| BFF: `LlmSubjects` の主体は `slo` を、`Subjects` だけの主体は `1` を、主体でなければヘッダなしを運ぶ（受信ヘッダは無視する） | BFF の試験 | 赤 |
| 共通: 外周の分類（なし／常時／SLO）と、内周の SLO の判定 | `SyntheticTrafficTests` | 赤 |
| 初回トークンの計器は、閲覧できる文書が無い・合成の抑止の縮退では記録せず、LLM のトークンでは 1 件記録する | `AskStreamFirstTokenMetricsTests` | 赤（縮退でも記録していた） |
| helm: SLO の Deployment・標識 2 つ・egress が同じ描画に揃い、既定では 1 バイトも出ない。止める条件 4 つ | `helm-synthetic-monitor.test.js` | 赤（CI の helm で確かめる。手元に helm は無い） |

## 実装中の追記（2026-10-30）

- **基点**: develop `3c380992`（#2345 のマージ後。コーディネータの指示）。
- **規則 9 の走査結果**:

  | 箇所 | 扱い |
  | --- | --- |
  | 共通の `SyntheticTraffic`（分類・値 `slo`・内周の判定・`LlmSubjects`） | 直した |
  | BFF の `AnalysisBffEndpoints`（3 経路） | 直した |
  | `RagOrchestrator`（抑止の条件・縮退の文の出所） | 直した |
  | `NoAccessAnswer` | 直した |
  | `AskStream` の端点 | 直した |
  | `AskTokenEvent` | 直した |
  | helm（values・deployment・synthetic-monitor・ci の values） | 直した |
  | realm | 直した |
  | Vault の bootstrap・SC-22 の管理項目・ExternalSecret | 直した |
  | `k8s-local-up.sh`（同じ門で ExternalSecret を当てる） | 直した |
  | alerts.yml・operations・`docs/observability/synthetic-traffic-exclusion.md`・`deploy/local/synthetic-monitor/README.md` と overlay の注記 | 直した |
  | probe.js の注記（「LLM は既定で呼ばれない」） | 既定では今も正しく、写しのバイト一致の試験があるので変えない |
  | `k8s-local-up.sh` の echo（「60 分側はここには無い」） | ローカルの門には今も無いので変えない |
  | `docs/how-to/deployment.md` の AllowLlmEgress の記述 | 今も正しいので変えない |
- **規則 10 の追加の発見**: 初回トークンの計器は、縮退の文でも進んでいた（方針 4）。60 分側を置くと、標本がこの偽の標本に埋もれる。だから同じ PR で直した。
- **realm の description**: realm の取り込みの上限は 255 文字である。`check-realm-constraints` の試験で検出して、短く直した。
- **ExternalSecret**: `k8s-local-up.test.js` は「置いたのにどの門でも当てない ExternalSecret」を検出する。60 秒側と同じ門（`SYNTHETIC=1`）で当てるようにした。
- **赤 → 緑**:
  - 共通の分類の試験: API が無いのでビルドが通らない状態から緑にした。
  - BFF の `SyntheticSloProbeBffTests`: SLO の 3 件が赤だった。
  - AiAnalysisService の `RagSyntheticLlmEgressTests`: SLO の 3 件が赤だった。60 秒の 3 件は従前の振る舞いの固定なので、修正前から緑である。
  - `AskStreamFirstTokenMetricsTests` の縮退の 1 件: 赤だった。
- **helm**: 手元に helm が無く、導入もしない。チャートの描画の試験（`helm-synthetic-monitor.test.js` の SLO の 6 件）は CI の static-checks-units で初めて走る。構文検査（`node --check`）だけは確かめた。
- **環境の注記**: `Knowledge.IntegrationTests` の `WikiSlugNonUniquePostgresTests` は、接続文字列が無いので手元で落ちる（本変更とは関係しない）。
- **［追加コミット］#2346 の監査と CI の是正**:
  - (1) dev 以外のクラスタでは、realm の後追いの dev の secret の守りが、稼働に無い `synthetic-monitor-slo` の作成を名指して非 0 で終える（有効化に関係なく起きる）。
    - 運用仕様書に「後追いの前に dev 以外の secret で作り、Vault へ置く」事前手順を書いた。
    - 先例として、`deploy/local/keycloak-setup/README.md` の守りの節と、ローテーションの手順書の対応表の `synthetic-monitor` の行を引いた。
    - 対応表に行を足した。
    - 試験を 1 件足した（`keycloak-realm-reconcile.test.js` の #2335）。
  - (2) `helm-synthetic-monitor.test.js` で、足した定数が `read` の定義より前にあり TDZ で落ちていた。CI の static-checks-units で赤になった。
    - 手元で `node scripts/helm-synthetic-monitor.test.js` を走らせて赤を再現し、定義の後へ移した。
    - helm が無いので fail-closed で止まるが、その手前の静的な試験 3 件までは緑になることを確かめた。
