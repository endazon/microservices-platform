---
title: 作業仕様書 — Obsidian プラグイン（自作）第 1 段: 設定・同期トークン・manifest → pull の縦の一筋（#1098）
type: spec
status: done
related_ids:
  - FR-19
  - FR-20
  - UC-11
  - SC-20
  - ADR-0037
  - ADR-0046
  - ADR-0054
  - IADR-0270
  - IADR-0331
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
---

# 仕様書: issue #1098 — Obsidian プラグイン第 1 段（設定 → manifest → pull）

> 実装 issue #1098（#451 の残射程のうち最大のもの）。**本リポジトリで最大級の新規実装**であるため、
> 本書で「この PR で作る最小の縦の一筋」を切り、残りを後続 issue へ明示的に送る。
> 実装判断（配置・ビルド・接続先・トークン保管・段分割・テストの器）は
> `IADR-0331` に置き、本書は範囲・母集合・受け入れ基準・実測を持つ。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-20（Obsidian 双方向同期）。前提として FR-19（個人資料）
- ユースケース（UC）: UC-11
- 画面（SC）: SC-20（Obsidian 連携設定。トークンの発行元。**本 PR は画面を触らない**）
- 関連 ADR: `ADR-0037`（決定 1〜20。とくに 1・2・4・7・12〜15。**課題 2** = 同期トークンは BFF の
  ブラウザセッションと別系統）/ `ADR-0046`（本文編集は Obsidian 同期のみ）/ `ADR-0054`（語彙）/
  08_data-egress-policy §個人資料の同期に関する例外（許容条件 1〜7）
- 実装 IADR: `IADR-0270`（サーバ側中核。決定 3・7 がプラグインの前提）/ `IADR-0331`（本作業）

## 着手条件の確認（実測 2026-09-02・`develop` `89b4d26e`）

1. `ADR-0037` の「着手可否の注記」★［2026-08-15］は**留保を外している**。決定 1〜20 と SC-20 全体は
   覆らない（`IADR-0142`）。**着手可**。
2. サーバ側の口は揃っている（issue 本文の②を追認）:
   `src/knowledge/backend/Services/DocumentService/Features/ObsidianSync/{Manifest,Pull,Push,Delete}`・
   `Features/SyncDevices/{Issue,Reissue,List,Revoke,RevokeAll}`。契約の正は
   `docs/api/FR-20_obsidian-sync.md`（同期プロトコル群は **BFF に載っていない**）。
3. 🔴 **issue の提案（第 1 段 = 読み取り方向のみ）を採る。** 覆さない。理由は `IADR-0331` 決定 1。

## 目的・背景

FR-20 のサーバ側（同期プロトコル・同期端末・監査・期限予告）と SC-20（設定画面）は入っているが、
**Obsidian の中で動く client が 1 ファイルも無く、利用者は FR-20 に到達できない**。本 PR は
プラグインの骨格（配置・ビルド・設定・トークン保管）と、**設定 → manifest → pull → Vault へ書き下ろす**
という縦の一筋を通し、プロトコル部を Obsidian 本体なしで単体テストできる形に切る。

## 母集合（着手前の実測。`.claude/rules/traceability.repo.md` 規則 9・10）

🔴 issue 本文の走査（2026-08-30・`a2c7e5b1`）を転記せず、`89b4d26e` で引き直した。

| 走査 | 件数 | 含意 |
| --- | --- | --- |
| `git ls-files \| grep -i obsidian` | 30 | すべてサーバ・画面・文書・IADR。**`manifest.json` / `main.ts` に当たるものは 0**（`git ls-files 'src/*' \| grep -E '(manifest\.json\|main\.ts)$'` の唯一の一致は `src/packages/ui/.storybook/main.ts`＝Storybook の設定で無関係） |
| `private-notes/sync` を TS/TSX から参照 | **0** | client 実装は無い |
| 同 `.cs` から参照（陽性対照） | 9 | サーバ側の口・テストは実在する（走査形が効いている） |
| 同 `deploy/` `openapi.yaml` から参照 | 1（`openapi.yaml` のみ） | 🔴 **エッジ（`deploy/local/edge-istio/virtualservice-app.yaml`）は `/bff` と `/` しか通さず、`/private-notes/sync/*` を外へ出す経路が無い**。実測: `https://localhost/bff/private-notes/sync/manifest` → **404**（BFF に無い）。プラグインは配備済みクラスタへ**到達できない**（後続 issue。`IADR-0331` 決定 4） |
| `obsidian` を workspace / knip / vitest / eslint / CI の設定から参照 | 0 | 配線はすべて本 PR が新設する |
| 既存 issue の重複検索（`Obsidian` / `プラグイン` / `private-notes/sync`） | #1098・#451 のみ | プラグイン側の実装 issue は他に無い |

**除外**: `src/ai-stock-trading`（submodule）・`node_modules`・`CHANGELOG.md`（自動生成）。

## 対象範囲

### 対象（本 PR ＝ 第 1 段）

1. **配置**: `src/obsidian-plugin/`（pnpm workspace メンバ `@platform/obsidian-plugin`。`src/pnpm-workspace.yaml` へ 1 行）
2. **ビルド**: esbuild → `dist/main.js` ＋ `dist/manifest.json`（Obsidian が読む形）＋ `dist/cli.mjs`（Node ハーネス）
3. **設定**（Obsidian の設定タブ）: 接続先 URL・同期フォルダ・同期トークン（**端末ローカル保管・再表示不可**）
4. **同期（pull のみ）**: `GET /private-notes/sync/manifest` → 差分計算 → `GET /private-notes/sync/notes/{id}` → Vault へ書き下ろし → 同期状態の記録
5. **失敗の可視化**: トークン未設定／401（欠落・不正・期限切れ・失効はサーバが区別しない）を**利用者に判る通知**で出し、**古いファイルを黙って残さない**（何もしないときはその旨を言う）
6. **衝突検知（読み取り方向の範囲）**: ローカルで編集された（最終同期時のハッシュと違い、サーバのハッシュとも違う）ファイルは**上書きしない**で通知に列挙する。ローカルで消されたファイルも再取得せず通知する
7. **プロトコル部の単体テスト**（Obsidian 実体なし）: client（401/404/不正 JSON）・命名（パス検証・衝突）・差分計算・同期の一巡・トークン保管
8. **CI**: `frontend.yml` / `frontend-tests.yml` の `paths:` に `src/obsidian-plugin/**` を足し、`frontend.yml` の既存ジョブにプラグインのビルドと `check-static-egress.js --require src/obsidian-plugin/dist` を足す（**ジョブ名・必須チェック名は変えない**）

### 対象外（理由と送り先）

| 項目 | 理由 | 送り先 |
| --- | --- | --- |
| push（Obsidian 側の編集・新規作成をサーバへ）・delete（論理削除の伝播）・「1 編集」の刻み（デバウンス） | 第 1 段は読み取り方向のみ（issue の提案どおり）。push を入れると競合解決 UI（3 択）と一体になり 1 PR に収まらない | 後続 issue（第 2 段） |
| 競合の解決 UI（ローカル採用／サーバ採用／両方残す） | 同上。本段は**検知して上書きしない**まで | 後続 issue（第 2 段） |
| サーバ側削除（`deleted=true`）のローカルへの伝播・サーバ側リネームの追従 | 消す向きの操作は第 2 段で「削除」と「同期停止」の区別（決定 4）と併せて決める。本段は件数を通知するだけ | 後続 issue（第 2 段） |
| `/private-notes/sync/*` をエッジで外へ出す経路 | `deploy/` 領域（本 issue の宣言ファイル領域外）。契約は変えない | 後続 issue（インフラ） |
| CI でのリリース資産（zip）作成・社内配布の手順化 | 配布形式（GitHub Release か社内共有か）は運用裁定 | 後続 issue |
| en ロケール・Lingui カタログ | SPA の i18n 基盤（IADR-0125）の射程外。プラグインの文言は ja 固定 | 後続（必要になったら） |
| カバレッジ ratchet（`coverage.include`）への算入 | 母数を動かすと床の再計測が要る。templates と同じ扱い（走らせるが数えない） | 後続 issue |
| SC-20 画面の変更（フォルダ設定・競合一覧） | 口が無い（画面仕様書 §未決事項）。本 PR は画面を触らない | #451 / 第 2 段 |

## 設計（要点。判断の記録は `IADR-0331`）

```text
src/obsidian-plugin/
  manifest.json / package.json / tsconfig.json / esbuild.config.js
  src/main.ts                      Obsidian 入口（Plugin）。コマンド 1 つ・設定タブ 1 つ
  src/settings/                    設定の型・既定値・設定タブ（トークンは data.json に置かない）
  src/protocol/                    Obsidian 非依存（Vitest で固定）
    types.ts                       サーバ契約の写し（SyncManifestEntry / PullNoteResponse / 例外）
    transport.ts                   HttpTransport ポート
    endpoint.ts                    接続先 URL の正規化（https 必須。loopback のみ http 可）
    syncClient.ts                  manifest / pull。401 → SyncAuthError、404 → SyncNotFoundError
    hash.ts                        SHA-256 hex（サーバの ContentHash と同じ計算）
    vaultPath.ts                   サーバの vaultPath → ローカルパス（検証・.md 補完・衝突）
    pullPlanner.ts                 差分計算（write / adopt / up-to-date / conflict / server-deleted / skipped）
    pullSync.ts                    一巡の実行（ポート: FileStore / SyncStateStore / Hasher）
  src/transport/                   HTTP の出口 2 つ（Obsidian requestUrl / Node fetch）
  src/obsidian/                    Obsidian アダプタ（Vault adapter・localStorage・data.json）
  src/cli/pull.ts                  Node ハーネス（実測用。同じ pullSync を実 HTTP で回す）
```

- **差分計算の規則**（`pullPlanner.ts`。詳細はテストが固定）:
  - サーバ `deleted=true` → `server-deleted`（ローカルは触らない・件数を通知）
  - パスが不正（空・絶対・`..`・制御文字）→ `skipped(invalid-path)`。2 件が同じローカルパスへ落ちる → 両方 `skipped(path-collision)`
  - ローカルに無い: 未追跡 → `write(new)`／追跡済み → `conflict(local-deleted)`（再取得しない）
  - ローカルが最終同期時のまま: 版もハッシュも同じ → `up-to-date`／違う → `write(updated)`
  - ローカルがサーバと同一内容 → `adopt`（状態だけ記録）
  - それ以外（ローカル編集あり）→ `conflict(local-modified)`（上書きしない）
- **状態**: `noteId → { localPath, version, contentHash（サーバ値）, localHash（書いた内容の計算値）, syncedAt }` を `data.json` に持つ。トークンは持たない。
- **トークン**: `localStorage`（端末ローカル）。キーは Vault 名で分ける。入力時のみ表示・保存後は「保存済み（この端末のみ）」の表示と削除ボタンだけ。

## 受け入れ基準（issue の第 1 段からの写像）

- [ ] Given 段分割と置き場所・配布方式 / When 決める / Then `IADR-0331` に残っている（論点 1〜4 すべてに答えている）
- [ ] Given 同期トークンを設定したプラグイン / When `manifest` を取得する / Then サーバの資料一覧と版が読める（単体: `syncClient.test.ts`。実測: §実測）
- [ ] Given 差分 / When `pull` する / Then Vault にファイルが書き下ろされる（単体: `pullSync.test.ts`。実測: §実測）
- [ ] Given 期限切れ・失効したトークン / When 同期する / Then 利用者に判る形で失敗する（単体: 401 → `SyncAuthError` → ファイル・状態を触らない。実測: 無トークン／不正トークン → 401）
- [ ] Given プロトコル部 / When 単体テストを走らせる / Then Obsidian 実体なしで緑になる
- [ ] Given `src/` / When `pnpm run lint` / `typecheck` / `test` / `build` / `format:check` / Then すべて成功する
- [ ] Given 成果物 / When `node scripts/check-static-egress.js --require src/obsidian-plugin/dist` / Then 違反 0
- [ ] Given 第 2 段以降 / When 本 PR を閉じる / Then 残りの範囲が追加 issue として起票されている

## テスト方針

- Obsidian API に触れる層（`main.ts` / `settings` / `obsidian/` / `transport/obsidianTransport.ts`）は**薄く**保ち、
  単体テストの対象は `protocol/` と `obsidian/tokenStore.ts`（`Storage` を注入）に限る。
- 🔴 否定形は陽性対照と対で置く: 401 で「何も書かない」は、200 で「書く」と同じテストファイルに置く。
  無効パスの `skipped` は、有効パスの `write` と対にする。
- 実 HTTP の証跡は Vitest ではなく `dist/cli.mjs` で取る（Obsidian 本体は無い。`IADR-0331` 決定 6）。

## 実測（証跡は本文末尾の「実測記録」節）

## 計画書との差異

- **プラグインへの受け渡し方法**（計画 SC-20 §未確定「手入力かワンタイムコード方式か」）は計画が未確定のまま
  である。本段は**手入力（貼り付け）**で実装し、ワンタイムコード方式へ替えるならプラグイン側の入力欄
  だけが変わる形に留めた（`IADR-0331` 決定 5）。計画側の裁定は不要と判断（計画が「未確定」と明記している
  論点の実装値であり、覆れば設定タブだけを直す）。
- **接続先の公開経路**が計画に無い。08_data-egress-policy の許容条件 3「同期経路が本システムの提供する手段」は
  プラグインを指しており、どの host で `/private-notes/sync/*` を受けるかは実装（配備）の判断である。
  本 PR では決めず、後続 issue に送る。

## 未決事項（残件として報告書へ）

1. `/private-notes/sync/*` のエッジ公開（`deploy/`）—— **#1154 に起票**
2. 第 2 段（push / delete / 競合解決 UI / サーバ側削除・リネームの伝播 / 「1 編集」の刻み）—— **#1153 に起票**
3. 配布（リリース資産化）・カバレッジ算入・en ロケール —— `IADR-0331` フォローアップ 3〜5（起票は配布形式の
   運用裁定と併せて行う。#1153 / #1154 の着地後）
4. 🔴 有効トークンでの実 HTTP 陽性（manifest 200 → pull → 書き下ろし）は本セッションで実行できていない
   （§実測記録）。手順は用意済みで、PR のレビュー時に 1 コマンドで再現できる

## 実測記録（2026-09-02）

### 単体・ゲート（`src/`。`pnpm --config.manage-package-manager-versions=false …`）

| 検査 | 結果 |
| --- | --- |
| `pnpm --filter @platform/obsidian-plugin run typecheck` | exit 0 |
| `pnpm exec eslint obsidian-plugin eslint.config.js vitest.config.ts` | 0 errors |
| `pnpm exec vitest run obsidian-plugin` | **7 files / 32 tests passed** |
| `pnpm run format:check` | All matched files use Prettier code style |
| `pnpm run lint`（全体） | 0 errors / 9 warnings（すべて既存の `react-refresh/only-export-components`） |
| `pnpm --filter @platform/obsidian-plugin run build` | `dist/main.js 23.8kb` / `dist/cli.mjs 14.0kb` / `manifest.json` |
| `node scripts/check-static-egress.js --require src/obsidian-plugin/dist` | OK（3 ファイル・外部オリジンからの取得なし） |
| `pnpm exec knip`（`check-knip.js` は Windows で `.bin/knip` を起動できず fail-open） | 区分別件数 devDependencies 4 / exports 16 / types 17 = 床と一致。`unlisted` は 2 で床 1 より 1 多いが、増分は `platform/frontend/src/features/index.ts` の `@ai-stock-trading/features`＝**submodule 未 populate の環境差**（`check-knip.js` 冒頭が予告する fail-closed の向き。CI は submodule を取得する）。プラグイン由来の指摘は 0 |
| `node scripts/check-{trace-blocks,doc-links,test-traceability,test-spec-coverage,doc-type-vocabulary,plan-id-qualification,adr-numbering}.js` / `gen-knowledge-graph --check` | すべて OK（`git add -A` 後に実行） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 1 回目は `check-nul-bytes` で **fail**（`vaultPath.ts` の制御文字の正規表現がエディタで生のバイトになっていた。#956 と同型）→ `String.fromCharCode` で組み直して再実行 → 通過 |
| `pnpm run typecheck` / `pnpm run test`（全体） | typecheck は `platform/frontend` が `@ai-stock-trading/features` 未解決で fail、vitest は 5 files / 7 tests fail —— **いずれも submodule `src/ai-stock-trading` が未 populate の環境差**（Layout / breadcrumbs / router / initialChunk の 4 件）と **Node 24 だけの既知の偽赤**（`orvalMutator.test.ts` の Blob 1 件。CI は Node 22）。プラグイン由来の失敗は 0。submodule を populate すると `pnpm install --frozen-lockfile` が lock との不一致で止まる（develop の CI は緑）ため、ローカルでは追い切らなかった |

### 実 HTTP（稼働 k3s・Obsidian 本体なし・`dist/cli.mjs`）

接続先は `kubectl -n microservices-platform port-forward svc/document-service 18093:8080`
（Pod は再起動していない）。エッジは `--cacert`（cert-manager の `local-edge-root-ca`）で検証し、
`--ssl-revoke-best-effort`（私設 CA に CRL が無いための Schannel の回避。`-k` は使っていない）。

| # | 手順 | 結果 |
| --- | --- | --- |
| N1 | `curl https://localhost/` | 200（エッジ到達） |
| N2 | `curl https://localhost/bff/private-notes/sync/manifest` | **404** —— BFF に同期プロトコルの口は無い（設計どおり。エッジ公開は後続 issue） |
| N3 | `curl https://localhost/bff/private-notes/devices`（セッション無し） | 401（BFF 側の口は在る） |
| N4 | `curl http://127.0.0.1:18093/private-notes/sync/manifest`（トークン無し） | **401**（DocumentService 実体） |
| N5 | 同 `Authorization: Bearer not-a-real-token` | **401**（不正トークンも同じ 401） |
| N6 | `MSP_SYNC_ENDPOINT=http://127.0.0.1:18093 MSP_VAULT_DIR=<tmp> node dist/cli.mjs`（トークン未設定） | exit **3**「同期トークンが未設定です」。HTTP 要求を出さない |
| N7 | 同 `MSP_SYNC_TOKEN=not-a-real-token` | exit **2**「認証失敗（401）: 同期トークンが受け付けられませんでした（未設定・不正・期限切れ・失効のいずれか）。Vault のファイルは変更していません。」—— `<tmp>` は作られもしない（陰性の確認） |
| N8 | `MSP_SYNC_ENDPOINT=http://kb.example.co.jp`（loopback 以外の http） | exit **3**「接続先は https でなければなりません」 |

🔴 **陽性（有効トークンで manifest 200 → pull → ファイル書き下ろし）は本セッションでは実行できていない。**
同期トークンの発行には JWT（`POST /private-notes/devices`）が要り、この開発クラスタでは
①人の利用者は全員 `CONFIGURE_TOTP` を抱えており（`developer` で実測: パスワード後に
`login-actions/required-action?execution=CONFIGURE_TOTP` へ遷移）、共有クラスタの利用者に TOTP を登録すると
並行中の他セッションのログインを変える、②機械主体（`abac-seeder` の client_credentials。
`scripts/seed-abac-policies.js` と同じ作法）は資格情報の交換を伴い、**本セッションの権限分類器が実行を
拒んだ**。手順は 1 コマンドに落としてある（作業台帳 §報告。`issue-sync-token.sh` → `cli.mjs`）。
陽性の写像は単体（`pullSync.test.ts` の 200 → 書く／401 → 書かない の対）で固定されており、
実 HTTP 側は N4〜N7 の陰性と N1〜N3 の経路確認までである。

### 受け入れ基準の充足

- [x] 論点 1〜4（＋受け渡し・段分割）が `IADR-0331` に在る
- [x] manifest 取得（単体 P1。実 HTTP は陰性まで）
- [x] pull → Vault 書き下ろし（単体 P5〜P7。実 HTTP は陰性まで）
- [x] 期限切れ・失効は利用者に判る失敗で、ファイルを触らない（単体 P3・実 HTTP N5〜N7）
- [x] プロトコル部が Obsidian なしで緑（32 件）
- [x] `lint` / `typecheck`（プラグイン）/ `test`（プラグイン）/ `build` / `format:check` 成功（全体の typecheck / test の赤は submodule 未 populate と Node 24 の環境差）
- [x] 成果物に外部 CDN・Web フォント・analytics を含まない（`check-static-egress.js`）
- [x] 残りの範囲を issue に起票（PR 本文参照）
