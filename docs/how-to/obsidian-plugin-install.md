---
title: 手順ガイド — Obsidian プラグイン（個人資料同期）のビルドと導入
type: how-to
status: in-progress
author: claude
created: 2026-09-02
updated: 2026-09-03
---
<!-- trace:
ids: [FR-19, FR-20, UC-11, SC-20, NFR-11]
adrs: [ADR-0021, ADR-0037]
iadrs: [IADR-0270, IADR-0338, IADR-0348]
specs: [20260902_issue-1098_obsidian-plugin-pull-stage1, 20260903_issue-1154_private-notes-sync-edge-route]
issues: [#1098, #451, #1154]
-->

# 手順ガイド: Obsidian プラグイン（個人資料同期）のビルドと導入

> **仕様ではなく作業手順の案内である**（`docs/README.md`）。仕様は
> [機能仕様書: Obsidian 双方向同期](../functional/FR-20_obsidian-sync.md) と
> [通信仕様書](../api/FR-20_obsidian-sync.md) を正とする。
>
> 🔴 **第 1 段（取り込みのみ）である。** Obsidian 側の編集・削除はナレッジベースへ送られない。
>
> **［2026-09-03 追記］配備済みクラスタのエッジから届く。** 従前の「接続先は `kubectl port-forward` した
> 文書サービスだけ」は本追記で置き換わる。接続先はエッジの基底 URL（`https://<エッジ>`）でよい。
> ただし**本番像は既定で出さない**ので、配備側で公開を有効にしておくこと（下記「接続先」）。

## 何が配られるか

`src/obsidian-plugin/` を pnpm workspace メンバとしてビルドすると `dist/` に 3 つできる。

| ファイル | 役割 |
| --- | --- |
| `main.js` | Obsidian が読むプラグイン本体（CommonJS） |
| `manifest.json` | プラグインの識別子 `msp-private-notes-sync`・版・最小アプリ版 |
| `cli.mjs` | Obsidian 本体なしで同じ同期処理を実 HTTP に当てる Node ハーネス（実測・検証用。配布物ではない） |

社内配布のリリース資産化（zip 等）は未整備で、いまは `dist/` をそのまま置く。

## ビルド

```bash
cd src
pnpm install
pnpm --filter @platform/obsidian-plugin run build
node ../scripts/check-static-egress.js --require obsidian-plugin/dist   # 外部 CDN・フォント・analytics が無いことの走査
```

## Vault への導入

1. Vault の `.obsidian/plugins/msp-private-notes-sync/` を作り、`dist/main.js` と `dist/manifest.json` を置く。
2. Obsidian の「設定 → コミュニティプラグイン」で **制限モードを解除**し、「個人資料同期（汎用プラットフォーム）」を有効にする。
3. プラグイン設定で次を入れる。
   - **接続先 URL**: 同期プロトコルを受ける基底 URL（`https://…`。末尾に `/private-notes/sync` は付けない）。
     配備済みクラスタではエッジの基底 URL をそのまま入れる（下記「接続先」）。
     ローカル検証では `http://127.0.0.1:<port>`（loopback だけ http を許す）。
   - **同期フォルダ**: 取り込み先（既定 `個人資料`）。**このフォルダに入れた資料は業務関連資料として扱われる**。
   - **同期トークン**: 画面「Obsidian 連携設定」で端末を登録して発行された値を貼り付けて **保存**。
     トークンは**この端末にだけ**保存され、Vault のファイル（`data.json`）には入らない。
     再表示はできない（再発行のみ）。
4. コマンドパレットの「個人資料をナレッジベースから取り込む（pull）」か、設定タブの「いま取り込む」を実行する。
   結果は通知に出る（取得 / 一致 / 最新 / 上書きしなかった件数）。

## 期限切れ・失効のとき

同期トークンは 30 日で切れ、自動更新は無い。切れたトークンは**プラグイン設定に残ったまま**になり、
同期は「同期トークンが無効です」で止まる（黙って古いままにはならない）。画面で再発行し、プラグイン設定へ
入れ直す。端末を失効させたときも同じ表示になる。

## 接続先（エッジ）

エッジは `/private-notes/sync/` 配下だけを文書サービスへ通す。**BFF は経由しない**（同期トークンは
ブラウザセッションと別系統の資格情報である）。外へ出るのはこの 1 前置だけで、個人資料の一覧・端末登録・
組織文書はエッジから届かない（画面配信へ落ちるので **404 ではなく画面**が返る）。

- **プラグインに入れるのはエッジの基底 URL だけ**である（例: `https://<エッジ>`）。公開パスは通信仕様書の
  パスと同一なので、`/private-notes/sync` を足さない。
- **本番像は既定で出さない**（opt-in）。配備側の値 `edge.privateNotesSync.enabled` を `true` にする。
  無効のままだと、正しい設定・正しいトークンでも同期は成立しない（画面配信へ落ち、`manifest` の応答が
  JSON にならない）。
- ローカル（`ISTIO=1` かつ `LOCALEDGE=1` のエッジ）では最初から通る。接続先は `https://localhost`。
  **証明書検証を切らないこと**（`-k` を使わない。ローカル CA を信頼させる手順は `deploy/local/edge-istio/README.md`）。
- 平文 http では同期しない（トークンが Bearer でそのまま載る）。プラグインが loopback 以外の http を拒む。

## ローカル検証（Obsidian 本体なし）

```bash
kubectl -n microservices-platform port-forward svc/document-service 18093:8080
MSP_SYNC_ENDPOINT=http://127.0.0.1:18093 \
MSP_SYNC_TOKEN_FILE=/path/to/token.txt \
MSP_VAULT_DIR=/tmp/vault \
node src/obsidian-plugin/dist/cli.mjs
```

終了コードは `0`（完了）/ `2`（401。トークン無効）/ `3`（設定不備）。出力の JSON にトークンは含まれない。
