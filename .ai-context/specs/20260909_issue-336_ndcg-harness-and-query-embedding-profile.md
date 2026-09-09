---
title: nDCG@10 の測定ハーネスを収集と集計に分けて先行させ、検索クエリの埋め込み経路に測定用の切替口を開ける
type: spec
status: done
related_ids: [FR-02, FR-03, NFR, NFR-02, ADR-0013, ADR-0016, ADR-0017, IADR-0025, IADR-0085, IADR-0313, IADR-0318, IADR-0422]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 測る道具を先に置く（#336）

## 起点

- FR-02（取り込み・埋め込み）／FR-03（検索）／NFR-02（検索の応答性能。ハーネスの置き場が同じ `perf/`）
- 計画 ADR: `ADR-0016`（既定はティア B・**モデル別コレクション分離**・高機密はセルフホスト固定）／
  `ADR-0017`（ティア A は Ruri v3。**nDCG@10 の PoC で確定**すると書いてある）／`ADR-0013`（`Embed` ポート抽象）
- 実装 ADR: [[IADR-0025]]（ルーティングと機密区分別コレクション）／[[IADR-0085]]（TEI を opt-in 配備物として置き、
  実測は稼働環境へ分離した）／[[IADR-0313]]（決定的ローカル埋め込み＝第 3 のティア）／[[IADR-0422]]（本作業で新設）
- issue: #336（`blocked`）

## なぜ今これをやるか —— issue は blocked だが、2 項目は環境非依存である

#336 の受け入れ基準 ①②③⑤（TEI 実配備・実モデル load・768 次元疎通・**Voyage のゼロ保持契約認定**）は
稼働環境と組織の判断に依存する。**⑤ は実装側では原理的に満たせない。**

一方、棚卸しコメント（2026-08-16 / 09-03 / 09-05）は**同じ 2 項目を 3 回続けて**「環境非依存で今すぐ着手できる」
と挙げ、3 回とも着手されずに次の棚卸しへ持ち越された。本作業はその 2 項目だけを片付ける。

| # | 項目 | 本作業 |
| --- | --- | --- |
| 1 | **nDCG@10 の測定ハーネスがリポジトリに無い**（`ndcg` の 14 ヒットはすべて散文。実行コードは 0） | 置く |
| 2 | **検索クエリの埋め込みが常にティア B へ固定され、A/B 測定が成立しない** | 切替口を開ける |
| — | 実測（TEI 配備・正解ラベル付け・契約認定） | **やらない**（環境・組織の手） |

## 🔴 現状（実測。`develop` `7c9d184`）

### 項目 1: 測る道具が無い

```console
$ grep -ril "ndcg" --exclude-dir={node_modules,.git,bin,obj} . | wc -l
21
$ ls perf/
graph-render  k6
```

`perf/k6/` の 4 ファイルは**レイテンシとスループットだけ**を測る（`search-load.js` は `p(95)<1500`）。
`check(res, {'status is 200'})` しか見ておらず、**返却結果の順位も ID も検証していない**。

### 項目 2: クエリは常に既定外部経路へ倒れる

`EmbeddingRouter.Route`:

```csharp
var sensitivity = request.Purpose == EmbeddingRoutePurpose.Query
    ? SensitivityClass.Public
    : request.Sensitivity;
```

これは `ADR-0016`「クエリの埋め込みは**検索対象コレクションの埋め込みと同じモデル**でなければ意味が無い」
を実装したものであり、**壊してはならない**。壊れているのは「切り替える手段が 1 つも無い」ことだけである。

### 🔴 さらに測って分かったこと —— 索引側と検索側で**コレクションの決め方が違う**

| 側 | コレクションを誰が決めるか |
| --- | --- |
| 取り込み（Ingestion） | **ゲートウェイの応答**（`EmbedApiResponse.Collection`）。`DocumentUpdatedConsumer` が `embedding.Collection` へ upsert する |
| 検索（Retrieval） | **自分の構成**（`Qdrant:CollectionName`）。応答の `Collection` を**読んでいない** |

この非対称が #1215 の事故そのものである —— 点が `knowledge_chunks_deterministic_v1` に 3 件在るのに
検索側は `knowledge_chunks_voyage_3_5`（0 点）を読み、**両サービスとも Ready のまま検索だけが全件 0 件**になった。
`check-stack-ready.js` の G13 が外から捕まえるが、**プロセスの中には照合が 1 つも無い。**

🔴 **切替口を開けると、この乖離は「静かに 0 件」では済まなくなる** —— 次元が同じ 2 つのコレクション
（voyage 1024 / deterministic 1024）で食い違うと、**別モデルの空間へ問い合わせて順位が返る**。
nDCG はその順位を採点するので、**壊れた測定が数字として成立してしまう。**

## 決定（詳細は [[IADR-0422]]）

### 決定 1: ハーネスは**収集と集計を分ける**（`scripts/measure-search-ndcg.js`）

`measure-abac-combinations.js` と同じ型に乗せる —— 読み取り専用・乱数/時刻非依存・Node 標準のみ・
集計は純関数で `scripts.repo.test.js` が単体試験・`--json` / `--dump` / `--input`。

**稼働環境が無いので収集部は実走できない。** だから `--dump` した生データを `--input` で集計し直せる形にし、
**集計側だけを TDD で書き切って CI で緑にする。**

### 決定 2: 切替口は**構成 1 つ**（`Embedding:Routing:QueryProfile`）で、契約は 1 バイトも変えない

案 (a)（`SearchRequest` に `EmbeddingProfile` を足す）は採らない。理由:

- **`SearchRequest` は BFF の公開契約**である。openapi → orval 生成物 → SPA まで波及する
- **クエリ側でティアを選べるようにする形**になり、統制の説明が複雑になる
  （選べるのは「どのコレクションを検索するか」であって「どの機密区分を読めるか」ではない、という
  区別を契約の上で毎回説明することになる）
- A/B 測定は**運用者が系全体を切り替えて 2 回測る**もので、要求ごとに混ぜる必要が無い

採るのは案 (b) だが、**置き場はルーティングを所有する LlmGateway 側**にする（Retrieval 側ではない）。
`Purpose=Query` のときだけ、候補を**その名前のエンドポイント 1 つに絞る**。

🔴 **越境判定の後に絞る。前ではない。** プロファイルは候補を**狭めることしかできない** ——
`EmbeddingEgress.AllowedTiers` と `Enabled` の篩を通った集合に対して名前で絞る。
そして **`Purpose=Index` には効かない** ので、文書本文の送信先は従来どおり機密区分だけが決める。
（**この 2 つの守りは別物である。** 実測では、篩の順序が守っているのは `Enabled`、
ティアを守っているのは「Index に効かない」ことだった。後述の変異試験 M-5 / M-6）

🔴 **未設定・不一致は「既定へ落とさない」。** 名前が実在しない／無効なエンドポイントを指すときは
**起動時に落とす**（`ValidateOnStart`）。黙って voyage へ落ちると、**Ruri を測ったつもりで
voyage を測る**という最悪の測定事故になる。

### 決定 3: 🔴 Retrieval は**ゲートウェイが答えたコレクション名**と**自分が読むコレクション名**を照合する

決定 2 の切替口は「クエリの埋め込みモデル」を動かす。**検索対象コレクションは別の構成が決めている。**
2 つが食い違ったまま measurement を走らせると、上記のとおり**壊れた測定が数字として成立する。**

そこで `IEmbeddingService` の 2 実装（REST / gRPC）が、応答の `Collection` を自分の `Qdrant:CollectionName` と
突き合わせ、**食い違えば空ベクトルへ降りる**（＝`Embedded=false` と同じ既存の縮退。キーワード系統は生きる）。

- **判定は 1 つ**（`QueryEmbeddingCollection.Matches`）。輸送 2 つが同じ関数を呼ぶ（`EmbedUseCase` と同じ姿勢）
- 応答の `Collection` が**空のときは照合しない**（情報が無い。ゲートウェイは拒否時に空を返し、そのときは
  既に `Embedded=false` で降りている）
- **既定構成では一致する**（`voyage-managed` → `knowledge_chunks_voyage_3_5` ＝ Retrieval の既定）。
  Helm の決定的ローカル経路も**両方を同時に**書き換えるので一致する。**変わるのは既に壊れている構成の見え方だけ**である

### 決定 4: 計画の裁定は要らない（と判断した根拠）

`ADR-0016` は (a) モデル別にコレクションを分ける、(b) クエリは検索対象コレクションと整合させる、
(c) 高機密文書の埋め込みはティア A 固定、を決めている。本作業は **(a)(b)(c) をいずれも動かさない** ——
(b) の「整合」を**構成で選べるようにし、かつ機械で照合する**だけである。

そのうえで `ADR-0016` 自身が「フォローアップ: … PoC（検索精度 nDCG@10）で確定する」と書いており、
**測る手段を用意することは同 ADR の宿題そのもの**である。よって環流（issue 起票）はしない。

**ただし planning へ持っていくべき問いは 1 つ残る**（起票はしない。#336 の報告に添える）:

> 高機密（ティア A / 768 次元 / `knowledge_chunks_ruri_v3`）の文書は、既定構成では**索引されるが検索されない**
> （検索側は単一コレクションしか読まない）。`ADR-0016` はコレクション分離を決めたが、**分離した複数の
> コレクションを 1 回の検索でどう束ねるか**は決めていない。`LlmGatewayEmbeddingService` の
> 「高機密コレクションの横断検索は FR-03 の後続課題」というコメントが、この未決を 2026-07 から抱えている。

## 触るファイル

| 面 | ファイル | 内容 |
| --- | --- | --- |
| 道具 | `scripts/measure-search-ndcg.js`（新規） | 収集（I/O）＋ 集計（純関数） |
| 種 | `perf/ndcg/qrels.example.json`（新規） | クエリ 5 件の雛形。**正解ラベルは空**（利用者が埋める） |
| 手順 | `perf/ndcg/README.md`（新規） | `perf/k6/README.md` と同じ「環境非依存の準備物」の型 |
| 索引 | `scripts/README.md` | 1 行追加 |
| 試験 | `scripts/scripts.repo.test.js` | 集計の単体試験 ＋ 検査器母集合の除外（測定器であって検査器ではない） |
| 経路 | `LlmGateway/Domain/Routing/EmbeddingRoutingOptions.cs` ほか 3 本 | `QueryProfile` の追加・適用・起動時検証 |
| 照合 | `RetrievalService/Infrastructure/ExternalServices/QueryEmbeddingCollection.cs`（新規） | 決定 3 の判定 1 つ |
| 客体 | 同 `LlmGatewayEmbeddingService.cs` / `LlmGatewayGrpcEmbeddingService.cs` | 照合を通す |
| 合成 | `RetrievalService/Program.cs` | 読むコレクション名を 1 か所で解決して渡す |
| 手順 | `docs/operations/operations.md` | 切替口とハーネスの位置を運用手順へ |
| 記録 | `docs/security/security.md` | 「常に固定」→「既定では固定」（決定 2 で条件付きになった） |

**契約（DTO・proto・openapi）は変えない。** 応答の `Collection` は**元から在る項目**であり、
決定 3 はそれを**読むようになった**だけである。

## 母集合の引き方（`.claude/rules/traceability.repo.md` 規則 9・10）

**規則 9（誤りの側の文字列で全走査してから挙げる）:**

```console
$ grep -ril "ndcg" --exclude-dir={node_modules,.git,bin,obj} .     # 21 ファイル
$ grep -rn "既定外部経路" --exclude-dir={node_modules,.git,bin,obj} .  # 19 ヒット / 13 ファイル
```

**規則 10（この変更で新たに誤りになる自分の記述を引き直す）:** 「クエリは**常に**既定外部経路へ固定」と
書いた記述は、決定 2 の後は「**既定では**固定」が正しい。19 ヒットの処遇:

| 処遇 | 対象 | 理由 |
| --- | --- | --- |
| 直す | `EmbeddingRouter.cs` / `IEmbeddingRouter.cs` / `EmbedDto.cs` / Retrieval の `LlmGatewayEmbeddingService.cs` | live なコードの記述 |
| 直す | `docs/security/security.md` | live な権威文書 |
| **直さない** | `.ai-context/adr/IADR-0025` / `.ai-context/specs/20260707_issue-98_*` | **凍結記録**（本文プロズを後から書き換えない） |
| **直さない** | `docs/api/openapi.yaml` ＋ `bff.schemas.ts`（生成物） | 記述は**要求契約の既定**を説明しており、既定は動いていない。触ると orval 再生成の連鎖が起きる |
| **直さない** | LlmGateway の既存試験 3 本のコメント | 既定の場合を書いており、そのまま真である（プロファイル指定の試験は**別に足す**） |

**導出値は走査ではなく計算し直した:** `scripts.repo.test.js` の検査器母集合は **53 本のまま**である ——
`measure-search-ndcg.js` は `measure-abac-combinations.js` と同じ**測定器**であり `NOT_CHECKERS` へ入れる
（判定を返さず、走らせると外部 I/O を試みる。検査器として spawn される母集合に入れてはならない）。

## テスト（受け入れ基準）

### 項目 1（集計の純関数。TDD で試験を先に書いた）

- [x] 理想順位は `1.0`
- [x] 逆順は既知値（関連度 3/2/1 を逆順 → `0.7899980042460358`。DCG・IDCG の内訳ごと固定）
- [x] 関連文書 0 件のクエリは**除外して件数を報告**する（0.0 として平均へ混ぜない）
- [x] 結果が k より少なくても落ちない
- [x] qrels に無い文書 ID は関連度 0
- [x] 🔴 **同一文書の重複（チャンク由来）は先頭だけを採る**（採らないと同じ文書で 2 度加点される）
- [x] `keyword` / `semantic` / `hybrid` をモード別に測り、差分表を出す
- [x] 🔴 **qrels が違う 2 つの run は比較させない**（digest 不一致で落とす）
- [x] `--input` で集計だけを追試できる

### 項目 2（切替口）

- [x] 未設定なら**現行と同一の決定**（既定の挙動が 1 バイトも変わらない）
- [x] プロファイル指定で当該エンドポイント（モデル・次元・コレクション）が選ばれる
- [x] 🔴 **プロファイルは越境を広げられない** —— `Purpose=Index` × confidential で `voyage-managed` を
      指しても deny（fail-closed）のまま
- [x] 🔴 無効・不在のプロファイルは**起動時に落ちる**（黙って既定へ落ちない）
- [x] 🔴 応答のコレクションと検索対象コレクションが食い違えば空ベクトルへ降りる（REST・gRPC の両方）
- [x] 一致すれば従来どおりベクトルを運ぶ（陽性対照）
- [x] 既存試験が全部緑のまま

## 変異試験（実走。すべて戻し、戻したことは緑で確認した）

🔴 **`scripts/scripts.test.js` は fail-fast である**（`ok()` が例外を投げてプロセスが落ちる）。
よって node 側の「赤」は**最初に落ちた試験**であり、総数ではない。xUnit 側は総数である。

| # | 変異 | 結果 |
| --- | --- | --- |
| M-1 | 割引を `log2(i+2)` → `log2(i+3)` にする（順位 1 に割引を掛ける） | 赤 —— 「逆順は既知値（0.78999800…）」。🔴 **「理想順位は 1.0」は生存した**（下記） |
| M-2 | 正解ラベル 0 件のクエリを除外せず `0.0` として平均へ混ぜる | 赤 —— 「正解ラベルが無いクエリは平均から除外し、ID を報告する」 |
| M-3 | 同一文書の重複除去をやめる | 赤 —— 「同一文書の重複は先頭だけを採る（1.0 を超えない）」 |
| M-4 | qrels 指紋の照合をやめる（違うラベルの結果を並べられる形） | 赤 —— 「qrels の指紋が違う入力は比較できない（落とす）」 |
| M-5 | `QueryProfile` を `Purpose=Index` にも適用する | 赤 **1 件**（`Route_QueryProfileはIndexに効かない`。287 passed / 288） |
| M-6 | プロファイルの絞り込みを越境判定・`Enabled` の篩の**前**へ移す | 赤 **1 件**（`Route_無効なQueryProfileは既定へ落ちずに拒否される`。287 passed / 288） |
| M-7 | `QueryEmbeddingCollection.Matches` を常に `true` にする | 赤 **3 件**（239 passed / 242） |

### 🔴 M-1 の生存が教えたこと（記録として）

**「理想順位は 1.0」という試験は割引の式を 1 バイトも固定していない。** 分子と分母が同じ順列なら、
どんな割引でも比は 1 になるからである。**満点の試験だけを置いていたら、割引を壊しても緑のままだった。**
逆順の既知値（と DCG / IDCG の内訳）がこの式を固定している唯一の試験である。

### 🔴 M-5 / M-6 の切り分けが教えたこと（当初の見立ての訂正）

着手時は「プロファイルの適用点を越境判定の前へ移すと**ティアの越境が開く**」と考えていた。**それは誤りである。**
`QueryProfile` は `Purpose=Query` にしか効かず、`Query` は `SensitivityClass.Public`（許容ティア A・B）へ
倒れるので、**適用点の前後でティアの集合は変わらない**。適用点の順序が実際に守っているのは
**`Enabled` の篩**（無効なエンドポイントを名指しで選べてしまう形）である（M-6）。

越境そのものを守っているのは **「プロファイルは `Index` に効かない」という設計**（M-5）のほうである。
本仕様書と [[IADR-0422]] の記述はこの実測に合わせてある。
## 実測（数字）

| 母集合 | 変更前 | 変更後 |
| --- | --- | --- |
| `node scripts/scripts.test.js`（`REQUIRE_REPO_TESTS=1`） | 758 | **769**（＋11） |
| `LlmGateway.Tests` | 275 | **288**（＋13） |
| `RetrievalService.Tests` | 232 | **242**（＋10） |

- platform ユニット単体試験: Failed 0（`AuthorizationService` 242 / `LlmGateway` 288 / `NotificationService` 90 /
  `Platform.Bff` 543・skip 1 ほか）。knowledge ユニット単体試験: Failed 0（合計 2,223 passed・skip 9）。
- `dotnet format --verify-no-changes` は両ユニットとも差分なし。build は 0 error
  （knowledge の警告 2 件は本作業と無関係な既存分＝`QdrantBuilder` の obsolete）。
- 検査器: `check-trace-blocks` / `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` /
  `check-test-traceability` / `check-test-spec-coverage` / `check-doc-type-vocabulary` / `check-nul-bytes` /
  `check-backend-libraries` / `check-unit-dependencies` / `check-reading-budget` /
  `check-test-name-references` / `gen-knowledge-graph --check` はすべて OK。
- 🔴 **`check-adr-numbering` は IADR-0418〜0422 を欠番として報告する。** これは**並行作業中の兄弟ブランチが
  確保している番号**であり、本ブランチ単体では埋められない（先着尊重。マージが揃えば解消する）。
  `IADR-0422` 自身の索引突合（`index-missing` / `title-too-long` / `title-drift`）は OK である。
- **同じ理由で `node scripts/scripts.test.js` はこのブランチ単体では最後まで走らない**（同テストは
  `check-adr-numbering` を実データに対して実走するため）。**欠番 5 件をローカルの捨てファイルで埋めて
  実走した結果が「769 tests passed」であり**、捨てファイルは削除して作業ツリーを clean に戻した。
  **この数字はそのやり方で得たものである**と明示しておく。
- 索引行のタイトルセルは初版が 204 字で `title-too-long` に触れた（上限 200）。**baseline へ足さず
  155 字へ縮めた**（ラチェットの向きに従う）。

## やらないこと

- **実測**（TEI の実配備・実モデル load・qrels の正解ラベル付け・Voyage のゼロ保持契約認定）。#336 は OPEN のまま
- **複数コレクションの横断検索**（決定 4 の残る問い。`ADR-0016` が決めていない）
- **`SearchRequest` への項目追加**（決定 2 で採らなかった案 (a)）
- **Helm / compose への `QueryProfile` の配線**。opt-in の測定用スイッチであり、環境変数
  `Embedding__Routing__QueryProfile` で与えられる。**実配備が来てから、実際に使う形で入れる**
