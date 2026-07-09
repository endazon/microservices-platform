---
title: 可変部品 共通実装ガイドの新設に伴う上流（10_composability-design）との相互参照追加・整合確認の提案
type: plan-feedback
status: open
category: その他
related_ids:
  - FR-14
  - FR-15
  - ADR-0018
source_repo: microservices-platform
source_ref: "PR #205 / docs/tech/composable-component-guide.md / docs/specs/20260709_composable-component-implementation-guide.md"
author: claude
created: 2026-07-09
---

# フィードバック: 可変部品 共通実装ガイドの新設 — 上流との相互参照追加・整合確認の提案

## 種別

その他（計画書の誤り・不足の指摘ではなく、実装側文書の新設に伴う相互参照の追加と整合確認の依頼）。

> 起票時の初版は「プラグイン提供者向け共通仕様の上流不足の疑い（要求の不足）」としていたが、
> PR #205 の AI レビュー（planning サブモジュール取得済み環境で実施）により、
> `10_composability-design.md` の §2「パイプライン段のプラグイン規約」・§3「イベント契約の標準化
> （後方互換の追加のみ許可）」・§4「差し替えポイント一覧」・§5「安全弁（誤構成対策）」が
> 当該共通仕様に相当する内容を既にカバーしていることが確認されたため、本記録へ**縮退**した。

## 起点となる計画書

- 機能要求（FR）: FR-14（宣言的構成とプラグイン追加のみで組み替え）・FR-15（構成情報 API）
- ユースケース（UC）: —
- 画面（SC）: —
- 関連 ADR: ADR-0018（コンポーザブルアーキテクチャ）
- 計画書リンク:
  - `projects/microservices-platform/06_technical/10_composability-design.md`
  - `projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md`
  - `projects/microservices-platform/06_technical/09_datasource-connectors.md`

## 現状（計画書の記述 / As-Is）

- `10_composability-design` §2〜§5 が、プラグイン段の規約・イベント契約の互換性ポリシー・
  差し替えポイント・誤構成対策（安全弁）という「可変部品提供者向けの上流共通仕様」を定めている。
- 一方で、実装リポジトリ側に新設した可変部品 共通実装ガイド
  （`docs/tech/composable-component-guide.md`）への参照は計画側に存在しない（新設のため当然）。
- 実装ガイドは上流に無い実装固有の手順（新サービスユニット追加・フロントエンド feature・
  合成ルート/DI 登録・CI 検証コマンド等）も扱っており、上流とガイドの対応関係は明文化されていない。

## 問題点 / あるべき姿（To-Be）

- 上流仕様（§2〜§5）と実装ガイドの対応が相互リンクされておらず、プラグイン実装者が上流仕様に
  辿り着けない／上流改版時に実装ガイドの追随漏れが起き得る。
- あるべき姿: 計画側 `10_composability-design` から実装ガイドへの参照を持ち、実装側は上流改版時に
  ガイド §1（接続仕様）を照合する運用が双方に明記されている状態。

## 実装で判明した経緯

- 作業仕様書 `docs/specs/20260709_composable-component-implementation-guide.md`（PR #205）で
  可変部品の実装指示が実装リポ内で分散していることを確認し、共通実装ガイドを新設した。
- 起票セッションの環境では planning サブモジュールが取得不可（リポジトリアクセスが
  microservices-platform のみ）だったため上流本文を照合できず、初版は「不足の疑い」とした。
  PR #205 の AI レビュー（planning 取得済み環境）が §2〜§5 の存在・カバー範囲を確認し、前提を訂正した。

## 提案（計画への反映案）

- 反映先候補: `06_technical/10_composability-design.md` の更新（参照追記）／その他（トリアージ判断）
- 提案内容:
  1. **相互参照の追加**: `10_composability-design` に実装リポジトリの
     `docs/tech/composable-component-guide.md`（実装向け詳細化）への参照を追記する。
  2. **整合確認**: 実装ガイド §1（基盤が提供する接続仕様）・§2（部品種別ごとの手順）が
     §2〜§5 の上流仕様と矛盾しないかを計画側でも一読・確認する（実装側は本セッションで
     上流本文の全文照合ができていないため、齟齬があれば指摘いただきたい）。
  3. **カバー範囲の差分確認（任意）**: 上流 §2〜§5 が主にパイプライン段・イベント契約を対象と
     しているのに対し、実装ガイドは新サービスユニット追加・フロントエンド feature・コネクタ
     （09_datasource-connectors）まで部品種別を広げている。これらを上流の共通仕様の射程に
     含めるかはトリアージで判断されたい。

## 影響範囲

- 計画側: `10_composability-design.md` への参照追記のみ（要求・ADR の変更なし）。
- 実装側: 上流改版時の照合運用をガイド §5 に明記済み（`docs/tech/composable-component-guide.md`）。
  対応がない場合も実装は進行可能だが、上流と実装指示の対応関係が暗黙のままとなり、
  外部チーム・サブモジュールでのサービス追加時に上流仕様が参照されないリスクが残る。
