# scripts — 補助スクリプト

補助成果物（CHANGELOG / OpenAPI）の生成・環境セットアップ・プロファイル適用を行う依存ゼロのスクリプト群。

| スクリプト | 役割 | 出力 |
| --- | --- | --- |
| `gen-changelog.js` | コミット履歴（`種別(起点ID): 要約`）から変更履歴を生成 | `CHANGELOG.md` |
| `gen-openapi-skeleton.js` | 通信仕様書（`docs/api/`）から OpenAPI 雛形を生成 | `docs/api/openapi.yaml` |
| `check-doc-links.js` | `docs/` 配下 Markdown の相対リンク（frontmatter の `plan_refs`/`related_specs`・本文リンク・インラインコードのパス）の実在を検査。破損があれば終了コード 1。`--require-planning` で planning サブモジュール未 populate を fail 扱いにする（#232 / IADR-0058） | 標準出力（レポート） |
| `check-ai-workflow-config.js` | Claude 系ワークフローのツール許可設定を検査。`claude_args` の記法誤り（空白分割で無効化）・ブロック内コメント・「SDK を用意して実行ツールを許可していない」不一致を検出。不備があれば終了コード 1。`--self-test` で検証器自体も試験 | 標準出力（レポート） |
| `check-unit-dependencies.js` | ユニット依存方向の機械検査（#231）。csproj の `ProjectReference`（ユニット外参照は `platform/backend/Shared/` の 2 プロジェクトのみ許可・platform→可変ユニット禁止・統合テスト例外）と `Foundation/` 配下の `using *.Composable.*` を静的走査。違反があれば終了コード 1。フロントの合成点制約は ESLint（`src/eslint.config.js`）が担う。方式の根拠は IADR-0057 | 標準出力（レポート） |
| `check-image-mapping.js` | `k8s-local-images.sh` の `MAPPING`（chart-image ↔ Dockerfile）と `deploy/docker-compose.yml` の `build` 定義の対応を機械検査（#275）。欠落・stale・Dockerfile 不一致・命名不整合・compose 専用除外（`frontend`）の腐り/二重掲載を検出し、ドリフトがあれば終了コード 1。ビルド可否は `images.yml`（#268 / IADR-0067）が担う。方式の根拠は IADR-0068 | 標準出力（レポート） |
| `verify-qdrant-attribute-payload.sh` | IADR-0014 / #71: 実機 Qdrant で ABAC 属性の格納表現・フィルタ通過を検証 | 標準出力（判定） |
| `setup.sh` | 開発環境セットアップ（SessionStart hook / devcontainer から実行） | — |
| `apply-profile.sh` | `AI_SETUP.md` で宣言したプロファイルに応じてキットを構成（`.example` 有効化等） | `.ai-profile` |

## プロファイルの適用

利用可能な AI（`claude-code` / `api` / `copilot`）を `AI_SETUP.md` で宣言し、対応する構成を適用する。

```bash
bash scripts/apply-profile.sh claude-code          # サブスクリプション
bash scripts/apply-profile.sh api                  # Anthropic API
bash scripts/apply-profile.sh --prune copilot      # Copilot のみ（Claude 系を削除）
```

## 使い方（ローカル）

```bash
node scripts/gen-changelog.js --out CHANGELOG.md
node scripts/gen-openapi-skeleton.js --src docs/api --out docs/api/openapi.yaml
node scripts/check-doc-links.js                    # 仕様書の相対リンク切れを検査（再発防止）
node scripts/check-ai-workflow-config.js           # AI ワークフローのツール許可設定を検査
node scripts/scripts.test.js                       # 上記スクリプト群の単体テスト
node scripts/check-unit-dependencies.js --self-test # 検査器の自己試験
node scripts/check-unit-dependencies.js            # ユニット依存方向の検査（#231）
node scripts/check-image-mapping.js --self-test    # 検査器の自己試験
node scripts/check-image-mapping.js                # MAPPING ↔ compose build のドリフト検査（#275）
node scripts/k8s-local-up.test.js                  # k8s-local-up.sh の opt-in ゲート横断 smoke test（#334・要 bash）
```

> `check-ai-workflow-config.js` は、AI レビュー / 実装が「ジョブは成功するのに検証を実行できない」
> 状態に陥る設定不備を機械的に止める。失敗モードの一覧は `impl-handoff-kit/HOWTO.md` の
> 付録3（トラブルシューティング）を参照。

## 検査（CI）

`ci.yml` が PR ごとに以下を実行する。**`scripts.test.js` は `scripts-tests` ジョブで走る**。

| ジョブ | 実行内容 |
| --- | --- |
| `scripts-tests` | `node scripts/scripts.test.js`（本 README のスクリプト群の横断テスト。`fetch-depth: 0` が必要） |
| `commit-messages` | `check-commit-messages.js`（コミット件名の規約と ADR/IADR 実在性） |
| `doc-links` | `check-doc-links.js`（相対リンクの実在） |
| `ai-workflow-config` | `check-ai-workflow-config.js --self-test` と本検査 |
| `pipeline-config` | `validate-pipeline-config.js --self-test`（任意コンポーネント。採否は HOWTO Part B-6） |
| `unit-dependencies` | `check-unit-dependencies.js --self-test` と本検査（#231 / IADR-0057） |
| `realm-constraints` | `check-realm-constraints.js --self-test` と本検査（#18 / #307 / #385） |
| `bff-downstreams` | `check-bff-downstreams.js --self-test` と本検査（#342 / IADR-0089） |
| `unit-service-ownership` | `check-unit-service-ownership.js --self-test` と本検査（#407 / IADR-0107） |
| `k8s-local-up-smoke` | `k8s-local-up.test.js`（#334 / IADR-0087・要 bash） |

> `scripts.test.js` を CI に載せないと「誰かが手で叩いたときだけ走るテスト」になる。
> 実際に、CHANGELOG 生成が全面的に壊れる回帰が PR の CI をすべて green のまま通り抜けたことがある
> （`changelog.yml` は push でしか起動しないため、壊れるのはマージ後）。

### リポジトリ固有のテストを足す場所

`scripts.test.js` は**キットが配布する共通テスト**であり、キットの更新のたびに差し替わる。
自前スクリプトの検査を同ファイルへ直接追記すると、同期のたびに手動マージが要り、
キットが同じテストを取り込んだ際に重複も生じる（重複はテストが落ちないため気付きにくい）。

固有テストは **`scripts/scripts.local.test.js`** に置く。`scripts.test.js` が存在すれば自動で
読み込む（無ければ何もしない）。これにより `scripts.test.js` をキットとバイト一致に保て、
同期は上書きコピー 1 回で済む。

```js
// scripts/scripts.local.test.js
module.exports = ({ ok, assert }) => {
  ok('本リポ固有の検査', () => {
    assert.ok(true);
  });
};
```

`ok` をそのまま受け取るため、件数の集計は自動で正しくなる（カウンタが分かれない）。

## 自動生成（CI）

- `.github/workflows/doc-links-planning.yml`: private な planning サブモジュール込みのリンク検査（#232 / IADR-0058）。夜間 + `workflow_dispatch` でトークン付き（Secret `PLANNING_REPO_TOKEN`。本リポジトリと planning 双方へ read 権限を持つ fine-grained PAT 推奨）に submodule を取得し、`check-doc-links.js --require-planning` を実行する。本体 `ci.yml` の `doc-links` は高速・トークン不要のまま非 planning リンクを毎 PR 検査する。
- `.github/workflows/image-mapping.yml`: `check-image-mapping.js`（`--self-test` ＋実チェック）を毎 PR/push で実行し、`MAPPING` と compose の `build` 定義のドリフトをマージ前に落とす（#275 / IADR-0068）。`ci.yml` には足さない（独立ワークフロー方針）。Node のみで docker 不要のため paths フィルタなしで常に結果を報告する。
- `.github/workflows/ci.yml` の `k8s-local-up-smoke` ジョブ: `k8s-local-up.test.js` を実行し、`k8s-local-up.sh` の opt-in ゲート（`PERSIST`/`OBSERVABILITY`/`VAULT`/`ARGOCD`/`HEADLAMP`/`LOCALEDGE`/`ESO`）を横断で固定する（#334 / IADR-0087）。あわせて **apiserver へ OIDC フラグを付与しない**ことを回帰として固定する（#399 / IADR-0105。旧 `HEADLAMP_OIDC_APISERVER` は除去済み・指定しても no-op）。外部バイナリを PATH 上の記録スタブへ差し替え、副作用ゼロでスクリプトを実行し発行コマンド列を検証する（実クラスタは作らない・Node + bash のみ）。
- `.github/workflows/changelog.yml`: `main` への push で CHANGELOG を再生成しコミットする。タグ push でリリースノートも生成する。
- `.github/workflows/openapi.yml`: OpenAPI を生成する。コードからの生成コマンド（`scripts/generate-openapi.sh` または変数 `OPENAPI_GENERATE_CMD`）が設定されていればそれを実行し、無ければ通信仕様書からの雛形生成にフォールバックする（「生成可能なら必ず生成」）。

> OpenAPI をコードから生成する場合は `scripts/generate-openapi.sh` を用意する（例: `dotnet swagger tofile ...` / `npx ...`）。未整備でも雛形は通信仕様書から生成される。
