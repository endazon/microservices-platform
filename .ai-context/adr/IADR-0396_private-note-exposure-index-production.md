---
title: IADR-0396 露出 3 トグルは 1 つの純関数へ寄せ、生産側の門と消費側の評価が同じ述語を呼ぶ。個人資料は裁量の分岐でしか可視にしない
type: impl-adr
status: Accepted
related_ids:
  - FR-19
  - FR-20
  - FR-21
  - UC-11
  - SC-19
  - SC-20
  - ADR-0036
  - ADR-0046
  - ADR-0054
  - ADR-0057
  - ADR-0061
  - IADR-0122
  - IADR-0253
  - IADR-0270
  - IADR-0278
  - IADR-0283
  - IADR-0296
  - IADR-0358
  - IADR-0388
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - "planning:projects/microservices-platform/07_adr/ADR-0061 決定 1〜6（露出 3 トグルの索引への載せ方）"
  - "planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-19 / FR-21 受け入れ基準 ⑨)"
  - "planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md (D-05 / D-06)"
related_specs:
  - ../specs/20260905_issue-1184_private-note-exposure-index-production.md
---

# IADR-0396: 露出 3 トグルの索引生産側への配線と、個人資料の可視性の閉じ方

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装判断）／起点 issue #1184

## 起点・関連

- 関連する計画書 ID: `FR-19`（露出 3 トグル・既定 OFF）/ `FR-20`（共有）/ `FR-21` 受け入れ基準 ⑨ /
  `UC-11` / `SC-19` / `SC-20` / `ADR-0061`（本件の裁定）/ `ADR-0036` D-05・D-06 / `ADR-0054` /
  `ADR-0057` 決定 1
- 関連する実装 ADR:
  - [IADR-0270](IADR-0270_private-note-obsidian-sync-backend-core.md) 決定 5 —— 「個人資料は
    `DocumentUpdated` を発行しない」。**本 ADR がその後継である**（旧 ID は残し、後継を併記する）
  - [IADR-0283](IADR-0283_rag-context-ai-input-exposure.md) —— `ai_input` の写しと RAG 経路の分離
  - [IADR-0253](IADR-0253_authz-scope-disjunction-contract.md) —— 認可スコープの選言（段 3・段 4）
  - [IADR-0122](IADR-0122_contract-schema-source-and-compat-gate.md) 決定 2 —— 契約の非破壊追加条件
- 関連する実装仕様書:
  [`20260905_issue-1184_private-note-exposure-index-production.md`](../specs/20260905_issue-1184_private-note-exposure-index-production.md)
  （母集合・実測の正本）

## コンテキストと課題

計画は `ADR-0061`（planning#492）で 6 点を裁定した。要点は「**1 つでも ON なら索引へ載せる／
3 つとも OFF なら載せない／用途の別は索引を分けずに文書属性で表す／ON → OFF は索引からの削除まで
及ぶ／判定軸は `doc_scope` / `owner` / `shared_with` / `confidentiality` / 露出の投影／
🔴 `confidentiality` だけで判定してはならない**」である。

実装の側の実測（作業仕様書 §1。`origin/develop` = `facebfe9`・`--is-shallow-repository` → `false`）:

| 段 | 実測 |
| --- | --- |
| 台帳 | `PrivateNote` が 3 トグルを保持する（既定 OFF） |
| 文書属性 | **`ai_input` だけ**が写る（[[IADR-0283]] 決定 4）。横断検索・グラフの綴りは **0 件** |
| 発行 | `/private-notes` 系は `DocumentUpdated` を**発行しない**（[[IADR-0270]] 決定 5） |
| 索引 | したがって個人資料は Qdrant に**存在しない**（稼働クラスタで実測。下記 §結果） |
| 判定軸 | `doc_scope` / `owner` は属性辞書で索引へ届く。**`shared_with` は届く手段が無い** |
| 消費側 | RAG は `ai_input` を見る。**検索・グラフは露出を見る口が無い** |

**したがって決めるべきは 7 点である。**

## 検討した選択肢と決定

### 決定 1: 露出の語彙は `search_exposure` / `graph_exposure` / `ai_input`。値は `included` / `excluded`

| # | 案 | 評価 |
| --- | --- | --- |
| **1-A** | **`AiInputExposure` を一般化した `DocumentExposure` を新設し、キーを 3 本持つ**（採用） | 値域・判定表・fail-closed の向きが 1 か所に揃う。軸を足すのが 1 行になる |
| 1-B | `AiInputExposure` と同型の型を 2 つ足す | 判定表が 3 つに複製される。**綴りだけ直して判定を直し忘れる**形が作れてしまう |
| 1-C | 3 値を 1 つの属性へ詰める（`exposure="search,graph"`） | 属性は単一文字列で集合を持てない。部分一致の絞り込みは `AttributeFilter`（完全一致）で書けない |

🔴 **`ai_input` は改名しない。** 3 者で語尾が揃わないのは承知のうえである ——
既に作成済みの個人資料の `Document.Attributes` に書かれた値であり（稼働クラスタに 4 件）、
改名はデータ移行を伴う。**計画の順序（属性への投影 → 索引 → 消費側 → 決定 5 の解除）に
移行の段は無い。** 非対称は `DocumentExposure` の**キー定数 3 行の中だけ**に閉じ、
外から見える形（`IsSearchAllowed` / `IsGraphAllowed` / `IsAiAllowed`）は対称である。

**否定形の名前を新たに持ち込まない**（`included` / `excluded` は既存の 2 値をそのまま使う）。
#1253 / #1254 が `bodyAbsent` → `hasBody` で寄せた向きと同じである（[[IADR-0388]]）。

### 決定 2: `AiInputExposure` は**残し、委譲する別名**にする

判定の実体を `DocumentExposure` へ移し、`AiInputExposure` は定数と `IsAllowed` を委譲するだけにした。
**削除して呼び出し面を一斉に書き換える案は採らない** —— 既存テスト（`AiInputExposureTests`）が
`ai_input` の判定表を固定しており、**別名のまま残せばそれが回帰試験としてそのまま働く**。

🔴 **別名は「述語の写し」ではない。** 委譲であることを `DocumentExposureTests` が
（`AiInputExposure.IsAllowed(x) == DocumentExposure.IsAiAllowed(x)`）機械で固定する。

🔴 **ただし定数（`AttributeKey` / `Included` / `Excluded`）はリテラルのまま複製した。**
一度は `DocumentExposure` の定数を参照する形にしたが、契約 baseline（`check-contract-schema`）は
**const の初期化式の字面**を比較するため、値が 1 バイトも変わらないのに
`constValueChanged`（breaking）3 件として検出され、`contract-breaking-allowlist.json` の
承認が要る状態になった（実測）。**判定を 1 つに寄せる目的は述語の側で達しており、
定数の複製は「値の一致をテストで固定する」形で閉じる**（[[IADR-0270]] 決定 6 が
`NotificationKinds` で採ったのと同じ判断。`DocumentExposureTests` が 4 つの値を assert する）。

### 決定 3: `shared_with` はイベントの独立項目で運び、索引には `tags` と同じリスト項目で載せる

| # | 案 | 評価 |
| --- | --- | --- |
| **3-A** | **`DocumentUpdated` へ `List<string>? SharedWith = null` を末尾・既定値付きで足し、ペイロードは最上位のリスト項目 `shared_with`**（採用） | 集合を集合のまま運べる。`Match.Keywords` が `tags` と同じ「いずれか一致」で通り、**Qdrant 側の条件生成は 1 行も増えない**（`AttributeValueKeys.ToPayloadKey` の写像だけ） |
| 3-B | 属性辞書へ `shared_with="alice,bob"` と詰める | 単一値。部分一致の絞り込みが `AttributeFilter`（完全一致）で書けない |
| 3-C | 消費側が `DocumentShare` を都度引く | サービス境界を跨ぐ同期呼び出しを検索の hot path へ足す（[[IADR-0153]] が禁じた向き） |

**解決は `DocumentEndpoints.PublishUpdatedAsync` の 1 か所で行う。**
呼び出し側に共有先を渡させると、「載せる経路」と「載せない経路」に割れて
**索引の中の判定軸が経路ごとに違うもの**になる（識別子 → 表示名の変換点を 1 つに保つのと同じ理由）。

**共有の付与・取り消しは再発行の契機である。** 索引が運ぶのは発行時点の写しなので、
再発行しないと付与は「共有した相手に永久に見えない」、取り消しは
「**取り消した相手に見え続ける**」（漏れる向き）になる。

### 決定 4: 🔴 判定軸は 1 つの純関数に寄せ、**生産側の門と消費側の評価が同じ関数を呼ぶ**

`DocumentExposure.IsIndexable` は**定義そのものが 3 軸の選言**である。

| 呼ぶ側 | 関数 | 役割 |
| --- | --- | --- |
| DocumentService（発行の門） | `IsIndexable` | 1 つでも ON のときだけ `DocumentUpdated` を出す（`PublishUpdatedIfIndexableAsync`。**発行する本番経路はすべてこれを通る**） |
| IngestionService（索引の門） | `IsIndexable` | 偽なら**索引から削除**して抜ける |
| RetrievalService | `IsSearchAllowed` | 検索結果から落とす（`HybridSearchService.Finish` の 1 点） |
| GraphService | `IsGraphAllowed` | ノードを作らない・消す（同期）／出力から落とす（`Seal`） |
| AiAnalysisService | `IsAiAllowed` | RAG 文脈から落とす（既存配線のまま） |

**なぜ生産側と消費側の両方に門を置くのか（多層防御）。** 生産側だけだと、
過去に索引された点や別経路で入った点が残る。消費側だけだと、
**索引に本文が残ったままフィルタに頼る**ことになり、`ADR-0061` 決定 4 が禁じた形になる。
**同じ関数を呼ぶ限り、二重化は判定の分裂を生まない。**

**組織文書は全キーが欠落するため 3 軸とも true** であり、既存経路の挙動は 1 ビットも変わらない
（回帰は陽性対照テストで対にして固定した）。

🔴 **門は「一部の経路だけ」に付けない。** 当初は露出を触る 4 経路（`SetExposure` / 共有の付与・取り消し /
Obsidian push）だけを門付きにし、`/documents/*` の 7 経路は無条件の発行のままにしていた。
消費側の門があるため実害は無かったが、**「発行の門がある」という説明とコードの実態が食い違う**
（PR #1281 のレビュー指摘）。後から経路を足した人がどちらの作法に倣えばよいか判らなくなるため、
`DocumentUpdated` を出す本番経路をすべて `PublishUpdatedIfIndexableAsync` へ寄せた。

### 決定 5: ON → OFF の撤収は**削除**で行う。撤収の契機は `DocumentUpdated` の再発行である

- `SetExposure` は「**今 ON**」または「**さっきまで ON だった**」ときに発行する。
  全 OFF のまま全 OFF を保存した場合は**何も出さない**（索引に存在しない状態をそのまま保つ）。
- 受け手は同じ述語で判定し、`IngestionService` は `DeleteByDocumentFromAllAsync`、
  `GraphService` はノードと端点の辺を削除する。

🔴 **`DocumentDeleted` を流用しない。** 文書は生きている。流用すると
却下済み AI 提案（`ADR-0033` 決定 10 が原則永久保持と定めたもの）とリンク先の名前まで消え、
**露出を戻した瞬間に却下したはずの提案が全部よみがえる**。撤収の射程は
「グラフに出さないために要る最小」＝ノードと辺である。

### 決定 6: 「用途の別」は消費側の**単一の funnel** で評価する

- 検索: `HybridSearchService.Finish` —— `SearchAsync` と `GraphExpandingSearchService` の
  3 つの return が**すべてここを通る**。経路ごとに書くと、段を足した人が落としても誰も気づかない。
  **切り詰め（`topK`）より前に落とす**（後だと除外した分だけ結果が減る）。
- グラフ: `AuthorizedGraphView.Seal` —— 出力の唯一の構築経路（[[IADR-0242]] 決定 2 の型ゲート）。

これにより「グラフ用途だけで索引に載った個人資料」は**索引には在るが横断検索には出ない**
（`ADR-0061` 決定 3「用途の別は索引を分けずに文書属性で表す」の実装形）。

### 決定 7: 🔴 個人資料を許可してよいのは**裁量（`owner` / `shared_with`）の分岐だけ**である

**これが `ADR-0061` 決定 6（`confidentiality` だけで判定してはならない）の実装形である。**

認可スコープの分岐（[[IADR-0253]] 決定 1）は**管理者が定義したポリシー 1 件 = 1 分岐**であり、
計画 `read` 規則の第 1 節「静的属性ベース」（例 `confidentiality ∈ {restricted}`）は
**文書の種別を問わない**。露出 ON の個人資料は、この分岐経由で
**`restricted` クリアランスを持つ他人に見える**。

これを運用（ポリシーの書き方）で守るのは**構造的に不可能**である。

- 個人資料を外すには静的分岐へ `doc_scope ∈ {organization}` を足すことになるが、
  **既存文書は `doc_scope` を持たない**（`ADR-0054` §結果: 遡及付与しない）ので、
  足した瞬間に**既存の組織文書が全部見えなくなる**。
- 足さなければ、露出 ON の個人資料が漏れる。

**どちらの向きにも壊れる**ため、実装の構造で閉じた。規則は 1 つ:

> `doc_scope == private-note` の資料を許可してよいのは、`owner` または `shared_with` を
> 条件に持つ分岐だけである。**条件を 1 つも持たない分岐（全件許可）は裁量ではない。**

これは `ADR-0036` D-05・D-06 の言い換えであり、新しい認可規則ではない。
判定は `PrivateNoteVisibility` の 1 か所に置き、消費 3 面（`InMemoryVectorStore` /
`QdrantVectorStore` / `AbacNodeFilter`）が同じ述語を呼ぶ。

**Qdrant 側の表現は否定条件 1 つ**（`doc_scope` が `private-note` である点を除く）である。
🔴 **`doc_scope != organization` と書いてはならない** —— キーを持たない既存の組織文書が全部落ちる。
**「`private-note` である点を除く」だけが、欠落を組織文書として残す**（[[IADR-0270]] 決定 2 の作法）。

**副作用（意図的）**: 条件を持たない分岐は「全件許可」から「**個人資料を除く全件許可**」になった。
既存の現状固定テスト（`QdrantMapping_BranchWithNoFilters_DropsTheDisjunction`）を
書き換えて新しい意味論を固定した（削除していない）。

## 理由

- **決定 1・2・4** は「判定規則の真実源を 1 か所へ保つ」ため。`ConfidentialityLevels` /
  `AiInputExposure` が採ったのと同型で、**供給側と消費側が別々に綴りを解釈する余地を残さない**。
- **決定 3** は計画が `shared_with` を「集合」と定義していることに忠実であり、
  かつ `tags` という**既に在るリスト項目の意味論**へ相乗りして写像の分岐を増やさない。
- **決定 5** は `ADR-0057` 決定 1・SC-19 の固定文言と同じ理由による ——
  残った本文はフィルタの実装ミス 1 つで露出に変わる。
- **決定 7** は計画の 🔴 を「気をつける」ではなく**型と述語**で閉じたものである。

## 結果

- **良い影響**:
  - `ADR-0061` 決定 1〜6 が実装され、受け入れ基準 8 項目がテストで固定される
  - `IADR-0253` 段 4（共有先ベースの分岐）が**索引の側で成立する** ——
    `DocumentShareEndpoints` が「別段とする」と留保していた配線が閉じた
  - `FR-21` 受け入れ基準 ⑨ が**経路として**成立する（従前は索引に 1 件も無く、成立し得なかった）
- **悪い影響・トレードオフ**:
  - 🔴 **露出属性の綴りが 3 者で対称でない**（`ai_input` だけ語尾が違う）。非対称は
    `DocumentExposure` のキー定数 3 行に閉じているが、**新しい軸を足す人は `AllKeys` を見ること**
  - 条件を持たない分岐の意味が変わった（決定 7 の副作用）。**緩む向きではない**
  - `DocumentUpdated` の発行ごとに共有先を 1 クエリ引く。タグ改名の一括再発行では文書数ぶん増える
    （管理者の稀な操作であり、まとめ読みは行っていない）
  - 🔴 **露出トグルの画面は既に在る**（SC-19 の一覧の「露出」列 ——
    `PrivateNotesPage.tsx` が 3 つのチェックを持ち BFF の端点を呼ぶ）。したがって本 ADR の配線は
    **着地と同時に利用者の手に届く**。「口が無いから当面は影響が無い」ではない
- **未実測**:
  - **稼働クラスタでの端点実行（露出 ON → 索引に載る）は行っていない。** 測ったのは
    「現在の索引に個人資料が 0 件であること」と「露出 ON の個人資料が 0 件であること」の 2 つである
    （下記）。**画面は在るので実行はできたが、実データを変える操作になるため行わなかった**
  - 実 Qdrant に対する `must_not` の挙動は**単体テストの写像固定のみ**であり、実機未確認である
    （`IngestToSearchQdrantTests` は Docker が要る。作業環境では 26 件 skip）

### 実データの確認（`ADR-0061` の指示「既存の個人資料を遡って索引へ入れるかは実データで確認してから決める」）

稼働 k3s（`microservices-platform` / `platform-infra`）で実測した。

```console
$ kubectl exec -n platform-infra postgres-... -- psql -U postgres -d document_svc -c \
  'select count(*) as private_notes,
          count(*) filter (where "IncludeInSearch" or "IncludeInGraph" or "IncludeInAi") as any_toggle_on
   from "PrivateNotes";'
 private_notes | any_toggle_on
---------------+---------------
             4 |             0

$ curl .../collections/knowledge_chunks_deterministic_v1/points/count -d '{"exact":true}'
{"result":{"count":6}}
$ curl ... -d '{"exact":true,"filter":{"must":[{"key":"attributes.doc_scope","match":{"value":"private-note"}}]}}'
{"result":{"count":0}}
$ curl ... -d '{"exact":true,"filter":{"must":[{"key":"attributes.confidentiality","match":{"value":"public"}}]}}'
{"result":{"count":6}}    ← 陽性対照（フィルタの経路は生きている）
```

**結論: 遡及索引の対象は 0 件である。** 露出 ON の個人資料が 1 件も無いのだから、
`ADR-0061` 決定 1 に従って載せるべきものが無い。**backfill は書かない。**

🔴 **陽性対照を対で置いた理由**: 最初に `attributes.confidentiality = internal` で数えたら 0 件
であり、そのままなら「フィルタが効いている」と誤読するところだった（実データは全件 `public`）。
**陰性の結論には陽性対照を対で置く。**

## 関連

- Supersedes: なし（[[IADR-0270]] は Superseded にしない —— 決定 5 以外は現行である。
  同 ADR 本文は当時の記録として書き換えず、日付つき追記で本 ADR を併記した）
- Superseded by: なし
- 実装 issue: **#1184（本 ADR を起こした issue）** / #451（FR-19 本体）/ #989（[[IADR-0253]]）/
  #447（[[IADR-0283]]）/ #1187（タグ承認）/ #1193（本文なし文書の索引）/ #1253・#1254（語彙統一）
- 裁定: planning#492（CLOSED / COMPLETED → 計画 `ADR-0061`）
