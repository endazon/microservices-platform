---
title: gitleaks の誤検知の fingerprint を develop 上の squash コミットにも足す（#2054）
type: spec
status: done
related_ids: [NFR-18, IADR-0570]
author: claude
created: 2026-10-22
updated: 2026-10-22
issue: "#2054"
---

# 仕様書: `.gitleaksignore` に develop 上の squash コミットの fingerprint を足す（#2054）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。実装の基点は MSP `origin/develop` `2767c148`。

## 起点

- #2054（Security ワークフローの gitleaks が #2048 の squash merge `af01abc5` の push で赤）。計画 ID: NFR-18（秘密の扱い）。
- 関連: #2024 / #2048 / IADR-0570（ExternalSecret e2e-accounts の導入。誤検知の初出）。

## 事象と原因

- #2048 は `.gitleaksignore` に誤検知の fingerprint を 3 行足したが、いずれも **PR ブランチのコミット**（`6dad64a0` / `c9b1b237` / `db7c148e`）を指す。
- fingerprint は `<commit-sha>:<path>:<rule-id>:<line>` で SHA を含む。squash merge で develop に載ったコミットは別 SHA（`af01abc5`）になるため一致しない。
- `af01abc5` は該当ファイル `deploy/local/vault/eso/externalsecret-e2e-accounts.yaml` の 75 行を含む（最終形に残る行なので squash 後も消えない）。`c9b1b237` / `db7c148e` の 2 件は PR 内の後続コミットで形を改めた中間状態であり、squash 後のコミットには現れない。

## 誤検知であることの確認

- 75 行は ExternalSecret の `spec.data[]` の要素で、同期先 Secret の**キー名**（英大文字・アンダースコアの識別子）を宣言する欄である。値は OpenBao の KV から ESO が実行時に取り込み、リポジトリには無い。
- generic-api-key ルールは、鍵名らしい語の隣の長い英大文字列を鍵と見分けられない（IADR-0570 の注記と同じ）。gitleaks のエントロピーは 3.6 で、識別子の文字分布である。

## 走査範囲の確定（`.github/workflows/security.yml` と gitleaks-action v3.0.0 の実装から）

| イベント | 走査範囲 |
|---|---|
| push | push に含まれるコミットだけ（`--log-opts=--no-merges --first-parent <先頭>^..<末尾>`、1 件なら `-1`） |
| pull_request | PR のコミットだけ |
| schedule / workflow_dispatch | `--log-opts` なし＝gitleaks 既定の全参照・全履歴（checkout は `fetch-depth: 0` なので origin の全ブランチとタグ） |

- gitleaks の版は action の既定 `8.24.3`（`GITLEAKS_VERSION` 未指定）。`.gitleaks.toml` は無い（既定ルール）。
- develop が `3cfe7ada` 以降緑なのは、push の走査が後続コミットしか見ないためで、`af01abc5` の検出が消えたことを示さない。週次 schedule（全履歴）で再び赤になる。

## 方針

- `.gitleaksignore` に `af01abc5…:deploy/local/vault/eso/externalsecret-e2e-accounts.yaml:generic-api-key:75` を足す。
- **PR ブランチ側の 3 行は残す。** 根拠:
  - `origin/feat/NFR-e2e-accounts-2024` が削除されておらず、3 コミットとも origin のブランチから到達できる。schedule は全参照を走査するので、消すと赤になる。
  - リポの慣行も同じ: 既存の fingerprint（`a154bc7e` / `142fcdc1` / `102de5ff` / `9573cc39` / `320cc30a`）はいずれも PR ブランチ側のコミットで、到達不能になったもの（`a154bc7e` は手元に無い）も消さずに残している。
- 回帰試験は新設しない（`scripts/` に `.gitleaksignore` の fingerprint を検査する既存の試験は無い。指示どおり新設しない）。
- 再発の型（squash merge で SHA が変わる）は、fingerprint を PR ブランチのコミットに付けたときに起きる。運用文書の改定は本件の範囲外とし、同型 2 回目で検討する。

## 受け入れ基準

1. gitleaks 8.24.3 で `af01abc5^..af01abc5` を走査して 0 件。
2. schedule と同じ範囲（全リモートブランチ＋タグの全履歴）を走査して 0 件。
3. 追加した注記の行自体が新たな検出を生まない（PR の push 走査が緑）。

## 結果

- 修正前: `af01abc5^..af01abc5` で 1 件（上記 fingerprint）。全履歴（`--full-history --remotes=origin --tags`、1587 コミット）で 1 件（同じ fingerprint のみ）。
- 修正後: 結果は PR 本文に記す（同じコマンドで 0 件）。
- マージ後に Security ワークフローを workflow_dispatch で 1 回流し、緑を確かめてから #2054 を閉じる。
