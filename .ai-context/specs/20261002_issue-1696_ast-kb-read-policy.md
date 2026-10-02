---
title: 作業仕様書 — AST の取引判断が KB を検索できるよう、読み手のサービスアカウントの ABAC 主体を解決する（#1696）
type: spec
status: done
related_ids: [FR-03, FR-05, FR-09, NFR-09, ADR-0080, ADR-0085, ADR-0088, IADR-0075, IADR-0133, IADR-0420, IADR-0456, IADR-0485, IADR-0492]
author: claude
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0085_project-attribute-scope-and-non-axis.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0080_set-valued-user-attributes-and-match-semantics.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1
  - planning:projects/ai-stock-trading/07_adr/ADR-0032_mcp-non-exposure-is-enforced-by-attributes-not-the-allowlist.md 決定 2 (2)
related_specs:
  - 20260927_issue-1664_owner-read-policy-seed-and-deploy-step
  - 20260928_issue-1682_paired-secrets-outside-sc22
issue: "#1696"
---

# 作業仕様書 — AST の KB の読み手の ABAC 主体（#1696）

> 本仕様書は実装着手前に作成する。裁定は #1696 のコメント（オーナー 2026-10-02・**案 B**）。対の AST 側は AST#1078（AST/IADR-0485）。
> 判断の記録は [IADR-0492](../adr/IADR-0492_ast-kb-reader-project-scoped-read-policy.md)。

## 裁定（案 B）の要点

- `project=ai-stock-trading` に限る read の専用ポリシーを作る。`clearance=internal`（基盤全体の internal）は与えない。
- 読み手のクライアントを書き手（`ai-stock-trading-kb-writer`）と分ける。最小権限とし、読み手は書けない。
- 機械の主体の userId は、読み手のクライアントに `profile` スコープを付けて `preferred_username` で解決する。
- 受け入れ基準: ①`/authz/scope` が Granted=true で AST の文書だけの範囲を返す ②実データで取引判断の参考情報が 1 件以上載る。
- 本番の NetworkPolicy（AST 名前空間からの ingress）は別件。触らない。

## 射程と、触らないもの

- **触る**: Keycloak の realm 宣言（`deploy/keycloak/microservices-platform-realm.json`）、ABAC の dev seed（`deploy/local/abac-seed/`）、
  realm の写しの突合（`scripts/check-realm-copy-drift.js` の片側宣言）、AST の app-secrets の dev の種（`deploy/local/vault/eso/bootstrap.sh`・
  `deploy/bootstrap/sc22-secret-items.json`）、試験、運用文書。
- **触らない**: 本番の NetworkPolicy（`templates/networkpolicy.yaml`）、検索サービス・認可サービスの本番コード（判定の仕組みは足りている）、
  MCP 経路の一律除外と割当禁止（IADR-0373）、属性辞書の `projects`（足さない。IADR-0492 決定 1）。
- 稼働クラスタのスクリプトは実行しない。受け入れ基準 ② は PoC で確かめる（issue は閉じない）。

## 設計（詳細は IADR-0492）

1. **realm**: 機密クライアント `ai-stock-trading-kb-reader`（client_credentials のみ・既定スコープ `profile` だけ・dev secret
   `ai-stock-trading-kb-reader-dev-secret-change-me`）と、その service-account（**ロールなし**・属性 `projects = ["ai-stock-trading"]` だけ）。
2. **ポリシー**（dev seed）: `read`・利用者の条件 `{ projects: [ai-stock-trading] }`・文書の条件 `{ project: [ai-stock-trading] }` の 1 本。
3. **秘密**: AST の app-secrets に `kb-reader-auth-client-id` / `kb-reader-auth-client-secret`（対になる秘密。`notWritable`）。
4. **userId の解決**: 本番コードの変更は無い。`profile` で `preferred_username = service-account-ai-stock-trading-kb-reader` が載り、
   `SearchAccessResolver` の `Identity.Name` → `ScopeUserAttributeSource.FindByUsernameAsync` がその名前で属性 `projects` を引く。

## 母集合（規則 9・10・11）

### 規則 9（誤りの側の文字列で全文書を走査してから追随先を挙げる）

| 走査した文字列 | 範囲 | 当たり | 追随 |
| --- | --- | --- | --- |
| `ai-stock-trading-kb-writer` | 追跡下の全ファイル | 50 余（`.ai-context/` を除くと docs 5・deploy 4・scripts 4・src 16） | 「AST の機械クライアントの列挙」に当たるもの: `docs/security/security.md`（dev secret の列挙・クライアントの説明・本番流用禁止の列挙）、`docs/migration/cutover-discard-and-rebuild.md`（「サービス用クライアント 4 つ」）、`docs/operations/paired-secret-rotation-runbook.md`（対の表）、`docs/operations/local-sso-recovery-runbook.md`（AST デプロイ時の export）、`scripts/check-realm-copy-drift.js`（`ONE_SIDED_CLIENTS`）。**除外**: 書き手そのものの挙動を述べる箇所（DocumentService の作成口・古い写しの規則・`IADR-0075` 等）は読み手と無関係 |
| `kb-auth-client` / `llm-auth-client` | 同上 | `bootstrap.sh`・`sc22-secret-items.json`・`SecretItemBootstrapSeedTests.cs` | 3 つとも追随（キー集合と件数 8 → 10） |
| `4 組` / `17 ＋ 4` / `*-auth-client-* 8` | `docs/`・`deploy/`・`src/` | `secret-rotation-runbook.md` 3・`paired-secret-rotation-runbook.md` 1・`bootstrap.sh` 2・`SecretItemBootstrapSeedTests.cs` 2 | すべて 5 組 / 10 件へ（導出値は計算し直した: 4 組 ＋ 読み手 1 組 = 5 組 = 10 キー） |
| `projects`（属性辞書・seed の注記） | `deploy/local/abac-seed/` | `attributes.json` の注記 1 | **規則 10 で新たに誤りになる**: 「辞書に無ければ ValidatePolicy が projects を条件に持つポリシーを作らせない」——実測で元から誤り（`ValidateConditions` は未定義キーを許容する）。本変更で projects のポリシーが実在するので直した |
| `判定軸` | `src/`・`docs/` | ADR-0085 の判定軸（project）に触れる記述は 0 件（当たりはすべて `doc_scope` / `owner` 等の別の軸） | 追随なし |
| `RAG` ＋ `kb-writer`（検索も書き手で認証している、という記述） | MSP 全体 | 0 件（AST 側にのみ在る。AST#1078 の作業仕様書で扱う） | — |

### 規則 10（この変更で新たに誤りになる自分の記述）

- `Fixtures/owner-read-seed-scopes.json` の注記「alice: 属性を 1 つも持たない利用者（AST の KB の書き手と同じ形）」——書き手は変わらないので正のまま。
- `deploy/local/abac-seed/README.md` の表「`policies.json` | … 所有者の write」——読み手の read が加わったので追記した。
- `docs/operations/operations.md` の「いつ読むか」表——読み手のポリシーの投入の行を足した。

### 規則 11（窓）

時間差を扱う是正ではない（ポリシー・主体の宣言の追加）。**該当なし。**

## 受け入れ基準と試験の写像

| 受け入れ基準 | 試験 | 種別 |
| --- | --- | --- |
| ① 読み手のトークンで `/authz/scope` が Granted=true | `AstKbReaderPolicySeedTests.読み手はGrantedでASTの文書だけの範囲を得る` / `OwnerReadPolicySeedTests`（fixture と実物の端点の一致に読み手を追加） | C# 統合 |
| ① AST の文書だけの範囲（束縛されない分岐は `project ∈ {ai-stock-trading}` の 1 本だけ・階段なし） | 同上 ＋ `AstKbReaderSearchScopeTests`（索引で AST の文書だけが出る・基盤の internal / 他プロジェクト / 個人資料は出ない） | C# 統合・単体 |
| ① 主体の属性は realm の宣言から（clearance を与えない） | `AstKbReaderPolicySeedTests.Realmの読み手の属性はprojectsだけでfixtureと一致する` ／ `scripts.repo.test.js`「realm: AST の KB の読み手は …」 | C#・JS |
| 読み手は書けない | `AstKbReaderPolicySeedTests.読み手のwriteは自分の所有だけでanalyzeは拒否される` ／ `scripts.repo.test.js`（ロール 0・seed のポリシーは read だけ） | C#・JS |
| userId は `profile` の `preferred_username` | `scripts.repo.test.js`（既定スコープに `profile`）・`check-realm-constraints.js`（検査 7 は SA の profile を求めないが、読み手の機械の判定と矛盾しないこと） | JS |
| 他の主体の範囲を変えない | `AstKbReaderPolicySeedTests.ASTの文書の分岐は読み手以外に立たない`（alice / bob / 書き手）／ 既存 fixture 3 主体の不変 ／ JS「projects=ai-stock-trading を持つのは読み手だけ」 | C#・JS |
| 秘密の種が realm と同値 | `SecretItemBootstrapSeedTests`（キー集合 12 ＋ 10・`kb-reader` の secret が realm と同値） | C# |
| ② 実データで参考情報 1 件以上 | 🔴 PoC で確かめる（本 PR では試験しない） | 稼働 |

## 変異の確認（赤になることを確かめる）

| 変異 | 期待 |
| --- | --- |
| realm の読み手に `clearance: ["internal"]` を足す | C# T-1・T-2 と JS「realm: …」が赤 |
| realm の読み手に `realmRoles: ["platform-operator"]` を足す | JS「realm: …」が赤 |
| 読み手の `defaultClientScopes` から `profile` を外す | JS「realm: …」が赤 |
| seed の読み手のポリシーの `userConditions` を `{}` にする | C# T-5（alice / bob / 書き手に AST の分岐が立つ）・T-6・JS「seed: …」が赤 |
| seed に同じ条件の `write` ポリシーを足す | C# T-4・T-6・JS「seed: …」が赤 |
| seed の読み手のポリシーを消す | C# T-2・fixture 一致（T-25）・検索の試験が赤 |

（実測の結果は PR／コミットの報告に記録する。）

## タスク

- [x] realm に読み手のクライアントと service-account を足す
- [x] seed に読み手のポリシーを足す・属性辞書の注記を直す・README
- [x] `check-realm-copy-drift.js` の片側宣言
- [x] app-secrets の種・SC-22 の `notWritable`・`SecretItemBootstrapSeedTests`
- [x] 試験（C#: 認可サービス・検索サービス／JS: seed と realm の形）
- [x] 運用文書（operations.md の投入手順・security.md・対の秘密の Runbook 2 本・切替の文書・SSO 復旧の Runbook）
- [x] IADR-0492
