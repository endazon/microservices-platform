---
title: 作業仕様書 — Obsidian プラグインの配布をリリース資産化し、move を配備済みクラスタで実測する（#1213）
type: spec
status: in-progress
related_ids:
  - FR-19
  - FR-20
  - UC-11
  - SC-20
  - ADR-0021
  - ADR-0037
  - IADR-0270
  - IADR-0338
  - IADR-0348
  - IADR-0352
  - IADR-0360
  - IADR-0375
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# 仕様書: issue #1213 — 配布のリリース資産化・`move` の配備後実測・実機目視の扱い

> #1098（第 1 段 pull）/ #1153（第 2 段 push・削除・競合）/ #1154（エッジ経路）/ #1176（リネームの口）が
> 着地したあとの残射程を集約した issue である。**本 PR は 3 つのうち 2 つを閉じ、1 つ（Obsidian 実機での
> 目視）は閉じない。** 閉じない理由と、その代わりに何を測ったかを本書と PR に明記する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-20（双方向同期）。前提 FR-19
- ユースケース（UC）: UC-11
- 画面（SC）: SC-20（本 PR は画面を触らない）
- 関連 ADR: `ADR-0037` 決定 1（転送方式＝自作プラグインの**社内配布**）・決定 2（双方向）・
  決定 7（競合を自動解決しない）/ `ADR-0021`（エッジ＝Istio Gateway）/
  08_data-egress-policy（成果物に外部 CDN・フォント・analytics を含めない）
- 実装 IADR: `IADR-0338`（第 1 段。決定 3 が配布のリリース資産化を**フォローアップ 3** へ切った）/
  `IADR-0348`（エッジ経路）/ `IADR-0352`（第 2 段）/ `IADR-0360`（リネームの口）/
  `IADR-0375`（本作業）

## 着手条件の確認（実測 2026-09-05）

1. `ADR-0037` は `Accepted`。「着手可否の注記」に FR-20 への留保は無い（#1098 / #1153 / #1176 と同じ）。
   **着手可**。
2. develop は取り込み済み（`git merge origin/develop` → `Already up to date.`）。
3. 稼働 k3s は #1088 で立て直し済み（PERSIST 既定・develop 最新イメージ・エッジ TLS）。
   `document-service` は 3h45m 稼働、イメージは `k3d-local/microservices-platform/document-service:latest`。

## 射程の確定（issue の記述を転記せず、自分で測った）

issue は 4 項を挙げるが、**着地済みのものが混ざっている**ので 1 件ずつ陽性対照つきで測った。

### ① 配布のリリース資産化 —— **未実装**（射程に残す）

| 測ったこと | 結果 | 陽性対照 |
| --- | --- | --- |
| `gh release list` | **0 件**（出力なし） | — |
| `git tag` | **0 件** | — |
| リリース資産を出すワークフロー | **無い** | `grep -ril "release" .github/workflows/` は **4 件**（`changelog.yml` / `ci.yml` / `codeql.yml` / `integration.yml`）を返す＝走査は効いている。どれも成果物を Release へ上げない |
| 手順書の記述 | `docs/how-to/obsidian-plugin-install.md:39`「社内配布のリリース資産化（zip 等）は未整備で、いまは `dist/` をそのまま置く」 | — |

### ② Obsidian 実機での目視 —— **実施不能**（射程に残し、別 issue へ切る）

Obsidian 本体（GUI アプリ）はこの環境にも CI にも無い。`IADR-0338` 決定 6 が既に
「Obsidian の GUI 操作は証跡にならない」として CLI ハーネス（`dist/cli.mjs`）を証跡の器に選んでいる。
**本 PR で「実機で目視した」とは書かない。** 代わりに測るものは ③ の実 HTTP 往復であり、
それは Modal（競合 3 択）・Vault イベント配線・設定タブの**目視**を代替しない。

### ③ `move` の配備後実測 —— **未実測**（射程に残す）

PR #1212 は契約とプラグインを足したが、実 HTTP は port-forward での 404/401 判別止まりだった。
配備済みイメージに口が在ることは測れた（下記）が、**200 の往復と 409 の陰性対照は測っていない**。

```console
$ kubectl -n microservices-platform port-forward svc/document-service 18213:8080
$ # 無認証での探り。端点の実在は 401 と 404 の差で判る（陽性 / 陰性の対）
notes/00000000-0000-0000-0000-000000000000/move            -> 401   ← 口は在る
notes/00000000-0000-0000-0000-000000000000/delete          -> 401   ← 陽性対照（既に在る口）
notes/00000000-0000-0000-0000-000000000000/nonexistent-op  -> 404   ← 陰性対照（無い口はこうなる）
GET /private-notes/sync/manifest                           -> 401
```

### ④ 本番でのエッジ経路の opt-in —— **着地済み**（射程から外す）

`edge.privateNotesSync.enabled`（既定 `false`）は `IADR-0348` / PR #1173 で入っており、
`templates/edge.yaml`・`templates/networkpolicy.yaml`・`values.yaml` の 3 箇所が実在する。
**本番配備で `true` にするのは運用操作**であってコード変更ではない（この worktree から本番は触らない）。
ローカルの実エッジ（overlay）は無条件に通るので、③ の実測はエッジ経由で行う。

### `IADR-0338` フォローアップ 3〜5 の現状

| 項 | 内容 | 現状 |
| --- | --- | --- |
| 3 | 配布のリリース資産化と社内配布手順 | **本 PR で閉じる**（① そのもの） |
| 4 | `coverage.include` への算入と床の再計測 | 未実施（`src/vitest.config.ts` の `coverage.include` に `obsidian-plugin` は無い）。**本 PR の射程外** —— 床の再計測は全ユニット横断の測り直しを伴い、配布とは別の作業である |
| 5 | en ロケール | 未実施。要求が出ていない（`IADR-0338` 決定 8 のとおり）。**射程外** |

## 母集合（着手前の実測。`.claude/rules/traceability.repo.md` 規則 9・10）

「配布に関わるファイル（ビルド設定・ワークフロー・手順書）」を、**誤りの側の文字列**で走査して引いた。

```console
$ git grep -l -I "obsidian-plugin" -- ':!src/ai-stock-trading'      # 22 件
$ git grep -l -I "msp-private-notes-sync" -- ':!src/ai-stock-trading'  # 4 件
$ git grep -n -I -E "リリース資産|社内配布手順|dist/ をそのまま置く|手動コピー" -- ':!src/ai-stock-trading'  # 10 件
```

| ファイル | 何を言っているか | 扱い |
| --- | --- | --- |
| `src/obsidian-plugin/package.json` | `version: 0.2.0`・`build` スクリプト | **更新**（版の正本の位置づけを決める） |
| `src/obsidian-plugin/manifest.json` | `version: 0.2.0`（Obsidian が読む版） | 触らない（版は据え置き。本 PR は配布経路を足すだけで機能を変えない） |
| `src/obsidian-plugin/esbuild.config.js` | `dist/` を作る唯一の場所 | 触らない |
| `.github/workflows/frontend.yml` | プラグインを build し egress 走査する | **更新**（版の整合を build のたびに見る 1 ステップ） |
| `.github/workflows/` に配布用ワークフロー | **無い** | **新設** |
| `docs/how-to/obsidian-plugin-install.md` | 「未整備で `dist/` をそのまま置く」（39 行）・ビルド節・導入節 | **更新** |
| `docs/functional/FR-20_obsidian-sync.md` | 「入っていないのは 配布のリリース資産化 である」（25 行） | **更新** |
| `.ai-context/adr/IADR-0338...md` | 決定 3 の「配布のリリース資産化は後続」・フォローアップ 3 | **日付つき追記**（本文は書き換えない） |
| `.ai-context/specs/2026090*_issue-{1098,1153,1154}_*.md` | 同じ「後続」の記述 | **触らない**（確定済み記録。凍結の射程 —— 経過追記の義務は無い） |
| `docs/api/FR-20_obsidian-sync.md` / `docs/screens/SC-20_*.md` / `docs/security/security.md` / `docs/tests/FR-20_*.md` | 契約・画面・セキュリティ・テスト観点 | **触らない**（配布経路は契約でも画面でもない） |
| `src/eslint.config.js` / `src/knip.jsonc` / `src/vitest.config.ts` / `src/pnpm-workspace.yaml` | プラグインを横断ゲートへ乗せる配線 | **触らない**（新しいソースファイルは足すが、既存の glob に入る） |
| `scripts/README.md` / `scripts/scripts.repo.test.js` | 検査器の一覧と固有テスト | **更新**（新しい検査器 1 本とその試験） |

除外理由: `src/ai-stock-trading` は別プロジェクトの submodule（`check-plan-id-qualification.js` の対象外と
同じ扱い）。`.ai-context/specs/` の確定済み記録は追随義務が無い（本 PR で誤りになる記述でもない ——
書かれた時点では正しく、いつの記述かが日付で判る）。

## 決めること（`IADR-0375` に落とす）

1. **タグ運用と版番号**: リポジトリはモノレポで、タグが 1 件も無い。プラグインだけを配るタグの形と、
   `manifest.json` の `version` との関係を決める。
2. **資産の形**: zip にするか、Obsidian の慣習どおり生ファイルを並べるか。
3. **版の食い違いをどこで止めるか**: `package.json` と `manifest.json` の 2 つに版があり、タグにも版がある。
4. **起動条件**: 既存の必須 check（8 件）を増やさないこと。

決定の内容は `IADR-0375` にある（ここへ複写しない）。

## 実装（射程が残っているものだけ）

### 1. `scripts/check-plugin-release-version.js`（新規）

- `src/obsidian-plugin/package.json` と `manifest.json` の `version` が**同値**であること。
- `--tag <ref>` を与えたら、そのタグが `obsidian-plugin-v<version>` の形で、`<version>` が上の版と
  同値であること。`refs/tags/` 前置を剥がして読む。
- `--self-test` は検査器自身の純関数試験（他の検査器と同じ段階ポリシー）。
- fail-closed: ファイルが読めない・`version` が無い・semver の形でない、はいずれも exit 1。

### 2. `.github/workflows/obsidian-plugin-release.yml`（新規）

- 起動は `push: tags: ['obsidian-plugin-v*']` と `workflow_dispatch` **のみ**。
  `pull_request` を持たない＝**PR で 1 度も起動しない**ので必須 check は増えない。
- 手順: checkout → Node 22 / pnpm → `pnpm install` → `check-plugin-release-version.js --tag` →
  `pnpm --filter @platform/obsidian-plugin run build` → `check-static-egress.js --require` →
  `gh release create`（資産 = `main.js`・`manifest.json`）。
- **外部 CDN・テレメトリを足さない**。使う action は checkout / setup-node / pnpm の既存 3 種のみで、
  Release の作成は `gh`（runner 同梱）で行う。
- `permissions: contents: write`（Release 作成に要る最小）。

### 3. `.github/workflows/frontend.yml`（更新）

- 既存の `build` ジョブの「Build Obsidian plugin」の**直前**に版の整合検査を 1 ステップ足す
  （ジョブ名・check 名は変えない）。タグはこの文脈に無いので `--tag` は渡さない。

### 4. 手順書・機能仕様書の更新

- `docs/how-to/obsidian-plugin-install.md`: 「未整備」を消し、**リリースからの導入**を第 1 の手順にする。
  ビルドからの導入は「開発者向け」として残す（消すと `cli.mjs` の実測手順の前提が消える）。
  版を上げてタグを打つ手順（リリース手順）を足す。
- `docs/functional/FR-20_obsidian-sync.md`: 冒頭の「入っていないのは 配布のリリース資産化 である」を
  実態へ合わせ、**残っているのは実機目視**であることを書く。

## テスト方針

`scripts/scripts.repo.test.js` へ節を足す（新しいテストファイルを作らない）。

1. 版が一致していれば exit 0（**実データ**。`src/obsidian-plugin/` の現物を読む）
2. `package.json` と `manifest.json` が食い違えば exit 1（一時ディレクトリの雛形で）
3. タグが `obsidian-plugin-v<version>` と一致すれば exit 0、ずれれば exit 1（`refs/tags/` 前置あり・なし）
4. タグの形が違う（`v0.2.0` / `obsidian-plugin-0.2.0`）なら exit 1
5. `version` が無い・semver でない・ファイルが無い → exit 1（fail-closed）
6. **変異試験**: 検査器から版の突合 1 行を消すと、②（食い違い）が exit 0 になって門が落ちる
7. 新設ワークフローの静的不変条件: `pull_request` トリガを持たない／タグ前置が検査器と同じ文字列／
   資産に `main.js` と `manifest.json` が並ぶ／`check-static-egress.js --require` を通る
8. `frontend.yml` が版の整合検査を「Build Obsidian plugin」より前に持つ
9. `scripts/README.md` が新しい検査器の行を持つ

## 実測（証跡は PR 本文へ）

稼働 k3s（Rancher Desktop・エッジは istio-ingressgateway）。**`curl -k` は使わない** ——
`--cacert` にローカル CA を渡す。Windows の curl は schannel で私設 CA の失効照会ができず exit 60 に
なるので `--ssl-no-revoke` を併用する（**`-k` とは違う**。`--cacert` を外すと 60 で落ちることを対で示す）。
同期トークンの発行は PR #1156 / #1173 の手順を踏襲する（Admin REST API で**一時**ユーザーと**一時**
direct-grant クライアントを作り、**終了時に両方削除**。**Keycloak pod で `kcadm.sh` を exec しない** ——
本体が OOMKilled になる）。

測る対（陽性 / 陰性）:

| # | 呼び出し | 期待 |
| --- | --- | --- |
| P1 | `POST /private-notes/sync/notes/{id}/move` 正しい版・空きパス | **200** ＋ 応答の `vaultPath` が新パス |
| P2 | 直後の `GET /private-notes/sync/manifest` | `vaultPath` が新パス・**`version` が進んでいない** |
| N1 | 同じ move を**古い版**で再送 | **409 `version_conflict`** |
| N2 | 既に埋まっているパスへ move | **409 `vault_path_conflict`** |
| P3 | 現在のパスと同じ値への move | **200**（冪等） |
| N3 | 他人の資料 / 不在 ID への move | **404**（存在秘匿。403 にしない） |
| N4 | トークン無し / でたらめなトークン | **401** |
| P4 | `dist/cli.mjs move` をエッジ URL に向けて実行 | exit 0・サーバ側の名前が変わる |
| — | 監査 | `private-note.sync.move` が刻まれ、**`vaultPath` を含まない** |

## 受け入れ基準（Given-When-Then）

- [ ] Given リリース資産 / When 利用者が手順書どおりに導入する / Then `dist/` を手でコピーせず
      （＝クローンもビルドもせず）にプラグインが入る
- [ ] Given 版が食い違ったタグ / When リリースワークフローが走る / Then 資産を作る前に落ちる
- [ ] Given 新設ワークフロー / When PR を出す / Then **1 度も起動せず**、必須 check（8 件）は増えない
- [ ] Given 配備済みクラスタ（エッジ経由） / When `move` を実 HTTP で叩く / Then 200 → manifest の
      `vaultPath` が変わる（陽性）・版ずれ 409／パス重複 409／不在 404／無認証 401（陰性）
- [ ] Given Obsidian 実機 / Then **未実施**（本 PR では閉じない。別 issue へ切る）

## 未決事項・射程外

- **Obsidian 実機での目視**（issue の受け入れ基準 2）。実機が無く、代替（CLI の実 HTTP）は
  Modal・Vault イベント配線・設定タブの目視を代替しない。**別 issue へ切る。**
- **署名と公開レジストリ（Obsidian community plugins）への登録**。`ADR-0037` 決定 1 は**社内配布**で
  あり、公開登録は計画に無い。やるなら別 issue（タグの形が公開登録の要件と食い違う点は `IADR-0375`）。
- **自動同期（保存時の自動 push）**。計画に無い（issue も射程外と明記）。
- `IADR-0338` フォローアップ 4（カバレッジ算入）・5（en ロケール）。
