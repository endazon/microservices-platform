---
title: IADR-0022 既定モデルを opus 化し、fable-5（最難関）と GitHub Copilot 経路を設定駆動で追加する
type: impl-adr
status: Accepted
related_ids:
  - ADR-0010
  - IADR-0007
  - FR-11
  - UC-02
author: claude
created: 2026-07-06
updated: 2026-07-06
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (Accepted)"
  - "../../planning/draft/feedback/20260706_adr-0010-model-decision-b.md (triage: accepted)"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
related_specs:
  - ../specs/20260706_ADR-0010_default-model-and-fable5-copilot.md
  - ../specs/20260702_FR-11_llm-egress-routing.md
---

# IADR-0022: 既定モデルを opus 化し、fable-5（最難関）と GitHub Copilot 経路を設定駆動で追加する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-06
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: ADR-0010（`Accepted`）、FR-11、UC-02、08_data-egress-policy
- 関連する実装 ADR: IADR-0007（設定駆動の LLM ルーティング）
- 関連する実装仕様書: [20260706_ADR-0010_default-model-and-fable5-copilot.md](../specs/20260706_ADR-0010_default-model-and-fable5-copilot.md)

## コンテキストと課題

ADR-0010（計画）は既定を `claude-opus-4-8`、定型を `claude-sonnet-4-6`/`claude-haiku-4-5`、最難関を
`claude-fable-5`／GitHub Copilot SDK と定める。実装側の既定は `claude-sonnet-4-6`、用途別は
`analysis→opus` 等で、`claude-fable-5` と GitHub Copilot SDK は未実装だった（Issue #69）。

計画側は Issue #69 の実装側推奨 (a)（実態追認）を把握のうえ、**(b) 実装追従**を採用して ADR-0010 を
`Accepted` に確定した（トリアージ記録 `draft/feedback/20260706_adr-0010-model-decision-b.md` = accepted）。
本 IADR は、この (b) をどう実装へ落とすかの設計判断を記録する。既存の IADR-0007（設定駆動ルーティング＋
越境マトリクス）を壊さず追従する必要がある。

論点:
1. 既定モデルと用途別マッピングをどう変えるか。
2. 「最難関」に fable-5 をどの用途へ割り当てるか。
3. GitHub Copilot をどのプロバイダ実装・どのティア・どの有効状態で追加するか（越境統制と整合させる）。

## 検討した選択肢

1. **越境マトリクスやルーターのロジックを拡張して新モデル/新プロバイダを直書きする**。
   最短だが IADR-0007 の「切替は設定駆動」に反し、契約改定・ティア再判定のたびにコード変更を招く。
2. **IADR-0007 の設定駆動を維持し、設定（`Llm:Model`/`PurposeModels`/`Endpoints`）とプロバイダ追加のみで追従する**（本決定）。
   越境マトリクス（`EgressMatrix`）はセキュリティ要件そのものなのでロジックを変えず、Copilot には
   ティアを割り当てるだけで既存の統制に載せる。
3. **Copilot を既定有効・ティアB として即座に本番投入する**。用途は広がるが、Copilot の契約条件
   （ZDR/学習不使用/レジデンシー＝08_data-egress-policy の B 認定要件）が未確定のまま機密データを
   越境させる恐れがあり、安全側原則に反する。

## 決定

選択肢2を採用する。

- **既定モデル**: グローバル既定を `claude-sonnet-4-6` → `claude-opus-4-8` に変更する
  （`Llm:Model` / `Llm:DefaultModel` および各コードのフォールバック文字列）。
- **用途別モデル**（`Llm:Routing:PurposeModels`、ADR-0010 のティア対応）:
  - `default` → `claude-opus-4-8`（既定）
  - `rag-answer` → `claude-sonnet-4-6`（定型）
  - `diagram-coding` → `claude-haiku-4-5`（定型・最軽量）
  - `analysis` → `claude-fable-5`（最難関）。深い AI 分析用途を最難関と位置づける。
- **fable-5 経路**: `claude-fable-5` は Claude SDK 経由のモデルのため、claude-managed（ティアB、
  プロバイダ `claude`）エンドポイントの `Models` に追加する。新エンドポイントは作らない。
- **fable-5 の ZDR 非対応対応（モデル単位の除外）**: 08_data-egress-policy の注意点は「Claude Fable 5 は
  ZDR（ゼロデータ保持）非対応。ZDR を要件とする用途では ZDR 対応が確認できたモデルに限定する」と明記する。
  IADR-0007 のティア判定はエンドポイント単位で、同一ティアB内のモデル差（fable-5 の ZDR 非対応）を区別できない。
  そこで **モデル単位の ZDR メタデータ**を導入する。`LlmEndpointOptions.NonZdrModels` に ZDR 非対応モデルを
  列挙し（`appsettings.json` の claude-managed では `["claude-fable-5"]`）、`EgressMatrix.RequiresZeroDataRetention`
  が真となる機密区分（**confidential/restricted**、未知区分も安全側で真）では `LlmRouter` が当該モデルを候補から
  除外する。除外の結果、confidential/restricted の analysis は fable-5 ではなく ZDR 対応の既定モデル
  （opus）へフォールバックする。適格モデルが 1 つも無い場合は送信しない（安全側で拒否）。
  public/internal（ZDR 非要件）では fable-5 を選択できる。
- **GitHub Copilot 経路**: `CopilotProvider`（`ILlmProvider`）を追加し、キー付き DI（`copilot`）で登録する。
  トランスポートは OpenAI 互換 `/chat/completions`（`SelfHostedProvider` と同型、ベアラトークン付与）とし、
  専用 NuGet 依存を増やさない。エンドポイント `copilot-managed` を設定に定義する。
- **Copilot のティアと有効状態**: 送信先ティア（08_data-egress-policy の契約条件）が未確定のため、
  **安全側でティアC・`Enabled=false`** とする（selfhosted と同じ後付けパターン）。越境マトリクスにより
  ティアC は confidential/restricted で不可、internal×C は要承認となる。契約条件確定後に設定で
  有効化・ティア再判定する。
  なお 08_data-egress-policy 62 行目は「GitHub Copilot／Azure 等も、エンタープライズデータ保護条件を
  確認のうえティアBとして扱う」とする。本 IADR の判断（未確定のため安全側でティアC）はこの記述と矛盾せず、
  **保護条件が確認できた段階で `copilot-managed` を設定でティアBへ再判定する**（下記フォローアップ）。

## 理由

- IADR-0007 の「切替は設定駆動」を維持することで、モデル追加・プロバイダ追加を **設定変更＋プロバイダ実装**
  に閉じ込め、越境統制（`EgressMatrix`）のテスト可能な固定表を温存できる（可監査性の担保）。
- fable-5 は Claude SDK 経由のため、既存 `ClaudeProvider`/ティアB エンドポイントに `Models` 追加だけで
  乗る。新プロバイダ・新ティアは不要で、変更最小。
- Copilot の契約ティアは計画ドキュメント上まだ確定していない。ADR-0010 は Copilot を「最難関の別経路」と
  位置づけるが、B 認定（ZDR/学習不使用/レジデンシー）の裏取りが無い状態で有効化すると機密データを
  未認定経路へ越境させかねない。実装は「経路を用意して安全側で無効化」し、確定後に有効化する
  （CLAUDE.md「曖昧な場合は止めて安全側」、IADR-0007 のティア判定メタデータ化フォローアップに整合）。

## 結果

- 良い影響: ADR-0010（`Accepted`）に実装が追従する。既定 opus・最難関 fable-5 が用途別に発火し、
  Copilot 経路はプロバイダ・DI・エンドポイント・越境統制まで組み込み済みで、確定後は設定 1 行で有効化できる。
- 悪い影響・トレードオフ: 既定 opus 化により既定経路のコストが上がる（用途別で定型は sonnet/haiku に
  縮退させ影響を限定）。Copilot は無効のため現時点では実送信されない（要件どおり経路のみ整備）。
- fable-5 の ZDR 非対応は `NonZdrModels` メタデータ＋`RequiresZeroDataRetention` により、confidential/restricted で
  自動除外される（fable-5 は public/internal の analysis に限定して発火）。IADR-0007 が挙げていた「ティア判定根拠の
  メタデータ化」フォローアップの一部を、モデル単位の ZDR 属性という形で前進させた。
- フォローアップ:
  - Copilot の送信先ティア確定（08_data-egress-policy の契約条件レビュー）→ 確定後に `copilot-managed`
    の `Tier`/`Enabled` を設定で更新。B 認定なら confidential まで、C のままなら public 用途に限定。
  - Copilot の実 API 認証・エンドポイント URL の確定（`Llm:Copilot:BaseUrl`/`Token`）。
  - `CopilotProvider` は `SelfHostedProvider` と OpenAI 互換レスポンス型・リクエスト構築が重複する。現状は既定無効・
    スコープ最小のため許容だが、Copilot 有効化時に共通の OpenAI 互換クライアント基底へ切り出す。
  - ZDR 対応状況の継続確認（モデル・契約で変動しうるため、`NonZdrModels` は都度見直す）。
  - 既定 opus 化のコスト監視（トークン計測・上限制御は ADR-0010 のフォローアップに合流）。

## 関連

- Supersedes: なし
- Superseded by: なし
