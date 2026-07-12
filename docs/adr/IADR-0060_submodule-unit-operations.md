---
title: IADR-0060 追加可変機能ユニットの submodule 運用（CI 自動発見・トークン付き取得・バージョン固定）
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - IADR-0056
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-14: 構成変更で完結する疎結合ユニット)"
---

# IADR-0060: 追加可変機能ユニットの submodule 運用

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-14（構成変更のみで完結する疎結合ユニット）
- 関連 ADR: [[IADR-0056]]（ユニット第一構成）／[[IADR-0057]]（依存方向検査）／[[IADR-0058]]（private submodule の CI 取得）
- 関連仕様書: `docs/specs/20260711_issue-230_submodule-unit-ops.md`、`docs/how-to/adding-a-unit-submodule.md`、[`src/README.md`](../../src/README.md)
- Issue: #230（IADR-0056 フォローアップ 4）

## コンテキストと課題

再編（#210 / IADR-0056）で、追加可変機能ユニットを `src/<unit>/`（`backend/` + `frontend/`）の git submodule としてリンクする構成が確定した。しかし実運用（テンプレート・CI 連携・単独ビルド規約・バージョン固定）が未整備で、`src/README.md` の手順も簡潔な箇条書きに留まる。**新規ユニットを最小の手数で組み込める運用**を定める必要がある。

制約:
- **バックエンド CI がユニットをハードコード**していた（platform / knowledge の slnx を名指し）。ユニット追加のたびに CI 編集が要る。
- 追加ユニットは**別リポジトリ（多くは private）**の submodule であり、CI での取得にはトークンが要る（[[IADR-0058]] と同じ論点）。
- 共通 MSBuild 設定（`src/Directory.Build.props` / `Directory.Packages.props`）は `src/` 直下の単一情報源で、ディレクトリ階層により全ユニットへ継承される。ユニットが**独自に `Directory.Build.props` を持つと、submodule 配置時に `src/` の単一情報源より近い階層で発見され上書き**してしまう（MSBuild は最も近い 1 つで停止）。

## 検討した選択肢

**CI のユニット取り込み**:
1. **slnx をハードコードし追加時に 1 行足す**（現状）: 単純だが編集を要し、追記漏れが起きうる。
2. **マトリクス化（`strategy.matrix`）で各ユニットを別ジョブに**: 並列化できるが、必須チェック名が `build-and-test (platform)` 等に分岐し、ブランチ保護の必須チェック設定を都度更新する必要が生じる（運用負荷・移行時の取りこぼし）。
3. **単一ジョブ内で `src/*/backend/backend.slnx` を自動発見してループ（本決定）**: チェック名（`build-and-test` / `lint`）を維持したままユニットを自動発見。チェックアウト済みのユニットは CI 編集なしで対象になる。

**単独ビルド規約（Directory.Build.props 上書き問題）**:
1. ユニットに常設の `Directory.Build.props` を置く: submodule 配置時に単一情報源を上書きするため不可。
2. **ユニットは共通設定を持たず、単独リポでのビルド時のみ親を import-chain するフォールバックを用いる（本決定）**: submodule 配置時は `src/` の単一情報源を尊重し、単独時のみ自前設定を効かせる。

## 決定

1. **CI 自動発見**: `ci.yml` の `lint` / `build-and-test` を `src/*/backend/backend.slnx` のループに変更する。チェック名は不変。チェックアウト済みの全ユニットを自動的に検査・ビルド・テストする。
2. **submodule 取得**: 追加ユニット（private）を CI で取得するには `actions/checkout` の `submodules: recursive` + read 権限を持つトークンを有効化する（[[IADR-0058]] の `doc-links-planning.yml` と同型。未取得ユニットは glob に現れず対象外になるため、取得の有効化が組み込みの前提）。
3. **テンプレート**: 新ユニットの雛形を `templates/unit-template/`（backend slnx + サンプルサービス、frontend package.json + features 合成点）として提供する。テンプレートは本リポジトリのビルド対象ではない（`src/` 外・どの slnx にも含めない）。
4. **単独ビルド規約**: ユニットは常設の `Directory.Build.props` を持たない。単独リポでのビルドが要る場合のみ、親を import-chain するフォールバック props を用いる（`templates/unit-template/README.md` に記載）。
5. **バージョン固定**: submodule は gitlink（特定コミット）で固定し、更新は本体リポの PR で pin を進める。Renovate/Dependabot の `git-submodules` マネージャで更新 PR を自動化できる（有効化はメンテナ判断）。

## 理由

- **ゼロ編集の組み込み**: 自動発見により、チェックアウト済みユニットは CI を編集せず検査対象になる（合成点 1 行 + submodule 取得の有効化のみ）。マトリクスと違い必須チェック名が安定し、ブランチ保護の再設定が不要。
- **単一情報源の保全**: ユニットに常設 props を置かない規約で、submodule 配置時の MSBuild 上書きを防ぐ（CLAUDE.md「個別プロジェクトで上書きしない」を担保）。
- **既存様式との一貫**: private submodule の CI 取得はトークン付き（[[IADR-0058]]）で統一。

## 結果

- `.github/workflows/ci.yml`: `lint` / `build-and-test` を `src/*/backend/backend.slnx` 自動発見ループへ。
- `templates/unit-template/`: 新ユニット雛形（backend/frontend）。
- `docs/how-to/adding-a-unit-submodule.md`: 通し運用手順（テンプレ→submodule→合成点→CI/トークン→バージョン固定）。
- `src/README.md`: サブモジュール追加手順を how-to へリンク・CI 自動発見に更新。

## フォローアップ（#230 に残す・本リポ外）

- **サンプルユニットでの通し検証（別リポジトリ必須）**: テンプレートから実ユニットを作成し submodule 追加、ビルド・テスト・compose 起動の end-to-end 確認。本リポジトリ内では完結できないため #230 にコメントで残す。
- CI の submodule 取得トークン（`UNIT_REPO_TOKEN` 等）の登録と、対象 checkout への適用（実ユニット追加時）。
- Renovate/Dependabot の `git-submodules` 有効化（メンテナ判断）。

## 関連

- Supersedes: なし
- Superseded by: なし
- フォローアップ改定: 決定②（追加ユニットの CI submodule 取得方式）は [[IADR-0065]] で改定した。
  private な `planning`（[[IADR-0058]]）を巻き込む `submodules: recursive` を避け、`src/*` のユニット
  submodule のみ非再帰取得する方式に変更（public ユニットはトークン不要）。本 ADR の他の決定は有効。
