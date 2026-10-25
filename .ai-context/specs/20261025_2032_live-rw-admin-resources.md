---
title: 作業仕様書 — live-rw の管理リソース（SC-05 文書・SC-06 データソース・SC-09 ポリシー・SC-12 MCP クライアント）の作成と後始末（E-52）
type: spec
status: in-progress
related_ids: [SC-05, SC-06, SC-09, SC-12, SC-02, SC-03, FR-05, FR-06, FR-16, UC-03, UC-05, UC-09, NFR, IADR-0629, IADR-0630, IADR-0631]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/05_screens（SC-05・SC-06・SC-09・SC-12）
issue: "#2032"
---

# 作業仕様書 — live-rw の管理リソース（#2032 / E-52）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。親は #2013（網羅表 M-076・M-082・M-092・M-104、基準書 §7、live-poc.md §3）。
> E-51（#2031・IADR-0630）の門・`e2eName`・`cleanup.ts`・`issuedSecrets.ts` を使う。
> 🔴 書き込みは使い捨てのスタック（e2e-live.yml）の中だけ。e2e-revocable では作らない（作る側は e2e-admin）。secret の値を書かない・出さない。

## 受け入れ基準（#2032 ＋ オーケストレーターの条件 2026-10-25）

- [ ] M-076: e2e-admin が SC-05 で `e2e-<runId>-doc-…` を登録 → 公開 → SC-02 の検索で見える → アーカイブ → 削除 —— live-rw
- [ ] M-082: SC-06 で `e2e-<runId>-source-…`（接続先は到達しないダミー）を登録 → 無効化。手動同期は押さない —— live-rw
- [ ] M-092: SC-09 で `e2e-<runId>-policy-…` を検証（dry-run。保存されない）→ 保存 → 削除 —— live-rw
- [ ] M-104: SC-12 で無人クライアント `e2e-<runId>-mcp-…` を登録（secret は記録しない・成果物の走査の対象に足す）→ 無効化 —— live-rw
- [ ] 既存のポリシー・属性辞書・タグ辞書・関係の型の辞書に差分が無い（各 test の前後で GET を突き合わせる。`e2e-` の行は除く） —— live-rw
- [ ] e2e-live で skip 0・失効 6・走査 0、sweep の dry-run で残骸 0・見ていない視点 0 —— live の実走

## 判断（IADR-0631）

1. **削除の口が無い種類の「後始末済み」**: データソースの DELETE は実体を消さず status を `disabled` にする（`DataSource.Disable()`）。
   MCP クライアントは BFF に削除の口が無く、無効化（`POST …/{clientId}/disable`。何度送っても 200）だけである。どちらも行が残る。
   sweep は、`e2e-` の一致のうち**無効化済み**（データソースは `status=disabled`、MCP クライアントは `enabled=false`）を「後始末済み（削除の口が無い）」として
   列挙し、残骸に数えない。無効化されていないものは残骸であり、`--delete` は無効化を送る。網羅表の M-082・M-104 の「削除」は API に無いので、
   本 PR は「無効化まで」を流し、網羅表の訂正を #2013 に残す。
2. **ポリシーは何も新しく許さない形で作る**: 利用者 `clearance=restricted` × 文書 `confidentiality=restricted` の read。seed の
   「restricted 取扱者は全区分を読める」に含まれるので、作った間も認可の結果が変わらない（同じ実行の他の試験を揺らさない）。
3. **M-076 の SC-02**: SC-05 の登録フォームは本文を持たないが、本文の無い文書も索引のメタデータ点（タイトル・タグ・属性）を持つので、
   タイトルの runId で検索に出る。`E2E_SEARCH_HITS=1` のときだけ判定する（e2e-live は 1）。取り込みは非同期なので開き直しながら待つ。
4. **辞書の差分なし**: 各 test の前後で `GET /bff/admin/authz/policies`・`/bff/admin/authz/attributes`・`/bff/tags`・`/bff/edge-types` を
   読み、`e2e-` で始まる行を除いて一致することを見る（読み取りだけ）。
5. **secret を記録しない**（M-104）: `mcp-clients.rw.spec.ts` は trace・スクリーンショット・動画を切る（ファイルの先頭でしか切れないので分ける）。
   登録の応答の `clientSecret` は後始末（無効化）を積んだ後に `E2E_ISSUED_MCP_CLIENT_SECRET` として記録し（成果物の走査が探す）、
   画面の「閉じる」で表示を消す。secret に触れる assertion を置かない。

## 試験の流れ

- `e2e/live/admin-resources.rw.spec.ts`（e2e-admin）:
  - M-076: `/admin/documents` →「＋ 新規登録」→ タイトル・機密区分「社内限」（タグは付けない）→ 保存（POST の応答から ID・直後に `track` DELETE）→
    行の「公開」→（`E2E_SEARCH_HITS=1`）`/search?q=<runId>` で見える → 「アーカイブ」→「削除」→ 行が消える。
  - M-082: `/admin/sources` →「＋ ソース登録」→ 名前・種別（ファイルサーバー）・接続先 `file:///nonexistent/<name>` → 登録する（応答の ID を `track` DELETE）→
    行の「無効化」→「無効」。
  - M-092: `/admin/abac` → 名前・read・条件 2 つ → 検証（POST validate 200・一覧は増えない）→ 保存（応答の ID を `track` DELETE）→ 行の「削除」→ 消える。
- `e2e/live/mcp-clients.rw.spec.ts`（e2e-admin・trace 等を切る）:
  - M-104: `/admin/mcp-clients` → 種別「無人」・ID・表示名・属性は付けない（何も読めない無人アカウント。属性が無ければ登録者の部分集合の判定も呼ばれない）→ 登録（応答から clientId を取り `track` disable → secret を記録）→
    secret の枠を閉じる → 行の「無効化」→「無効（即時接続拒否）」。

## 母集合（規則 9・10）

- sweep の種類と後始末の判定を書いている箇所: `e2e-live-sweep.js`・`scripts.repo.test.js`（#2030 節）・IADR-0629 決定 5・E2E_STANDARDS.md §7・scripts/README.md。
- 網羅表の M-082・M-104 の「削除」: #2013 のコメント 2（網羅表）。リポジトリに無い。PR で訂正を出す。

## 変更するファイル

- 新規 `e2e/live/admin-resources.rw.spec.ts`・`e2e/live/mcp-clients.rw.spec.ts`・`e2e/live/support/dictionaries.ts`・IADR-0631
- `scripts/e2e-live-sweep.js`・`scripts/scripts.repo.test.js`・`scripts/README.md`・`docs/tests/E2E_STANDARDS.md`・`.ai-context/adr/README.md`

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1199 passed（#2030 節の sweep の試験に、無効化済みのデータソース・MCP クライアントを後始末済みとして
  数えないことと、無効化されていない MCP クライアントへ無効化を送ることを足した）。
- `E2E_LIVE=1 E2E_LAYER=ro,rw E2E_STACK=e2e-live … --list`: 52 件（live-rw に M-076・M-082・M-092・M-104 を足した）。
- `tsc -p tsconfig.node.json` 0 件・eslint 0 件（警告 1: SC-02 の段の `E2E_SEARCH_HITS` の分岐）・prettier 緑。文書の検査は緑。
- live: e2e-live（PR）で ro,rw を流した結果は PR に書く。
