---
title: 破壊的操作の管理者限定が機械クライアント（AST の KB 書き込み）を締め出す。人間と機械の扱いが未分離である
type: plan-feedback
status: open
category: 記述の不足
related_ids: [FR-06, UC-03, SC-05, IADR-0044, IADR-0075]
source_repo: microservices-platform
source_ref: "fix/FR-06-document-write-admin-only / docs/specs/20260809_issue-629_document-write-admin-only.md（PR #645・実装側 issue #629）"
author: Claude（実装）
created: 2026-08-09
dispatched: true
---

# フィードバック: 破壊的操作の管理者限定が機械クライアントを締め出す

## 起票状況（**planning#306 として伝達済み・裁定待ち**）

`feedback/README.md` の手順は 3 段（1. `/plan-feedback` 実行 → 2. 記録作成 → 3. 計画リポへの伝達）である。

| 手順 | 状態 |
| --- | --- |
| 2. `feedback/` への記録作成 | **完了**（本ファイル） |
| 3-a. `planning/draft/feedback/` へのコピー | **完了**（`20260809_document-write-machine-client.md`。**PR [planning#306](https://github.com/endazon/project-planning/pull/306) でマージ済み**） |
| 3-b. Issue 起票 | **本件では実施しない**（3-a のコピー経路を採ったため。いずれか一方で足りる） |

## 種別

**記述の不足**（確定した統制が、想定していなかった呼び出し元へ及ぶ）。
**裁定 Q19 の誤りの指摘ではない** —— Q19 は**画面と人間のロール**について正しい。
**その統制を API 層へ写したときに、人間以外の呼び出し元が射程に入った**という報告である。

## 起点となる計画書

- 機能要求（FR）: **FR-06**（文書管理）
- ユースケース（UC）: **UC-03**
- 画面（SC）: **SC-05**（文書管理画面）
- 関連 ADR: 実装側 [[IADR-0044]]（後段の多層防御）・[[IADR-0075]]（AST の KB 書き込み用サービスクライアント）
- 計画書リンク:
  [`05_screens/01_screens.md`](../planning/projects/microservices-platform/05_screens/01_screens.md)
  §SC-05「管理系 3 画面の閲覧ロール」（**破壊的操作の列挙の正**）
- 先行する裁定: 質問票 第12回 **Q19**（2026-08-05 確定）

## 現状（実測）

実装 #629 で SC-05 の文書書き込みを管理者限定へ狭める作業中、
**`POST /documents` を狭めると別プロジェクトの機能が止まる**ことが判明した。

```console
$ grep -rn 'PostAsJsonAsync("/documents"' --include=*.cs src/
src/ai-stock-trading/.../HttpKnowledgeBaseWriter.cs:49   ← BFF を経由せず DocumentService を直接叩く

$ （deploy/keycloak/microservices-platform-realm.json の users を読む）
service-account-ai-stock-trading-kb-writer -> ['platform-operator']
```

[[IADR-0075]] は **`platform-admin` の付与を最小権限を理由に明示的に却下**している。
したがって `AdminOnly` を積むと **AST/FR-08 の KB 書き込みが 403 で恒久的に止まる**。

## 問題点

**計画は「誰が押すか」を人間のロールで定めているが、API は人間以外からも呼ばれる。**

Q19 の趣旨（運用者に破壊的操作をさせない）は**画面の話として完全に正しい**。
しかし実装は多層防御（[[IADR-0044]]）でサービス層にも同じロール要件を課すため、
**画面を通らない機械クライアントが巻き添えになる**。計画にはこの区別が無い。

**同じ構造は今後も起きる** —— 基盤 API を可変ユニットが直接呼ぶ設計（`ADR-0018`）を採っている以上、
**人間向けの統制を API 層へ写すたびに機械クライアントの棚卸しが要る**。

## 実装側の暫定対応（利用者裁定 2026-08-09）

**`POST /documents` だけサービス層で据え置いた**（admin ＋ operator のまま）。

| 面 | 扱い | 理由 |
| --- | --- | --- |
| BFF の書き込み 5 口（`POST` 含む） | **管理者限定** | **人間の画面はここを通る。人間に対する境界は閉じている** |
| サービス側の `PUT` / `PATCH` / `publish` / `archive` / `DELETE` | **管理者限定** | 機械クライアントはこの 5 口を呼ばない（[[IADR-0075]] 自身が「構造上 `POST` しか発行しない」と記録） |
| **サービス側 `POST`** | **据え置き** | 狭めると AST が止まる。**裁定を待つ** |

**残る露出は限定的である** —— `DocumentService` はメッシュ内部でイングレス非公開であり、
人間の運用者が到達する経路（BFF・画面）は塞がっている。
据え置きは `Create_OperatorRole_IsStillAllowed_ForMachineClientUntilArbitration` が固定しており、
**「狭め漏れ」と読んで塞いだ瞬間に赤くなる**。

## 依頼したいこと

**「破壊的操作は管理者限定」が機械クライアント（サービスアカウント）にも及ぶのかを明記する。**

| 案 | 内容 | 実装側の帰結 |
| --- | --- | --- |
| **案 A（実装側の推奨）** | **統制の対象は「人間の利用者」であると明記する。** 機械クライアントは専用のサービスアカウントと最小権限で管理し、画面の統制とは別建てにする | **現状のまま**。据え置きが明文に一致する |
| 案 B | 機械クライアント用に**専用ロール**（例「文書作成のみ」）を定義する | realm へロール追加 ＋ [[IADR-0075]] 改定 ＋ AST 側の確認 |
| 案 C | 機械クライアントにも管理者限定を適用する | **AST/FR-08 が機能停止する**。AST 側に代替経路が要る |

**案 A を推す理由**: Q19 の趣旨は「運用者という**人間の職掌**に破壊的操作をさせない」ことであり、
サービスアカウントはそもそも職掌の概念の外にある。機械の権限は最小権限で個別に設計するのが自然で、
[[IADR-0075]] は既にそう作られている。

**あわせて、基盤 API を可変ユニットが直接呼ぶ箇所の棚卸しを提案する。**

## 参考

- 実装の判断の全文: [作業仕様書 §追補 1](../docs/specs/20260809_issue-629_document-write-admin-only.md)
- **本件と対になる環流**: 公開・アーカイブが破壊的操作の列挙に無い件も同時に伝達した
  （planning `draft/feedback/20260809_destructive-operation-list-publish-archive.md`。同じ PR planning#306）
