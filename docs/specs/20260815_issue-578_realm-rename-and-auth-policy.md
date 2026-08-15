---
title: 作業仕様書 — レルムを platform へ改名し、SC-14 / SC-15 の realm ポリシーを投入する（#578）
type: spec
status: in-progress
related_ids:
  - SC-13
  - SC-14
  - SC-15
  - SC-16
  - NFR
  - ADR-0026
  - ADR-0045
  - IADR-0061
  - IADR-0197
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../adr/IADR-0197_realm-rename-and-auth-policy.md"
  - "../adr/IADR-0061_deploy-rename-migration.md"
  - "../screens/SC-14_otp-mfa.md"
  - "../screens/SC-15_password-reset.md"
  - "../tests/SC-14_otp-mfa.md"
  - "../tests/SC-15_password-reset.md"
---

# 作業仕様書: レルム改名（決定 31）と SC-14 / SC-15 の realm ポリシー投入（決定 28）

## 1. 起点と、この作業に至るまでの経緯

起点は issue #578 である。**ただし #578 の表題と本文の前提は、計画側の裁定によって覆っている。**

| # | 計画側の裁定（planning#247・`891b199` / PR planning#284） | #578 への影響 |
| --- | --- | --- |
| 決定 30 | **SC-14/15/16 の担当は #438 が持つ。**「どの issue にも属さない」という前提は事実と異なる。#578 は #438 の**下位タスク**（realm 設定と画面仕様書の作成）と位置づける | **表題と診断の訂正が要る。** 起きていたのは委譲の失敗ではなく、受け取った #438 が着手されないまま滞留していたことである |
| 決定 28 | SC-13〜16 は **go-live 必須**。MFA なし稼働は採らない。メールも範囲内（ADR-0045 が `Accepted`） | #578 が go-live ブロッカーに挙げた「メール基盤が無い」は**計画上は解消**している |
| 決定 31 | レルム名 `microservices-platform`→**`platform`**、クライアント `spa-web`→**`platform-spa`**。**改名は go-live 前に完了させる**（9 クライアントの再設定を伴う） | **`05_screens` の SC-13〜16 のルート（`/realms/platform/...`）は現行実装では成立しない。改名が先である** |

**利用者裁定（2026-08-15）**により、本作業は次の 3 点を採る。

1. **#578 は #438 のサブ issue として残し、先行着手する**（畳んでクローズしない）
2. **改名も本作業でまとめて実施する**（realm ポリシー投入だけを先行させ、改名を #438 へ残す案は採らない）
3. **UC-09 / UC-10 の所在確定は別 issue へ切り出す**（本 issue の射程は SC-14 / SC-15 に閉じる）

## 2. 母集合（`.claude/rules/traceability.md` §是正・追随の母集合の取り方）

### 2.1 規則 1・2 —— 誤りの側から、変種を列挙して引く

改名対象は「**レルム名としての `microservices-platform`**」と「**クライアント ID としての `spa-web`**」だけである。
**文字列 `microservices-platform` そのもので引くと 38KB 分の一致が出るが、その大半はリポジトリ名・Helm チャート名・
k8s Namespace・イメージ接頭辞であり、改名対象ではない**（決定 31 が改めるのはレルムとクライアントの 2 つに限る）。

引いた変種は次の 7 つである。

| 変種 | 意味 |
| --- | --- |
| `/realms/microservices-platform` | OIDC issuer / エンドポイント URL |
| `"realm": "microservices-platform"` | realm export の realm 名そのもの |
| `microservices-platform-realm` | realm export の**ファイル名**（`deploy/keycloak/microservices-platform-realm.json`） |
| `ABAC_REALM` / `ABAC_SEED_REALM` / `OIDC_REALM` | スクリプトの環境変数**既定値** |
| `spa-web` | クライアント ID |

**空振りした変種も記録する**（規則 2 は「あり得る形をすべて列挙してから引く」であり、0 件の確認も引いた結果である）:
`spa_web` = 0 件 ／ `spaWeb` = 0 件 ／ `realm: microservices-platform`（YAML 形式）= 0 件。
既に `platform-spa` を含むのは `docs/specs/20260807_issue-599_planning-pin-fr22.md` の 1 件だけで、これは
**決定 31 が見えるようになったことを記録した確定済み仕様書**であり、改名の対象ではない。

### 2.2 規則 3・4 —— 拡張子・行で絞らない。パスから引く

走査は `git grep -l -I` で**追跡下の全ファイル**を対象とし、**拡張子で絞っていない**。
実際、対象には `.json` / `.yaml` / `.yml` / `.sh` / `.js` / `.ts` / `.cs` / `.md` の 8 種が含まれ、
**拡張子で絞っていたら `deploy/local/vault/oidc/bootstrap.sh` と
`src/platform/frontend/docker-entrypoint.d/40-render-config.sh` を落としていた**。

### 2.3 引いた結果

| 区分 | 件数 | 扱い |
| --- | --- | --- |
| **live な機能資産と現行案内文書** | **57 ファイル** | **改名する** |
| 確定済み `docs/specs/` | 24 ファイル | **不変**（書いた時点の記録） |
| `feedback/` | 1 ファイル | **不変**（計画リポへ送った内容の写し） |
| `docs/adr/` 本体 | 12 ファイル | **不変**（後述 2.4） |
| `docs/adr/README.md`（索引） | 1 ファイル | **不変。IADR-0197 の行を追加するのみ** |
| `CHANGELOG.md` | 2 箇所 | **不変**（生成物） |

### 2.4 規則 5 —— 軸を 1 本で終わらせない。**先例を実測した**

「ADR 本体を書き換えるか」は、記憶や好みで決めずに**同型の先例を実測して**決めた。

**本リポジトリは同じ改名を 1 度やっている** —— [IADR-0061](../adr/IADR-0061_deploy-rename-migration.md)
（`knowledge-platform` → `microservices-platform`。#228 / 2026-07-12）。その改名の**後**に旧名がどこに残っているかを数えた。

```console
$ git grep -l -I 'knowledge-platform' -- . ':!planning' ':!src/ai-stock-trading' \
    | sed 's|/[^/]*$||' | sort | uniq -c | sort -rn
     12 docs/specs
      8 docs/adr          ← ADR 本体は書き換えていない
      1 feedback
      1 docs/tech         （20260707_wikijs-poc-record.md ＝ PoC の実測記録）
      1 docs/superpowers/specs
      1 docs/superpowers/plans
      1 docs/migration
      1 README.md         （「改名済み」と述べる地の文。旧名を出すのが正しい）
```

**先例は「live な資産と案内文書は改名し、記録（specs / feedback / ADR 本体 / PoC 記録 / superpowers）は書き換えない」を採っている。**
本作業もこれに揃える。`.claude/rules/traceability.md` §書式を適用する母集合（「確定済みの `docs/specs/`・`feedback/`・
`docs/superpowers/` は書いた時点の記録であり、後から注記を足すのは記録の改竄にあたる」）とも整合する。

**ADR 本体を書き換えない理由をもう 1 つ挙げる** —— `IADR-0084` / `IADR-0086` / `IADR-0090` / `IADR-0092` /
`IADR-0093` の realm URL は、**その ADR が決定した内容そのもの**である（「issuer は in-cluster 正準名
`http://keycloak:8080/realms/microservices-platform` を用いる」）。書き換えると
「この ADR は改名後の値を決定した」という**偽の主張**になる。`IADR-0061` に至っては**前回の改名の記録そのもの**であり、
書き換えれば記録が消える。

**代わりに、旧名が現行値でないことは 1 箇所にだけ書く** —— IADR-0197 が
「本 IADR 以前の IADR に現れる `microservices-platform`（レルム名）/ `spa-web` は改名前の名称であり、
現行値は本 IADR が持つ」と述べる。**同じ事実を 12 箇所の追記ブロックへ複写しない**
（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) / #733 で撤去した型を作り直さない）。

### 2.5 規則 8 —— 是正で新たに誤りになる自分の記述

改名によって**本作業自身が書く文書のうち**、次が誤りに変わり得る:

- `CLAUDE.md` の「Keycloak public client `spa-web`」（本作業で是正する。live な必読規約であるため対象）
- `docs/tech/tech-requirements.md` の同記述（同上）
- **`docs/screens/SC-04_wiki-access.md` ほか既存画面仕様書**に `spa-web` が無いことを確認済み（0 件）
- 検査器 `scripts/check-realm-constraints.js` の `REQUIRED_CLIENT_URLS` は **`wiki-js` だけを見ており `spa-web` を持たない**（実測）。したがって改名で stale にならない

## 3. 変更内容

### 3.1 改名（決定 31 / ADR-0026）

| 対象 | 変更前 | 変更後 |
| --- | --- | --- |
| レルム名 | `microservices-platform` | **`platform`** |
| 基盤 SPA のクライアント ID | `spa-web` | **`platform-spa`** |
| realm export のファイル名 | `deploy/keycloak/microservices-platform-realm.json` | **変えない**（下記） |

> **★ ファイル名は改名しない（着手後に測って翻した）。** 当初は [IADR-0061](../adr/IADR-0061_deploy-rename-migration.md) の
> 先例に倣って `platform-realm.json` へ改めたが、**`check-doc-links` が破損リンク 10 件を検出した** ——
> 確定済み `docs/specs/` の 10 ファイルが frontmatter の `related_specs` と本文のリンクで
> **この realm ファイルへ実リンクを張っている**。§2.4 の決定によりそれらは書き換えないため、
> **ファイル名を変えるとリンクが 10 本切れる**。
> **決定 31 が定めるのはレルム名とクライアント ID の 2 つだけで、export のファイル名は含まない。**
> 計画が求めていない改名のために記録側のリンクを壊す理由が無いので、現状維持とした。
> （IADR-0061 のときは確定済み仕様書が realm ファイルへリンクを張っていなかったため、この衝突は起きなかった。
> **先例に倣うだけでは足りず、いま何が参照しているかを引き直す必要があった** —— 規則 8 の型である。）

**他の 8 クライアント**（`wiki-js` / `bff` / `ai-stock-trading-kb-writer` / `headlamp` / `grafana` / `argocd` /
`minio` / `vault`）**の clientId は変えない。** 決定 31 が改名すると述べたのはレルム名と**基盤 SPA の**
クライアント ID の 2 つであり、「9 クライアントの再設定を伴う」とはレルム名変更に伴う **issuer URL の追随**を指す
（各クライアントの `redirectUris` は相対的な URL であり変更不要。issuer を参照している側の設定が動く）。

### 3.2 realm ポリシー（決定 28 / ADR-0026）

**現状は 8 項目すべて未設定である**（実測。`resetPasswordAllowed = True` のみ真だが**これは Keycloak の既定値**であり
SC-15 の実装ではない）。ADR-0026 の確定値を投入する。

| 項目 | ADR-0026 の確定値 | realm.json のキー |
| --- | --- | --- |
| パスワードポリシー | 12 文字以上・英大/小/数字/記号のうち **3 種以上**・**直近 5 世代**と不一致 | `passwordPolicy` |
| TOTP | 6 桁・時刻ずれは**前後 1 ステップ（30 秒）**まで許容 | `otpPolicyType` / `otpPolicyDigits` / `otpPolicyPeriod` / `otpPolicyLookAheadWindow` |
| TOTP 必須化 | 未登録者を初回セットアップへ誘導 | `requiredActions[CONFIGURE_TOTP].defaultAction = true` |
| ロックアウト | **5 回連続失敗で 15 分**の一時ロック | `bruteForceProtected` / `failureFactor` / `waitIncrementSeconds` / `maxFailureWaitSeconds` |
| リセットリンク有効期限 | **30 分** | `actionTokenGeneratedByUserLifespan` |
| このデバイスを記憶 | **30 日** | `rememberMe` / `ssoSessionIdleTimeoutRememberMe` / `ssoSessionMaxLifespanRememberMe` |
| 表示名 | 「汎用プラットフォーム」（ADR-0026 §理由） | `displayName` |

### 3.3 **投入しないもの —— `smtpServer`**

**`smtpServer` は入れない。** 実環境の接続値（ホスト・ポート・認証情報）が要り、**利用者裁定（2026-08-15）で
「実環境が要るものはそのまま触らない」と定めている**。何が足りないかを明記して分離する（#578 受け入れ基準が
「実環境依存の項目は**何が足りないか明記して**分離」と求めているとおり）。

**足りないもの（ADR-0045 決定 1 = Google Workspace への SMTP リレー）**:

| # | 不足 | 供給元 |
| --- | --- | --- |
| 1 | SMTP ホスト / ポート（`smtp.gmail.com` / 587 想定）と STARTTLS 設定 | 実環境 |
| 2 | 送信元アドレスと**アプリパスワード相当の認証情報** | 組織のメールテナント。**平文コミット禁止**のため Secret 経由 |
| 3 | 送信元の表示名・返信先 | 運用判断 |

**この 3 つが揃うまで SC-15 のメール送出は成立しない。** ただし**メール基盤が止まったときの代替**
（ADR-0045 決定 9-b = 管理者による本人確認済みリセット。`UPDATE_PASSWORD` 必須アクション）は
**realm 設定だけで成立し SMTP を要さない**ため、本作業で `UPDATE_PASSWORD` を有効な必須アクションとして投入する。

### 3.4 テーマ（`loginTheme` / `accountTheme`）は投入しない

SC-13〜16 の**画面実装そのもの**（Keycloak テーマの実体）は #438 の射程である（決定 30）。
本作業は決定 30 が名指しした下位タスク＝「**realm 設定と画面仕様書の作成**」に閉じる。
`loginTheme` / `accountTheme` は**参照先のテーマが存在しないと Keycloak の起動時に解決できない**ため、
テーマ実体と同時に入れる。

## 4. 影響範囲

| 区分 | ファイル |
| --- | --- |
| realm | `deploy/keycloak/microservices-platform-realm.json`（レルム名・クライアント ID の改名 ＋ ポリシー投入。**ファイル名は不変**） |
| deploy | `docker-compose.yml` / `helm/microservices-platform/values.yaml` / `local/values-local.yaml` / `local/argocd/oidc/argocd-cm-patch.yaml` / `local/headlamp/headlamp.yaml` / `local/observability/grafana.yaml` / `local/infra/keycloak.yaml` / `local/vault/oidc/bootstrap.sh` |
| backend | 全サービスの `appsettings{,.Development}.json`（10 サービス × 2）／`Platform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs`（既定値）／`PlatformAuthJwtBearerOptionsTests.cs` |
| frontend | `public/config.js` / `docker-entrypoint.d/40-render-config.sh` / `foundation/config/runtimeConfig.ts` / `runtimeConfig.test.ts` |
| scripts | `measure-abac-combinations.js` / `seed-abac-policies.js` / `verify-oidc-edge-flow.sh` / `k8s-local-up.sh` / `k8s-local-up.test.js` / `scripts.repo.test.js` |
| perf | `k6/lib/config.js` / `k6/README.md` |
| live docs | `CLAUDE.md` / `docs/operations/operations.md` / `docs/security/security.md` / `docs/how-to/local-development.md` / `docs/tech/tech-requirements.md` / `docs/tech/composable-component-guide.md` / `deploy/local/**/README.md` / `src/platform/frontend/README.md` |
| 新規 | `docs/screens/SC-14_otp-mfa.md` / `SC-15_password-reset.md`／`docs/tests/SC-14_otp-mfa.md` / `SC-15_password-reset.md`／`docs/adr/IADR-0197_*.md` |

## 5. 受け入れ基準（#578 の 5 項目への写像）

- [ ] `docs/screens/SC-14_*.md` / `SC-15_*.md` が存在する
- [ ] `docs/tests/SC-14_*.md` / `SC-15_*.md` が存在する
- [ ] realm 設定が ADR-0026 の確定要件を満たす（`smtpServer` は §3.3 のとおり**何が足りないかを明記して分離**）
- [ ] `resetPasswordAllowed = True` が Keycloak の既定値であって SC-15 の実装ではないことを仕様書に記録する
- [ ] #452 / #438 のどちらが何を引き受けるかが双方の本文から辿れる（**決定 30 により #438 が担当。#578 はその下位タスク**）
- [ ] レルム名 `platform` / クライアント `platform-spa` へ改名され、`/realms/microservices-platform` の残存が live 側で 0 件
- [ ] `node scripts/check-realm-constraints.js` が通る
- [ ] バックエンド・フロントのテストが通る

## 6. この作業で扱わないこと

| 対象 | 理由 |
| --- | --- |
| **Keycloak テーマの実装**（SC-13〜16 の画面そのもの） | 決定 30 により #438 の射程。本作業は下位タスク＝realm 設定と仕様書に閉じる |
| **`smtpServer` の投入** | 実環境の値が要る（§3.3）。利用者裁定「実環境が要るものは触らない」 |
| **人事システム連携プロビジョニング**（ADR-0026） | 方式（SCIM / バッチ / API）が未選定。ADR-0026 §フォローアップ |
| **SC-16 / SC-17 の仕様書** | #578 の射程は SC-14 / SC-15。SC-16 は #438、SC-17 は別 |
| **UC-09 / UC-10 の所在確定** | 利用者裁定により**別 issue へ切り出す** |
| **`docs/adr/` 本体・確定済み `docs/specs/`・`feedback/`・`CHANGELOG.md` の旧名** | §2.4 のとおり記録であり書き換えない。現行値は IADR-0197 が 1 箇所で持つ |
