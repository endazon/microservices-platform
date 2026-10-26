---
title: 作業仕様書 — SC-04 の深いリンクの同一性を DocumentId にし、slug の一意索引を外し、slug が複数の文書に一致するときは本文を返さない。旧形式の共有 URL（?page=<slug>）は 1 件に決まるときだけ新形式へ書き換えて開く（#2108）
type: spec
status: done
related_ids: [FR-13, FR-05, UC-07, SC-04, ADR-0091, ADR-0011, IADR-0021, IADR-0365, IADR-0541, IADR-0009, IADR-0644]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2108"
---

# 作業仕様書 — SC-04 の深いリンクを DocumentId に寄せる（#2108）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。親は #2047（指摘 M-11）。依頼はコーディネーター（2026-10-26）。

## 計画（ADR-0091。Accepted・利用者裁定 2026-09-09）

- 決定 1: SC-04 の深いリンクの同一性は `DocumentId`。slug に一意性を課さない（台帳の一意索引は落とす）。`page=` の優先を解いて `doc=` を正に据える。
- 決定 2: slug の経路を残すなら、**同じ slug が複数の文書に一致するときは本文を返さない**。応答は不存在・権限外と区別しない（IADR-0009）。
  **`?page=<slug>` の既存リンクの扱い（無視するか、表題で引き直すか）は実装が決めてよい。** 禁じるのは「1 件に決まらないのに 1 件返す」ことだけ。
- フォローアップ 1: 一意索引を落とす変更に決定 2 を同じ変更で含める（索引だけ先に落とさない）。フォローアップ 2: IADR-0365 決定 4 を改める旨を IADR に残す。フォローアップ 3: slug 経路を残したかを環流する。
- 🔴 **旧 URL の扱いは ADR-0091 決定 2 が実装の裁量として定めている**（コーディネーターの条件「ADR-0091 が旧 URL の扱いを定めていなければ止まる」には当たらない）。以下はその裁量の範囲で決める。

## URL の鍵（前と後）

| | 前（IADR-0365 決定 4） | 後（本件） |
| --- | --- | --- |
| ページツリー・検索結果のリンク | `?page=<slug>` | `?doc=<documentId>` |
| SC-01 の出典・SC-03 の「Wiki で閲覧」 | `?doc=<documentId>`（変えない） | 同じ |
| 本文の中のページ間リンク（`/ja/doc/<id>` の書き換え） | `?doc=<documentId>`（変えない） | 同じ |
| `page` と `doc` が両方あるとき | **`page` が勝つ** | **`doc` が勝つ**。URL から `page` を外す（`replace`） |
| `page` だけ（旧形式の共有 URL） | slug で開く | **互換の解決**: `GET /bff/wiki/pages/{slug}` で引き、**1 件に決まって開けたら**、URL を `?doc=<その documentId>` へ書き換える（`replace`。履歴を増やさない）。1 件に決まらない・権限外・不存在は 404 の中立表示（区別しない） |
| `q`（検索語） | 保つ | 保つ |

- 後段の `GET /wiki/pages/{slug}` は残す（旧 URL の互換の解決に使う）。slug が 2 件以上の行に一致すれば 404（ADR-0091 決定 2）。数えるのは状態・権限を問わない全行（権限外の同名文書の有無を、応答の違いで漏らさない）。
- 改題した文書の旧 URL（古い題名の slug）は、その slug を今持つ行が無ければ 404 になる。これは従来と同じで、改題後の共有は `doc=` で切れない（決定 1 の目的）。

## 台帳（WikiService）

- `IX_Pages_Slug` を一意でない索引にする（名前は変えない。slug での引き当ての速さは保つ）。マイグレーションを 1 本足す（起動時の `MigrateAsync` が当てる）。
- slug の選び方（IADR-0541 決定 1・2 の候補列）は残す。新しい slug は引き続き互いに異なる値を選ぶので、旧形式の URL はほとんどの場合 1 件に決まる。
- IADR-0541 決定 3 の「保存時の `IX_Pages_Slug` の 23505 を付け替えて保存し直す／恒久的な衝突として ack する」は、一意でない索引では起こり得ないので外す（計数の理由 `slug_conflict` も外す）。起こり得ない場合への防御を残さない（CLAUDE.md の禁止事項）。

## 受け入れ基準

- [x] 後段: 同じ slug の文書が 2 件あるとき、`GET /wiki/pages/{slug}` は 404（2 件とも権限内でも、片方が権限外でも、片方がアーカイブ済みでも）。1 件なら従来どおり 200。修正前は 200（任意の 1 件）で赤
- [x] 後段: モデルの `Slug` の索引が一意でない（`IX_Pages_Slug`）。修正前は一意で赤
- [x] 後段: 同名の題名の別文書 2 件が両方ページになる（従来どおり）
- [x] 画面: ツリーと検索結果のリンクは `?doc=<documentId>`。開いているページの現在地も `doc` で判定する
- [x] 画面: `page` と `doc` が両方あるとき `doc` で取得し、slug の経路は呼ばず、URL から `page` が消える（現行試験の反転）
- [x] 画面: `?page=<slug>` だけの旧 URL は slug で引いて開き、URL が `?doc=<documentId>` に書き換わる。検索語は保つ。404 なら中立の表示で、書き換えない
- [x] E2E（smoke・a11y）を新しい URL に合わせる。旧 URL の互換を 1 本足す
- [x] 文書: 画面仕様書・テスト仕様書（SC-04・FR-13）・機能仕様書・通信仕様書の slug の意味論を改める。IADR-0644 を起こす（IADR-0365 決定 4・IADR-0541 決定 3 を改める）

## 母集合（規則 9）

`git grep -n "page=\|IX_Pages_Slug\|slug の一意\|pages/{slug}\|slug_conflict\|SaveWithSlugFallbackAsync"`（`.ai-context/` は除く）で引いた。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `sc04-wiki/types/wikiSearch.ts`・`api/useWikiQueries.ts`・`components/WikiBrowsePage.tsx` | **直す** | 優先と鍵 |
| `sc04-wiki/components/WikiBrowsePage.test.tsx` | **直す** | 反転と追加 |
| `src/platform/frontend/e2e/sc04-wiki.smoke.spec.ts`・`a11y.smoke.spec.ts` | **直す** | 新しい URL・by-doc の応答 |
| `src/platform/frontend/src/app/Layout.tsx` のコメント（`/wiki?page=…`） | **直す**（コメントだけ） | 例の URL |
| WikiService の `GetPageBySlug`・`WikiDbContext`・`DocumentSyncConsumer`・`WikiPage`・`WikiSyncSkippedMetrics`・`Program.cs` のコメント | **直す** | 索引・決定 2・外す防御 |
| WikiService の試験（`WikiEndpointsAbacTests`・`WikiSyncPermanentFailureTests`） | **直す** | 追加と、外す防御の試験の削除 |
| `docs/screens/SC-04_wiki-access.md`・`docs/tests/SC-04_wiki-access.md`・`docs/tests/FR-13_wiki-browsing-abac.md`・`docs/functional/FR-13_wiki-browsing.md`・`docs/api/BFF_bff-surface.md`・`docs/api/openapi.yaml` | **直す** | 意味論の記述 |
| `src/knowledge/frontend/src/features/sc04-wiki/types/sanitizeWikiHtml.ts`（本文の中のリンクを `doc=` へ書き換える） | 外す | 既に `doc=` |
| SC-01・SC-03 の「Wiki で閲覧」 | 外す | 既に `doc=` |
| `src/ai-stock-trading` | 外す | submodule |

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。

## PoC の配備で要ること

- wiki-service の起動時に、マイグレーション 1 本（`IX_Pages_Slug` を一意から一意でない索引へ作り直す）が自動で当たる。データの移行・再同期は要らない（既存の slug は値を変えない）。
