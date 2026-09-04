---
title: 手順ガイド — Obsidian プラグイン（個人資料同期）の入手・導入・配布
type: how-to
status: in-progress
author: claude
created: 2026-09-02
updated: 2026-09-05
---
<!-- trace:
ids: [FR-19, FR-20, UC-11, SC-20, NFR-11]
adrs: [ADR-0021, ADR-0037]
iadrs: [IADR-0270, IADR-0338, IADR-0348, IADR-0352, IADR-0360, IADR-0375]
specs: [20260902_issue-1098_obsidian-plugin-pull-stage1, 20260903_issue-1153_obsidian-plugin-push-delete-conflict-stage2, 20260903_issue-1154_private-notes-sync-edge-route, 20260903_issue-1176_obsidian-sync-rename-contract, 20260905_issue-1213_obsidian-plugin-release-assets]
issues: [#451, #1098, #1153, #1154, #1176, #1213]
-->

# 手順ガイド: Obsidian プラグイン（個人資料同期）の入手・導入・配布

> **仕様ではなく作業手順の案内である**（`docs/README.md`）。仕様は
> [機能仕様書: Obsidian 双方向同期](../functional/FR-20_obsidian-sync.md) と
> [通信仕様書](../api/FR-20_obsidian-sync.md) を正とする。
>
> **双方向である**（取り込み・送信・論理削除・競合の 3 択。2026-09-03）。
>
> **配備済みクラスタのエッジから届く**（2026-09-03）。接続先はエッジの基底 URL（`https://<エッジ>`）でよい。
> ただし**本番像は既定で出さない**ので、配備側で公開を有効にしておくこと（下記「接続先」）。
> ローカル検証は従来どおり `kubectl port-forward` した文書サービスでも通る。
>
> **［2026-09-05］利用者はリポジトリを取得しない。** GitHub Release の資産（`main.js` と
> `manifest.json`）を落として Vault へ置く（下記「入手（利用者向け）」）。クローンからのビルドは
> **開発者向け**の節へ移した。

## 何が配られるか

配られるのは **2 つだけ**である。

| ファイル | 役割 |
| --- | --- |
| `main.js` | Obsidian が読むプラグイン本体（CommonJS） |
| `manifest.json` | プラグインの識別子 `msp-private-notes-sync`・版・最小アプリ版 |

独自の見た目を持たないので `styles.css` は無い。`cli.mjs`（Obsidian 本体なしで同じ同期処理を実 HTTP に
当てる Node ハーネス）はビルドすると `dist/` にできるが、**実測・検証用であって配布物ではない**ため
リリース資産には含めない。

## 入手（利用者向け）

1. リポジトリの **Releases** を開き、`obsidian-plugin-v<版>` のリリースを選ぶ。
2. 資産の `main.js` と `manifest.json` を落とす。
3. 下の「Vault への導入」へ進む。**クローンも pnpm も要らない。**

版は `manifest.json` の `version` と一致する（Obsidian の「コミュニティプラグイン」画面に出る版と
同じ値）。入れ替えたのに版が変わらないときは、古い資産を落としている。

## Vault への導入

1. Vault の `.obsidian/plugins/msp-private-notes-sync/` を作り、`main.js` と `manifest.json` を置く。
2. Obsidian の「設定 → コミュニティプラグイン」で **制限モードを解除**し、「個人資料同期（汎用プラットフォーム）」を有効にする。
3. プラグイン設定で次を入れる。
   - **接続先 URL**: 同期プロトコルを受ける基底 URL（`https://…`。末尾に `/private-notes/sync` は付けない）。
     配備済みクラスタではエッジの基底 URL をそのまま入れる（下記「接続先」）。
     ローカル検証では `http://127.0.0.1:<port>`（loopback だけ http を許す）。
   - **同期フォルダ**: 同期対象（既定 `個人資料`）。**このフォルダに入れた資料は業務関連資料として扱われる**。
     フォルダの外へ移したファイルは同期が止まるだけで、ナレッジベース側は削除されない。
   - **同期トークン**: 画面「Obsidian 連携設定」で端末を登録して発行された値を貼り付けて **保存**。
     トークンは**この端末にだけ**保存され、Vault のファイル（`data.json`）には入らない。
     再表示はできない（再発行のみ）。
4. コマンドパレットの「個人資料を同期する（取り込み → 送信）」か、設定タブの「いま同期する」を実行する。
   結果は通知に出る。「取り込みのみ」「送信のみ」も選べる。

## 同期の振る舞い

| 操作 | 結果 |
| --- | --- |
| 同期フォルダで `.md` を保存する | 保存の間隔が **30 秒以上空くごとに 1 版**として記録され、次の送信でまとめて送られる（オフラインで 10 回保存すれば 10 版） |
| 同期フォルダで新しい `.md` を作る | 次の送信でナレッジベースに新しい資料ができる（ファイル名がタイトル） |
| 同期フォルダのファイルを削除する | 次の送信でナレッジベース側が**論理削除**（90 日保管・画面から復元可）になる |
| 同期フォルダの外へ移す | 同期が止まるだけ。ナレッジベースには何も送らない |
| 同期フォルダの中で名前を変える | 次の送信で**ナレッジベース側の名前も変わる**（中身より先に名前が送られる）。同じ名前の資料が既にある／ナレッジベース側が先に進んでいる場合は名前だけ変わらず、競合として確認する（勝手に別名は付けない） |
| ナレッジベース側で名前が変わった | 取り込み時にローカルのファイルが移動する（ローカルで編集していれば旧ファイルを残して通知） |
| ナレッジベース側で削除された | ローカルのファイルは**消さない**。送信時に「ローカルを採用（新規として送り直す）／サーバを採用（ゴミ箱へ）」を確認する |
| 両方で編集していた | 送信時に**競合**として 1 件ずつ確認ダイアログが出る。「ローカルを採用」「サーバを採用」「両方残す」「保留」から選ぶまで、どちらも上書きされない |

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

## ビルド（開発者向け）

利用者は上の「入手」で済む。ここから先は**プラグインを直す人と、配る人**の手順である。

```bash
cd src
pnpm install
pnpm --filter @platform/obsidian-plugin run build
node ../scripts/check-static-egress.js --require obsidian-plugin/dist   # 外部 CDN・フォント・analytics が無いことの走査
```

`dist/` に `main.js` / `manifest.json` / `cli.mjs` ができる。手元で試すときは前 2 つを Vault の
プラグインフォルダへ置く（利用者にこの手順を踏ませない）。

## 配る（リリース手順）

配布は**タグを打つだけ**である。資産の作成と公開はワークフローが行う。

1. 版を上げる。**`src/obsidian-plugin/package.json` と `manifest.json` の `version` を同じ値にする**
   （Obsidian が見るのは `manifest.json` の方だが、片方だけ動かすと配布物と workspace の版がずれる）。
   手元で確かめる:

   ```bash
   node scripts/check-plugin-release-version.js
   ```

2. その変更を通常どおり PR で `develop` へ入れる。
3. 着地したコミットにタグを打って push する。**タグの形は `obsidian-plugin-v<版>`** である。

   ```bash
   git tag obsidian-plugin-v0.2.0
   git push origin obsidian-plugin-v0.2.0
   ```

4. ワークフローが版とタグの一致を確かめ、ビルドし、成果物の外部参照を走査し、リリースを作って
   `main.js` と `manifest.json` を資産として上げる。タグと `manifest.json` の版がずれていれば、
   **資産を作る前に落ちる**。
5. 落ちた場合は版を直して入れ直し、タグを打ち直す。既に在るタグに対して回し直すだけなら、
   Actions の「Obsidian Plugin Release」を手動実行してタグ名を入力する。

裸の `v0.2.0` は使わない —— モノレポであり、`v*` はリポジトリ全体のリリースノート生成が既に使っている。

**署名と公開レジストリ（Obsidian community plugins）への登録は行っていない。** 配布は社内に閉じる。

## ローカル検証（Obsidian 本体なし）

```bash
kubectl -n microservices-platform port-forward svc/document-service 18093:8080
export MSP_SYNC_ENDPOINT=http://127.0.0.1:18093 MSP_SYNC_TOKEN_FILE=/path/to/token.txt MSP_VAULT_DIR=/tmp/vault
node src/obsidian-plugin/dist/cli.mjs pull                      # 取り込み（既定）
node src/obsidian-plugin/dist/cli.mjs record 個人資料/メモ.md    # 保存イベントに相当（1 版を積む）
node src/obsidian-plugin/dist/cli.mjs push                      # 送信（新規・更新・論理削除）
node src/obsidian-plugin/dist/cli.mjs sync                      # 取り込み → 送信
node src/obsidian-plugin/dist/cli.mjs delete 個人資料/メモ.md    # 削除イベントに相当
node src/obsidian-plugin/dist/cli.mjs rename 個人資料/a.md archive/a.md   # フォルダ外へ移す（同期停止。HTTP は出さない）
node src/obsidian-plugin/dist/cli.mjs move 個人資料/a.md 個人資料/b.md    # 名前を変えて送信（ナレッジベース側の名前も変わる）
node src/obsidian-plugin/dist/cli.mjs resolve 個人資料/メモ.md local|server|both   # 競合を非対話で解決
```

終了コードは `0`（完了）/ `2`（401。トークン無効）/ `3`（設定不備）/ `4`（競合あり。`resolve` で解決する）。
出力の JSON にトークンは含まれない。`MSP_EDIT_QUIET_MS=0` にすると `record` のたびに版を刻める（実測用）。
