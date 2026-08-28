---
title: 作業仕様書 — #438 MFA の実効的な強制と Keycloak 監査イベントの有効化（IADR-0197 フォローアップ 5・6）
type: spec
status: done
related_ids:
  - FR-05
  - FR-09
  - NFR
  - SC-14
  - SC-17
  - ADR-0026
author: claude
created: 2026-08-28
updated: 2026-08-28
related_adrs:
  - IADR-0294
  - IADR-0197
---

# #438 MFA の実効的な強制と監査イベントの有効化

## 目的

ADR-0026 は「**TOTP による MFA を必須**とする」「SC-17 の**操作を監査ログに記録する**」を確定している。
IADR-0197 は realm へ確定値 8 項目を投入したが、**同 IADR 自身が 2 つの未達を明記して #438 へ送っている**。

- **フォローアップ 5**: 「`defaultAction: true` は『MFA 必須』の十分条件ではない」——
  ① realm import で作られる dev 4 ユーザーは `users[].requiredActions` が全件未設定で `defaultAction` が遡及しない、
  ② Keycloak 既定 `browser` フローの OTP サブフローは Conditional であり、**OTP 未登録の利用者はパスワードのみでログインできる**。
- **フォローアップ 6**: ADR-0045 決定 9-b の「申請者・承認者・実行者を監査ログへ残す」が成立していない ——
  realm は `eventsEnabled` / `adminEventsEnabled` / `adminEventsDetailsEnabled` / `eventsListeners` の**いずれも未設定**。

本作業はこの 2 つを閉じる。

## 母集合の引き方と、引いた結果

**軸 1（誤りの側から引く）**: dev アカウント名・平文パスワードで全ファイルを走査
（`poc-user|poc-operator|Developer-2026|Admin-Dev2026|Poc-Passwd2026|PocOperator-2026`。
除外は `node_modules` / `.git` / `src/ai-stock-trading`〔別プロジェクトの submodule〕のみ。**行フィルタも `head` も掛けていない**）。
→ **56 行 / 22 ファイル**。うち**ログイン導線を実際に駆動するもの**は 3 件だけである。

| ファイル | 使う grant | 本変更の影響 |
| --- | --- | --- |
| `scripts/verify-oidc-edge-flow.sh:202-233` | **authorization_code + PKCE**（ログインフォームを curl で駆動） | **影響する。** TOTP 設定画面が挟まるため手順 3〜6 が止まる |
| `perf/k6/lib/config.js:33-35` | `grant_type: password`・`client_id` 既定 `platform-spa` | **影響しない。** `platform-spa` は `directAccessGrantsEnabled: false`（実測）であり、**この経路は変更前から動いていない**（README も「有効化が前提」と書く opt-in） |
| `docs/operations/local-sso-recovery-runbook.md:132` / `docs/operations/operations.md:238` | ブラウザ手動ログイン | **影響する。** 初回に TOTP 登録が挟まる |

残る 19 ファイルは**実測値の記録・ABAC の対照表・過去の作業仕様書**であり、ログイン手順を持たない（除外）。

**軸 2（🔴 1 回目は誤り。訂正して再実行した）**:

- **誤った 1 回目**: `grant_type|GrantType|ResourceOwnerPassword|RequestPasswordToken` を
  `--include=*.cs --include=*.json --include=*.ts --include=*.yaml --include=*.yml --include=*.sh` で走査し、
  **「password grant を使う本番コードは 1 行も無い」と結論した。誤りである。**
  **`--include=*.js` を落としていた** —— キットの母集合規則 **3「拡張子で絞らない。パスの除外だけで取る」**の破りである。
  誤りに気付いたのは自分の走査ではなく、**変更した realm に対して既存テストが落ちたから**である
  （`scripts.repo.test.js` が「`bff` の `directAccessGrantsEnabled` が true であること」を主張していた）。
- **訂正後**: `git grep -n -I -E "grant_type|GrantType|ResourceOwnerPassword|RequestPasswordToken|directAccessGrants" -- . ':!src/ai-stock-trading'`
  （**拡張子で絞らず、パス除外のみ**）。

| 経路 | 対象 realm | 判定 |
| --- | --- | --- |
| `scripts/seed-abac-policies.js:117` | **platform**（client `bff`・利用者 `admin`） | 🔴 **壊れる。1 回目の走査で取りこぼした本体** |
| `scripts/seed-search-documents.js:74` | **platform**（上の関数を再利用） | 🔴 **壊れる。`grant_type` の字面を持たず、ヘルパ経由なので軸 2 の語では出ない** —— `passwordFromRealm` を軸に足して初めて出た |
| `scripts/measure-abac-combinations.js:476` | **master**（`admin-cli`） | 影響しない。master realm は本変更の対象外 |
| `perf/k6/lib/config.js:33` | platform（`platform-spa` 既定） | 影響しない。`platform-spa` は直接付与が無効で、**変更前から動いていない** |
| `deploy/local/wiki-oidc/README.md:73` | **master** | 影響しない |
| `scripts/verify-oidc-edge-flow.sh:226` | platform（authorization_code） | 🔴 **壊れる。** OTP の段が挟まる |
| `SessionTokenRefresher.cs:110` | platform（refresh_token） | 影響しない |

**教訓（規則 3 と 2 の併用）**: 「password grant を使う経路」は `grant_type` の**字面では引けない**。
ヘルパへ切り出されている呼び出し元は、**ヘルパ名を軸に足して**初めて出る。
軸を 1 本で終わらせない（規則 5）。

**軸 3**: realm の全 client の `directAccessGrantsEnabled` を列挙。
→ **`bff` ただ 1 つが `true`**。他 8 client はすべて `false` または未設定。

### 引いた結果の帰結（🔴 走査で初めて出た欠陥）

**`bff` の `directAccessGrantsEnabled: true` は MFA のバイパス口である。**
利用者名・パスワード・client secret を持つ者は、**ブラウザフローを一切通らずに**トークンを取得できる。

そして重要なのは、**この口を閉じなくても投入器は壊れる**という点である ——
`CONFIGURE_TOTP` が未消化の利用者に対し、Keycloak は password grant を
`invalid_grant: Account is not fully set up` で拒む。**逃げ場は無く、投入器の主体を変えるしかない。**

## 決定した範囲（詳細と論拠は IADR-0294）

1. **対話ログインする 4 ユーザーへ `requiredActions: ["CONFIGURE_TOTP"]` を付与する。**
   `service-account-ai-stock-trading-kb-writer` は**除外する**（`serviceAccountClientId` を持つ）。
2. **realm の全 client で直接付与を無効にする**（`bff` を `false` へ）。
3. **`delete_credential` を `enabled: false` にする**（登録後に MFA 無しへ戻れる口）。
4. **監査イベントを有効化する**（`eventsEnabled` ほか 6 キー・イベント種 29）。
5. **`authenticationFlows` は宣言しない**（IADR-0294 決定 2。書くと既定フローが一切登録されず、
   この環境では一度も起動できないため間違えれば全経路のログインが不能になる）。
6. **dev 投入器 2 本をサービスアカウントへ移す**（IADR-0294 決定 4）。
   realm へ `abac-seeder`（client_credentials）を新設し、`passwordFromRealm` を削除する。
7. **検証器が TOTP を出せるようにする**（IADR-0294 決定 6）。`scripts/lib/totp.js` を新設し、
   `verify-oidc-edge-flow.sh` が OTP の段を通す。

## 受け入れ基準と実測

| # | 基準 | 実測 |
| --- | --- | --- |
| 1 | 検査 5 が realm の不変条件を静的に固定する | `node scripts/check-realm-constraints.js` → **exit 0** |
| 2 | 検査 5 に検出力がある（陽性対照＋変異） | `--self-test` **57 件 OK**（うち MFA 系 15 件＝正例 1・陽性対照 1・変異 9・境界 4） |
| 3 | **実データへの変異が検出される**（合成データだけで満足しない） | 実 realm の写しへ 3 変異（TOTP 除去／直接付与再開／監査キー欠落）→ **3 件とも exit 1**、無変異は exit 0 |
| 4 | TOTP 計算が正しい | RFC 6238 §Appendix B の SHA-1 ベクタ **5/5 一致**（`scripts.test.js` が固定） |
| 5 | 検査器スイートが緑 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → **655 tests passed** |
| 6 | 文書検査が緑 | `check-trace-blocks` / `check-doc-links` / `check-doc-updated` / `gen-knowledge-graph --check` / `check-adr-numbering` / `check-knip` / `check-reading-budget` → **すべて OK** |
| 7 | 影響する文書を追随させた | `deploy/local/README.md` / `docs/operations/operations.md` / `docs/operations/local-sso-recovery-runbook.md` / `docs/security/security.md` |

### 🔴 破れた予測

**「password grant を使う本番コードは 1 行も無い」は誤りだった。** 上記 §軸 2 のとおり、
拡張子で絞ったせいで `.js` の投入器 2 本を取りこぼしていた。**誤りに気付かせたのは自分の走査ではなく既存テストである。**
仮にそのテストが無ければ、realm だけを変えて「壊れる依存は無い」と書いたまま出していた。

## 検証できないこと（未実行を緑と書かない）

**実 Keycloak での疎通は 1 度も行っていない**（Docker / k3s がこの環境に無い）。
したがって次の 3 つは**未検証である**。

1. `requiredActions` を付けた利用者が、実際に TOTP 登録画面へ誘導されること。
2. 直接付与を閉じたことで、既存の経路が想定どおり（かつ想定外でなく）落ちること。
3. **`verify-oidc-edge-flow.sh` の OTP の段が実画面で通ること** ——
   HTML の抽出（`kc-totp-secret-key` の id、`totp` / `otp` のフィールド名）は
   **Keycloak 24 の構造をこう仮定した fixture** に対してしか通していない。
   計算そのもの（`totp.js`）は RFC ベクタで固めてあるので、壊れるならここである。

IADR-0197 が求めた「実機で確かめること（`kcadm.sh` で `developer` の実ログインを 1 回試せば決着する）」は
**残件として IADR-0294 §結果へ送った**。最初の実測機会は `integration-stack.yml` の次の develop 実行である
（同ワークフローは **PR では起動しない**）。「統制を定めた」と「統制が働いている」を書き分けること。
