---
title: BFF 文書書き込みのスコープ確認往復の最適化検討（Issue #179）
type: spec
status: completed
related_ids:
  - FR-06
  - FR-09
  - NFR
  - UC-03
  - IADR-0041
  - IADR-0044
  - IADR-0045
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/02_nfr.md (NFR 性能)"
---

# 仕様書: BFF 文書書き込みのスコープ確認往復の最適化検討（Issue #179）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-06（文書）／FR-09（認可）／NFR（性能）
- 関連 ADR: [[IADR-0041]]（BFF ABAC 書き込みゲート）／[[IADR-0044]]（後段の認可）／[[IADR-0045]]（本件の決定）
- Issue: #179（PR #171 レビュー 🟢 / priority:could）

## 目的・背景

`DocumentBffEndpoints.ForwardIfInScope` が書き込みのたびに「GET（スコープ確認）→ 書き込み」の 2 往復を
後段へ行う点を、NFR（性能）として最適化できないか検討する。

## 調査結果（結論）

- [[IADR-0044]]（#174）が後段 `DocumentService` に課したのは**ロール認可のみ**で、**文書単位の ABAC
  スコープ照合ではない**ことを実コード（`DocumentEndpoints.cs`）で確認した。
- したがってプリフライト GET は**冗長ではなく、ABAC 書き込みゲートの唯一の実施点**（[[IADR-0041]]）。
  素朴な削除は**セキュリティ回帰**（スコープ外 admin/operator による変更・存在秘匿の破れ）となる。
- Issue #179 自身が「実測後に着手・早すぎる最適化を避ける」と明記。`priority:could`。

## 対応内容（本 PR）

コードの往復削減は行わず、**決定を記録し将来の footgun を防止**する。

1. [[IADR-0045]] を新設し、「2 往復を維持・最適化は実測まで保留」「素朴なプリフライト GET 削除は
   セキュリティ回帰」を根拠付きで記録。安全な代替案（後段条件付き書き込み／BFF 属性キャッシュ等）と
   その適用条件（実測での正当化）も明記。
2. `DocumentBffEndpoints.ForwardIfInScope` にガードコメントを追記し、コード直近で削除禁止の根拠を示す。
3. Issue #179 は「実測で正当化されるまで保留」の NFR として **open のまま追跡**（クローズしない）。

## 受け入れ基準

- [ ] 後段の認可が ABAC スコープを含まないことをコードで確認・記録した。
- [ ] IADR-0045 に決定・根拠・代替案・適用条件を記録した。
- [ ] コード直近にガードコメントを追加した。
- [ ] コード動作変更なし（BFF ビルドが通る）。

## 影響・リスク

コード動作の変更なし（コメントと ADR のみ）。将来の最適化時に安全性の判断材料を残す。
