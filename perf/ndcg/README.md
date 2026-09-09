# 検索の関連性ハーネス（nDCG@10）— 埋め込みモデルの A/B（#336）

計画 ADR-0016 / ADR-0017 は、埋め込みモデルの確定を **「PoC（検索精度 nDCG@10）」** に委ねている。
本ディレクトリはその測定の受け皿である。**環境非依存の準備物であり、実測はデプロイ済み環境と
正解ラベル（qrels）が用意でき次第、下記手順で実行する**（`perf/k6/README.md` と同じ型）。

| 指標 | 目的 | 実体 |
| --- | --- | --- |
| nDCG@10（graded relevance・log2 割引） | 埋め込みモデル・検索モードの**関連性**の比較 | `scripts/measure-search-ndcg.js` |
| p95 レイテンシ / スループット | **速さ**の SLO | `perf/k6/`（別軸。関連性は測らない） |

🔴 **`integration-stack.yml` の `SEARCH_HITS=1` は本指標の代替にならない。** あちらが測るのは
「索引に入る・ABAC を通る・埋め込みが供給される・後段へ到達できる」の 4 点であって、
**返ってきた順位が妥当かは 1 バイトも見ていない**（門のコメント自身がそう断っている）。

## 前提（実測に要るもの。いずれも本リポジトリでは用意できない）

1. **稼働環境**（検索 API に到達でき、本番相当のデータが索引されていること）。
2. **正解ラベル（qrels）** —— クエリごとに「どの文書が関連するか」を人が判断した表。
   `qrels.example.json` を `qrels.json` へ複製して埋める（**`qrels.json` と `*.dump.json` は
   `.gitignore` 済み**。社内文書の題名・ID が入るため）。
3. **A/B の相手側**（セルフホスト埋め込み＝ TEI の実配備と実モデル load）。#336 の受け入れ基準 ①②③。

## qrels の書式

```json
{
  "version": 1,
  "k": 10,
  "queries": [
    { "id": "q1", "text": "就業規則 休暇", "relevance": { "3f1c…": 3, "9ab2…": 1 } }
  ]
}
```

- 関連度は **0..3 の整数**（3=まさにこれ / 2=関連する / 1=部分的 / 0=無関係）。書かなければ 0 である。
- 文書 ID は検索応答の `documentId`。**qrels は文書単位**で、検索が返すチャンクは文書へ畳んで採点する
  （同じ文書の 2 つ目以降のチャンクは落とす。落とさないと同じ文書で二重に加点される）。
- **正解が 1 件も無いクエリは平均から除外され、その件数と ID が報告される。**
  0.0 として混ぜると「ラベルを付けていない」が「精度が低い」に化けるためである。

## 実行

```bash
# 1) 収集 ＋ 集計（既定のモデルで測る）
NDCG_BASE_URL=https://edge.example NDCG_TOKEN=<jwt> NDCG_LABEL=voyage-3.5 \
  node scripts/measure-search-ndcg.js --qrels perf/ndcg/qrels.json --dump perf/ndcg/voyage.dump.json

# 2) 集計だけ（保存済みの順位から。**環境なしで追試できる**）
node scripts/measure-search-ndcg.js --input perf/ndcg/voyage.dump.json

# 3) A/B（2 回の収集を並べる。qrels の指紋が違えば落ちる）
node scripts/measure-search-ndcg.js --input perf/ndcg/voyage.dump.json --input perf/ndcg/ruri.dump.json
```

主な環境変数は `scripts/measure-search-ndcg.js` の冒頭にある（`NDCG_BASE_URL` / `NDCG_SEARCH_PATH` /
`NDCG_TOKEN` / `NDCG_MODES` / `NDCG_LABEL` / `NDCG_K`）。**認証は取得済みのアクセストークンを与えること** ——
MFA 必須化により realm の対話利用者はパスワードグラントで取得できない（`perf/k6/README.md` と同じ事情）。

## 🔴 A/B のときに必ず対で切り替える 2 つの設定

セルフホスト（Ruri v3 / 768 次元 / `knowledge_chunks_ruri_v3`）を測るときは、**クエリの埋め込み**と
**検索対象コレクション**の両方を動かす。片方だけだと**別モデルの空間へ問い合わせる**ことになる。

| 何を | どこで | 値の例 |
| --- | --- | --- |
| クエリの埋め込み先 | LlmGateway `Embedding__Routing__QueryProfile` | `selfhosted-ruri` |
| 検索が読むコレクション | RetrievalService `Qdrant__CollectionName` | `knowledge_chunks_ruri_v3` |

**食い違ったまま測ることはできない。** RetrievalService は、ゲートウェイが答えたコレクション名と
自分が読むコレクション名を突き合わせ、違えばクエリのベクトルを捨てる（＝意味検索の系統を使わず
キーワード系統だけで応答する）。**壊れた測定が数字として成立しないための歯止め**である。

またエンドポイント名の綴り間違い・無効なエンドポイントの指定は、**LlmGateway が起動時に落とす**
（黙って既定へ落ちると「Ruri を測ったつもりで voyage を測る」ことになるため）。

## 結果の読み方・記録

- モードは `keyword` / `semantic` / `hybrid` の 3 つを測る。**埋め込みの寄与は `semantic` と
  `hybrid` に出る**（`keyword` はモデルを替えても動かないはずで、動いたら測定系を疑う＝陰性対照）。
- 比較表は**先頭の run が基準**で、差分（`delta`）はそこからの増減である。
- 実測結果（日時・環境・データ量・qrels の指紋・モデル・平均 nDCG）は
  `docs/operations/operations.md` の埋め込み節と #336 へ残す。
- **劣化の判定は #336 の受け入れ基準に従う**（「voyage-3.5 比で大幅劣化しないこと」。劣化時は BGE-M3 へ切替）。

## 制約（現状）

- **環境ブロック**: 実測には稼働環境・本番相当データ・正解ラベル・TEI の実配備が要る。
  本ハーネスは受け皿であり、数値の実測は環境が用意でき次第行う（#336 は実測完了まで OPEN）。
- **集計は環境非依存で検査済み**である（`scripts/scripts.repo.test.js`。`node scripts/scripts.test.js` で走る）。
