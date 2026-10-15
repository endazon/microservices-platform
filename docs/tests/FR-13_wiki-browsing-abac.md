---
title: FR-13 Wiki 閲覧の ABAC 適用 テスト仕様書
type: test-spec
status: draft
created: 2026-07-03
updated: 2026-10-15
author: claude
---
<!-- trace:
ids: [FR-03, FR-04, FR-05, FR-07, FR-13, UC-07, NFR-06]
adrs: [ADR-0004, ADR-0011, ADR-0027]
iadrs: [IADR-0541, IADR-0540, IADR-0023, IADR-0478]
specs: [20261015_1934_wiki-slug-unique, 20260703_FR-13_wiki-browsing-abac, 20260927_issue-1640_consumer-outbound-call-timeouts]
issues: [#1934, #1640]
-->

# テスト仕様書: Wiki 閲覧の ABAC 適用

## 起点となる計画書（トレーサビリティ）

- 機能要求: 正規化文書を Wiki サービスで閲覧できること
- ユースケース: Wiki で閲覧する
- 関連 ADR: Wiki エンジンの採用（ABAC は本システムが真実源）／認可＝ABAC（deny-by-default）

## テスト対象・範囲

- 対象: `AbacPageFilter`（許可スコープの評価意味論）、`WikiEndpoints`（一覧・個別の ABAC 適用）、
  `DocumentSyncConsumer`（更新イベント → ページ同期）。
- 対象外: 横断検索・AI 回答本体（それぞれの機能のテスト仕様で検証済み）、負荷/p95、Wiki.js 本体・OIDC 連携。

## テスト観点

- deny-by-default: `Granted=false` で一覧が空・個別が 404。
- 評価意味論: フィルタ間 AND・値集合内 OR・属性欠落は不一致。
- セキュリティ: 権限外文書が一覧・本文のいずれにも現れない（受け入れ基準②・Wiki 閲覧ユースケースの例外フロー）。
- 反映: `DocumentUpdated` の受信で作成・更新される（受け入れ基準③）。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | `Granted=false` | `AbacPageFilter.Matches` | false（不可視） | 権限外を出さない | 自動 |
| T-02 | `Granted=true`・フィルタ空 | 同上 | true（全件可） | 閲覧 | 自動 |
| T-03 | 許可値に一致する属性値 | 同上 | true（OR 一致） | 閲覧 | 自動 |
| T-04 | 許可値外の属性値 | 同上 | false | 権限外を出さない | 自動 |
| T-05 | 複数フィルタの一部が不一致 | 同上 | false（AND） | 権限外を出さない | 自動 |
| T-06 | 属性キー欠落 | 同上 | false（欠落は安全側） | 権限外を出さない | 自動 |
| T-07 | `Granted=false` | `GET /wiki/pages` | 空配列 | 権限外を出さない | 自動 |
| T-08 | public/restricted の 2 ページ・許可=public | `GET /wiki/pages` | public のみ返る | 権限外を出さない | 自動 |
| T-09 | 許可=public | `GET /wiki/pages/by-doc/{restricted}` | 404（存在秘匿） | 権限外を出さない | 自動 |
| T-10 | 許可=public | `GET /wiki/pages/by-doc/{public}` | 200 | 閲覧 | 自動 |
| T-11 | 許可=public | `GET /wiki/pages/{restricted-slug}` | 404 | 権限外を出さない | 自動 |
| T-12 | status=normalized の更新イベント | 発行→消費 | ページ作成・属性保持 | 更新反映 | 自動 |
| T-13 | status=draft の更新イベント | 発行→消費 | 同期されない | 更新反映 | 自動 |
| T-14 | 同一 DocumentId で 2 回発行 | 発行→消費 | 1 ページに更新（タイトル/属性が最新） | 更新反映 | 自動 |
| T-15 | 同期済みページ・status=archived 受信 | 発行→消費 | Wiki.js 非公開化＋メタデータ Archived | 削除/アーカイブ伝播 | 自動 |
| T-16 | 未同期 ID の archived 受信 | 発行→消費 | 例外なし（冪等）・Wiki.js 非公開化のみ試行 | 同上 | 自動 |
| T-17 | アーカイブ後に published 再受信 | 発行→消費 | メタデータ Active へ復帰（可逆） | 同上 | 自動 |
| T-18 | 同期済みページ・DocumentDeleted 受信 | 発行→消費 | Wiki.js 実体撤去＋メタデータ行削除 | 同上 | 自動 |
| T-19 | 未同期 ID / 再送の DocumentDeleted | 発行→消費 | 例外なし（冪等） | 同上 | 自動 |
| T-20 | Archived ページ・権限あり | `GET /wiki/pages` / by-doc | 一覧に出ない・個別 404（存在秘匿維持） | 同上 | 自動 |
| T-21 | 未存在ページの singleByPath（errors 6003） | `UpsertPageAsync` | 例外化せず create へ進む（稼働実測整合） | 更新反映 | 自動 |
| T-22 | アーカイブの Wiki.js 送信シェイプ | `ArchivePageAsync` | content/title/tags を含む全項目 update＋unpublish | 削除/アーカイブ伝播 | 自動 |
| T-23 | 縮めた受け口の期限（30 秒）の下で、本文の取得・Wiki.js への反映・アーカイブ・撤去のどれかが止まる（1 回の期限 1 秒） | 発行→消費 | 受け口の期限より前に、その呼び出し先の**時間切れ**（`ConsumerTimeoutException`）として投げる（再試行・デッドレターへ）。呼び出し元の取り消しは取り消しのまま外へ出る（対照） | 更新反映・削除/アーカイブ伝播 | 自動 |
| T-24 | 期限の構成と配線 | 起動 | 既定は本文 20 秒・Wiki.js 15 秒。同期（本文＋Wiki.js）か撤去（Wiki.js）が既定の実行期限 60 秒以上になる構成は起動を止める（等しいときも）。本番の配線は期限を DI に置き、両受け口の実行期限は既定のまま | 同上 | 自動 |
| T-25 | 同名の題名の別文書 2 件・同じ文書の 2 回の同期・接尾辞付きの slug の再同期（元の持ち主は削除済み）・題名の変更・記号だけの題名・500 字の題名 | 発行→消費 | 別文書は両方ページになり slug は互いに異なる（先に来た方は題名だけ、後の方は文書 ID の先頭 8 桁の接尾辞）。同じ文書は 1 行のまま更新し slug を保つ。接尾辞付きの slug は保たれ、題名が変われば取り直す。記号だけの題名は `doc-<8 桁>`。候補は列の長さ 500 に収まり末尾は文書 ID を丸ごと含む | 更新反映 | 自動 |
| T-26 | 保存時に slug の一意違反（23505・`IX_Pages_Slug`）が 1 回だけ／毎回起きる。文書 ID の一意違反・DB の接続断 | 発行→消費 | 1 回だけなら文書 ID 32 桁の接尾辞へ付け替えて保存する。毎回なら付け替えて 1 度だけ保存し直した後、例外を上げずに終え、Warning（文書 ID・索引名）と計数 `wiki.sync.skipped{reason=slug_conflict}` を 1。文書 ID の一意違反と接続断は付け替えずに例外のまま上げる。本番の索引名と判定の制約名が一致する | 更新反映（恒久的な失敗で DLQ を汚さない） | 自動 |
| T-27 | 本文の取得でオブジェクトストレージが NoSuchKey（専用の型・汎用の型にエラーコード）・404 のバケットなし・500・503・時間切れ・接続の失敗を返す | 読み手 | NoSuchKey だけを本文の欠落へ写す（所在を運ぶ）。状態コード 404 だけでは写さず、他は元の例外のまま上げる（取り込みと同じ判定） | 同上 | 自動 |
| T-28 | 本文が無い。本文の一時障害（500） | 発行→消費 | 欠落は例外を上げずに終え、Warning（文書 ID・本文の所在・更新日時）と計数 `reason=body_missing` を 1。メタデータも Wiki.js も書かない。一時障害は例外のまま | 同上 | 自動 |
| T-29 | 本番と同じ共通既定の Wolverine（ローカルキュー）で、slug の恒久的な衝突・本文が無い・1 回目だけ DB の接続断 | 発行→消費 | 恒久的な失敗は試行 1 回で成功として終わり、デッドレターへ行かない（受け口の中の付け替えは 1 度だけ）。接続断は従来どおり再試行され（2 回目で保存）、デッドレターへ行かない | 同上 | 自動 |

## 実装マッピング

- `AbacPageFilterTests`（T-01〜T-06）
- `WikiEndpointsAbacTests`（T-07〜T-11、T-20）
- `DocumentSyncConsumerTests`（T-12〜T-14）
- `DocumentDeleteArchiveSyncTests`（T-15〜T-19）
- `WikiJsGraphQlClientTests`（T-21〜T-22。稼働 Wiki.js 2.5.314 の実測応答を再生）
- `WikiSyncTimeoutTests`（T-23〜T-24。配線は同じファイルの `WikiSyncTimeoutWiringTests`）
- `WikiSyncPermanentFailureTests`（T-25〜T-29。23505 は保存の割り込みで再現する —— InMemory は一意索引を強制しない）
- `DocumentLifecycleEventTests`（DocumentService 側: archive/delete のイベント発行）
