---
title: 作業仕様書 — Qdrant の全文ペイロードインデックスを作り、ハイブリッド検索のキーワード側を実際に効かせる（#1116）
type: spec
status: done
related_ids:
  - FR-02
  - FR-03
  - FR-05
  - UC-01
  - SC-01
  - SC-02
  - NFR
  - NFR-08
  - ADR-0009
  - ADR-0016
  - IADR-0014
  - IADR-0151
  - IADR-0252
  - IADR-0255
  - IADR-0256
  - IADR-0313
  - IADR-0315
  - IADR-0316
author: claude
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0009_vector-db-qdrant.md
---

# 作業仕様書: Qdrant 全文ペイロードインデックス（#1116）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-03**「キーワードと自然文の双方で横断検索できる（ベクトル検索＋全文検索のハイブリッド）。
  利用者は検索モードを 3 値（ハイブリッド〔既定〕／キーワード／意味）から選べ…」
- ユースケース（UC）: UC-01（横断検索）
- 画面（SC）: SC-01（検索窓・モード選択）／SC-02（結果一覧）
- 非機能: NFR-08（文書 数万〜数十万件）・NFR-01/NFR-04（検索 p95）・NFR-06（縮退運転）
- 関連 ADR: ADR-0009（ベクトルDB = Qdrant）／ADR-0016（埋め込みのモデル別コレクション）
- 実装 ADR: [[IADR-0014]]（ペイロードキーの表現）／[[IADR-0151]]（facet と payload index の未整備）／
  [[IADR-0252]]（「200 ＋ 空リスト」を PASS にしない・正負の対照を対で置く）／
  [[IADR-0255]]（`200 ＋ 空` が 3 つの失敗と区別できない）／[[IADR-0256]]（縮退と故障の切り分け）／
  [[IADR-0313]]（決定的ローカル埋め込みと門）／[[IADR-0315]]（Qdrant サーバ版はクライアントへ揃える）
- 本作業の実装 ADR: **[[IADR-0316]]**（`develop` の最大値 +1。並行 PR と衝突した場合はマージ直前に改番する）

## 目的・背景

FR-03 の言う「全文検索の側」が、どの配備でも成立していない。
`QdrantVectorStore.KeywordSearchAsync` はペイロード `text` への full-text `Match` で `ScrollAsync` するが、
**`text` に full-text ペイロードインデックスを作るコードがリポジトリのどこにも無い**（母集合 軸1 で実測。
本文のヒットは記録の記述だけで、呼び出しは 0 件）。

## 🔴 起票時の記述と実測が食い違った点（鵜呑みにしない）

issue #1116 は「`catch (RpcException)` が**常に**成立し、`KeywordSearchAsync` は**常に空配列を返す**」と書く。
**稼働 Qdrant v1.18.1（[[IADR-0315]] で 1.9.2 から上げた版）へ実際に当てたところ、これは成立しない。**

`Match { Text = q }` はインデックスが無くても **例外にならず、部分文字列の全走査へ黙って落ちる**
（実測は §実測 1）。つまり欠陥は「常に 0 件」ではなく、**「全文検索でないものが全文検索の顔をして動いている」**である。

| 索引なしの実際の挙動 | なぜ悪いか |
| --- | --- |
| `anpop` が `msp-searchseed-tanpopo` に当たる | **語でない断片が当たる**（偽陽性） |
| `検索 tanpopo` が 0 件・`tanpopo 検索` が 1 件 | **語順に依存する**（＝トークン集合の一致ではない。全文検索の意味論ではない） |
| 全点走査 | NFR-08（数万〜数十万件）で p95 目標（NFR-01/04）を保てない |

**起票の結論（FR-03 のキーワード側が成立していない）は変わらない。機構の記述だけが版に追随していない。**
`RpcException` へ倒れる記述は **v1.9.2 時代の観測**であり、[[IADR-0313]] §コンテキストの記述も同じ前提に立つ。

## 母集合の引き直し（着手時に自分で引いた）

`.claude/rules/traceability.repo.md` §是正・追随の母集合の取り方 に従い、**issue 本文の「反映先」を転記せず**、
**誤りの側の文字列で**・**拡張子で絞らず**・**パス除外だけで**引いた（実行は `git grep -nI`、
除外は `src/ai-stock-trading`（submodule）・`node_modules`・`bin`・`obj` のみ）。

| 軸 | 検索語 | 目的 |
| --- | --- | --- |
| 1 | `CreatePayloadIndex\|CreateFieldIndex\|TextIndexParams\|PayloadSchemaType\|payload_index\|payload_schema` | 索引生成の呼び出しが在るか |
| 2 | `Match \{ Text\|KeywordSearch` | 全文 Match の実装点と、それを語る記録 |
| 3 | `CreateCollectionAsync\|CollectionExistsAsync\|EnsureCollections` | 索引を足すべき生成点 |
| 4 | `全文検索\|全文インデックス\|full.?text\|フルテキスト` | 挙動を述べている**生きた文書** |
| 5 | `qdrant`（大小文字） | 配備・設定・検査器の追随先 |

### 引いた結果と、変更する / しない の別

| 反映先 | 扱い | 理由 |
| --- | --- | --- |
| `src/.../IngestionService/Infrastructure/ExternalServices/QdrantIngestionVectorStore.cs` | **変更**（索引生成） | コレクション生成と同じ 1 点に置く |
| `src/.../IngestionService/Infrastructure/ExternalServices/QdrantBootstrapHostedService.cs` | **変更**（失敗の可視化） | 索引生成の失敗を「起動時の一般警告」に埋めない |
| `src/.../RetrievalService/Infrastructure/ExternalServices/QdrantVectorStore.cs` | **変更**（縮退の可観測化） | 受け入れ基準 3 |
| `src/.../RetrievalService/Program.cs` | **変更**（health check / メトリクス登録） | 同上 |
| `scripts/verify-oidc-edge-flow.sh` | **変更**（段 S4 追加） | 受け入れ基準 4 |
| `scripts/verify-qdrant-fulltext-index.sh` | **新設** | 受け入れ基準 2（実機の正負対照を再現可能にする） |
| `docs/functional/FR-03_hybrid-search.md` | **変更** | 業務ルール④・異常系表・未決事項が**索引が無い前提**で書かれている |
| `docs/functional/FR-05_abac-access-control.md` | **変更** | 「全文インデックス未整備 → 全文側のみ縮退」が現況と食い違う |
| `docs/tests/FR-03_hybrid-search.md` | **変更** | 「対象外: 実 Qdrant の full-text Match 挙動」「別途検証する」が本作業で解消する |
| `docs/observability/*` | **変更**（新規指標の登録） | 受け入れ基準 3 のメトリクスを運用側から引けるようにする |
| `.ai-context/adr/IADR-0256`・`IADR-0313`・`.ai-context/specs/2026*` | **変更しない** | **凍結記録**。本文プロズを後から書き換えない（`traceability.repo.md`）。<br>`.ai-context/specs/` は日付つき追記が許されるが、**確定済みの他 issue の仕様書に本件の追記を差し込まない** |
| `deploy/local/infra/qdrant.yaml`・`deploy/docker-compose.yml` | **変更しない** | サーバ版は [[IADR-0315]] で 1.18.1 に揃っており、`multilingual` は同版の公式イメージで**実際に使えることを実測した**（§実測 2）。ビルドフラグの差し替えは不要 |
| `.ai-context/adr/IADR-0151` が残した `tags` / `attributes.<key>` の未索引 | **変更しない（射程外）** | 本 issue は `text` の全文索引である。フィルタ経路全体の索引方針は別判断（[[IADR-0151]] フォローアップのまま） |
| `docs/api/openapi.yaml` | **変更しない** | 契約は 1 バイトも変えない（下記 決定 3） |

**「反映先」は issue 本文より 6 件多い**（issue は `docs/` を 1 件も挙げていない）。

## 対象範囲

- 対象: `text` ペイロードの full-text インデックス生成（新規・既存の両方に冪等に）／トークナイザの選定／
  キーワード側が死んでいることの**可観測化**／退行防止の門と変異試験。
- 対象外: 検索品質（nDCG）の評価、`tags` / `attributes.*` の索引、`Match::Phrase` / `TextAny` の採用、
  埋め込みモデルの変更、`SearchResponse` の契約変更。

## 実測

### 実測 1: 索引が無いときの `Match { Text }` は「部分文字列の全走査」である

稼働 k3s（Rancher Desktop `v1.35.4+k3s1`・`platform-infra` 名前空間・`qdrant/qdrant:v1.18.1`）へ
`kubectl port-forward` し、**`KeywordSearchAsync` と同形の gRPC 呼び出し**（`ScrollAsync` +
`FieldCondition{Key="text", Match{Text=q}}`）を `Qdrant.Client` 1.18.1 から直接出した。

```
collection=knowledge_chunks_deterministic_v1 points=3
payload_schema:
  (empty)                       ← 索引は 1 つも無い（develop の姿）

   1  <- msp-searchseed-tanpopo
   1  <- tanpopo
   1  <- anpop                  ← 🔴 語でない断片が当たる
   0  <- zzzznotexistword
   0  <- 検索 tanpopo            ← 🔴 語順に依存する
   1  <- tanpopo 検索
```

**`RpcException` は 1 度も出なかった。**

### 実測 2: トークナイザの比較（同一コーパス・同一クエリ集合）

使い捨てコレクション（`..._tokenizer_probe`）に既知 4 文書（日本語・英数字識別子・型番・略語）を入れ、
`(索引なし) / Word / Whitespace / Prefix / Multilingual` を張り替えながら同じ 27 クエリを引いた。

| クエリの種類 | 索引なし | Word | Whitespace | Prefix | **Multilingual** |
| --- | --- | --- | --- | --- | --- |
| 英数字識別子（`IngestionService` `MarkdownUri` `ABAC`） | ○ | ○ | ○ | ○ | **○** |
| ハイフン内部の語（`tanpopo` `searchseed` `7800X3D`） | ○ | ○ | **×** | ○ | **○** |
| 日本語の語中（`索引` `チャンク` `埋め込み` `オブジェクトストレージ`） | ○ | **×** | **×** | **×**（語頭のみ） | **△**（当たる語がある） |
| 同じ文書群の別の日本語（`文書` `検索` `統合` `合言葉`） | ○ | × | × | × | **×**（§既知の限界） |
| 陰性対照（`zzzznotexistword` `Kubernetes`） | 0 件 | 0 件 | 0 件 | 0 件 | **0 件** |
| 語でない断片（`anpop` `estionServ`） | **🔴 誤爆** | 0 件 | 0 件 | 0 件 | **0 件** |
| 語順を替えた複数語（`検索 tanpopo`） | **🔴 ×** | ○ | ○ | ○ | **○** |
| 索引を使うか（全走査を避けるか） | **🔴 全走査** | ○ | ○ | ○（巨大） | **○** |

`multilingual` は `qdrant/qdrant:v1.18.1` の**公式イメージでそのまま受理された**
（`create-index tokenizer=Multilingual -> Completed`、サーバが `"tokenizer":"multilingual"` を echo する）。
**ビルドオプションの差し替えは要らない。**

### 実測 3: 索引の作成は冪等であり、パラメータ変更は上書きされる

同じ `CreatePayloadIndexAsync` を 2 度呼んでも `Completed`。
`Multilingual` → `Word` で呼び直すと `payload_schema.text.params.tokenizer` が `word` へ**置き換わった**。
**＝「後付け」も「張り替え」も、起動時に無条件で 1 回呼ぶだけで収束する。移行スクリプトは要らない。**

### 実測 4: 門を落とせるクエリ（受け入れ基準 4・5 の要）

seed の合言葉 `msp-searchseed-tanpopo` を**そのまま**引くと、**索引の有無によらず当たる**
（索引なしでも部分文字列として一致する）。**現行の門（`SEARCH_HITS=1`）が本欠陥を通すのはこのためである。**
語を**同じまま順序だけ替える**と、索引の有無で結果が割れる。

| クエリ | 索引なし | 索引あり（multilingual） |
| --- | --- | --- |
| `msp-searchseed-tanpopo` | 1 | 1 | ← 現行の門。**欠陥を通す** |
| `tanpopo searchseed msp` | **0** | **1** | ← 新しい門。**索引が無いと落ちる** |
| `zzzznotexistword` | 0 | 0 | ← 陰性対照 |

## 決定（詳細は [[IADR-0316]]）

### 決定 1: トークナイザは `multilingual` を採る

実測 2 のとおり、`multilingual` は他の 3 つを**すべての軸で上回る**。索引なしとの比較では、
日本語の一部（漢字＋助詞が連なる語）で再現率を落とす代わりに、偽陽性・語順依存・全走査を解消する。

**索引なしの「当たり」は全文検索ではなく部分文字列一致である。**
版依存で（v1.9.2 では例外、v1.18.1 では部分文字列）静かに変わる挙動に FR-03 を預けない。

### 決定 2: 索引はコレクション生成と同じ 1 点で、無条件・冪等に張る

`QdrantIngestionVectorStore.EnsureCollectionsAsync` を
「コレクションが無ければ作る」→「**その後、存在の有無によらず `text` の全文索引を張る**」へ改める。
実測 3 により、これだけで新規・既存の両方を冪等に賄える。

🔴 **`CollectionExistsAsync` の `continue` を残さない。** 残すと**既存コレクションにだけ索引が付かない**——
本件がまさに「既に在るコレクションに後付けできていない」欠陥である。

### 決定 3: 縮退は**応答の外側**で観測できるようにする（契約は 1 バイトも変えない）

先例（#972 / #992 / [[IADR-0252]] / [[IADR-0313]] 決定 1）が定めた形をそのまま踏襲する ——
**「200 ＋ 空」を正常に見せない**が、**なぜ空かを応答へ載せない**（存在秘匿・[[IADR-0009]]）。
[[IADR-0313]] は案 3（応答へ縮退の別を載せる）を明示的に退けている。

🔴 **本件の縮退は例外を伴わない**（実測 1）。したがって `catch (RpcException)` を賑やかにしても**何も捕まらない**。
**観測すべきは「索引が在るか」そのものである。**

1. **readiness の Degraded**: RetrievalService に `qdrant-fulltext-index` health check を足し、
   構成中のコレクションの `payload_schema` に `text: Text` が無ければ **Degraded** を返す。
   **Unhealthy にしない** —— ベクトル側は生きており、検索全体を落とすのは NFR-06（縮退運転）に反する。
   Degraded は `/health/ready` を 200 のまま本文へ現す（k8s の probe は落ちず、運用からは見える）。
2. **メトリクス**: `search.keyword_degraded.total`（0 が正常。`EdgeTypeFallbackMetrics` と同型）。
   縮退の**理由**をタグ（`missing_index` / `backend_error`）で分ける。基数は 2 に閉じている。
3. **ログ**: `RpcException` の握り潰しは残す（検索全体を落とさない）が、
   **メトリクスを必ず 1 つ上げてから返す**。「ログ 1 行だけ」をやめる。

### 決定 4: 退行防止の門は **`mode=keyword` ＋ 語順を替えた合言葉**で置く

`SEARCH_HITS=1` の既存段（ハイブリッド）は**足せない** —— ベクトル検索は閾値の無い kNN であり、
索引に 3 点しか無い使い捨てスタックでは**どんな語でも全点が返る**（[[IADR-0313]] §既知の限界）。
したがって「ベクトルでは当たらない語」は原理的に作れない。**モードで系統を切り分ける**。

段 S4（`SEARCH_HITS=1` に含める）:

| # | 判定 | 索引が無いとどうなるか |
| --- | --- | --- |
| 正の対照 | `mode=keyword` ＋ 語順を替えた合言葉 → **1 件以上・seed を含む** | **0 件で FAIL**（実測 4） |
| 陰性対照 | `mode=keyword` ＋ コレクションに無い語 → **0 件** | 0 件のまま PASS（全開放の検出） |

🔴 **合言葉を 2 つに増やさない。** `documents.json` の `probeTerm` を `-` で割って**順序を替えて空白で繋ぐ**。
単一情報源のまま「トークン化された索引でしか答えられないクエリ」になる。

### 実測 5: 稼働クラスタでの通し（是正後・変異試験つき）

新しいイメージ（`nerdctl --namespace k8s.io build` で containerd の k8s.io 名前空間へ）で
取り込み・検索の 2 サービスを再起動して測った。**helm の値は 1 バイトも変えていない。**

| # | 何をしたか | 実測 |
| --- | --- | --- |
| 1 | 取り込みサービスを再起動 | **既に在る 2 コレクション**の `payload_schema` が `{}` → `{"text":{...,"tokenizer":"multilingual","min_token_len":1,"max_token_len":40,"lowercase":true}}`（**後付けが効いた**） |
| 2 | 検索サービスの `/health/ready` | **200 `Healthy`** |
| 3 | 🔴 **変異**: 索引を消す | `/health/ready` が **200 `Degraded`**（pod は Ready のまま＝ NFR-06） |
| 4 | 変異中に `mode=keyword` で検索 | 語順を替えた合言葉 **0 件** ／ 合言葉そのまま **1 件** ／ 索引に無い語 **0 件** |
| 5 | 取り込みサービスを再起動（索引が戻る） | 語順を替えた合言葉 **1 件** ／ 索引に無い語 **0 件** ／ 語でない断片 `anpop` **0 件** |

🔴 **4 の「合言葉そのまま 1 件」が、旧来の門（`SEARCH_HITS=1`）が本欠陥を通す理由である**（実測で確認した）。

**エッジの TLS は `-k` を使わずに測った。** ローカル CA（`cert-manager/local-edge-root-ca`）を
`--cacert` で渡して `https://localhost/` が **`code=200 ssl_verify_result=0`**、
**別の CA を渡すと証明書チェーンの検証で落ちる**（＝検証が実際に効いていることの負の対照）。

## 受け入れ基準（issue 本文が正）

- [ ] 1. `text` の full-text ペイロードインデックスを、**新規・既存の両方に冪等に**作る
- [ ] 2. **陽性対照と陰性対照を対で**実測する（索引に在る語で N 件・在らない語で 0 件）
- [ ] 3. 縮退（`catch (RpcException) → 空配列 + LogWarning`）を静かにしない
- [ ] 4. `#1113` の門へ、**ベクトルでは当たらないがキーワードでは当たる**検査を足す
- [ ] 5. 変異試験: 索引の作成を外すと門が落ちることを実測する

## テスト方針

| # | 何を | どこで |
| --- | --- | --- |
| U-1 | `EnsureCollectionsAsync` が**既存コレクションにも**索引生成を呼ぶ | `IngestionService.Tests`（ポートの偽装） |
| U-2 | 索引パラメータ（tokenizer=multilingual・lowercase・min/max token len）が宣言どおり | 同上（純関数として切り出す） |
| U-3 | `KeywordSearchAsync` の縮退でメトリクスが 1 上がる | `RetrievalService.Tests` |
| U-4 | health check が `text` 索引の有無で Healthy / Degraded を切り替える | `RetrievalService.Tests` |
| U-5 | 合言葉から**キーワード専用クエリ**を導く関数（語順の入れ替え） | `scripts/scripts.repo.test.js` |
| E-1 | 実機 Qdrant での正負対照 | `scripts/verify-qdrant-fulltext-index.sh`（新設・opt-in） |
| E-2 | 統合スタックの門 | `scripts/verify-oidc-edge-flow.sh` 段 S4 |

**Testcontainers は使わない** —— 本環境（containerd / Rancher Desktop）は Docker API を持たず、
`Knowledge.IntegrationTests` は **skip のまま緑になる**。実機の判定は E-1 / E-2 に置く。

## 既知の限界（隠さない）

- 🔴 **日本語の再現率は部分的で、文書に依存する（言い切らない）。** 公式イメージ v1.18.1 の実測:
  短い日本語の文では `索引` `チャンク` `埋め込み` `オブジェクトストレージ` が当たるが、
  同じ文書群の `文書` `検索` `統合` `合言葉` は当たらない。漢字＋ひらがなでは助詞まで
  1 トークンに入る（`文書は` は当たるが `文書` は当たらない）。
  🔴 **実配備の seed チャンク（日本語＋識別子＋記号の長文）では日本語 12 語すべて 0 件**で、
  同じ文書の識別子（`IngestionService` 等）は当たった。
  **索引なしの部分文字列一致では当たっていた日本語の語が、当たらなくなる場合がある。**
  形態素解析器（Lindera 等）を積んだ Qdrant の自前ビルドは本作業の射程外とし、必要なら別 issue で扱う。
- **検索品質（nDCG）は測っていない。** 本作業が測るのは「全文の系統が索引を使って動くか」までである。
- 決定的ローカル埋め込みのベクトルに意味的な近さは無い（[[IADR-0313]]）。門の解釈に持ち込まない。

## 測れなかったもの（隠さない）

| 測れなかったもの | 理由 |
| --- | --- |
| `scripts/verify-oidc-edge-flow.sh` の**通し実行**（段 S4・S5 を含む） | 🔴 稼働クラスタの `developer` は **TOTP が登録済み**で、段 4（認可コードの取得）が `OIDC_TOTP_SECRET` を要求して止まる（本変更と無関係の環境状態）。**代わりに、段 S4 が判定するのと同じ問い合わせ（`mode=keyword` ＋語順を替えた合言葉）を、稼働中の検索サービスへ直接当てて測った**（実測 5）。BFF と ABAC を経由しない点だけが違う |
| 統合スタック CI（`integration-stack.yml`）での実走 | マージ後の nightly で初めて測れる |
| `Knowledge.IntegrationTests` の 43 件 | **Testcontainers が Docker API を要し、containerd（Rancher Desktop）では skip のまま緑になる。** 判定に使わない（件数は内訳で出す） |
| `LOCALEMBED=1` の配備での通し | helm の値を変えることになり、同じクラスタを使う並行作業に影響する。**索引の生成と検出は helm の値に依存しない**（取り込みの起動時処理と検索の readiness）ため、値を変えずに測れる範囲で測った |

## 未決事項

- `tags` / `attributes.<key>` の payload index（[[IADR-0151]] フォローアップ）は本作業では触らない。
- `Match::Phrase` / `Match::TextAny`（v1.18 の新フィールド）の採否は、利用者の検索体験の裁定が要る。
- 日本語の再現率を上げる（形態素解析器つきの Qdrant 自前ビルド）かどうかは、実運用の検索ログを見てから。
