---
title: IADR-0557 Wiki 同期は Wiki.js の今のページが送る内容と同じなら pages.update を送らない。Wolverine のリスナーの並列度は上げない（Wiki.js 2.5 の rebuild-tree が並ぶと pageTree の主キーで衝突する）
type: impl-adr
status: Accepted
related_ids: [FR-13, UC-07, ADR-0011, ADR-0027, IADR-0021, IADR-0541, IADR-0554]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-13 Wiki 閲覧・同期）
related_specs:
  - ../specs/20261017_1971_wiki-sync-throughput.md
---

# IADR-0557: Wiki 同期は変わっていないページを書かない（#1971）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-17
- 決定者: claude（オーナーの依頼 #1971）

## 起点・関連

- 起点 issue: #1971（PoC・2026-10-16。一括操作の後、`pages.create` が 1 件約 8.4 秒・毎分約 7 件で、`wiki-service.DocumentUpdated` に約 4,800 件が溜まった）。
- 関連する計画書 ID: FR-13・UC-07。
- 関連する計画 ADR: ADR-0011（Wiki.js が閲覧・編集の実体）・ADR-0027（fan-out・再試行・DLQ）。
- 関連する実装 ADR: [IADR-0021](./IADR-0021_wiki-js-sync-graphql-push.md)（GraphQL push・`singleByPath` → `create` / `update` の冪等 upsert）。
- 関連する実装仕様書: `.ai-context/specs/20261017_1971_wiki-sync-throughput.md`（実測の表・母集合・受け入れ基準）。
- 採番: push の直前に `origin/develop` と開いている PR の IADR の最大を確かめ、その +1 を採った（PR 本文に記録）。

## コンテキストと課題

- 固定 digest の Wiki.js 2.5.316 をローカルで起動して測った（作業仕様書の表）。1 件の書き込みは Wiki.js の中で律速している:
  - `pages.create` は描画（ジョブ `render-page`）と木の再構築（ジョブ `rebuild-tree`）を待つ。どちらも**1 回ごとに Node の子プロセスを fork し、DB へ接続し直す**。
    `rebuild-tree` は全ページを読み、線形探索で木を組み、`pageTree` を truncate してから入れ直す（ページ数に従って遅くなる）。
  - `pages.update` は描画の子プロセス 1 本（と `pageHistory` へ版を 1 行）。**内容が同じでも同じだけかかる。**
  - CPU 0.5 で create 9.0 秒・update 5.1 秒（PoC の 8.4 秒を再現）、CPU 1 で 4.9 秒・2.6 秒、CPU 2 でも 4.7 秒・2.1 秒。
- wiki-service 側（本文の取得・台帳・`singleByPath`）は 1 件 0.02 秒程度で、律速ではない。
- 一括の再発行（`republish-document-updated.js`）が流す `DocumentUpdated` の大半は、Wiki.js に既にあって内容の変わらない文書である。
  従来の upsert はそれを毎回 `pages.update` で書き直していた。

## 検討した選択肢

1. **Wolverine のリスナーの並列度を上げる（issue の案 1）** —— 実測で、並列 2 で作成の 2/8、並列 4 で 6/8 が
   `Error when running job rebuild-tree: insert into "pageTree" … duplicate key` で失敗した（`rebuild-tree` はトランザクションを張らずに truncate → insert する）。
   毎分は 12.1 → 13.9 → 15.4 で、CPU が律速のため 1 割強しか増えない。update も 23.0 → 25.8 → 26.2。**採らない。**
2. **前回同期した内容の要約（ハッシュ）を wiki_svc の台帳に持ち、同じなら飛ばす（issue の案 2 の原案）** —— 往復 0 回で済むが、
   比べる相手が Wiki.js ではなく台帳になる。Wiki.js の DB だけが作り直された（PoC の既定は emptyDir）・Wiki.js の画面で本文が直された、のときに
   台帳は「同じ」と言い、Wiki.js は直らない（従来の再発行は直していた）。台帳の列（マイグレーション）も要る。採らない。
3. **Wiki.js の今の状態と比べ、同じなら `pages.update` を送らない（採用）** —— 既存の `singleByPath` の 1 往復で取る列を増やすだけ。
   比べる 1 往復は 0.014 秒（中央値）で、省ける update は 2.6〜5.1 秒。
4. **Wiki.js の CPU を上げる（案 3）** —— 0.5 → 1 で 1 件が約半分になるが、配備先のノードの余力の判断が要る。本件では変えない（報告に判断材料を残す）。
5. **一括操作の fan-out を選べるようにする（案 4）** —— ADR-0027 手順 3 の fan-out の契約に触れる。計画の判断が要るので本件では実装しない。
6. **駆動スクリプトが Wiki のキューも見る（案 5）** —— 速くはならず、索引だけを直す再発行の量の制御を Wiki の速さへ合わせるかは運用の判断。本件では入れない。

## 決定

1. **`WikiJsGraphQlClient.UpsertPageAsync` は、最初の `singleByPath` で `id title content render isPublished tags { tag }` を取り、
   Wiki.js のページが送る内容と同じなら `pages.update` を送らない**（`HoldsSameContent`）。同じとは次のすべてを満たすこと:
   - 表題と本文が序数比較で等しい（Wiki.js は受け取った値をそのまま保存する。往復で一致することを実測した）。
   - タグの集合が等しい。こちらの値は Wiki.js の `associateTags` と同じ正規化（trim・小文字・重複除去）をしてから比べる（順序は見ない）。
   - `isPublished` が true（update は常に true を送るので、アーカイブ済みのページは違う）。
   - `render` が空でない（描画の子プロセスが前回落ちていたページは、update で描き直させる）。
2. **`isPrivate` は比べない**。Wiki.js 2.5 の `updatePage` は `isPrivate` を書かない（`patch` の列に無い）ので、違っていても update では直らない。
3. 判定に迷う差（文化依存の小文字化の違い・改行の違いなど）は「違う」に倒し、従来どおり update を送る。**飛ばすのは結果が同じと言えるときだけ**である。
4. ページが無い（6003）ときの `pages.create`・失敗の写像（IADR-0554 の value too long・他は一時障害）は変えない。ポート（`IWikiJsClient`）の署名も変えない。
5. **wiki-service の `DocumentUpdated` のリスナーの並列度は上げない**（選択肢 1 の実測）。

## 理由

- 同じ内容の update は Wiki.js の状態（本文・表題・タグ・公開状態）を変えない。送らなくても同期の結果は同じで、**冪等な upsert という同期の意味論は保たれる。**
- 比べる相手を Wiki.js にしたので、Wiki.js の側だけが変わった（作り直された・画面で直された）ときは従来どおり上書きされる。

## 結果

- ローカルの Wiki.js 2.5.316（CPU 1）で本クライアントを走らせた: 同じ内容の upsert は 1 件 0.013〜0.014 秒（従来 2.6 秒）、
  `pageHistory` は増えず、`render-page` のジョブも起きない。本文を変えた upsert は従来どおり 2.7 秒で update される。
- 再発行のうち内容の変わらない文書は Wiki の律速から外れる。**作成（Wiki.js にページが無い）は速くならない**（Wiki.js の子プロセス 2 本と木の再構築が残る）。
- Wiki.js の側で、同じ内容の再発行のたびに `pageHistory` の版が増えることはなくなる。

## 残余リスク

- (a) Wiki.js の描画の設定（レンダラ）を変えても、変わっていないページは描き直されない（従来は再発行で描き直された）。
  描き直すときは Wiki.js の管理画面のユーティリティ（全ページの再描画）を使う。
- (b) Wiki.js の画面でスクリプト・CSS（`extra`）だけを書き換えたページは、従来は次の update で空に戻っていたが、本文が同じなら残る。
- (c) 作成の多い一括操作（Wiki.js の DB が空になった後の全件の再発行など）は従来の速さ（CPU 0.5 で毎分約 7 件）のままである。案 3・案 4 の判断が要る。
- (d) `DocumentDeleted` のリスナーは `DocumentUpdated` と並んで動くので、作成と削除が重なると `rebuild-tree` が並び得る（従来から。本件で変えていない）。
  失敗した側は一時障害として再試行される。並んだ後の `pageTree` の整合は確かめていない（木は Wiki.js の画面のナビゲーションに使われ、本システムは使わない）。

## 試験

- `WikiJsGraphQlClientTests`: 同じ内容なら往復 1 回（タグの正規化 4 通り）・比べる項目のどれかが違えば update（6 通り）・`isPrivate` だけの違いは飛ばす。
  比較を常に false / 常に true にする変異でそれぞれ赤になることを確かめた（作業仕様書・PR 本文）。

## 関連

- #1971・#1931（一括アーカイブ）・#1966（IADR-0554）
