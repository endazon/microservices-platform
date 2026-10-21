---
title: 作業仕様書 — 投入器の主体 abac-seeder を既定で無効にし、投入器・運用スクリプトは実行の間だけ有効にして、資格情報を稼働の Secret から読む（GHSA-rh44-px44-p48q）
type: spec
status: done
related_ids: [NFR-18, ADR-0124, IADR-0576, IADR-0572, IADR-0485, IADR-0294, IADR-0133, IADR-0503]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密は相手と対で書く。初期投入は無いときだけ作る）
issue: "GHSA-rh44-px44-p48q"
---

# 作業仕様書 — 投入器の主体を既定で無効にする（GHSA-rh44-px44-p48q）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0576**（新規）に置く。
> 多層防御の強化（最小権限・常時有効な管理主体を残さない）の 4 本目。参照は GHSA の ID だけとする。
> 🔴 稼働中のクラスタには何も実行しない。試験は偽の Keycloak（HTTP / fetch）と PATH のスタブで行う。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR-18**。計画 ADR: **ADR-0124 決定 1**（矛盾しない。投入器の client secret を対になる秘密として保管先へ置き、対で回す運用に入れる）。
- 実装 ADR: **IADR-0576**（新設）。前提 **IADR-0572**（守りの対象の導出。投入器の置き場の表を `msp/abac-seeder-oidc` へ変える）・**IADR-0485**・**IADR-0294**（投入器は client_credentials）・**IADR-0133**（ABAC の初期投入）・**IADR-0503**（再発行の駆動器）。

## 要件（オーナー指示 2026-10-22）

1. 投入した後に abac-seeder を無効化する運用にする。
2. 運用スクリプト（republish・archive）は `REPUBLISH_CLIENT_SECRET` か realm の既定値で abac-seeder を使っていた。secret を入れ替えた後も動く経路にする（例: 実行時に OpenBao〔ESO が作った Secret〕から読む）。
3. 無効化と、実行時だけの一時有効化の両立を IADR に書く。
4. 該当するスクリプトの試験を足す。

## 受け入れ基準と試験の写像

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | realm の宣言で `abac-seeder` は `enabled: false`（取り込み・後追いが無効で作り、無効へ戻す） | `scripts.repo.test.js`「投入器の主体は realm の宣言で既定無効…」 |
| AC2 | 資格情報は 明示 ＞ 稼働の Secret `platform-infra/abac-seeder-oidc`。どちらも無ければ realm の宣言の値へ落ちずに止まる | 同「投入器の資格情報は 明示 ＞ 稼働の Secret の順で…」・「明示も Secret も無ければ…止まり」 |
| AC3 | 入れ替えた後の値（Secret の値）で名乗り、宣言の値は使わない。値を出力しない | 同「REPUBLISH_CLIENT_SECRET が無ければ稼働の Secret の値で名乗る」 |
| AC4 | 一時の有効化は GET した表現を丸ごと PUT して enabled だけを変え、この実行が有効にしたものだけを、成功・失敗のどちらでも戻す。元から有効なら触らない。戻す候補は PUT の前に控え、拒まれたときだけ外す | `abac-seeder-session.js --self-test`・駆動器の試験 2 件（成功と DLQ での失敗の両方で対、元から有効なら書かない） |
| AC5 | 投入器（seed-abac-policies / seed-search-documents / seed-tag-dictionary）・アーカイブの駆動器（admin-page-driver）も同じ口を使う | 同「…どれも一時の有効化と戻しを持つ」（静的）・駆動器の既存の試験（偽の Admin REST を足した） |
| AC6 | 起動器は ESO の有無によらず Secret `abac-seeder-oidc` を作り、ESO=1 では ExternalSecret（Merge）を当てて同期を待つ。Vault の種は `msp/abac-seeder-oidc` を無いときだけ作る | `k8s-local-up.test.js`「IADR-0576: 起動器は…」・`scripts.repo.test.js` の置き場の表と seed の突合（IADR-0572） |
| AC7 | SC-12 の否定形（M8）は、既定の無効のまま変わらないことを見る | `check-mcp-client-provisioning.js`（integration-stack） |
| AC8 | 手で閉じる・状態を見る入口は `--live` が無ければ何もしない | `scripts.repo.test.js` #1550 節（live-scripts.json に登録） |

## 方式

- 共通の口 `scripts/lib/abac-seeder-session.js`: `resolveSeederSecret`（明示 ＞ Secret）・`ensureEnabled`（無効なら有効にして控える）・`restore`（控えたものだけ戻す。失敗は名指すだけで例外にしない）・`restoreThenExit`（中断）。切り替えは master realm の管理者（明示 `KC_ADMIN_USER` / `KC_ADMIN_PASSWORD` ＞ Secret `keycloak-admin`）で Admin REST。
- 戻しの確実さ: 入口の `finally`（republish の main・駆動器の run）と入口の `.then/.catch`（投入器）、SIGINT と全体の上限（`restoreThenExit`。port-forward を残したまま戻してから片付ける）。戻し損ねは realm の後追い（宣言 `enabled: false`）が次の起動で閉じる。
- 🔴 `seed-abac-policies.js` の SIGINT の handler は、入口として走るときだけ登録する（再発行・アーカイブの駆動器はこのモジュールを require するので、同期の exit が駆動器の戻しより先に走るのを防ぐ）。
- Secret の置き場: Vault `msp/abac-seeder-oidc` → ExternalSecret（Merge）→ `platform-infra/abac-seeder-oidc`。起動器は ESO の有無によらず Secret を作る（env か宣言の値。dev ではない context で env が無ければ IADR-0572 の判定が止める）。許可リスト `deferred[]` へ足す（対になる秘密）。

## 母集合（規則 9・10）

走査語: `clientSecretFromRealm(` / `REPUBLISH_CLIENT_SECRET` / `abac-seeder`（scripts・deploy・.github・docs）。

| 場所 | 扱い |
| --- | --- |
| `republish-document-updated.js`・`lib/admin-page-driver.js`（archive-bodyless / strip-untrusted / archive-ast-quote の本体）・`seed-abac-policies.js`・`seed-search-documents.js`・`seed-tag-dictionary.js` | 直す（共通の口へ） |
| `check-mcp-client-provisioning.js`（M8 の否定形が abac-seeder の enabled=true を前提にしていた） | 直す（既定の false が変わらないこと） |
| `deploy/keycloak/microservices-platform-realm.json` | 直す（`enabled: false`） |
| `deploy/local/vault/eso/bootstrap.sh`・`scripts/k8s-local-up.sh`・`deploy/bootstrap/sc22-secret-items.json` | 直す（置き場・種・Secret・ExternalSecret） |
| `measure-abac-combinations.js`（master realm の管理者で読むだけ。abac-seeder を使わない） | 対象外 |
| `.github/workflows/integration-stack.yml`・`cutover-rehearsal.yml`（投入器を呼ぶ） | 変えない（起動器が Secret を作り、投入器が自分で一時有効化する） |
| `docs/operations/paired-secret-rotation-runbook.md`・`docs/security/security.md` | 直す |

## 変異

| 変異 | 赤になる試験 |
| --- | --- |
| M1: 駆動器から `ensureEnabled` を外す | 駆動器の試験（PUT が無い） |
| M2: `restore` を `finally` から外す | 同（戻しが無い・失敗の経路） |
| M3: `resolveSeederSecret` に realm の宣言の値への fallback を戻す | 「明示も Secret も無ければ…止まり」 |
| M4: 有効化の PUT で表現の一部だけを送る | `--self-test`（表現を丸ごと） |
| M5: realm の宣言を `enabled: true` に戻す | 「既定無効」 |

## 配備前の手順（PoC。値は書かない）

PR 本文に順序つきで書く。要点: 配備の前に Vault の KV `msp/abac-seeder-oidc`（キー `client-secret`）と Secret `platform-infra/abac-seeder-oidc` を、認証基盤の abac-seeder の現在の値（入れ替え後ならその値）で作る。配備の後の起動（realm の後追い）で abac-seeder は無効になる。

## 残余

- 同時に 2 本のスクリプトが動くと、先に終わった方が無効へ戻し、後の方のトークンの取り直しが失敗し得る（運用で 1 本ずつ走らせる。後の方は失敗として止まる＝安全側）。
- 戻し損ね（強制終了）の窓は、次の起動の realm の後追いまで、または手で閉じるまで。
- Secret の読み先は kubectl の現在の context に依存する（ずれれば認証失敗で止まる＝安全側）。
