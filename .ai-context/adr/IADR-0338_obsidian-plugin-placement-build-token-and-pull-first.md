---
title: IADR-0338 自作 Obsidian プラグインは src/ の workspace メンバに置き、esbuild で束ね、同期トークンは端末ローカルに保管し、第 1 段は pull のみとする
type: impl-adr
status: Proposed
related_ids: [FR-19, FR-20, UC-11, SC-20, ADR-0037, ADR-0046, ADR-0054, IADR-0270]
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
issue: "#1098"
---

# IADR-0338: 自作 Obsidian プラグインの配置・ビルド・接続先・トークン保管・段分割

- 状態: Proposed
- 日付: 2026-09-02
- 決定者: claude（実装判断）／起点 issue #1098（#451 の残射程）

## 起点・関連

- 関連する計画書 ID: FR-20（前提 FR-19）/ UC-11 / SC-20 / `ADR-0037`（決定 1〜20・課題 2）/
  `ADR-0046` / `ADR-0054` / 08_data-egress-policy §個人資料の同期に関する例外（許容条件 1〜7）
- 関連する実装仕様書: `.ai-context/specs/20260902_issue-1098_obsidian-plugin-pull-stage1.md`
- 前提 IADR（覆さない）: `IADR-0270`（同期トークンは別系統の不透明トークン・401 は区別しない・
  push は `edits[]`・競合は 409 で自動解決しない）/ `IADR-0121`（pnpm workspace・vitest 横断）/
  `IADR-0125` 決定 5（成果物の egress 走査）

## コンテキストと課題

計画 `ADR-0037` 決定 1 は転送方式を「自作 Obsidian プラグイン（社内配布）」と確定し、サーバ側
（`/private-notes/sync/*`・同期端末・監査・期限予告）と SC-20 は実装済みだが、**Obsidian の中で動く
client が 1 ファイルも無い**（#1098。母集合は作業仕様書 §母集合）。issue は着手時に決めるべき論点を
4 つ挙げた —— ①置き場所 ②ビルドと配布 ③接続先 ④テストの器。加えて計画が「未確定」と明記する
**⑤接続設定（エンドポイント・トークン）のプラグインへの受け渡し方法**（05_screens SC-20 §未確定）と、
issue の提案する**⑥段分割**を決める。

## 検討した選択肢と決定

### 決定 1: 段分割 —— 第 1 段は **設定 → manifest → pull（読み取り方向）** に限る。push / delete / 競合解決 UI は第 2 段

| 案 | 評価 |
| --- | --- |
| **A. 第 1 段 = pull のみ（採用。issue の提案どおり）** | 縦の一筋（設定・トークン・HTTP・差分計算・Vault 書き込み・失敗の可視化）が 1 PR で閉じる。push を持たないので「1 編集の刻み」（`ADR-0037` フォローアップ 5）と 3 択の競合 UI を先送りできる |
| B. 双方向を一度に | 競合解決 UI（差分 2 ペイン・3 択）と push のデバウンス設計が一体になり、レビュー可能な単位を超える。#1098 自身が「1 PR に収まらない」と判断している |
| C. push を先に | pull 無しでは競合（409）の「サーバ採用」を実現できず、決定 7 の 3 択が成立しない |

- 第 1 段でも**衝突は検知する**（決定 9）。「ローカルで編集された資料を上書きしない」は pull だけでも要る
  —— push が無い段では、ローカルの編集は**まだサーバへ送っていない編集**であり、上書きすると失われる。
- サーバ側削除（manifest の `deleted=true`）は**件数を通知するだけ**でローカルを触らない。消す向きの操作は
  第 2 段で決定 4（「対象フォルダから外す」≠「削除」）と `ADR-0037` フォローアップ 11 と併せて決める。

### 決定 2: 置き場所 —— `src/obsidian-plugin/` を pnpm workspace メンバ（`@platform/obsidian-plugin`）にする

| 案 | 評価 |
| --- | --- |
| **A. `src/obsidian-plugin/`・workspace メンバ（採用）** | typecheck / lint / format / 単体テストの横断ゲート（`pnpm -r run typecheck`・`eslint .`・`prettier --check .`・横断 vitest）に**追加の配線なしで**乗る。`pnpm-workspace.yaml` へ 1 行 |
| B. `src/packages/obsidian-plugin/` | `packages/*` は「ユニットが共用するワークスペースパッケージ」（`IADR-0121` 決定 4。実体は `@platform/ui`）。プラグインは誰にも import されない成果物であり、意味が違う。CI の `paths:` に自動で入る利点はあるが、分類を曲げてまで得るものではない |
| C. 新ユニット `src/obsidian/{backend,frontend}` | ユニットは「基盤または可変機能セットの自己完結した実装単位」（`src/README.md`）で `backend/` を伴う。プラグインに backend は無く、合成点にも載らない |
| D. 別リポジトリ | サーバ契約（`docs/api/FR-20_obsidian-sync.md`）と同じリポジトリに居ないと、契約変更でプラグインが静かに壊れる。planning 依存の撤去（`IADR-0228`）と同じ理由で、追随義務のある別リポジトリを増やさない |

- 🔴 **アプリ（`*/frontend`）でもライブラリ（`packages/*`）でもない第 3 の成果物**なので、
  どちらのグロブにも入れず `pnpm-workspace.yaml` に名指しで置く。CI の `paths:` も同様に名指し
  （`src/obsidian-plugin/**`。push / pull_request の両方）。

### 決定 3: ビルドと配布 —— esbuild で `dist/main.js` ＋ `manifest.json`（＋ Node ハーネス `cli.mjs`）を作り、CI は成果物を **egress 走査**する。配布のリリース資産化は後続

- Obsidian が読むのは `main.js`（CommonJS。`obsidian` は本体が与えるので external）と `manifest.json`。
  `styles.css` は持たない（設定タブと通知だけで独自の見た目が無い）。
- **08_data-egress-policy の「外部 CDN 禁止」は成果物の中身にも当たる**（#1098 論点 2）。`check-static-egress.js`
  の**射程を広げるのではなく、走査先を 1 つ足す**（`frontend.yml` の既存ステップに
  `--require src/obsidian-plugin/dist`。検査器のコードは触らない）。ジョブ名・必須チェック名は変えない。
- 配布は本段では**手動**（`dist/` の 3 ファイルを Vault の `.obsidian/plugins/msp-private-notes-sync/` へ置く。
  手順は `docs/how-to/obsidian-plugin-install.md`）。GitHub Release への資産化・社内配布の運用は後続 issue
  （配布形式は運用裁定）。

### 決定 4: 接続先 —— BFF ではなく **`/private-notes/sync/*` を Bearer 同期トークンで直接**呼ぶ。基底 URL は設定値。🔴 エッジ公開は後続

- `ADR-0037` 課題 2（同期トークンはブラウザセッションと別系統）と `IADR-0270` 決定 3（DocumentService が
  自前で検証）どおり。`docs/api/FR-20_obsidian-sync.md` も同期プロトコル群を「BFF なし」と明記する。
  実測（2026-09-02）: `https://localhost/bff/private-notes/sync/manifest` → **404**（BFF に口が無い）。
- **BFF に載せる案は採らない**: BFF の資格情報は HttpOnly セッション Cookie（`ADR-0032`）で、CSRF ヘッダも
  要る。プラグインは Cookie を持たず、Bearer の別系統を BFF に通すと「BFF は Cookie セッションだけ」という
  境界が崩れる。
- 🔴 **現行のエッジ（`deploy/local/edge-istio/virtualservice-app.yaml`・本番チャート `templates/edge.yaml`）は
  `/bff` と `/`（SPA）しか通さず、`/private-notes/sync/*` は外へ出ていない。** 配備済みクラスタに対して
  プラグインは**到達できない**。公開経路（どの host・path で DocumentService へ振るか）は `deploy/` 領域で
  #1098 の宣言ファイル領域の外なので、**後続 issue に切る**（契約は変えない）。本 PR の実測は
  `kubectl port-forward svc/document-service` を接続先にする。
- 接続先は https に限る（同期トークンが Bearer で平文のまま載る）。**loopback（localhost / 127.0.0.1）だけ
  http を許す**（port-forward の検証用）。

### 決定 5: トークン保管 —— **`data.json` に置かず、端末ローカルの localStorage**（Obsidian `app.saveLocalStorage`）に平文で置く。受け渡しは**手入力（貼り付け）**

| 案 | 評価 |
| --- | --- |
| A. `data.json`（プラグイン設定）に平文 | 🔴 **不採用**。`data.json` は Vault の一部で、Obsidian Sync / git で**別の端末へ複製される**。同期トークンは**端末ごとに発行し端末ごとに失効する**（`ADR-0037` 決定 11・13）ので、Vault と一緒に運ばれる置き場は設計と矛盾する（端末 A の失効が端末 B の複製に効かない） |
| **B. 端末ローカルの localStorage（採用）** | Obsidian の `app.saveLocalStorage` は Vault 固有・端末ローカルで、Vault のファイルには入らない。暗号化は無いが**露出はその端末のプロファイル内に閉じる** |
| C. OS キーチェーン（Electron `safeStorage`） | main プロセス限定でプラグイン API から届かない。モバイルにも無い |
| D. メモリのみ（起動ごとに貼り直し） | 30 日有効の資格情報を毎回貼らせると、利用者は平文をメモに残す。安全側に倒したつもりで露出が増える |

- 計画は受け渡し方法を「手入力かワンタイムコード方式か」で**未確定**としている（05_screens SC-20 §未確定）。
  本段は**手入力**で実装し、ワンタイムコード方式へ替わっても**設定タブの入力欄だけが変わる**形に留めた
  （`TokenStore` ポートは変わらない）。計画側の裁定を要しない実装値と判断した（覆れば設定タブを直す）。
- 一度保存したトークンは画面へ戻さない（SC-20「再表示できない」と同じ規律を受け側でも守る）。期限切れの
  トークンが**ここに残ったままになる**旨の固定文言（SC-20 と同文）を設定タブに置く。

### 決定 6: テストの器 —— Obsidian 依存を 4 つのポートで切り、プロトコル部を Vitest で固定する。実 HTTP の証跡は Node ハーネスで取る

- ポート: `HttpTransport`（Obsidian `requestUrl` ／ Node `fetch`）・`FileStore`（Vault adapter ／ fs）・
  `SyncStateStore`（`data.json` ／ JSON ファイル）・`TokenStore`（`app.saveLocalStorage` ／ 環境変数）。
  `protocol/`（client・endpoint・hash・vaultPath・pullPlanner・pullSync）は Obsidian を import しない。
- 横断 vitest（`src/vitest.config.ts` の `test.include`）に足す。**`coverage.include` には入れない**
  （雛形と同じ扱い。母数を動かすと床の再計測が要る。算入はフォローアップ）。
- ESLint の BFF 境界規則（`fetch` 禁止）は **`transport/` の 2 ファイルだけ**除外する。規則の意図
  「SPA から出る HTTP を 1 箇所へ収束させる」はプラグインには当たらないが、**出口を 2 ファイルに限る**
  ことで同じ規律を保つ（`protocol/` で `fetch` を書けば error になる）。
- Obsidian 本体は CI にも実測環境にも無い。**同じ `runPullSync` を `dist/cli.mjs`（Node）から実 HTTP に
  当てて証跡を取る**（Obsidian の GUI 操作は証跡にならない）。

### 決定 7: サーバの `ContentHash` と同じ計算（UTF-8 本文の SHA-256 小文字 hex）をプラグインでも行う

- 「ローカルがサーバと同じ内容か」「最終同期時から変わったか」の判定に使う。Web Crypto
  （`globalThis.crypto.subtle`）は Obsidian（Electron / モバイル）と Node ≥ 19 の両方に在り、実装は 1 つで済む。
- 状態には**サーバの `contentHash`**（版の同一性）と**書いた内容から計算した `localHash`**（ローカル変更の検知）を
  別に持つ。サーバ値だけだと、サーバの計算とローカル内容がずれたときに毎回「ローカル編集あり」に化ける。

### 決定 8: UI 文言は ja 固定。SPA の Lingui カタログには載せない

- `IADR-0125` の i18n 基盤は SPA（`platform/frontend`）の設計で、プラグインは別の実行環境である。
  社内配布（`ADR-0037` 決定 1）の第 1 段で en を要求する利用者は居ない。必要になったら Obsidian の
  ロケール（`moment.locale()`）で切り替える形を後続で足す。

### 決定 9: 差分計算の規則（pull 側の衝突検知）

| ローカル | 追跡状態 | 判定 |
| --- | --- | --- |
| 無い | 未追跡 | `write(new)` |
| 無い | 追跡済み（同じパス） | `conflict(local-deleted)` —— **再取得しない**（利用者の削除を黙って戻さない） |
| 最終同期時のまま | 版・ハッシュがサーバと同じ | `up-to-date`（pull しない＝本文の egress を増やさない） |
| 最終同期時のまま | サーバが進んだ | `write(updated)` |
| サーバと同一内容 | 任意 | `adopt`（書かずに状態だけ記録） |
| どちらとも違う | 任意 | `conflict(local-modified)` —— **上書きしない** |
| — | サーバ `deleted=true` | `server-deleted`（件数を通知。ローカルは触らない） |
| パス不正・衝突 | — | `skipped`（Vault の外・`..`・制御文字／同じローカルパスへ落ちる 2 件） |

## 理由

- **決定 1・9** は `ADR-0037` 決定 7（自動解決を既定にしない）を pull だけの段でも守るため。「読み取り方向
  だけなら競合は無い」は誤りで、ローカルの未送信編集は pull の上書きで失われる。
- **決定 2** は「検査を足す前に既存ゲートへ乗せる」ため。workspace メンバなら typecheck / lint / format / test の
  4 つが**設定を 1 行も足さずに**プラグインを見る。CI の `paths:` は名指しで足す（同型の取りこぼしは
  `scripts.repo.test.js` の #801 節が `test.include ⊆ paths:` で突合する）。
- **決定 4** は `ADR-0037` 課題 2 と `IADR-0270` 決定 3 の直接の帰結。エッジ公開の不在は本 IADR が**発見**した
  事実であり、契約を変えずに配備側で塞ぐ。
- **決定 5** は「トークンが端末に紐づく」という計画の設計（決定 11・13）を保管場所で裏切らないため。
  `data.json` は最も安直だが、Vault の複製経路（Obsidian Sync / git）で**別端末へ漏れる**。

## 結果

- 良い影響:
  - FR-20 の client 側に骨格ができ、**設定 → manifest → pull → Vault** の縦の一筋が Obsidian 実体なしで
    テストされる（単体 32 件）。第 2 段は `protocol/` に push / delete を足す形で進められる
  - 成果物（`dist/`）が egress 走査に掛かり、外部 CDN・フォント・analytics の混入を CI が止める
  - トークンが Vault の複製経路に乗らない
- 悪い影響・トレードオフ:
  - 🔴 **配備済みクラスタへは到達できない**（エッジに経路が無い。後続 issue）。本段の実測は port-forward
  - 🔴 **第 1 段は片方向**であり、`ADR-0037` 決定 2（双方向）は未達。利用者の編集はサーバへ届かない
  - トークンは端末ローカルとはいえ平文。端末のプロファイルへ到達できる主体には読める
    （08_data-egress-policy §受け入れたリスク「端末要件を課さない」の範囲内）
  - 単体テストはカバレッジの母数に入っていない（ラチェットが守っているのは SPA と `@platform/ui` だけ）
- フォローアップ:
  1. `/private-notes/sync/*` のエッジ公開（`deploy/`。契約は変えない）
  2. 第 2 段: push（`edits[]`・デバウンス）/ delete / 競合解決 UI（3 択）/ サーバ側削除・リネームの伝播
  3. 配布のリリース資産化（zip）と社内配布手順
  4. `coverage.include` への算入と床の再計測
  5. en ロケール（必要になったら）

## 関連

- Supersedes: なし
- Superseded by: なし
- 実装 issue: #1098（本体）/ #451（親）
