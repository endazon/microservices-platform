---
title: 作業仕様書 — 決定的ローカル埋め込みで「検索が実際に効くこと」を統合スタックの門にする（#992 / #466）
type: spec
status: done
related_ids:
  - FR-02
  - FR-03
  - FR-05
  - FR-21
  - UC-01
  - SC-01
  - SC-02
  - NFR
  - ADR-0016
  - ADR-0017
  - IADR-0025
  - IADR-0085
  - IADR-0252
  - IADR-0255
  - IADR-0256
  - IADR-0284
  - IADR-0313
  - IADR-0314
  - IADR-0315
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-model-routing.md
  - planning:projects/microservices-platform/07_adr/ADR-0017_embedding-model-selection.md
related_specs:
  - 20260828_issue-992_search-observability.md
  - 20260822_issue-466_edge-smoke-and-step-loss-gate.md
issue: "#992 #466"
---

# 作業仕様書 — 決定的ローカル埋め込みで検索の命中を門にする（#992 / #466）

## 目的と射程

**「投入経路を使って既知の文書を入れ、既知の語で `POST /bff/search` が必ず 1 件以上返すこと」を
統合スタック（nightly）の門にする。** `200 ＋ 空` を緑と読ませない。

射程は **#992 案 2（埋め込みを CI で供給する）と、その結果としての `SEARCH_HITS=1` の CI 搭載**である。
案 1（seed）は [[IADR-0284]] で着地済み、判定の段（S3）も `verify-oidc-edge-flow.sh` に実装済みで、
**残っていたのは埋め込みの供給だけ**である（同 IADR フォローアップ 1）。

射程外:

- **案 3**（`POST /bff/search` の応答で縮退を区別可能にする）。下記「決めたこと 1」で不採用にした
- Qdrant の全文インデックス欠落（下記「実測で見つけた別の欠陥」）。別 issue へ回す
- ティアA セルフホスト（TEI / Ruri v3）の実配備。`embedding.enabled` の既存 opt-in は 1 バイトも触らない

## 母集合（自分で引いた・[[IADR-0141]] 決定 1 / 規則 9・10）

**誤りの側**（この変更で新たに偽になる自分の記述）と**あり得る形**を列挙してから、
**拡張子で絞らず・行フィルタで絞らず**、追跡下の全ファイルをパスから引いた
（除外は `src/ai-stock-trading`＝別リポの submodule のみ）。

```
git grep -l -- "<語>" | grep -v "^src/ai-stock-trading"
```

| 軸 | 語 | 出た件数 | 扱い |
| --- | --- | --- | --- |
| 1 | `SEARCH_HITS` | 6 | 判定側。CI 搭載で記述が変わるのは workflow / IADR-0284 / 本 spec |
| 2 | `IADR-0284` | 16 | 「案 2 の裁定待ち」を書いているものだけが対象 |
| 3 | `埋め込みの供給` | 5 | 同上（保留の理由を書いた箇所） |
| 4 | `Endpoints__1` | 8 | **配列 index の前提**。index 2 を足すので前提が増える |
| 5 | `knowledge_chunks_` | 18 | コレクション名の集合。新コレクションを足す |
| 6 | `ExpectedProviderByTier` | 1 | ティア↔プロバイダの検証。ティアA を集合へ広げる |
| 7 | `selfhosted-embedding` | 13 | ティアA の唯一のプロバイダという前提 |

**実際に直す集合**（上の和集合から「凍結記録」と「無関係」を除いたもの）:

- `src/platform/backend/Services/LlmGateway/`（provider / validator / appsettings / Program / Tests）
- `src/knowledge/backend/Services/IngestionService/`（テストは新規追加のみ。appsettings は触らない）
- `deploy/helm/microservices-platform/`（values.yaml / templates/deployment.yaml）
- `scripts/k8s-local-up.sh` ＋ `scripts/k8s-local-up.test.js`
- `.github/workflows/integration-stack.yml`
- `docs/operations/operations.md`（ティア表と index の注記）
- `.ai-context/adr/IADR-0284*`（フォローアップ 1 の消化を追記）

**除外したものと理由**（黙って落とさない）:

| 除外 | 理由 |
| --- | --- |
| `.ai-context/specs/2026*`（既存 5 件）・`.ai-context/superpowers/` | **凍結記録**。確定済みの作業仕様書の本文は書き換えない（`traceability.repo.md`「凍結の射程」）。`IADR-0284` だけは live な決定記録なのでフォローアップの消化を追記する |
| `.ai-context/adr/IADR-0025` / `IADR-0085` / `IADR-0294` | 既に確定した決定であり、本変更はそれらを覆さない（ティアA の**追加**であって置換ではない） |
| `deploy/docker-compose.yml` の `Endpoints__1` | compose 経路は本件の対象外（統合スタックは k8s）。**index を動かさない**ので現行のまま正しい |
| `docs/api/openapi.yaml` | **生成物**。BFF の契約を変えないので再生成差分も出ない |
| `src/knowledge/backend/Services/RetrievalService/appsettings.json` | 既定のコレクションは voyage のまま据え置く。切替は**配備の上書き**で行う（本番像を dev のために動かさない） |
| `src/.../IngestionService/appsettings.json` | 同上。stub のコレクションを本番像の常設一覧へ入れない |

## 実測（着手前・稼働 k3s / Rancher Desktop v1.35.4+k3s1）

`bash scripts/verify-oidc-edge-flow.sh`（既定モード）を稼働クラスタへ当てた結果、
**issue の主張どおり「検索が壊れていても緑になる」形が再現した**。

```
[11/11] 認証ありで検索を叩く（200 かつ SearchResponse の形であること）
  PASS  POST /bff/search（認証あり）→ 200・契約どおりの形（results 0 件）
        🔴 本段は件数を判定に使っていない。件数の判定は SEARCH_HITS=1 の段が行う（#992）。
```

（同じ実行で段 7〜10 に 4 件の FAIL が出るが、**これは稼働クラスタが現行 develop より古いイメージで
動いている乖離**であり本件とは別である。母集合の確認のためにそのまま記録する。）

## 実測で見つけた別の欠陥（本作業の射程外・別 issue へ）

🔴 **Qdrant の全文（text）ペイロードインデックスを、どのコードも一度も作っていない。**

- `QdrantIngestionVectorStore.EnsureCollectionsAsync` は `CreateCollectionAsync`（VectorParams）だけを呼ぶ
- リポジトリ全体で `CreateFieldIndex` / `TextIndexParams` は **0 件**（実測）
- `QdrantVectorStore.KeywordSearchAsync` の `Match { Text = query }` はインデックス未作成だと
  `RpcException` になり、`catch` が **警告ログ 1 行を残して空リストへ縮退**する

したがって**ハイブリッド検索の全文側は、実配備では常に 0 件**である。検索はベクトル側だけで
成立している。本作業の門はベクトル側で命中するので成立するが、**「全文へ振り替えれば埋め込み
無しでも測れる」という道は原理的に無い**（[[IADR-0284]] 決定 4 の判断が、想定と別の理由で正しかった）。

## 決めたこと

判断の記録は [IADR-0313](../adr/IADR-0313_deterministic-local-embedding-for-search-gate.md)。要点:

1. **穴 1（`200 ＋ 空` が 3 つの失敗と区別できない）は「検証の仕方」で塞ぐ。契約は変えない。**
   応答へ縮退の別を載せると存在秘匿（[[IADR-0009]]）が崩れ、`docs/api/openapi.yaml`（生成物）と
   orval 生成物・フロントまで巻き込む。**既知の文書 ＋ 既知の語で ≥1 件**という正の対照を置けば、
   3 経路のどれが縮退しても 0 件になり門が落ちる（[[IADR-0252]] と同じ型）。
2. **穴 2（索引に何も入らない）は #992 案 2 ＝「決定的なローカル埋め込み」で塞ぐ。**
   LlmGateway に **ティアA（社外送信なし）の決定的プロバイダ**を足し、**既定無効**にする。
3. **越境判定（`EmbeddingEgress` / `EmbeddingRouter.Route`）は 1 バイトも触らない。**
   触るのは「ティアA に置けるプロバイダは 1 つだけ」という**検証器の前提**だけである。
4. 有効化は **`LOCALEMBED=1`**（`ABACSEED` / `SEARCHSEED` と同型の opt-in・既定オフ）。
5. CI（nightly）に `LOCALEMBED=1` と `SEARCH_HITS=1` を載せる。

## 変更対象

| ファイル | 変更 |
| --- | --- |
| `LlmGateway/Infrastructure/ExternalServices/DeterministicEmbeddingProvider.cs` | 新規。文字 3-gram ＋ FNV-1a のハッシングベクトル（L2 正規化） |
| `LlmGateway/Domain/Routing/EmbeddingRoutingOptionsValidator.cs` | ティア↔プロバイダを 1 対 1 から**1 対多**へ |
| `LlmGateway/appsettings.json` | `Embedding:Routing:Endpoints[2]` に `deterministic-local`（`Enabled: false`） |
| `LlmGateway/Program.cs` | keyed 登録 ＋ **有効時の起動警告** |
| `LlmGateway/Tests/DeterministicEmbeddingProviderTests.cs` | 新規 |
| 既存 LlmGateway テスト | 端点数・検証器の期待の追随 |
| `deploy/helm/.../values.yaml` `templates/deployment.yaml` | `embedding.deterministicLocal`（既定 false）と 3 サービスへの env 注入 |
| `scripts/k8s-local-up.sh` | `LOCALEMBED=1` で `--set` を足す（未設定ならバイト等価） |
| `scripts/k8s-local-up.test.js` | opt-in トークンの検出力・既定不在・チャートとの整合 |
| `.github/workflows/integration-stack.yml` | `LOCALEMBED=1` ＋ `SEARCH_HITS=1` |
| `docs/operations/operations.md` | ティア表へ 1 行・配列 index の注記 |

## 受け入れ基準

1. `LOCALEMBED` 未設定なら `helm upgrade` の引数が 1 バイトも変わらない（既定バイト等価）
2. `embedding.deterministicLocal.enabled=false`（既定）で `helm template` の出力が現状と等価
3. `Embedding:Routing:Endpoints[2].Enabled` は appsettings で `false`
4. 決定的プロバイダは**同じ入力に対し常に同じベクトル**を返す（プロセス跨ぎ・実行跨ぎ）
5. 次元がルーターの決定と一致する（不一致は `/embed` が fail-closed する）
6. 稼働クラスタで **seed 文書が `POST /bff/search` で 1 件以上返る**ことを実測する
7. 変異試験: 埋め込みを外す／索引を空にすると門が落ちることを実測する
8. `EmbeddingEgress.AllowedTiers` の**振る舞いが変わっていない**ことをテストで固定する

## 未決事項・既知の限界（隠さない）

- **本門は「語の関連性」を測らない。** ベクトル検索は閾値を持たない kNN であり、
  無関係な語でも最近傍が返る。門が測るのは
  **「索引に入っている・ABAC を通る・埋め込みが供給される・後段へ到達できる」**の 4 点である。
- **決定的ハッシュ埋め込みに意味的な近さは無い**（表層の 3-gram の重なりだけ）。
  **検索品質の評価には使えない。** nDCG の実測は従来どおり稼働環境（実モデル）依存である。
- 稼働クラスタは develop より古い（realm 名・無認証読み取り）。**本作業で再構築して実測する。**


## ［2026-08-30 追記 / #992］射程が広がった —— 途中で 2 つの欠陥を実測で掘り当てた

**当初の射程（埋め込みの供給）だけでは受け入れ基準を満たせなかった。**
稼働 k3s へ当てて初めて、`#992` 理由 2「そもそも索引に何も入らない」が
**issue が書いた理由（`MarkdownUri`）とは別の理由でも真だった**ことが分かった。

| # | 掘り当てた欠陥 | 記録 |
| --- | --- | --- |
| A | **Wolverine の発行経路が本番コードに一度も無く、イベントがどの配備でも 1 通も出ていなかった** | [IADR-0314](../adr/IADR-0314_wolverine-outbound-routing-and-queue-binding.md) |
| B | **Qdrant サーバ（1.9.2）とクライアント（1.18.1）の版が食い違い、1024 次元を「次元 0」で拒否していた** | [IADR-0315](../adr/IADR-0315_qdrant-server-version-follows-client.md) |

**どちらも「取り込みが一度も走っていなかった」ために露出していなかった。**
A を直すと B が現れ、B を直して初めて索引に点が入った。
**この順序でしか見つからない**（静的に読んでも、A の下流にある B は決して現れない）。

射程を広げた判断の根拠: **どちらも受け入れ基準（既知の語で ≥1 件返る）の必要条件**であり、
これを分けると「門は入ったが毎晩赤い」状態を作ることになる（[[IADR-0284]] 決定 6 が
まさにそれを避けようとした形である）。

## 実測（稼働 k3s / Rancher Desktop v1.35.4+k3s1）

### 基準（変異なし）—— CI が実行するのと同じコマンド

```
$ ABAC_POSITIVE=1 SEARCH_HITS=1 bash scripts/verify-oidc-edge-flow.sh
...
[18/20] seed 文書が一覧に見え、本文の参照（markdownUri）を持つこと
  PASS  seed 文書が一覧に在り markdownUri を持つ（取り込みの入口条件を満たしている）
[19/20] 属性を持たない利用者（poc-operator）の検索が 0 件であること（全開放を検出する）
  PASS  POST /bff/search（poc-operator）→ 200・0 件（deny-by-default が効いている）
[20/20] seed 文書の合言葉で検索してヒットすること（0 件を PASS にしない）
  PASS  seed 文書がヒットした（3 件・合言葉 msp-searchseed-tanpopo を含む）
結果: PASS 27 / FAIL 0（段 20/20）
```

取り込みの証跡:

```
Ingestion complete for ce6e5f5f-a550-4499-a0cb-81596ae42f95: 3 chunks
GET /collections/knowledge_chunks_deterministic_v1 -> points=3
```

**この 3 点は、本作業の前は 0 点だった**（同じ経路・同じ seed で）。

### 変異試験

| 変異 | 手段 | 結果 |
| --- | --- | --- |
| **M2** 検索が別のコレクションを見る | `retrieval-service` の `Qdrant__CollectionName` を voyage 側へ | ✅ **段 20 が FAIL**（`ヒットしない（0 件）`）。他の段は基準どおり |
| **M-a** 発行側の経路宣言を外す | `RoutePlatformEvent<DocumentUpdated>()` を削除 | ✅ `check-event-topology.js` が検出 |
| **M-b** 購読側の束ねを外す | `BindPlatformQueue<DocumentUpdated>(...)` を削除 | ✅ 同上 |
| 対照 基準 | — | ✅ PASS 27 / FAIL 0 |

**M3（BFF → RetrievalService 不達）は実施できなかった。** BFF の readiness が
`UriHealthCheck` で後段の到達性を見ているため、**不達にすると pod が Ready にならず、
門より前（`check-stack-ready.js`）で止まる**。これは弱点ではなく、多層で捕まえている形である。

### 測れなかったもの（隠さない）

| 測れなかったもの | 理由 |
| --- | --- |
| `Knowledge.IntegrationTests` の 41 件 | Testcontainers が Docker API を要し、containerd（Rancher Desktop）では **skip のまま緑**になる。判定は CI に委ねる |
| `node scripts/check-deploy-manifests.js` | ツール検出が `command -v` をシェル越しに呼ぶため **Windows では常にツール不在**になる。代わりに `helm template … \| kubeconform -strict` を手で実行し **40 resources / Valid 40 / Invalid 0** を確認した |
| 統合スタックの CI 実走（`integration-stack.yml`） | 本 PR がマージされ nightly が回るまで測れない |
