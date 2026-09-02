---
title: FR-20 Obsidian 双方向同期 テスト仕様書
type: test-spec
status: completed
created: 2026-08-23
updated: 2026-09-02
author: Claude
---
<!-- trace:
ids: [FR-19, FR-20, FR-22, UC-11, SC-20]
adrs: [ADR-0037, ADR-0046]
iadrs: [IADR-0270, IADR-0331]
specs: [20260823_issue-451_private-note-obsidian-sync-core, 20260902_issue-1098_obsidian-plugin-pull-stage1]
issues: [#451, #1098]
-->

# テスト仕様書: Obsidian 双方向同期

## テスト対象・範囲

同期プロトコル（manifest / push / pull / delete）・同期トークン（発行・期限・再発行・失効）・
監査・期限予告の検知、および **Obsidian プラグイン第 1 段（取り込みのみ）のプロトコル部**。
**対象外**: プラグインの push / delete / 競合解決 UI（第 2 段・未実装）・Obsidian 本体上の GUI 操作
（本体は CI に無い）・実ブローカ／実ストレージでの結合（この環境では実行していない）。

実体: `DocumentService.Tests` の `ObsidianSyncProtocolTests` / `SyncDeviceTokenTests`（サーバ側）、
`src/obsidian-plugin/src/**/*.test.ts`（プラグイン側。Vitest・Obsidian 実体なし）。

## テスト観点

- 🔴 スコープの否定形（他者の資料・共有された資料・組織文書が見えない）は、
  陽性対照（本人の資料は見える）と対で置く —— 「常に 404」の実装でも否定形だけは緑になる。
- トークンの期限（30 日）・予告（7 日前）は端末を過去時刻で播種して検証する。

## テストケース一覧（計画の受け入れ基準からの写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 有効なトークンで自分の資料の作成・一覧・取得ができる（陽性対照） | `有効なトークンで自分の資料を作成しマニフェストと本文取得ができる` |
| 2 | トークン欠落・不正・失効はいずれも同じ 401 | `無効なトークンはすべて401になる` |
| 3 | 同期用資格情報で他者の資料（共有されたものを含む）・組織文書を取得できない | `他者の資料は共有されていても組織文書でも同期資格情報から到達できない` |
| 4 | オフライン 10 編集 → 1 同期でも 10 版として保持される | `一回の同期に10編集を載せると10版が刻まれる` |
| 5 | 競合は自動解決（後勝ち）せず 409 で利用者へ返る | `版がずれたpushは409になり後勝ちで上書きされない` |
| 6 | Obsidian 側の削除はサーバ側で論理削除に留まり復元できる | `同期経由の削除は論理削除でありサーバから即時消滅しない` |
| 7 | 新規作成のフェイルセーフ既定（スコープ属性・所有者・restricted・トグル OFF・共有 0 件） | `同期経由の新規作成はフェイルセーフ既定で作られる` |
| 8 | 本文 1 MB 超は 413（切り詰めない） | `一メガバイト超の本文は413で拒否される` |
| 9 | 同期の実行記録（誰が・いつ・何件）が残り、タイトル・内容を含まない | `同期の監査ログは件数のみでタイトルを含まない` |
| 10 | トークンは利用者が自ら発行・期限 30 日・一覧に平文が現れない | `トークンは利用者が自ら発行でき期限は30日で一覧に平文は現れない` |
| 11 | 期限切れトークンは 401（有効なものは通る） | `期限切れトークンは401になる` |
| 12 | 手動再発行で旧トークンは即時無効・自動リフレッシュ経路は存在しない | `再発行すると旧トークンは即時無効になり新トークンが使える` |
| 13 | 全端末の一括失効 | `一括失効で全端末のトークンが同時に無効になる` |
| 14 | 他人の端末は見えず・失効も再発行もできない（存在秘匿） | `他人の端末は見えず失効も再発行もできない` |
| 15 | 期限 7 日前の通知が窓内で 1 回だけ・当日／事後の追加通知なし | `トークン期限の7日前通知は窓内で1回だけ検知される` |

## テストケース一覧（Obsidian プラグイン第 1 段。Obsidian 実体なし）

| # | 受け入れ基準 | テスト（ファイル › 名前） |
| --- | --- | --- |
| P1 | 同期トークンを設定したプラグインで manifest を取得し、資料一覧と版が読める（陽性対照） | `syncClient.test.ts` › `manifest を Bearer 同期トークンで取得し、契約どおりの形なら返す` |
| P2 | pull は本文つきの応答を返す | `syncClient.test.ts` › `pull は資料 ID を URL エンコードして本文つきの応答を返す` |
| P3 | 401 は理由を問わず利用者に判る失敗（期限切れ・失効・不正を区別しない） | `syncClient.test.ts` › `401 は SyncAuthError になる`／`pullSync.test.ts` › `401 なら SyncAuthError を投げ、ファイルにも状態にも触らない` |
| P4 | 契約と違う形・不正な JSON・想定外の状態コードは黙って空にせず止める | `syncClient.test.ts` › `契約と違う形・不正な JSON・想定外の状態コードは SyncProtocolError になる` |
| P5 | 差分のある資料だけ pull して Vault へ書き、同期状態を保存する | `pullSync.test.ts` › `差分のある資料だけ pull して Vault へ書き、同期状態を保存する` |
| P6 | 変化が無ければ manifest だけ読み、本文の取得（egress）を増やさない | `pullSync.test.ts` › `変化が無い 2 巡目は manifest だけ読み、pull も書き込みもしない` |
| P7 | サーバが進めば上書き、ローカルで編集された資料は上書きしない（自動解決しない） | `pullSync.test.ts` › `サーバが進んだ資料は上書きし、ローカルで編集された資料は conflict として残す`／`pullPlanner.test.ts` の conflict 2 件 |
| P8 | ローカルで消された追跡済み資料は再取得しない・サーバ側削除は件数のみ | `pullPlanner.test.ts` › `追跡済みの資料がローカルに無ければ conflict(local-deleted) で再取得しない`／`deleted=true の資料は server-deleted として報告するだけ` |
| P9 | Vault の外へ出るパス・制御文字・同じパスへ落ちる 2 件は取り込まない（有効なパスの取り込みと対） | `vaultPath.test.ts` › `絶対パス・親参照・制御文字・空は理由付きで拒否する`／`pullPlanner.test.ts` › `不正なパスは invalid-path、同じローカルパスへ落ちる 2 件は両方 path-collision で skipped` |
| P10 | 接続先は https のみ（loopback だけ http 可） | `endpoint.test.ts` の 4 件 |
| P11 | ローカル内容のハッシュはサーバの `ContentHash` と同じ計算 | `hash.test.ts` › `既知のベクタと一致する（空文字・abc）` |
| P12 | トークンは端末ローカルに保存・削除でき、設定ファイルには入らない | `tokenStore.test.ts` の 2 件 |

## 実行

```bash
dotnet test src/knowledge/backend/Services/DocumentService/Tests
cd src && pnpm exec vitest run obsidian-plugin   # プラグイン側（横断 vitest にも含まれる）
```

実 HTTP の証跡（Obsidian 本体なし）は `src/obsidian-plugin/dist/cli.mjs` で取る
（[手順ガイド](../how-to/obsidian-plugin-install.md) §ローカル検証）。

## 関連

- 機能仕様書: [FR-20_obsidian-sync](../functional/FR-20_obsidian-sync.md)
- 通信仕様書: [FR-20_obsidian-sync](../api/FR-20_obsidian-sync.md)
- テスト仕様書: [FR-19_private-notes-lifecycle](FR-19_private-notes-lifecycle.md)
