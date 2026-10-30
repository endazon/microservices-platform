---
title: 作業仕様書 — 実装と食い違う陳腐化したコメントを直す（SearchAccessResolver・SC-05 / 10 / 09 / 01 / 21。#2147）
type: spec
status: done
related_ids: [NFR-09, FR-03, FR-06, FR-09, FR-10, FR-18, SC-05, SC-10, SC-09, SC-01, SC-21]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2147"
---

# 作業仕様書 — 陳腐化したコメントの是正（#2147）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `05cb882b`）。親 #2047（監査の指摘 L-18。監査の基点 `7297af58`）。

## 方針

- **挙動は変えない。** コメント（と、同じ誤りを持つ文書）を実装に合わせるだけである。
- 監査の挙げた 6 か所は、それぞれ今の実装と照らして判定する。既に直っているものは触らない。
- 母集合は監査の列挙ではなく、**誤りの側の文字列で全文書を走査**して取る（規則 9）。

## 監査の 6 か所の判定（develop `05cb882b` で実測）

| 箇所 | 監査の時点の記述 | 今の実装 | 判定 |
| --- | --- | --- | --- |
| `SearchAccessResolver.cs:22-24` | 「`/search` は認可を持たない（#1318 欠陥 B）／本 PR で認可は掛けない／未認証は 401 ではなく空応答」 | `SearchEndpoints.cs` の群は `RequireAuthorization()`。未認証は 401（同ファイルの注釈と `SearchEndpointsAuthorizationTests`） | **直す** |
| `DocumentManagementPage.tsx:73-80` | 「タグ辞書からの補完…本画面が補完へ載せる作業は未了」 | `api/useTagOptions.ts`（#449）が `/bff/tags` から選択肢を引いている | **直す** |
| `OperationsDashboardPage.tsx:186-189`（今は 217-220） | 「本画面へ到達できるのは platform-admin だけ」 | ルートは `RequireRole anyOf={[Admin, Operator]}`（#544）。結論（ConfigViewer は admin または operator なので判定は常に真）は変わらない | **直す**（前提だけ） |
| `AdminAbacSettingsPage.test.tsx:1276` | 「MCP クライアント管理が未実装（#445 待ち）」 | #2123 で反転済み（2026-10-26。導線がある陽性の試験） | 既に直っている |
| `SearchChatPage.tsx:27-30` | 個人資料の印の着手保留 | #2111 で解除の追記済み | 既に直っている |
| `AiSuggestionListPage.tsx:38-40`（監査の 254-256 は、監査の基点の同ファイルが 236 行で該当が無い。同名の試験の 254-256 は色だけに頼らない表示の試験で、誤りではない） | 「この文言が実際に出る経路は、まだ通っていない（ADR-0050 待ち）」 | `GraphDocumentSyncConsumer` が本文指紋の変化で `TryReinstate` を呼ぶ（試験 `GraphDocumentSyncConsumerTests`）。却下時の指紋は #2319 で埋まった | **直す** |

## 母集合（規則 9・10）

走査: `git grep -n "到達できるのは platform-admin\|補完へ載せる作業は未了\|実際に出る経路は、まだ通っていない\|は認可を持たない\|#445 待ち\|経路が通っていない\|経路がまだ通っていない\|起動する導線は本画面にも文書詳細にも無い\|生成を起動する導線"`（submodule・凍結記録〔specs・superpowers・adr〕・`CHANGELOG.md` を除く）。

| 当たり | 扱い |
| --- | --- |
| 上の「直す」4 か所 | 直す |
| `docs/screens/SC-21_ai-suggestion-list.md:27・141・163`（再提示の理由が付く経路が通っていない） | 直す（同じ事実の誤り） |
| `attribute_values.proto:23`・`GrpcAttributeValuesTests.cs:169`「REST の受け口は認可を持たない」 | 直す。REST の属性値の受け口は #1902 で撤去済み。`/search` も認証必須になった。gRPC の面が狭まる向きであることは変わらない |
| `docs/screens/SC-09_admin-abac-settings.md:268`（#445 待ち） | 既に「［2026-10-26 解消］…以下は当時の理由である」の下にある。触らない |
| `AdminAbacSettingsPage.test.tsx:1306` | 反転の経緯の記録。触らない |
| `SearchEndpointsAuthorizationTests.cs:115`（`/health/live` は認可を持たない） | 正しい（陰性対照）。触らない |
| `FR-18_ai-suggestions.md:19`・`SC-21_ai-suggestion-list.md:32・170`（利用者が生成を起動する導線が無い） | 直す（追記）。**#2106 の規則 10 の取りこぼし**である。導線は #2106 で SC-03 の承認欄に置いた（配置は planning#857 の裁定待ちの暫定） |

- 規則 10: 直した記述が、また別の誤りを生まないかを確かめる。
  - SC-10 は「admin / operator」に改める。ConfigViewer の判定（admin または operator）と同じ集合なので、「常に真」の結論は変わらない。
  - 再提示の経路は「通った」と書く。ただし稼働での発火は確かめていないので、「稼働で確かめた」とは書かない。

## 受け入れ基準

- [ ] 上の各コメントと文書が実装と一致する（挙動の変更 0。試験・ビルド・lint は従前どおり緑）。

## 実装中の追記（2026-10-30）

- 直したのはコメント 6 か所（C# 2・proto 1・TSX 3）と文書 2 本（SC-21 の画面仕様書・FR-18 の機能仕様書）である。挙動の変更は無い。
- `attribute_values.proto` はコメントだけの変更である。`check-contract-schema` は OK（型の変更 0）。
- 走査の結果と扱いは §母集合 のとおり。
- SC-21 の画面仕様書の対応表の「再提示の理由表示」は「一部する」から「する」へ改めた。経路が通り、購読側の試験（`GraphDocumentSyncConsumerTests`）が固定しているためである。

