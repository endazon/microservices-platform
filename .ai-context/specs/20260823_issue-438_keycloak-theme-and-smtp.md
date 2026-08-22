---
title: 作業仕様書 — Keycloak テーマ（loginTheme/accountTheme）の実装と smtpServer 設定手順の整備（#438 残作業）
type: spec
status: done
related_ids:
  - SC-13
  - SC-14
  - SC-15
  - SC-16
  - ADR-0026
  - ADR-0045
  - IADR-0197
  - IADR-0261
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
related_specs:
  - "20260815_issue-578_realm-rename-and-auth-policy.md"
  - "../adr/IADR-0197_realm-rename-and-auth-policy.md"
  - "../adr/IADR-0261_keycloak-theme-and-smtp-injection.md"
  - "../../docs/screens/SC-13_login.md"
  - "../../docs/screens/SC-14_otp-mfa.md"
  - "../../docs/screens/SC-15_password-reset.md"
  - "../../docs/screens/SC-16_account-settings.md"
  - "../../docs/operations/keycloak-smtp-relay-setup-runbook.md"
---

# 仕様書: Keycloak テーマ（loginTheme/accountTheme）の実装と smtpServer 設定手順の整備（#438 残作業）

## 0. 起点と、残作業の実態（着手前の裏取り）

issue #438 本文・全 2 コメントを読んだ。

- 本文（起票時）: 認証認可（Keycloak＋ABAC）の再実装。スコープに「Keycloak 統合: 認証画面テーマ（SC-13〜16）」
  「管理画面バックエンド: SC-09・SC-17」「AuthorizationService（ABAC 評価エンジン）の再実装」を含む。
- 2026-08-07 コメント: 計画側裁定で判明した 3 項目（レルム/クライアント改名・realm 設定・SC-14/15/16 の担当）。
- **2026-08-21 コメント（棚卸しセッションの実測。issue 作成者本人）**: PR #746（2026-08-15 マージ）により
  レルム改名・パスワードポリシー・OTP ポリシー・ブルートフォース対策・アクショントークン有効期限・
  `rememberMe`・`requiredActions` は**投入済み**。**未実装のまま残っている項目は `smtpServer` と
  `loginTheme`/`accountTheme` の 2 点のみ**であり、他は完了扱いとしてよいと明記している。

**この理解は最新である。** 本作業はこの 2 点に閉じる。ABAC 評価エンジン（AuthorizationService）・
SC-09・SC-17 は本 issue のコメントで「完了扱いでよい」対象に含まれておらず、かつ 2026-08-21 コメントが
「この 2 点についてのみオープンのままとする」と明示しているため、**本作業では触らない**
（ABAC 側の状態を疑う場合は別途の裏取りが要るが、issue 作成者本人の直近裁定を覆す根拠が無い限り、
本作業の射程外として扱う）。

## 1. 母集合（`.claude/rules/traceability.md` §是正・追随の母集合の取り方）

### 1.1 realm 定義ファイルは 1 つだけか

```console
$ find /home/user/microservices-platform -iname "*-realm.json" -o -iname "*realm-export*"
deploy/keycloak/microservices-platform-realm.json
```

**本リポジトリの追跡下には 1 ファイルのみ。** `src/ai-stock-trading`（submodule・別プロジェクトの計画・ADR を
持つ）は `.claude/rules/traceability.repo.md`「複数プロジェクトを跨ぐ場合の ID 修飾」の対象外実体であり、
本作業の母集合から除外する（AST は自身の realm/レルムを持ち、ADR-0026/0045 は MSP の計画 ADR であって
AST の射程外）。

### 1.2 loginTheme/accountTheme/smtpServer への既存参照

```console
$ grep -rn "loginTheme\|accountTheme\|adminTheme\|emailTheme" --include="*.json" --include="*.yaml" --include="*.yml" .
（マッチ無し）
$ grep -rln "smtpServer\|smtp" deploy/ docs/ .ai-context/
docs/tests/SC-15_password-reset.md
docs/screens/SC-15_password-reset.md
.ai-context/specs/20260816_issue-600_fr22-in-app-notifications.md
.ai-context/specs/20260815_issue-578_realm-rename-and-auth-policy.md
.ai-context/adr/IADR-0197_realm-rename-and-auth-policy.md
.ai-context/adr/IADR-0215_notification-service-and-in-app-delivery.md
```

**テーマ・SMTP のいずれも実体が無いことを確認済み**（#578/IADR-0197 の実測と一致）。
`.ai-context/adr/IADR-0215` は FR-22 通知の実装 ADR で、**メール送出（SMTP の実体）を意図的に本 PR の
射程から外している**（「実環境が要るものは触らない」IADR-0197 決定 5 を踏襲）ことを確認した——本作業の
「smtpServer は設定手順の整備までが限度」という枠と整合する。

### 1.3 除外したもの

| 対象 | 除外理由 |
| --- | --- |
| `src/ai-stock-trading`（submodule） | 別プロジェクトの計画・ADR を持つ。ADR-0026/0045 は MSP 固有 |
| `.ai-context/specs/20260815_issue-578_*.md`（確定済み） | 凍結記録。本文を書き換えない（`.claude/rules/traceability.repo.md`） |
| `deploy/local/edge/` | 統括指示により他エージェントが直前に変更した領域。触らない |
| `src/` 配下全般 | 統括指示により対象外（本作業は設定・テーマの作業） |
| `scripts/` 配下 | 統括指示により対象外。§5 残件で詳述する gap の原因 |

## 2. 対象範囲

- 対象: Keycloak テーマ実体（login/account）の作成、realm.json への `loginTheme`/`accountTheme`/
  国際化設定の投入、docker-compose と k8s ローカルへの配線、smtpServer の**設定手順**（Vault/ESO 経由の
  注入方式・kcadm 反映手順）の整備、関連文書（SC-13〜16 画面仕様書・運用 Runbook）の作成・更新。
- 対象外: `smtpServer` への実環境値の投入そのもの（利用者裁定「実環境が要るものは触らない」）。
  ABAC 評価エンジン・SC-09・SC-17（2026-08-21 コメントにより本 issue の残作業から除外）。
  `scripts/k8s-local-up.sh` の変更（統括指示により対象外。§5 残件参照）。

## 3. 設計

### 3.1 テーマ

[IADR-0261](../adr/IADR-0261_keycloak-theme-and-smtp-injection.md) 決定 1 を参照。要点:

- `deploy/keycloak/themes/platform/{login,account}/theme.properties`（`parent=keycloak`。テンプレート非複製）
- 各 `resources/css/platform.css`（システムフォントのみ・外部 CDN 不使用。08_data-egress-policy 遵守）
- realm.json: `loginTheme`/`accountTheme` = `"platform"`、`internationalizationEnabled: true`、
  `supportedLocales: ["ja","en"]`、`defaultLocale: "ja"`（SC-13 主要素「言語切替」）

### 3.2 配線

| 環境 | 状態 |
| --- | --- |
| docker-compose | **完了**。`./keycloak/themes/platform` をホストマウント（`docker-compose.yml`）。テーマキャッシュを dev 用に無効化 |
| k8s ローカル（`deploy/local/`） | **declarative な受け皿のみ完了**。`deploy/local/infra/keycloak.yaml` に `optional: true` の ConfigMap 参照を追加した。**ConfigMap の生成は `scripts/k8s-local-up.sh` の変更が要り、本作業の範囲外**（統括指示）。当面は `deploy/local/README.md`「手動でステップ実行する場合」の手順で手動作成する |

### 3.3 smtpServer

[IADR-0261](../adr/IADR-0261_keycloak-theme-and-smtp-injection.md) 決定 2 を参照。realm.json への静的投入は
しない。Vault（`secret/msp/keycloak-smtp`）→ ExternalSecret（`keycloak-smtp`）→ kcadm 反映という経路を
用意し、手順を [Runbook](../../docs/operations/keycloak-smtp-relay-setup-runbook.md) にまとめた。
ExternalSecret の apply も現時点は手動（§5 残件）。

## 4. 受け入れ基準

- [x] Keycloak テーマ（login/account）の実体が存在し、realm.json から参照される
- [x] SC-13 の主要素「言語切替」が realm 設定で成立する（国際化）
- [x] docker-compose 環境でテーマが有効になる（マウント・キャッシュ無効化）
- [x] `smtpServer` の実環境値をコミットせず、投入手順とシークレット注入方式（Vault/ESO）を示す
- [x] `node scripts/check-realm-constraints.js` が通る
- [ ] k8s ローカル環境でテーマが自動的に解決される（**scripts/ 配線が必要で本作業は届かない**。手動手順で代替）

## 5. テスト方針

本作業は設定・テーマ・文書であり、`dotnet`/フロントのユニットテスト対象コードを変更しない
（統括指示により `dotnet` は実行しない）。検証は静的検査（realm JSON の妥当性・`check-realm-constraints.js`・
`check-doc-links.js`・`check-trace-blocks.js`）と、YAML/JSON のパース確認に限る。

## 6. 計画書との差異

- 差異: なし。ADR-0026・ADR-0045 の確定要件どおりに実装した。**smtpServer の実環境値未投入は差異ではなく
  ADR-0045 自身が計画に値を書かないと定めている**（決定 2）ことの帰結である。

## 7. 未決事項・残件

1. **k8s ローカル（`deploy/local/`）で loginTheme/accountTheme を自動解決するには
   `scripts/k8s-local-up.sh` の変更が要る**（`keycloak-theme-platform` ConfigMap の生成・
   `externalsecret-keycloak-smtp.yaml` の apply を組み込む）。本作業の担当領域外（統括指示）。
   **`bash scripts/k8s-local-up.sh` を素で実行すると、ログイン画面が「テーマが見つからない」500 になる**
   （`deploy/local/README.md`「既知の制約」に明記済み）。フォローアップ issue 化を推奨する。
2. `smtpServer` の実値投入（組織のメールテナントからの供給待ち）。
3. SC-13/SC-16 の画面・テスト仕様書の一部（テスト仕様書は本作業で新設していない。§残件として明記）。
4. IADR-0261 は確定番号である（統括側が採番し索引へ登録済み）。
