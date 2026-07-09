---
title: IADR-0045 BFF 文書書き込みのスコープ確認往復は多層防御の要のため現時点で維持し最適化を保留する
type: impl-adr
status: Accepted
related_ids:
  - FR-06
  - FR-09
  - NFR
  - UC-03
  - ADR-0004
  - IADR-0009
  - IADR-0038
  - IADR-0041
  - IADR-0044
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-09)"
  - "../../planning/projects/microservices-platform/02_requirements/02_nfr.md (NFR 性能)"
---

# IADR-0045: BFF 文書書き込みのスコープ確認往復は多層防御の要のため維持し、最適化を測定まで保留する

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-06（文書）／FR-09（認可）／NFR（性能）／UC-03
- 関連 ADR: [[IADR-0041]]（文書書き込みの BFF ABAC スコープゲート）／[[IADR-0038]]（読み取りの BFF ABAC ゲート）／[[IADR-0009]]（存在秘匿）／[[IADR-0044]]（後段サービスの認可・多層防御）
- 関連仕様書: `docs/security/security.md`
- Issue: #179（PR #171 レビュー 🟢 / NFR / priority:could）

## コンテキストと課題

`DocumentBffEndpoints.ForwardIfInScope`（[[IADR-0041]]）は、書き込み（更新／メタ更新／公開／
アーカイブ／削除）のたびに **(1) 対象文書の GET（ABAC スコープ確認）→ (2) 実処理の転送** という
2 往復を後段 `DocumentService` に対して行う。書き込み頻度が増えるほど後段負荷が倍加する（NFR）。

Issue #179 は「往復を削減できないか」を将来の改善点として起票した。当初の素朴な最適化候補は
「[[IADR-0044]] で後段 `DocumentService` に認可を課したのだから、BFF のプリフライト GET は冗長では
ないか（1 往復に畳めるのでは）」というもの。

## 決定

**現時点では 2 往復を維持し、最適化は実測（書き込み QPS・レイテンシ）で必要性が確認されるまで保留する。**

素朴な「プリフライト GET の削除」は**セキュリティ回帰**であり採らない。

## 根拠

[[IADR-0044]]（#174）が後段に課したのは **ロール認可（`platform-admin` / `platform-operator`）のみ**で、
**文書単位の ABAC スコープ照合ではない**。`DocumentService` の書き込みハンドラは対象文書の属性
（`confidentiality` 等）と利用者スコープの突合を行わない。すなわち：

- **文書単位の ABAC スコープ強制は BFF のプリフライト GET が唯一の実施点**（[[IADR-0041]]）。
- プリフライト GET を削除すると、admin/operator ロールを持つが当該文書の ABAC スコープ**外**の
  利用者が、閲覧できない文書を更新・削除できてしまう（[[IADR-0009]] の存在秘匿＝スコープ外は 404 も破れる）。

したがって [[IADR-0044]] 後も 2 往復は**冗長ではなく、ABAC 書き込みゲートの実体**である。

加えて Issue #179 自身が「早すぎる最適化を避け、実測後に着手する」と明記しており、`priority:could`。
現状の dev 規模では往復のレイテンシは問題化していない。

## 代替案（将来・実測で正当化された場合）

いずれも本 issue のスコープ（NFR・could）を超える設計変更のため、**測定で必要性が示されてから**
別 issue で扱う。

1. **後段 `DocumentService` に ABAC スコープ判定を内包する条件付き書き込み**: 利用者の解決済み
   スコープ（または資格情報）を渡し、サービス側で属性突合し不一致を 404 で返す。1 往復化できるが、
   [[IADR-0041]] が「ABAC スコープは BFF に集約」とした方針の再検討を要し、スコープ解決の重複
   （AuthorizationService 呼び出し）をどこに置くかの整理が必要。
2. **BFF での属性の短期キャッシュ**: 直近取得した文書属性を TTL 付きでキャッシュし、書き込み直前の
   GET を省く。整合性（他者更新での属性変化）とキャッシュ無効化の設計が必要。
3. **条件付き更新にスコープ判定を含める**: DocumentService の楽観ロック（ExpectedVersion）に
   ABAC 述語を相乗りさせる。契約が複雑化する。

## 影響

- コード変更なし（本 ADR は決定の記録）。既存の 2 往復・存在秘匿・[[IADR-0041]] のゲートを維持する。
- Issue #179 は「実測で正当化されるまで保留」の NFR として open のまま追跡する（クローズしない）。
- 将来この経路を「最適化」する変更は、本 ADR の根拠（ABAC 書き込みゲートの唯一の実施点）を
  必ず考慮し、素朴なプリフライト GET 削除を行わないこと。

## 参照

- Issue #179 / PR #171 レビュー（🟢）
- [[IADR-0041]] / [[IADR-0044]] / [[IADR-0038]] / [[IADR-0009]]
- `src/Bff/KnowledgePlatform.Bff/Foundation/Endpoints/DocumentBffEndpoints.cs`（`ForwardIfInScope`）
