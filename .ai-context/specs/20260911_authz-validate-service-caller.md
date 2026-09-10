---
title: POST /authz/attributes/validate に ServiceCaller を掛け、/authz のサービス面を計画 ADR-0089 決定 2 に揃える
issue: "#1255"
plan_refs:
  - FR-05
  - FR-09
  - ADR-0088
  - ADR-0089
adr_refs:
  - IADR-0413
status: done
created: 2026-09-11
---

# 作業仕様書: `/authz/attributes/validate` の認可（計画 ADR-0089 決定 2）

## 起点

- 計画 ADR-0089 決定 2（2026-09-09）: `/authz` のサービス面は**すべて**呼び出し元サービスの資格を要求する。同 §結果
  フォローアップ 2 は「主体は実装側、時期は着地の直後」。#1255 の実装者（AI）が `MapValidateDocumentAttributes()` が
  `admin` サブグループの外＝認可なしで残っていることを再確認し、所有者不在として報告した。

## 実測（origin/develop）

`AuthzEndpoints.cs`: `services`（`ServiceCaller`）には `MapResolveScope()` だけ、`g.MapValidateDocumentAttributes()` は素通し。
呼び出し元は無い（`deploy/local/abac-seed/README.md`「呼ぶ取り込み経路は現時点で存在しない」・`src/` に `attributes/validate`
の呼び出しは AuthorizationService の試験以外に無い）。`docs/security/security.md` は既に本端点を ServiceCaller と記述。

## 設計

| 対象 | 変更 |
| --- | --- |
| `AuthzEndpoints.cs` | `MapValidateDocumentAttributes()` を `services` サブグループ（`ServiceCaller`）へ移す。コメントを追随 |
| `AuthzManagementEndpointTests.cs` | 既存の陽性 2 件を `CreateServiceCallerClient()` で呼ぶ。陰性対照 2 件（platform-service なし / 管理者の利用者トークン → 403）を追加 |
| IADR-0413 | 日付つき追記（掛けたポリシー・呼び出し元の追随は不要）。索引の行に注記 |

新 IADR は起こさない（ADR-0089 が「`/scope` と揃える」と方向を定めており、IADR-0413 の決定の延長）。

## 走査した母集合（規則 2・9）

`attributes/validate` で追跡下の全ファイルを走査: `AuthzEndpoints.cs`（変更）、`ValidateAttributes/Endpoint.cs`（据え置き・
グループ側で守る）、`AuthzManagementEndpointTests.cs`（変更）、`docs/api/openapi.yaml`（据え置き・security 記述は既存）、
`docs/security/security.md`（据え置き・既に ServiceCaller と記述）、`docs/functional/FR-09_*.md`（据え置き）、
`deploy/local/abac-seed/*`（据え置き・呼び出し元不在の記述）。

## 受け入れ基準

- [x] AuthorizationService.Tests 全緑（AuthzManagementEndpointTests 16 件）
- [x] 変異: 門を外すと陰性対照 2 件だけが赤
- [x] `dotnet format --verify-no-changes` 差分なし
- [ ] 着地後、計画 ADR-0089 フォローアップ 2 として planning#573 へ着地報告を環流する
