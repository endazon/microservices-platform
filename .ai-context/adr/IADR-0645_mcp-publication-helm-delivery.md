---
title: IADR-0645 MCP の公開構成は Helm values に JSON の文字列のまま持ち、ConfigMap で mcp-service へ配送する。CI の検証は配備ファイルから取り出して McpServer の読み込みそのものに通す
type: impl-adr
status: Accepted
related_ids: [FR-16, ADR-0024, ADR-0034, ADR-0123, ADR-0018, IADR-0028]
author: claude
created: 2026-10-26
updated: 2026-10-26
related_specs:
  - ../specs/20261026_2117_mcp-publication-configmap.md
---

# IADR-0645: MCP の公開構成の配送と CI 検証（#2117）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-26
- 決定者: claude（#2117。親 #2047 の指摘 M-21 のうち公開構成の配送。依頼はコーディネーター 2026-10-26）

## 起点・関連

- 起点 issue: #2117。
- 実装する計画 ADR: ADR-0024（公開構成は Git 管理・CI スキーマ検証・GitOps 適用。§5「検証を通らない構成は適用不可」）。
  同 ADR の 2026-09-28 訂正は「CI にこの検証を行う専用のステップは無い」と記録している。本 IADR がそのステップを置く。
- 型を倣う実装 ADR: IADR-0028（宣言的パイプライン構成の ConfigMap 配送と checksum の注記）。
- 作業仕様書: [20261026_2117_mcp-publication-configmap](../specs/20261026_2117_mcp-publication-configmap.md)。

## 背景

- McpServer の `ToolPublicationConfigLoader` は `Mcp:PublicationConfigPath` を読むが、配備側にこのキーを渡す箇所が 1 つも無かった。
  公開構成はイメージに焼き込んだ `Configuration/mcp-publication.json` だけで、変えるにはイメージの再ビルドが要った。
- 公開構成の検証規則は `ToolPublicationConfigValidator` の 1 か所にある。同検証器の注記は、規則を 2 か所に書くと
  片方だけが緩むので 1 つの関数に閉じる、と定めている（管理 API の属性割当も同じ関数を通る）。

## 決定

### 決定 1: 配送は Helm values → ConfigMap → マウント → `Mcp__PublicationConfigPath`

- `services.mcp.mcpPublication` を足す（既定は空）。設定したときだけ:
  - ConfigMap `mcp-publication-config`（キー `mcp-publication.json`）を描く。
  - mcp-service に `/etc/microservices-platform/mcp` で読み取り専用でマウントし、`Mcp__PublicationConfigPath` でそのファイルを指す。
  - Pod テンプレートに `checksum/mcp-publication`（値の sha256）を付ける（ConfigMap の更新だけでは Pod は読み直さない。IADR-0028 と同じ）。
- **空ならイメージの既定を読む**（今と同じ）。既定と values-local の描画は変わらない。PoC への影響は無い。
- 描画で止める条件: JSON として読めない値（`fromJson`）、mcp 以外のサービスへの設定、`extraEnv` / `extraEnvAppend` の
  `Mcp__PublicationConfigPath` との併記（2 つの経路で読み先を指すと、どちらが効いているかが描画を読まないと分からない）。

### 決定 2: values には公開構成を JSON の文字列のまま持つ（YAML の木にしない）

- **CI の検証を McpServer の検証器そのもので行うため**である。
  - YAML の木で持つと、検証には試験側に YAML の読み手が要る（バックエンドの試験に依存が無い）。
  - あるいは規則を別の言語（node の検査器・Go テンプレート）へ写すことになる。これは決定の背景にある「2 か所に書くと片方だけが緩む」の形そのものである。
- JSON の文字列なら、配備ファイルから `mcpPublication: |` のブロックを取り出し、**起動時と同じ読み込み（`ToolPublicationConfigLoader.Load`）**へそのまま渡せる。
- **ブロック以外の書き方（1 行の JSON・`>`・YAML の木・値なし）は CI で誤りにする。** 取り出せない書き方を黙って飛ばすと、その構成は CI の検証を素通りして配備される。
- 受け入れる代償: values の中で JSON を手で書く（YAML の利点を使えない）。公開構成は数件のツールと属性割当で、手で書ける量である。

### 決定 3: CI の検証はバックエンドの単体試験（build-test）に置く

- `McpServer.Tests` の `PublicationConfigDeploymentFilesTests` が `deploy/` 以下の全 YAML を走査し、取り出した公開構成とイメージの既定を
  起動時と同じ読み込みに通す。**列挙を持たず、0 件の走査を緑にしない**（CI 用の values `ci/mcp-publication-values.yaml` に 1 つ置く）。
- 描画の形（ConfigMap の値が values の文字列とバイト等価・マウント先と環境変数の一致・checksum）は `scripts/helm-mcp-publication.test.js`
  （helm のあるジョブ）が固定する。中身の検証はそちらでは行わない（決定 2）。
- CI 用の values の公開構成は**イメージの既定とわざと違う中身**にする。同じ中身だと、差し替えが効かずに既定を読んでいても起動の試験が通る。

## 結果

- 良い影響: 公開構成を Git の PR で変えられ、PR の時点で起動時と同じ検証に通る（ADR-0024 の「Git 管理・CI スキーマ検証・GitOps 適用」が実体を持つ）。
- 残る制約: リポジトリの外に置いた values（別リポジトリの GitOps 等）は本試験の走査に入らない。そこでは起動時の fail-fast だけが効く。
- 計画への環流: ADR-0024 の 2026-09-28 訂正（「CI に専用のステップは無い」）は、本 IADR のマージ後に事実と異なる。planning へ報告する。
