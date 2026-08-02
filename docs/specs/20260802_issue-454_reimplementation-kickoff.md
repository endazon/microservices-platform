---
title: 全面再実装の着手準備（planning PR #144 の取り込みと進行規約の確定）
type: spec
status: done
related_ids: [NFR, IADR-0116]
author: Claude
created: 2026-08-02
updated: 2026-08-02
plan_refs:
  - "../../planning/projects/microservices-platform/INDEX.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/README.md"
---

# 仕様書: 全面再実装の着手準備（planning PR #144 の取り込みと進行規約の確定）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性・運用性。再実装プログラムの基盤整備）
- ユースケース（UC）/ 画面（SC）: なし（**再実装の対象範囲**は `FR-01..21` / `UC-01..11` / `SC-01..21` 全域）
- 関連 ADR: [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)（本作業で起案）
- 計画書リンク: [`planning/projects/microservices-platform/INDEX.md`](../../planning/projects/microservices-platform/INDEX.md)
- 上流の起点: project-planning PR #144（2026-08-02 マージ。`aeb97c4` → `df8bce5`）
- 本リポジトリの起点: #454（親トラッキング issue）

## 目的・背景

計画リポジトリの大幅更新（オープン issue 40 件の反映・ADR 11 件の起案・モックアップ全 24 画面の同期）を
受け、本リポジトリの実装をほぼ全面的に作り直す（#454）。子 issue は 18 件・4 フェーズに及ぶ。

**本作業は #454 のうち「着手準備」だけを対象とする**。すなわち、各子 issue が同じ前提の上で並行して
走り出せるように、(1) 新しい計画書を本リポジトリから参照可能にし、(2) 計画 ID レンジの拡大に
機械的検査を追随させ、(3) 18 件を回す進行規約を確定する。各子 issue の実装そのものは行わない。

準備を独立させる理由は、**このまま着手すると全子 issue が同じ誤りを踏む**ためである。

- planning submodule が PR #144 前（`aeb97c4`）を指しており、新しい ADR-0030〜0039・FR-17〜21・
  SC-18〜21 が**本リポジトリからは存在しない**。`check-commit-messages.js` の ADR 実在性検査は
  submodule を読むため、`feat(ADR-0030): ...` は**全 PR で落ちる**。
- `.claude/rules/traceability.md` の ID レンジが `FR-01..15` / `UC-01..07` / `SC-01..11` のままで、
  新 ID の参照が監査 / `trace-check` から「計画書に存在しない ID」と誤検出される。
- 併せて、計画 ID レンジ拡大は **AST（ai-stock-trading）との ID 衝突の性質を変える**（後述）。

## 対象範囲

- 対象:
  1. `planning` submodule の pin 更新（`aeb97c4` → `df8bce5`）
  2. `.claude/rules/traceability.md` の ID レンジ更新と、AST との衝突注記の是正
  3. 進行規約の確定（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)）と索引への登録
  4. 本仕様書（子 issue と起点 ID の対応表・着手順・ブロッカーの一覧）
- 対象外:
  - **各子 issue の実装**（#438〜#453 / #455〜#458）。それぞれの着手前に個別の作業仕様書を作成する。
  - 既存実装の削除・データの移行 / 破棄（#457 に集約。#456 完了前は旧データを破棄しない）
  - `.ai-context/` の生成物（`.gitignore` 対象のためコミットしない。必要なら各セッションで `/sync-plan`）
  - `CHANGELOG.md`（`changelog.yml` の生成物）

## 設計

### 1. 計画書の差分（PR #144 で何が増えたか）

`df8bce5` を `aeb97c4` と突き合わせた結果、本リポジトリの実装に効く増分は次のとおり。

| 区分 | 内容 |
| --- | --- |
| 機能要求 | `FR-16` に加え **`FR-17`〜`FR-21` が起案**（知識グラフ・AI 提案・個人資料・Obsidian 連携・文書本文の受け入れ経路）。`FR-01..16` は `fixed` |
| ユースケース | **`UC-08`〜`UC-11` を新設**（外部 AI エージェント利用・MCP クライアント管理・関係を辿る・個人資料の自己管理） |
| 画面 | **`SC-18`〜`SC-21` を新設**し、`SC-01`〜`SC-17` のモックアップを全面同期（wireframe 全 21 画面） |
| 技術検討 | `14_knowledge-graph-graphrag.md` を新設。`04_ai-rag-stack` / `05_observability-ops` / `07_abac-attribute-model` / `08_data-egress-policy` / `11_mcp-server-integration` / `13_frontend-stack` を改訂 |
| 計画 ADR | **`ADR-0030`〜`ADR-0039` を追加**（`ADR-0035` は番号予約のみで**未起案**）。`ADR-0004` / `0010` / `0024` / `0025` / `0031` / `0032` を改訂 |

計画 ADR の状態（`df8bce5` 時点の各ファイル front matter 実測）:

| ADR | 主題 | 状態 |
| --- | --- | --- |
| ADR-0030 | バックエンドアプリケーション層のライブラリ標準 | Accepted |
| ADR-0031 | フロントエンド技術スタック（React 19 + Vite + TanStack） | Accepted |
| ADR-0032 | SPA 認証は BFF セッション方式（Token Handler） | Accepted |
| ADR-0033 | 知識グラフのデータモデルとストア | Proposed |
| ADR-0034 | グラフ探索の ABAC 強制 | Proposed |
| **ADR-0035** | **GraphRAG 検索戦略** | **未起案（欠番・番号予約のみ）** |
| ADR-0036 | 所有者ベースの裁量アクセス制御 | Proposed |
| ADR-0037 | Obsidian 同期方式 | Proposed |
| ADR-0038 | analysis 用途の Fable 5 不使用 | Proposed |
| ADR-0039 | SC-18 のグラフ描画ライブラリ | Proposed |

`Proposed` の 6 件は **#454 の進め方の原則 4** のとおり決定内容は裁定済み（記録上の保留）であり、実装は
これらに従う。**未起案の `ADR-0035` に依存する範囲（RAG へのグラフ組み込み）だけは着手しない。**

### 2. ID レンジの拡大と AST との衝突（`.claude/rules/traceability.md`）

裸の ID は MSP（本リポジトリの主プロジェクト）の名前空間を指す。レンジを実測値へ更新した。

- 更新前: `FR-01..15` / `UC-01..07` / `SC-01..11`
- 更新後: `FR-01..21` / `UC-01..11` / `SC-01..21` / `ADR-0001..0039`（`ADR-0035` は欠番）

副作用として、**AST との ID 衝突の性質が変わる**。従来 `AST/FR-17` は「一方にしか存在しない ID」で
あったため、修飾を忘れても「参照切れ」として目立った。MSP に `FR-17`（知識グラフ）が生まれたことで、
これは**双方に存在し意味の異なる ID** へ変わり、修飾の欠落は**誤帰属**（別プロジェクトの要求へ
静かに紐づく）として現れる。`SC-18`〜`SC-21` も同様である。この差は検査で捕まらないため、規約本文に
明記した。

### 3. 進行規約（IADR-0116）

利用者裁定「プルリクは issue 毎に行う」を、既存の CI ゲートと整合する形で確定した。要点のみ再掲する
（根拠と却下した代替案は [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) を参照）。

- 子 issue 1 件 = ブランチ 1 本 = PR 1 本。`develop` へ直接マージし、長寿命の統合ブランチを設けない。
- ブランチ名は `<種別>/<起点ID>-<概要>`。起点 ID は子 issue タイトルのスコープ `()` の先頭 ID を採る
  （`NFR` しか無い場合は 2 番目以降の具体 ID を優先）。
- PR が大きくなる場合は **PR ではなく issue を分割**し、#454 のチェックリストへ追加する。
- 既存実装の一括削除は行わない。廃止・データ移行 / 破棄は #457 に集約し、**#456 完了前に旧データを破棄しない**。
- 各 PR は `/verify` 通過と `docs/DEFINITION_OF_DONE.md` の充足を条件とする。

### 4. 子 issue と起点 ID・ブランチ名の対応

各子 issue タイトルのスコープから機械的に導いた（`<種別>/<起点ID>-<概要>`）。着手時はこの名前を使う。

**フェーズ 0 — 基盤標準・先行検証（他のすべてに先行）**

| issue | 起点 ID | ブランチ名 |
| --- | --- | --- |
| #455 バックエンドアプリケーション層標準 | NFR, ADR-0030 | `feat/ADR-0030-backend-app-standard` |
| #446 SPA 基盤の新スタック移行 | NFR, ADR-0031 | `feat/ADR-0031-spa-foundation` |
| #453 退行防止テスト基盤 | NFR | `test/NFR-regression-test-foundation` |
| #456 ABAC 属性組み合わせ数の実測 | FR-17, FR-18 | `chore/FR-17-abac-attribute-measurement` |

**フェーズ 1 — platform コア**

| issue | 起点 ID | ブランチ名 |
| --- | --- | --- |
| #442 エッジ・実行基盤・CI/CD | NFR, ADR-0021/0023/0007/0008 | `feat/ADR-0021-edge-runtime-cicd` |
| #441 メッセージング・サービス間通信 | NFR, ADR-0027〜0029 | `feat/ADR-0027-messaging-service-comm` |
| #438 認証認可（Keycloak＋ABAC） | FR-05, FR-09, ADR-0036 | `feat/FR-05-authn-authz-abac` |
| #439 BFF セッション方式移行 | NFR, ADR-0032 | `feat/ADR-0032-bff-session-auth` |
| #440 LLM ゲートウェイ | FR-11, ADR-0038 | `feat/FR-11-llm-gateway` |
| #458 セキュリティ暫定運用の解消 | NFR, ADR-0005 | `feat/ADR-0005-security-interim-resolution` |

**フェーズ 2 — knowledge コア**

| issue | 起点 ID | ブランチ名 |
| --- | --- | --- |
| #447 取り込み・変換・コネクタ | FR-01, FR-02, FR-12, FR-21 | `feat/FR-01-ingestion-conversion` |
| #448 検索・RAG・AI 分析 | FR-03, FR-04, FR-07, FR-08 | `feat/FR-03-search-rag-analysis` |
| #449 文書管理・Wiki 閲覧 | FR-06, FR-13 | `feat/FR-06-document-wiki` |
| #443 可観測性・運用 | FR-10, NFR, ADR-0006 | `feat/FR-10-observability-ops` |
| #444 構成変更容易性 | FR-14, FR-15, ADR-0018 | `feat/FR-14-composability` |
| #445 MCP サーバー統合 | FR-16, ADR-0024 | `feat/FR-16-mcp-server-integration` |

**フェーズ 3 — 新機能・画面 / フェーズ 4 — 切替**

| issue | 起点 ID | ブランチ名 |
| --- | --- | --- |
| #450 知識グラフ・AI 提案 | FR-17, FR-18, ADR-0033/0034/0039 | `feat/FR-17-knowledge-graph-suggestions` |
| #451 個人資料・Obsidian 同期 | FR-19, FR-20, ADR-0036/0037 | `feat/FR-19-private-note-obsidian-sync` |
| #452 フロントエンド全画面 | SC-01〜21 | `feat/SC-01-frontend-all-screens` |
| #457 切替計画 | NFR | `chore/NFR-cutover-plan` |

### 5. 着手時のブロッカー（フェーズ順以外に効く制約）

| ブロッカー | 影響する issue | 解除条件 |
| --- | --- | --- |
| **ADR-0035 未起案** | #448（RAG へのグラフ組み込み）・#450（GraphRAG 連携） | #456 の実測 → 計画側で ADR-0035 起案。該当スコープのみ保留し、他は進める |
| **旧データが必要** | #456 | 旧データ破棄（#457）**より前**に実施する |
| **Wiki.js 個人スコープ出し分けの前提検証** | #451（ADR-0037 が覆り得る） | #449 の一部をフェーズ 0 で先行実施 |
| **`.github/workflows/` は GitHub App 権限で編集不可** | #442・#453 ほかワークフローに触る全 issue | ローカル（`workflow` スコープ）からコミット / プッシュする |

## 受け入れ基準

本作業（準備）の受け入れ基準である。各子 issue の受け入れ基準は個別の作業仕様書で定義する。

- [x] `git submodule status planning` が `df8bce5`（project-planning PR #144 のマージコミット）を指す
- [x] `planning/projects/microservices-platform/07_adr/` に `ADR-0030`〜`ADR-0039`（`ADR-0035` を除く）が実在し、
      `check-commit-messages.js` の ADR 実在性検査が新 ADR スコープを解決できる
- [x] `.claude/rules/traceability.md` の ID レンジが計画書の実測値（`FR-01..21` / `UC-01..11` / `SC-01..21`）と一致する
- [x] 進行規約が IADR として記録され、`docs/adr/README.md` の索引に登録されている
- [x] 子 issue 18 件すべてに起点 ID とブランチ名が対応づけられている（上表）
- [x] `node scripts/check-doc-links.js` が破損リンク 0 で成功する
- [x] `node scripts/check-commit-messages.js` が本 PR のコミット件名を通す

## テスト方針

本作業はドキュメントと submodule pin のみで、実行コードを変更しない。したがって受け入れ基準の検証は
既存の機械的検査に写像する。

| 受け入れ基準 | 検証手段 |
| --- | --- |
| submodule pin / 新 ADR の実在 | `git submodule status planning`・`ls planning/projects/microservices-platform/07_adr/` |
| ID レンジが計画書と一致 | 計画書からの ID 抽出（`grep -oE 'FR-[0-9]+'` 等）と規約本文の突合 |
| リンク健全性 | `node scripts/check-doc-links.js` |
| コミット件名の規約適合 | `node scripts/check-commit-messages.js`・`pr-title.yml`（PR タイトル） |
| IADR 索引の整合 | `docs/adr/README.md` に `IADR-0116` の行が在ること（連番・欠番なし） |

受け入れ基準 → テスト写像を各 PR で必須化する仕組み自体は **#453 の対象**であり、本作業では扱わない。

## 計画書との差異

- 差異: なし。本作業は計画書を取り込み、参照可能にするだけであり、計画の解釈を変更していない。
- ただし **`ADR-0035` の欠番**は計画側の既知の未完了事項（実測待ち）であり、差異ではなく前提として扱う。
  実測（#456）の結果は `/plan-feedback` で計画側へ環流する。

## 未決事項

1. **ADR-0035（GraphRAG 検索戦略）の起案**。#456 の実測が前提。それまで #448 / #450 の該当スコープは保留する。
2. **#453 の受け入れゲートの具体値**（カバレッジしきい値・契約 / アーキテクチャテストの範囲）。#453 の
   作業仕様書で確定し、確定後に IADR-0116 の規約 6 へ追記する。
3. **既存実装の破棄範囲**。#457 で確定する。本作業では削除を一切行わない。
