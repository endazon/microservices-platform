---
title: 作業仕様書 — MCP の公開構成（許可リスト）を Helm values から ConfigMap で配送し、配備ファイルに書いた公開構成を CI で起動時と同じ検証に通す（#2117）
type: spec
status: done
related_ids: [FR-16, ADR-0024, ADR-0034, ADR-0123, IADR-0645]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2117"
---

# 作業仕様書 — MCP 公開構成の配送と CI 検証（#2117）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。親は #2047（指摘 M-21 の公開構成の配送）。

## 現状（origin/develop で再確認）

- `McpServer/Infrastructure/ExternalServices/ToolPublicationConfigLoader.cs` は `Mcp:PublicationConfigPath` が空のとき
  `AppContext.BaseDirectory/Configuration/mcp-publication.json`（イメージに焼き込んだ既定）を読む。ファイルが無ければ公開 0 件で起動し、
  あれば `ToolPublicationConfigValidator` に通して、違反があれば起動を止める。
- `deploy/`・`scripts/`・`.github/workflows/` に `mcp-publication` / `PublicationConfigPath` の参照は 0 件である。
  公開構成を変えるにはイメージの再ビルドが要り、配備ファイル側で差し替える口が無い。
- 計画 ADR-0024 は公開構成を「Git 管理・CI スキーマ検証・GitOps 適用」と定める。同 ADR の 2026-09-28 訂正は
  「CI にこの検証を行う専用のステップは無い」と現状を記録している（実際に弾いているのは登録の API と起動時の検証）。

## 方針

- **配送（Helm）**: `services.mcp.mcpPublication`（公開構成の JSON を**文字列のまま**持つ。既定は未設定）を足す。設定したときだけ:
  - ConfigMap `mcp-publication-config`（キー `mcp-publication.json`・値は文字列そのもの）を描く。
  - mcp-service の Deployment へ `/etc/microservices-platform/mcp` に読み取り専用でマウントし、
    `Mcp__PublicationConfigPath=/etc/microservices-platform/mcp/mcp-publication.json` を渡す。
  - Pod テンプレートに `checksum/mcp-publication`（文字列の sha256）を付ける。構成の変更（Git → ArgoCD 同期）でロールアウトさせる（pipeline-config と同型）。
  - 描画の時点で JSON として読めない値は `fail` で止める（`fromJson`）。mcp 以外のサービスに書いた場合と、
    `extraEnv` / `extraEnvAppend` に `Mcp__PublicationConfigPath` を併記した場合も止める（2 つの経路で同じ値を指さない）。
  - 未設定なら今と同じ（イメージの既定を読む）。PoC と values-local の描画は変わらない。
- **なぜ文字列で持つか**: CI の検証を **McpServer の検証器そのもの**（`ToolPublicationConfigValidator`）で行うため。
  values を YAML の木で持つと、検証には YAML の読み手が要る（試験側に依存が無い）か、規則を別言語に写す（2 か所に書くと片方だけが緩む。
  同検証器の注記が禁じる形）ことになる。JSON の文字列なら、配備ファイルから正規表現で取り出してそのまま検証器へ渡せる。
- **CI 検証（xUnit・build-test で走る）**: `McpServer.Tests` に配備ファイルの走査を置く。
  - `deploy/` 以下の `*.yaml` / `*.yml` から `mcpPublication: |` のブロックを取り出し、JSON として読み、`ToolPublicationConfigValidator.Validate` に通す。
    違反があればファイル名と違反を並べて落とす。**0 件の走査は緑にしない**（CI 用の values に 1 つ置く。下記）。
  - イメージの既定（`Configuration/mcp-publication.json`）も同じ検証に通す。
  - 取り出しの関数は単体で試す（正しい構成・違反のある構成・JSON として壊れた構成・ブロックが無いファイル）。
- **CI 用の values**: `deploy/helm/microservices-platform/ci/mcp-publication-values.yaml` に正しい公開構成を 1 つ置く
  （`check-deploy-manifests.js` は `ci/*.yaml` を走査で拾って lint・template・kubeconform に通す）。
- **描画の試験（node・helm）**: `scripts/helm-mcp-publication.test.js`。既定と values-local は ConfigMap・マウント・環境変数を描かない（陰性対照）。
  CI 用の values では ConfigMap の値が values の文字列と一致し、マウントの先と環境変数のパスが一致し、checksum が値の sha256 と一致する。
  壊れた JSON・mcp 以外への設定・`extraEnv` との併記は描画で止まる。変異（パスの食い違い・checksum の欠落）を判定器へ通すと赤になる。
- **起動の試験（xUnit）**: CI 用の values から取り出した文字列をファイルに書き、`Mcp:PublicationConfigPath` で指して
  `ToolPublicationConfigLoader.Load` が同じツールを返す（ConfigMap の中身で起動する）。
- 実装 ADR: IADR-0645（配送の形と、検証を検証器そのもので行うための文字列表現）。

## 受け入れ基準

- [x] helm の values（`services.mcp.mcpPublication`）から公開構成を差し替えられ、mcp-service に ConfigMap がマウントされ `Mcp__PublicationConfigPath` が渡る（描画の試験。修正前は赤）
- [x] 既定・values-local の描画は変わらない（陰性対照）
- [x] McpServer は ConfigMap の中身（values の文字列）で起動する（起動の試験）
- [x] 不正な公開構成（初期公開範囲外のツール・公開名の重複・個人資料の属性割当・壊れた JSON）を配備ファイルに書くと CI（xUnit）で落ちる。修正前はその検査が無い
- [x] 描画の試験を CI のジョブ（helm のあるジョブ）へ足す。運用文書（公開構成の変え方）と FR-16 の通信仕様書・テスト仕様書を改める

## 母集合（規則 9）

`git grep -n -i "mcp-publication\|PublicationConfigPath\|公開構成"`（submodule を除く）で引いた。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `McpServer/Infrastructure/ExternalServices/ToolPublicationConfigLoader.cs` | 変えない | 読み手は既に `Mcp:PublicationConfigPath` を持つ |
| `McpServer/McpServer.csproj` の Content 注記（「実環境では Helm values からマウント」） | **直す** | 注記が初めて事実になる。キー名を書く |
| `deploy/helm/microservices-platform/values.yaml`・`templates/deployment.yaml`・新 `templates/mcp-publication-config.yaml` | **直す** | 配送 |
| `docs/api/FR-16_mcp-server.md` §宣言的公開構成 | **直す** | 配送と CI 検証の所在 |
| `docs/tests/FR-16_mcp-server.md` | **直す** | 試験の追加 |
| `docs/operations/operations.md` | **直す** | 公開構成の変え方（values を変えて PR → CI → 同期） |
| `docs/api/BFF_bff-surface.md:173`・`docs/api/openapi.yaml` の「Git 経由の公開構成変更」 | 外す | 記述は正しいまま（変更の経路が実体を持つだけ） |
| `docs/authz/FR-16_mcp-server.md:83`（「公開構成のスキーマ検証と管理 API の両方で拒否」） | 外す | 起動時の検証の記述として正しい |
| `docs/api/east-west-grpc.md` | 外す | 宛先の記述であり配送に触れない |
| 計画 ADR-0024 の 2026-09-28 訂正（「CI に専用のステップは無い」） | 外す（環流） | 本件で CI のステップができる。マージ後に planning へ報告する |

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
