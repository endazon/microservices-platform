---
title: ABAC 属性組み合わせ数の実測 — 測定スクリプトの新設と経路B 実機での実測
type: spec
status: done
related_ids: [FR-17, FR-18, FR-05, FR-09, ADR-0033, ADR-0034, ADR-0035, ADR-0036]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - planning:projects/microservices-platform/06_technical/14_knowledge-graph-graphrag.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
  - planning:projects/microservices-platform/07_adr/ADR-0035_graphrag-retrieval-strategy.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
related_specs:
  - ../adr/IADR-0014_qdrant-attribute-payload-key.md
  - ../../docs/how-to/local-development.md
  - feedback:20260805_abac-attribute-combination-measurement-result.md
---

# 仕様書: ABAC 属性組み合わせ数の実測（issue #456）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-17（知識グラフ）・FR-18（AI 提案）。測定対象の属性体系は FR-05・FR-09（ABAC）由来
- ユースケース（UC）: なし（読み取り専用の測定作業）
- 画面（SC）: なし
- 関連 ADR（計画）:
  ADR-0033（計画リポ）（Proposed。ストア選定は実測後）／
  ADR-0034（計画リポ）（Proposed。クラスタ要約の作り分け粒度は本 ADR では決めないとした）／
  ADR-0035（計画リポ）（Proposed。**案 B（実測なしで起案）**を採用し、要約の粒度は「機密区分単位（4 通り）から始める」と決定済み。実測は**稼働後の検証項目**として §結果 に明示された）
- 関連する技術検討（計画）:
  14_knowledge-graph-graphrag（計画リポ） §6「コミュニティ要約の粒度と費用の決定手順」（**本作業が満たすのは手順 1「実測する」**）／
  07_abac-attribute-model（計画リポ）（文書属性・利用者属性の正）
- 実装 issue: [#456](https://github.com/endazon/microservices-platform/issues/456)
- 環流済み: [planning#187](https://github.com/endazon/project-planning/issues/187)（CLOSED・回答あり）

## 目的・背景

計画側は planning#187 の回答で **案 B（実測なしで ADR-0035 を起案）** を採り、ADR-0035 は
「要約の粒度は**機密区分単位（4 通り）から始める**」と決定した。したがって #456 は
**ADR-0035 起案のブロッカーではなくなった**が、同回答は次の宿題を明示的に残している。

> 旧データ破棄（実装側 #457）の前に、属性組み合わせ数を機械的に数えられるスクリプトを用意する。
> 実測できるかは環境に依存するが、**測る手段を用意すること自体は環境に依存しない**。

本作業は (1) その**測定手段をリポジトリに残す**こと、(2) 現在**稼働している経路B 実機**
（Rancher Desktop 内蔵 k3s。Keycloak ＋ `document_svc` に実データ 2,362 件）で**実際に測る**ことの 2 つを行う。
測れる機会があるうちに測る（#457 の切替でデータを破棄すると測定機会を失う）。

## 対象範囲

- 対象:
  - 測定スクリプト `scripts/measure-abac-combinations.js` の新設（Node 標準ライブラリのみ・外部依存なし）
  - 集計ロジックの単体テスト（`scripts/scripts.repo.test.js` へ追加）
  - 稼働中の経路B 実機に対する実測と、結果の記録
  - 実測結果の #456 および計画リポジトリへの環流（`/plan-feedback`）
- 対象外:
  - ADR-0035 の改訂そのもの（計画リポジトリ側の操作。環流までが本作業）
  - GraphRAG・コミュニティ要約の実装（#450 / #448）
  - 旧データの破棄・切替（#457）
  - LLM 費用の実試算（14_knowledge-graph-graphrag（計画リポ） §6 手順 2）。本作業はその**入力となる組み合わせ数**までを出す

## 設計

### 測定する 3 つの粒度

14_knowledge-graph-graphrag（計画リポ） §6 手順 3 が
「属性組み合わせ単位 → **ロール単位** → **機密区分単位**」と粒度の段階を定めている。
測定はこの 3 段階すべてを同時に数える（粒度を落とす判断がその場でできるようにするため）。

| 粒度 | 数え方 | ADR-0035 との対応 |
| --- | --- | --- |
| 属性組み合わせ単位 | 文書側の ABAC 属性の**実在する値の組**の異なり数 | 最も細かい。費用が最大 |
| ロール単位 | realm ロールの**実在する保有集合**の異なり数 | 中間 |
| 機密区分単位 | `confidentiality` の**実在する値**の異なり数 | ADR-0035 の採用粒度（設計上は 4 通り） |

### データ源

| 面 | 源 | 取得方法 |
| --- | --- | --- |
| 文書属性 | `document_svc` の `Documents.Attributes`(jsonb) / `Tags`(jsonb) | `psql`。既定は `kubectl exec` 経由（経路B）。`ABAC_DOC_DSN` 指定時は直接接続 |
| 利用者属性 | Keycloak realm `microservices-platform` の users（`clearance` / `department` / realmRoles） | Keycloak Admin REST。既定は `kubectl exec` ＋ `kcadm.sh`。`ABAC_KC_URL` 指定時は直接 |
| 属性辞書 | `authz_svc` の `AttributeDefinitions`（scope=document/user） | `psql`。ABAC 対象キーの判定に用いる |

**ABAC 対象キーの決め方**（重要）。`Documents.Attributes` には `publishedAt` のような
高基数のメタデータも入るため、素朴に全キーを組み合わせると意味のない巨大な数になる。次の優先順で決める。

1. `AttributeDefinitions`（scope=`document`）が 1 件以上あれば、**その `Key` 集合**を対象とする
2. 無ければ 07_abac-attribute-model（計画リポ） の
   文書基本属性（`confidentiality` / `department` / `owner` / `shared_with` / `project` / `lifecycle` / `data_class` / `region`）を対象とする
3. どちらにも該当しない実在キーは「**ABAC 対象外の実在キー**」として**別枠で列挙**する（隠さない）

利用者側は [`BffScopeResolver.ExtractUserAttributes`](../../src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Authz/BffScopeResolver.cs)
が JWT から取り出す `clearance` / `department` の 2 キーを対象とする（実装の意味論に合わせる）。

### 出力

人が読める要約（既定）と `--json`（機械可読）。いずれも次を含む。

- 文書総数・利用者総数
- 3 粒度それぞれの組み合わせ数と、上位分布
- 利用者属性 × 文書属性の**直積**と、そのうち**ポリシーで到達可能な組**（`Policies` が 0 件なら deny-by-default で 0 と明記）
- 計画が必須とする文書属性のうち**実データに存在しないキー**の一覧（乖離の可視化）

### 再現性

- 乱数・現在時刻に依存しない（同一データなら同一出力）
- 読み取り専用（`SELECT` と Keycloak の GET のみ。書き込み・削除を行わない）
- 経路B が無い環境でも `ABAC_DOC_DSN` / `ABAC_KC_URL` で任意の環境へ向けられる

## 受け入れ基準

issue #456 の「受け入れの観点」を転記し、本作業で満たす条件を確定する。

- [x] 実測結果が数値（組み合わせ数・上位分布）で報告され、計画側 issue / feedback に記録されている
      （feedback/20260805_abac-attribute-combination-measurement-result.md（環流記録。計画リポ `projects/microservices-platform/10_feedback/20260805_abac-attribute-combination-measurement-result.md` へ移設））
- [x] 測定スクリプトが再実行可能な形でリポジトリに残っている（`scripts/measure-abac-combinations.js`）
- [x] 3 粒度（属性組み合わせ / ロール / 機密区分）をすべて数え、ADR-0035 の採用粒度（機密区分単位）と実測を突き合わせている
- [x] スクリプトが読み取り専用であり、実行してもデータを変更しない（`SELECT` と Keycloak の GET のみ）
- [x] 集計ロジックに単体テストがあり、`node scripts/scripts.test.js`（companion の `scripts.repo.test.js`）で走る
- [x] 計画が必須とする文書属性と実データの乖離が結果に含まれている（`department` / `owner` / `lifecycle` の不在）

## テスト方針

- **単体テスト**（`scripts/scripts.repo.test.js`）: 集計は純関数として切り出し、固定の入力データに対して
  3 粒度の数え方・ABAC 対象キーの決定順（辞書優先 → 計画既定）・対象外キーの分離を検証する。
  データ取得（`kubectl` / `psql` / Keycloak）は注入可能にし、テストからは実環境に触れない。
- **実機実行**: 稼働中の経路B に対して実行し、出力を本仕様書の §実測結果 に記録する。

## 計画書との差異

- 差異: あり（内容・対応: 計画 07_abac-attribute-model（計画リポ） は
  `confidentiality` / `department` / `owner` / `lifecycle` を**必須**の文書属性とするが、
  実機の `document_svc` には `confidentiality` しか存在しない。実装が計画の属性体系を満たしていない可能性がある。
  **本作業では是正せず**、測定結果として乖離を可視化し `/plan-feedback` と実装 issue で扱う）

## 未決事項

- 実測値が ADR-0035 の前提（機密区分 4 通り）と食い違った場合に、計画をどう扱うか（環流して計画側の判断を仰ぐ）
- 経路B の実データは取り込み経路（datasource / ingestion）由来であり、**本番相当の分布ではない**。
  この限界を結果に明記する（14_knowledge-graph-graphrag（計画リポ） §6 手順 1 は「実装リポジトリまたは本番相当データで」測ると定めている）

## 実測結果

**測定日**: 2026-08-05 ／ **対象**: 経路B ローカル k8s（Rancher Desktop 内蔵 k3s v1.35.4。
realm `microservices-platform`・`document_svc` 実データ）／ **コマンド**: `node scripts/measure-abac-combinations.js`

| 項目 | 実測値 |
| --- | --- |
| 文書数 | 2,368（属性・タグの異なり行 268） |
| 利用者数 | 4 |
| 属性辞書（`AttributeDefinitions`）の定義数 | **0** |
| ABAC ポリシー（`Policies`）の件数 | **0** |
| **粒度 1: 属性組み合わせ単位** | **1**（`confidentiality=internal` のみ・2,368 件すべて） |
| **粒度 2: ロール単位** | **4**（`platform-admin+platform-operator+trading-owner+wiki-editor` ／ 同 ＋`Administrators` ／ `platform-operator` ／ ロール無し 各 1 人） |
| **粒度 3: 機密区分単位** | **1**（設計上は 4 通り。`public` / `confidential` / `restricted` は実データに現れない） |
| 利用者属性（`clearance` × `department`）の組み合わせ | 3（`restricted/engineering` 2 人・`internal/engineering` 1 人・属性なし 1 人） |
| 到達可能な 利用者 × 文書 の組（read） | **0**（有効ポリシー 0 件＝deny-by-default） |

文書数は**測定時点の値**である（定期同期（#299）が動いているため増え続ける。同日の再実行では 2,428 件。
**組み合わせ数・分布は変わらない**——増えているのは同一属性の文書だからである）。

ABAC 対象キーの決定は**計画の文書基本属性へ縮退**した（属性辞書が空のため）。実データに現れる
`kind` / `source` / `symbol` / `publishedAt` / `periodKey` / `confirmedAt` / `assumptionsVersion` は
ABAC 対象外の実在キーとして分離した（これらを軸に数えると「組み合わせ数」がタイムスタンプの異なり数になる）。

### 実機での裏取り（BFF 経由の実挙動）

「到達可能 0」が机上の計算でなく実挙動であることを、実トークンで確認した。

1. Keycloak（realm `microservices-platform`・client `bff`）から `developer` の実トークンを取得。
   `iss=http://keycloak:8080/realms/microservices-platform`・**`clearance=restricted` / `department=engineering`
   のクレームが実際に載っている**ことを確認（プロトコルマッパーは機能している）。
2. そのトークンで `GET /bff/documents` → **`[]`（HTTP 200）**、`POST /bff/search` → **`{"results":[],"totalHits":0}`**。
3. 同時刻に `document-service` を直接叩くと文書は**返る**。

→ 空になっているのは文書が無いからではなく、**ABAC ポリシーが 1 件も無いため `BffScopeResolver` が
deny-by-default で縮退している**ためである。

### 考察（ADR-0035 との突き合わせ）

- ADR-0035 は要約の粒度を「**機密区分単位（4 通り）から始める**」と決定した。実測では機密区分は
  **1 通り**しか実在しないため、決定は**実測に対して安全側**である（粗い側から始める方針とも整合する）。
  費用試算（14_knowledge-graph-graphrag（計画リポ） §6 手順 2）の
  入力としては、**上限 4・実測 1** を用いればよい。
- ただし本実測は**取り込み経路（datasource / ingestion）由来のデータのみ**であり、
  人手で投入した組織文書を含まない。**本番相当の分布ではない**（§未決事項の限界がそのまま当てはまる）。
  属性の多様性が 1 通りに留まるのはデータ源が単一であることの反映であって、
  「本番でも 1 通りである」ことを意味しない。
- 計画が**必須**とする文書属性 `department` / `owner` / `lifecycle` は実データに 1 件も無い。
  `owner` は ADR-0036（計画リポ）
  の所有者ベース裁量制御の基礎であり、**現状の取り込み経路はその前提を満たしていない**。
- ABAC ポリシーが 0 件であること自体は仕様どおりの初期状態（ポリシーは SC-09 の管理画面から登録する）だが、
  **投入経路が実行されない限り検索・文書一覧が常に空になる**。実機の観測として別 issue で扱う。
