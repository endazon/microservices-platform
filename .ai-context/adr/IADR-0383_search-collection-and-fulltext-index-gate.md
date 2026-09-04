---
title: IADR-0383 検索側が読むコレクションに点と全文索引が在ることを門 G13 で落とす
type: impl-adr
status: Proposed
related_ids:
  - FR-02
  - FR-03
  - NFR-09
  - UC-01
  - SC-01
  - ADR-0009
  - ADR-0016
  - IADR-0025
  - IADR-0255
  - IADR-0284
  - IADR-0313
  - IADR-0315
  - IADR-0318
  - IADR-0339
  - IADR-0369
  - IADR-0377
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0009_vector-db-qdrant.md
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-model-routing.md
---

# IADR-0383: 検索の読み書き先を門にする（#1215）

- 状態: Proposed
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 計画 ADR: **ADR-0009**（ベクトル DB = Qdrant）／**ADR-0016**（埋め込みのルーティング）
- 実装 issue: **#1215**（稼働 dev クラスタの検索が全件 0 件）
- 先行: [IADR-0025](./IADR-0025_embedding-provider-routing-and-model-collections.md)（モデル別コレクション）／
  [IADR-0313](./IADR-0313_deterministic-local-embedding-for-search-gate.md)（決定的ローカル埋め込み。3 サービス同時配線）／
  [IADR-0315](./IADR-0315_qdrant-server-version-follows-client.md)（Qdrant サーバの版をクライアントへ揃える）／
  [IADR-0318](./IADR-0318_qdrant-fulltext-payload-index.md)（全文ペイロード索引 `text`）／
  [IADR-0339](./IADR-0339_japanese-fulltext-app-side-bigram.md)（日本語 2-gram ペイロード `text_ngram`）／
  [IADR-0255](./IADR-0255_edge-smoke-step-loss-gate-and-unobservable-search.md)（`200 ＋ 空` が 3 つの失敗と区別できない）／
  [IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md)（門 G9〜G11 の作法）／
  [IADR-0377](./IADR-0377_mesh-mtls-single-writer-and-drift-gate.md)（門 G12）
- 作業仕様書: [20260905_issue-1215](../specs/20260905_issue-1215_search-collection-gate.md)

## コンテキストと課題

#1215 は稼働 dev クラスタで「検索が全件 0 件」になる 3 つの原因を記録した。そのうち本 IADR が扱うのは
**原因 2 —— 検索が読むコレクションと点が在るコレクションが違う**である（検索側は
`knowledge_chunks_voyage_3_5`（0 点）、点は `knowledge_chunks_deterministic_v1`（3 点））。

**この形は既存の検査を 1 つも起こさない。**

- 取り込みも検索も**健全で `Ready`** である（どちらも自分の仕事は成功している）。
- 検索の応答は `200 ＋ 空`。これは「該当が無い」とまったく同じ形であり、状態コードでは区別できない
  （[IADR-0255]）。
- RetrievalService の readiness（`QdrantFullTextIndexHealthCheck` / `QdrantCjkNgramIndexHealthCheck`）は
  **索引の有無しか見ず、点の在り処を見ない**。しかも本文を外から読めないことがある
  （`verify-oidc-edge-flow.sh` 段 19 が実測で「readiness の本文を読めなかった」に落ちている）。
- 全文索引が無いときも静かである。Qdrant v1.18.1 は例外を投げず**部分文字列の全走査**へ落ちる
  （[IADR-0318]）ので、**「当たっている」ことは索引が在る証拠にならない**。

**乖離は片側だけの変更で必ず生まれる。** 決定的ローカル埋め込みは 3 サービス（llmgateway / ingestion /
retrieval）へ**同時に**配線して初めて読み書きが揃う（[IADR-0313]）。1 つ欠けると、読み先と書き先が
別のコレクションになる。chart はこれを 1 つの `if` にまとめているが、**稼働がそのとおりである保証は無い**
（#1215 の稼働クラスタは実際にずれていた）。

### 現状（2026-09-05 の実測。#1088 のクラスタ作り直し後）

稼働 k3s を測り直したところ、**症状は解消していた**。読み先・書き先はともに
`knowledge_chunks_deterministic_v1` で、3 点が在り、`text`（multilingual）/ `text_ngram`（prefix）の
両索引が張られている。`SEARCH_HITS=1 SEARCH_SEEDED=1 bash scripts/verify-oidc-edge-flow.sh` は
**PASS 26 / FAIL 0（段 19/19）** で、陽性（seed 文書が当たる）と陰性（在らない語が 0 件）が対で成立する。

**だから門が要る。** 症状は配備状態に依存して消えたり戻ったりするが、門はリポジトリに残る。

## 決定

### 決定 1: 門 G13 を `check-stack-ready.js` に置き、**点の在り処**を本体の判定にする

**どれかのコレクションに点が在るのに、検索側が読むコレクションの点が 0 なら失敗**とする。
これが #1215 の原因 2 そのものの形であり、他のどの検査でも捕まらない。

**どこにも点が無い状態は notice に落とす。** まだ何も取り込んでいないだけの素のスタックと
区別できないためで、一律に赤にすると意味の無い赤を量産する。ただし **`SEARCHSEED=1` を宣言した
実行では 0 点を失敗にする**（G10 の `PERSIST` と同じ「宣言された期待に対して測る」作法）。
🔴 **検査を飛ばす向きの env は持たない**（門の fail-closed を崩さない）。

### 決定 2: 読み先は**走査して**決め、既定値へ落とさない

稼働 Deployment の env `Qdrant__CollectionName` を走査し（**サービス名は書かない**）、
無ければ RetrievalService の `appsettings.json` の既定を読む。**どちらからも決まらなければ失敗**。
複数の Deployment が別々の値を持っていれば「どれが検索側か決まらない」として失敗にする。

既定値（`knowledge_chunks`）へ落とすと、実装が既定を変えたとき「在らないコレクションを期待する検査」へ
静かに変わる。**落とさないほうが安全である。**

### 決定 3: 索引の期待値はアプリの実装から走査する（値を検査器へ書き写さない）

単一情報源は `QdrantIngestionVectorStore.BuildFullTextIndexParams()` /
`BuildCjkNgramIndexParams()` の 2 つの純関数と、キーを持つ 2 つの定数
（`FullTextKey` / `CjkBigramPayload.PayloadKey`）である。門はこれを走査して tokenizer・
`min_token_len`・`max_token_len`・`lowercase` を組み立て、稼働の `payload_schema` と突き合わせる。
**読めなければ失敗**（空の期待値で緑にしない）。G7 の locale と同じ姿勢である。

`scripts.repo.test.js` は「検査器のソースに tokenizer の文字列が直書きされていないこと」を
**走査した値そのもので**確かめる ——書き写しへの後戻りを機械で止める。

### 決定 4: 収集は**使い捨て pod 1 個**で行い、稼働 Pod を 1 つも触らない

Qdrant の pod には curl も wget も無く（実測）、Qdrant はエッジにも出ていない。
`kubectl port-forward` は非同期の背景プロセスになり、本検査の同期構造（`spawnSync`）に載らない。
そこで G6（`nslookup`）と同じく **busybox を立てて消す**。一覧を 1 回、詳細を
`sh -c` のループで 1 回、**合計 2 回**の pod で全コレクションを読む（コレクションごとに立てない）。

- 🔴 **`--rm --attach` を使わない。** 完了が速いと attach が間に合わず**出力を静かに取り落とす**
  （実測で踏んだ。空文字が返り「コレクションが 1 件も無い」と読み違えた）。
  終端まで有界に待ち、`kubectl logs` で読み、**消し切ってから**戻る（残すと次回の G1 が残骸を拾う）。
- 🔴 発行するのは **GET だけ**である。Qdrant へ書き込みを一切行わない。
- コレクション名は Qdrant から来た値なので、シェルへ載せる前に形（`[A-Za-z0-9_.-]+`）を確かめ、
  想定外なら測らずに落とす。

### 決定 5: `verify-qdrant-fulltext-index.sh` と統合しない

あちらは**使い捨てコレクション**を作って索引の**挙動**（語順・断片・日本語・1 文字語）を陽性・陰性の対で
測る器であり、**稼働コレクションには触れない**（読み取りもしない）。G13 は逆に**稼働コレクションの
状態**だけを読む。役割が違うので別々に置く。運用文書は両方の使いどころを併記する。

## 却下した代替案

- **RetrievalService の readiness に委譲する**: 索引の有無しか見ず、**点の在り処を見ない**。
  かつ本文を外から読めないことがある（実測）。門を「読めないことがある情報」に預けない。
- **検索を実際に叩いて件数を見る**: 認証・ABAC・seed の有無が絡み、落ちたときに原因を名指しできない。
  それは `SEARCH_HITS=1` の段の仕事であり、G13 は**その手前の前提**（読み書き先の一致）を測る。
- **点が 0 なら常に失敗**: 素のスタック（seed していない）が必ず赤になる。決定 1 のとおり
  「宣言された期待に対して測る」形へ倒した。
- **CI の門ステップへ `SEARCHSEED=1` を渡す**: その時点では up の中の seed（best-effort）から
  取り込みが**非同期で**進んでいる途中であり、「まだ届いていない」を欠陥として報告する（間欠赤）。
  取り込みの成立は後段の `SEARCH_HITS=1` の門が測る。

## 結果・影響

- 「両サービスとも健全なのに検索だけが全件 0 件」という**無音の故障**が、対象を名指しして赤になる。
- CI（`integration-stack.yml`）の門ステップは変更しない（G13 は既存の呼び出しに自動的に加わる）。
- 稼働クラスタへの副作用は使い捨て pod 2 個のみ（作って消す）。既存 Pod は再起動しない。
- **門は「読み書き先が一致していること」しか言わず、どちらの向きが正しいかは言わない。**
  Voyage を使う運用へ戻すときは、鍵の供給と `embedding.*` の一致を別に確かめること。
