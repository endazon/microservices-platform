---
title: Wiki 同期の slug を文書 ID 由来の接尾辞で一意にし、slug の恒久的な衝突と本文の NoSuchKey を再試行・DLQ に回さず記録して ack する（#1934）
type: spec
status: done
related_ids: [FR-13, UC-07, NFR-06, ADR-0011, ADR-0027, ADR-0106, IADR-0020, IADR-0021, IADR-0540, IADR-0541]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-13 Wiki 閲覧 / NFR-06 障害時の縮退運転）
issue: "#1934"
---

# 仕様書: Wiki 同期の slug の一意化と恒久的な失敗の ack（#1934）

## 起点となる計画書（トレーサビリティ）

- FR-13（Wiki 閲覧・同期）・UC-07・NFR-06（障害時の縮退運転）。
- ADR-0011（Wiki.js。ABAC は本システムが真実源）・ADR-0027（Wolverine。共通既定の再試行・DLQ）・ADR-0106（SeaweedFS）。
- 実装判断の記録: [IADR-0541](../adr/IADR-0541_wiki-slug-unique-and-permanent-sync-failures.md)。本文の NoSuchKey の判定は [IADR-0540](../adr/IADR-0540_ingestion-body-missing-is-permanent.md) に揃える（その残余の解消）。
- 起点: #1934（PoC・2026-10-15 20:40〜。再索引の再発行で wiki-service が `IX_Pages_Slug` の 23505 を 30 分で 360 件、DLQ が 48 → 86）。

## 根本原因（コードで確かめた）

- `DocumentSyncConsumer` は**文書 ID で既存ページを引いて更新する**（`IX_Pages_DocumentId` も一意）。**同じ文書の再同期は既に upsert であり、自分自身と衝突しない。**
- slug は `WikiPage.ToSlug(title)`（題名の小文字化・記号を `-` に）だけで決まり、**文書 ID を含まない**。したがって衝突するのは
  1. **同名の題名を持つ別の文書**（AST の情報収集の同じ見出しのニュース・PoC が重複して作った「経費精算規程」）、
  2. 既存の文書の**題名の変更**で、別の文書の slug と同じ slug へ写るとき、
  3. 記号だけの題名（slug が空文字）を持つ文書が 2 件以上あるとき。
- 衝突は題名が変わらない限り再試行で直らない（恒久的）。Wolverine の共通既定で 4 試行の後 DLQ へ入る。

## 決めたこと（要約。詳細は IADR-0541）

1. **slug の候補列**: `[base, base-<文書IDの先頭8桁>, base-<文書ID 32桁>]`（base が空なら `doc-<8桁>`・`doc-<32桁>` から）。**他の文書が使っていない最初の候補**を採る。
2. **既存の slug は保つ**: 既存ページの slug が今の題名の候補列に入っていれば変えない（URL を壊さない。接尾辞付きで作られたページも、元の持ち主が消えた後に素の slug へ戻らない）。
   題名が変わって候補列から外れたときだけ取り直す（題名の変更で slug が変わるのは従来どおり）。
3. **保存時の 23505（`IX_Pages_Slug`）**: 並行に同名の別文書を同期した競合。受け口の中で 1 度だけ**文書 ID 32 桁の接尾辞**へ付け替えて保存し直す。
   それでも `IX_Pages_Slug` に当たる（または既に 32 桁だった）なら**恒久的な失敗**として Warning＋計数（`wiki.sync.skipped{reason=slug_conflict}`）を残して ack。
   **`IX_Pages_DocumentId` の 23505（同じ文書の並行作成）は再試行に任せる**（次の試行で更新の経路に入って直る）。
4. **本文の NoSuchKey**: `StorageMarkdownReader` で `NoSuchKeyException` か `ErrorCode == "NoSuchKey"` だけを `WikiBodyMissingException` へ写す（IADR-0540 決定 1 と同じ判定。404 単独・`NoSuchBucket`・5xx・時間切れ・接続の失敗は写さない）。
   受け口は Warning＋計数（`reason=body_missing`）で ack。メタデータも Wiki.js も書かない（既存のページは前の版のまま残る）。
5. **一時障害**（DB 接続断・Wiki.js の失敗・5xx・時間切れ）は従来どおり例外のまま（再試行 → DLQ）。
6. **マイグレーション不要**: 索引（`IX_Pages_Slug` 一意）は変えない。既存の行は一意制約の下で書かれているので衝突を含まず、slug も保たれる。
   失敗して行が無い文書は、配備後の再発行で接尾辞付きの slug を得る。

## 母集合（規則 9）

- slug を書く箇所: `git grep -n "Slug" -- 'src/knowledge/backend/Services/WikiService/**/*.cs'`（試験を除く）→ 書くのは `WikiPage.CreateFromDocument` / `WikiPage.Sync` だけ。読むのは一覧・個別・検索の応答と `GetPageBySlug`（slug で 1 行を引く。一意であることを前提にする —— 索引を外さない理由）。
- `DocumentSyncConsumer` を構築する箇所（構築子へ計器を足すため。規則 10）: `git grep -n "new DocumentSyncConsumer(\|IWikiContentReader, "` → 試験 3（`DocumentSyncConsumerTests`・`DocumentDeleteArchiveSyncTests`・`WikiSyncTimeoutTests`）と Wolverine の器 1（`PipelineRecomposeTests`）・`Program.cs`。統合試験（`Knowledge.IntegrationTests` の `WikiServiceFactory`）は `Program.cs` を通るので追随不要。
- wiki-service でオブジェクトストレージを読むのは `StorageMarkdownReader` だけ（`git grep -n "GetTextAsync" -- 'src/knowledge/backend/Services/WikiService/**'`）。graph-service の読み手は射程外（残余）。

## 受け入れ基準と試験の写像（`WikiSyncPermanentFailureTests`・テスト仕様書 T-25〜T-29）

| 受け入れ基準 | 試験 |
| --- | --- |
| 同名表題の別文書 2 件が両方ページになる（slug は別。先に来た方が素の slug） | `同名の題名の別文書は両方ページになり接尾辞で一意になる` |
| 同じ文書の 2 回の同期で 1 ページ（更新）・slug は変わらない | `同じ文書の再同期は一ページのまま更新しslugを保つ` |
| 接尾辞付きの slug は元の持ち主が消えても保たれる／題名の変更で取り直す | `接尾辞付きのslugは再同期で保たれる`・`題名が変われば新しい題名でslugを取り直す` |
| 記号だけの題名でも空の slug にならず一意 | `記号だけの題名は文書ID由来のslugになる` |
| 保存時の slug 競合は 32 桁へ付け替えて保存し直す | `保存時のslug競合は文書ID全桁へ付け替えて保存し直す` |
| 付け替えても衝突するなら Warning＋計数で ack・DocumentId の競合と DB 接続断は例外のまま | `付け替えても衝突すれば記録してackする`・`文書IDの一意違反と接続断は例外のまま上げる` |
| 本文の NoSuchKey の分類（IADR-0540 と同じ） | `NoSuchKeyは本文の欠落として写す`・`一時障害は本文の欠落へ写さない` |
| 本文の欠落は Warning＋計数で ack・何も書かない | `本文が無ければ記録だけしてackする` |
| 実配送: 恒久失敗は試行 1 回・DLQ なし／一時障害は再試行 | `実配送で恒久的な失敗は一回の試行で終わりDLQへ行かない`（2 例）・`実配送でDB接続断は再試行される` |

## 変異試験（すべて赤になることを確かめた）

| 変異 | 赤になった試験 |
| --- | --- |
| M1 空きを確かめず常に題名だけの候補を採る | 同名の別文書・記号だけの題名・題名の変更 |
| M2 既存の slug を保たない | 接尾辞付きの slug は再同期で保たれる |
| M3 保存時の付け替えをしない | 付け替えて保存し直す・付け替えても衝突すれば記録・実配送（slug） |
| M4 恒久判定を 23505 すべてにする（制約名を見ない） | 文書 ID の一意違反は例外のまま |
| M5 本文の欠落の catch を外す | 本文が無ければ記録だけ・実配送（本文） |
| M6 NoSuchKey の判定を状態コード 404 にする | 404 `NoSuchBucket`・本文が無ければ記録だけ・実配送（本文） |
| M7 slug の恒久的な失敗を数えない | 付け替えても衝突すれば記録してackする |

## 実 PostgreSQL での確認（手元・PostgreSQL 16。使い捨ての試験で行い、コミットしない）

- `MigrateAsync` で既存のマイグレーションを適用（スキーマは変えていない）。
- 同名の題名「経費精算規程」の別文書 2 件を各 2 回同期 → 2 行（`経費精算規程` / `経費精算規程-cf9ae56e`）。
- 2 つの DbContext から同じ題名「同時」を並行に保存（本文の取得で 2 本を待ち合わせ、両方が素の slug を選ぶようにした）→ 実際の 23505 `IX_Pages_Slug` を受けて
  後の方が `同時-<32 桁>` へ付け替えて保存した（2 行とも残った）。制約名の判定が実際の Npgsql の例外で効くことを確かめた。

## 残るもの

- graph-service の読み手（`StorageContentReader`）も同じ NoSuchKey で再試行・DLQ になり得る（IADR-0540 の残余のうち wiki だけを本件で閉じる）。
- 本文の無い文書の Wiki 上の扱い（既存ページを残す・消す）は #1931 項目 3 の裁定に従う。本件は何も書かず何も消さない。
- 既に DLQ にある wiki-service の分は古い写しなので再投入しない（issue の指示どおり、配備後の再発行で作り直す）。
