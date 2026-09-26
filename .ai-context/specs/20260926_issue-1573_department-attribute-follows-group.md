---
title: 作業仕様書 — ABAC が読む利用者属性 department を部門グループ所属に一致させる（#1573・計画 ADR-0115 決定 3）
type: spec
status: in-progress
related_ids:
  - FR-05
  - FR-09
  - UC-05
  - SC-17
  - ADR-0115
  - ADR-0026
  - ADR-0088
  - IADR-0301
  - IADR-0329
  - IADR-0369
  - IADR-0385
  - IADR-0413
  - IADR-0428
  - IADR-0468
  - IADR-0472
  - IADR-0473
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0115_department-domain-is-realm-group-and-default-from-registrant.md 決定 3（利用者の部門の正本は部門グループ。属性はグループに合わせて直し、逆向きには直さない。自動で導くか検知して直すかは実装判断）
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md §利用者属性（department の正本は部門グループ）
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1（判定に使う属性は IdP から引き直す）
related_specs:
  - 20260926_issue-1557_department-domain-validation.md
  - 20260926_issue-754_department-from-registrant-group.md
issue: "#1573"
---

# 作業仕様書 — 利用者属性 department を部門グループ所属に一致させる

## 目的と射程

計画 ADR-0115 決定 3 は「利用者の部門の正本は部門グループへの所属であり、ABAC が読む利用者属性 `department` はそれと一致させる。
食い違いを見つけたら属性をグループに合わせて直す（逆向きには直さない）」と定めた。本作業は**検知して直す形**でこれを入れる。

**前提**: PR #1572（#1557）が足した `IIdentityAdminClient.FindGroupByPathAsync` を使うため、本ブランチは #1572 の上に積む（#1572 を先にマージする）。

**射程外**: SC-17 の部門欄（属性を直接編集できる）と決定 3 の整合は計画側の問いとして環流する（本作業は SC-17 の挙動を変えない）。
① フォルダ写像・データソースの部門は本作業と無関係。

## 受け入れ基準（#1573）

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 食い違い（属性がグループのコードと違う・属性が無い）を検知する | T-43（計画の純関数・同期） |
| AC-2 | 属性をグループのコードへ直す。グループの所属は変えない | T-44 |
| AC-3 | 部門グループが 0 個・2 個以上の利用者は未解決として上書きしない（消しもしない）。入れ子は上位のコードに畳む | T-45 |
| AC-4 | 冪等（2 周目の書き込みは 0 件） | T-46 |
| AC-5 | 既定で無効（明示の構成で有効化するまで IdP へ問い合わせもしない）。値域外の構成は起動時に落ちる | T-47 |
| AC-6 | IdP 実装: 所属者・子グループはページを最後まで読む。書き込みは `department` 1 キーだけで他の属性の多値を落とさず、捨てられたら例外 | T-48 |

## 設計（詳細は IADR-0473）

- **置き場所**: AuthorizationService の定期処理（`DepartmentAttributeSyncHostedService` → `DepartmentAttributeSync`）。判定は純関数
  `DepartmentAttributeReconciliation.Plan`（Domain）。
- **opt-in**: `DepartmentAttributeSync:Mode` = `Off`（既定・IdP へ問い合わせない）／`Report`（検知して記録するだけ）／`Fix`（直す）。
  周期 `DepartmentAttributeSync:Interval`（既定 1 時間）。helm / compose の既定値は置かない（＝ Off）。
- **集め方**: `/department` を `FindGroupByPathAsync` で引き、子グループを辿り（入れ子を含む）、各グループの直接の所属者を属性つきで読む。
  部門コードはグループの**パス**の第 1 セグメント。所属者として現れない人（0 個・サービスアカウント）には触れない。
- **直し方**: `SetDepartmentAttributeAsync`（新設。`department` 1 キーだけを差し替え、他の属性は多値のまま持ち越す。読み直して確かめる）。
- **触らないもの**: グループ所属・他の属性・ロール・有効状態・セッション・クレーム・マッパー・クライアント・secret。
  realm の reconcile Job（`deploy/local/keycloak-setup/`）は変えない。

## 母集合の引き直し（IADR-0141 決定 1）

**軸 1 — 「一致を保つ仕組みが無い」と書いている箇所（誤りの側）**: `git grep -n "同時に変える\|二重管理\|一致を保つ\|食い違いの検知"`（`CHANGELOG.md` 除外）。
実装リポジトリには該当が無い（計画 ADR-0115 の「配備までの暫定手段」「悪い影響」が持つ記述であり、計画側の追随は環流で行う）。

**軸 2 — 利用者属性 department の出所を語る箇所**: `git grep -n "department" -- docs/screens/SC-17* docs/tests/SC-17* docs/security docs/operations`。

| 引いた箇所 | 扱い |
| --- | --- |
| `docs/screens/SC-17_user-account-management.md` の部門欄（「属性辞書の許可値のみ」） | **反映**（Fix を有効にすると、部門グループにちょうど 1 つ属する人の部門欄の編集は次の周期でグループへ戻る旨を注記） |
| `docs/tests/SC-17_user-account-management.md` | **反映**（T-43〜T-48 を足す） |
| `docs/operations/operations.md` | **反映**（有効化・段階的な適用・ロールバックの手順を足す） |
| `docs/security/security.md` の構成の表（`RetentionAnchor:Source`） | **反映**（同じ表へ `DepartmentAttributeSync:Mode` を足す。IdP へ書く定期処理であり、既定 Off であることを明記） |
| `deploy/keycloak/microservices-platform-realm.json`（`department` マッパー） | **除外**（クレームの出所。本作業は属性を直すだけでマッパーを変えない。AST のクライアントが依存する） |
| `deploy/local/keycloak-setup/reconcile-realm.js` | **除外**（人間の利用者の属性は実行時所有で触らない境界。IADR-0369 決定 2） |
| `BffScopeResolver.cs` 等の読み手 | **除外**（読み方は変えない） |

**軸 3 — ポートの実装・装飾（パスから引く）**: `git grep -ln "IIdentityAdminClient"` → Keycloak / InMemory の 2 実装、試験の装飾
`TestIdentityDirectory.StubIdentityAdminClient`、契約試験 `IdentityAdminContractTests`（メソッド名の陽性対照）。**4 つとも更新する。**

**軸 4 — 自分の変更で新たに誤りになる記述（規則 10）**: `IIdentityAdminClient` の冒頭注記「属性の実体は Keycloak のユーザー属性ひとつ」は正しいまま
（正本の所在が変わるのは `department` だけで、実体は属性のまま）。SC-17 画面仕様の「部門＝属性辞書の許可値のみ」は Fix 有効時に実効が変わるため注記する。
