---
title: IADR-0197 レルムを platform へ改名し、ADR-0026 の認証ポリシーを realm へ投入する（テーマと SMTP は #438 へ残す）
type: impl-adr
status: Accepted
related_ids:
  - SC-13
  - SC-14
  - SC-15
  - SC-16
  - NFR
  - ADR-0026
  - ADR-0045
  - IADR-0061
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
---

# IADR-0197: レルムを `platform` へ改名し、ADR-0026 の認証ポリシーを realm へ投入する

- 状態: Accepted（2026-08-15）
- 決定者: 利用者（2026-08-15 の裁定「サブ issue 化＋先行着手」「改名も今回まとめてやる」）＋ claude（実装）

## 起点・関連

- 計画書 ID: `ADR-0026`（認証UXとアカウント管理）／`ADR-0045`（メール配信）／`SC-13`〜`SC-16`
- Issue: #578（#438 の下位タスク）。担当の正は #438（計画 決定 30）
- 先例: [[IADR-0061]]（`knowledge-platform` → `microservices-platform` の改名。#228）

## コンテキストと課題

計画側の裁定（planning#247・`891b199`）が 3 つの前提を確定した。

1. **決定 31**: レルム名を `microservices-platform` → **`platform`**、基盤 SPA のクライアント ID を `spa-web` → **`platform-spa`** へ改名する。**`05_screens` の SC-13〜16 のルート（`/realms/platform/...`）は改名後の値であり、改名が済むまで SC-13〜16 は着手できない。**
2. **決定 28**: SC-13〜16 は go-live 必須。**MFA なしでの稼働は採らない。**
3. **決定 30**: SC-14/15/16 の担当は #438。#578 はその**下位タスク（realm 設定と画面仕様書の作成）**。

一方、実装側の realm は **ADR-0026 が定める 8 項目すべてが未設定**であった（#578 の実測）。
`resetPasswordAllowed = true` だけが真だが、**これは Keycloak の既定値**であって SC-15 の実装ではない。

## 決定

### 決定 1: 改名は「レルム名としての用法」だけに適用する

`microservices-platform` という文字列はリポジトリ名・Helm チャート名・k8s Namespace・イメージ接頭辞としても
広く使われているが、**決定 31 が改めるのはレルムと基盤 SPA クライアントの 2 つだけ**である。改名の母集合は
**7 変種**（`/realms/microservices-platform` ／ `"realm": "microservices-platform"` ／ `microservices-platform-realm`（ファイル名）／
`ABAC_REALM` `ABAC_SEED_REALM` `OIDC_REALM` の既定値 ／ `spa-web`）で引き、**57 ファイル**を対象とした。

**他の 8 クライアント**（`wiki-js` / `bff` / `ai-stock-trading-kb-writer` / `headlamp` / `grafana` / `argocd` / `minio` / `vault`）
**の clientId は変えない。** 決定 31 の「9 クライアントの再設定を伴う」は、**レルム名変更に伴う issuer URL の追随**を指す。

**realm export のファイル名は変えない**（`deploy/keycloak/microservices-platform-realm.json` のまま）。
着手時は [[IADR-0061]] の先例に倣って `platform-realm.json` へ改めたが、**`check-doc-links` が破損リンク 10 件を検出して差し戻した** ——
確定済み `docs/specs/` の 10 ファイルが、frontmatter の `related_specs` と本文の Markdown リンクで
**このファイルへ実リンクを張っている**。決定 2 によりそれらは書き換えないため、**ファイル名を変えるとリンクが 10 本切れる**。

**決定 31 が定めるのはレルム名とクライアント ID の 2 つであり、export のファイル名は含まない。**
計画が求めていない改名のために記録側のリンクを壊す理由が無いので、**ファイル名は現状維持とする**。
（[[IADR-0061]] のときは確定済み仕様書が realm ファイルへリンクを張っていなかったため、この衝突は起きなかった。
**先例に倣うだけでは足りず、いま何が参照しているかを引き直す必要があった** —— `.claude/rules/traceability.md` 規則 8 の型。）

### 決定 2: 記録は書き換えない。**現行値は本 IADR が 1 箇所で持つ**

**確定済み `docs/specs/`（24 件）・`feedback/`（1 件）・`docs/adr/` 本体（12 件）・`docs/adr/README.md`（索引）・
`CHANGELOG.md`（生成物）は改名しない。**

**先例を実測して決めた** —— [[IADR-0061]] の改名（`knowledge-platform` → `microservices-platform`）の後、旧名は
`docs/specs` 12 件・**`docs/adr` 8 件**・`feedback` 1 件・`docs/superpowers` 2 件・`docs/migration` 1 件・
`docs/tech`（PoC 実測記録）1 件・`README.md`（「改名済み」と述べる地の文）1 件に残っている。
**先例は「live な資産と案内文書は改名し、記録は書き換えない」を採っている。**

ADR 本体を書き換えない理由はもう 1 つある。[[IADR-0084]] / [[IADR-0086]] / [[IADR-0090]] / [[IADR-0092]] / [[IADR-0093]] の
realm URL は**その ADR が決定した内容そのもの**であり（「issuer は in-cluster 正準名 … を用いる」）、書き換えると
**「この ADR は改名後の値を決定した」という偽の主張**になる。[[IADR-0061]] に至っては**前回の改名の記録そのもの**である。

**代わりに事実を 1 箇所へ置く**:

> **本 IADR 以前の IADR・確定済み仕様書・`feedback/`・`CHANGELOG.md` に現れる `microservices-platform`（レルム名としての用法）
> および `spa-web` は、改名前の名称である。現行値はレルム `platform` / クライアント `platform-spa` であり、本 IADR が正本である。**

12 箇所へ日付つき追記ブロックを複写しない（[[IADR-0141]]。#733 で撤去した型を作り直さない）。

### 決定 3: 「4 種のうち 3 種以上」は `regexPattern` の選言で表す

ADR-0026 は「英大文字／小文字／数字／記号のうち **3 種以上**」と定める。**Keycloak の組み込みポリシーでは表せない** ——
`upperCase(n)` / `lowerCase(n)` / `digits(n)` / `specialChars(n)` はいずれも **AND** であり、「4 種のうち 3 種」という
**選言を表現できない**。したがって `regexPattern` の先読みで 4 通りの組み合わせを選言として書く。

```
length(12) and passwordHistory(5) and regexPattern(^(?:(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])|…).*$)
```

**Keycloak のパーサ制約に触れる書き方である**ため、2 点を機械検査で固定した。Keycloak は
`passwordPolicy` を **`" and "` で分割**し、各要素の**最初の `(` から末尾の `)` まで**を引数と読む。したがって
**正規表現に `" and "` を含めてはならず**、**`regexPattern(...)` は末尾に置かねばならない**。

### 決定 4: 確定値は宣言表にして `check-realm-constraints.js` が検査する

#578 は「`realm-constraints` ジョブが既に realm を検査しているので、そこへ載せられるか調べること」を求めていた。
**載る。** 同スクリプトへ**検査 3**（ADR-0026 の確定値との突合）を足した。値は宣言表
（`AUTH_POLICY_SCALARS` / `AUTH_POLICY_REQUIRED_ACTIONS`）に集約し、逸脱を CI で止める。

**`enabled` だけでは足りない点を明示的に検査する** —— 未登録者を初回セットアップへ誘導するのは
`CONFIGURE_TOTP` の **`defaultAction`** であり、`enabled: true` / `defaultAction: false` は「MFA 必須」を満たさない。
自己試験はこの**変異**を含む（変異なしの正例だけでは、走査対象に入っていなくても「逸脱なし」が成立してしまう）。
自己試験は 12 件 → **31 件**。

### 決定 5: `smtpServer` とテーマは投入しない

| 対象 | 理由 |
| --- | --- |
| **`smtpServer`** | 実環境の接続値が要る（利用者裁定 2026-08-15「実環境が要るものは触らない」）。**足りないもの 3 点は [SC-15 画面仕様書](../screens/SC-15_password-reset.md) に明記した** |
| **`loginTheme` / `accountTheme`** | **参照先のテーマが存在しないと Keycloak が解決できない**ため、テーマ実体と同時に入れる。テーマ実装は #438 の射程（決定 30） |

**ただし ADR-0045 決定 9-b の代替手順**（メール停止時の管理者による本人確認済みリセット）は
**realm 設定だけで成立し SMTP を要さない**ため、`UPDATE_PASSWORD` を有効な必須アクションとして投入した。

## 理由

- **改名を先送りすると SC-13〜16 が着手できない**（決定 31）。#578 が引き受けた「realm 設定」も、改名後の realm に
  対して入れなければ二度手間になる。**改名とポリシー投入は同じ realm ファイルを触るため、分けると衝突する。**
- **記録を書き換えないのは、記録の改竄を避けるためだけでなく、追跡可能性のため**でもある。「当時なぜそう作ったか」と
  「移行がどこまで済んだか」は、旧名が残っていることで初めて読める（`.claude/rules/traceability.md` §Superseded の書式と同じ考え方）。
- **確定値を宣言表にしたのは、次に誰かが realm を触ったときに黙って壊れないようにするため**である。#578 の
  「8 項目すべて未設定」は、**検査が無かったから 4 か月気づかれなかった**。

## 結果

- **良い影響**: SC-13〜16 の着手ブロッカー（決定 31）が外れた。ADR-0026 の 8 項目のうち **`smtpServer` を除く 7 群が投入され、CI で固定**された。
- **悪い影響 / トレードオフ**: 旧名が記録側（37 ファイル）に残るため、**全文検索では新旧が混在して見える**。本 IADR の決定 2 を読まないと現行値が判断できない。
- **リスク（未検証）**: **realm import 時にパスワードポリシーが dev ユーザーの資格情報へ適用されると、import が失敗し得る。**
  realm 内の dev ユーザーは平文 `value` を持ち、`admin`（5 文字・英小のみ）と `developer`（9 文字・英小のみ）は
  **新ポリシー（12 文字以上・3 種以上）を満たさない**。Keycloak のポリシー検証は REST 層（`UserResource.resetPassword`）と
  必須アクションで働き、**realm import の資格情報投入経路は通らない**と読んでいるが、**実機で確認していない**。
  **CI は Keycloak を起動しないため、この点は静的には検出できない**（`.github/workflows/` に Keycloak を起動するジョブは無い。実測）。

## フォローアップ

0. **AST（`src/ai-stock-trading`）側の消費者設定を追随させる。** 本リポジトリの母集合は
   `src/ai-stock-trading` を除外している —— **AST は独自の計画リポジトリと ADR を持つ別プロジェクトであり、
   submodule のため本リポジトリからは是正できない**（[[IADR-0120]]）。しかし `AST/IADR-0093`（KB writer の
   クロスレルム s2s）により、**AST は MSP のレルムを Authority として消費する**。改名の影響が AST 側へ及ぶ。

   > **`IADR-0093` は名前空間が衝突している。** 本リポジトリの [[IADR-0093]] は MinIO の OIDC 連携であり、
   > **別の決定**である。`.claude/rules/traceability.md`「複数プロジェクトを跨ぐ場合の ID 修飾」に従い
   > **`AST/IADR-0093` と修飾する**（裸の `IADR-0093` は常に本リポジトリを指す）。

   **実測（AST pin `7f69fb5` 時点。7 ファイル）**:

   | 区分 | 箇所 |
   | --- | --- |
   | **live な値**（実際に効く） | `deploy/helm/ai-stock-trading/values-local.yaml:106,141`（`KnowledgeBase__Auth__Authority`） |
   | **テストの期待値** | `backend/Shared/AiStockTrading.Shared.KnowledgeBase.Tests/KnowledgeBaseAuthTests.cs:26,32,40,46,69` |
   | **例・コメント** | `.env.example:149` ／ `values.yaml:275` ／ `InformationCollectionService.Api/appsettings.Development.json:29` |
   | **記録（書き換えない）** | AST 側 `docs/adr/IADR-0093_kb-writer-cross-realm-s2s.md:90` ／ `docs/specs/20260719_kb-writer-cross-realm-s2s.md:80` |

   **いま壊れてはいない** —— `KnowledgeBase:Auth:*` は既定空＝ no-op であり（`AST/IADR-0093` 決定 4）、
   統合は無効のままだからである。**しかし #438 がこの統合を有効化した時点で、旧レルム名のままだと
   401 になり fail-safe（未保存）へ倒れる。** 症状が「静かに保存されない」であるため気づきにくい。

   **是正は AST 側のリポジトリで行う必要がある**（本リポジトリからは変更できない）。

   > **［発見の経緯］** この抜けは PR #746 の AI レビューが指摘した。**最初の確認は偽陰性だった** ——
   > superproject で `git grep -- src/ai-stock-trading` を実行すると **0 件**が返る。
   > **`git grep` は submodule の中へ降りない**（エラーも警告も出ない）。submodule のディレクトリへ
   > 入って実行し直して 7 件を確認した。**母集合をパスで絞るとき、submodule 境界は「除外した」のではなく
   > 「最初から見えていない」** —— 規則 3（拡張子で絞らない）と同じ型の落とし穴である。

1. **上記リスクを実環境で確認する**（#438）。import が失敗する場合の是正は dev ユーザーのパスワードをポリシー準拠へ変えることだが、**`docs/` の複数箇所と `docs/operations/local-sso-recovery-runbook.md` が現行値を案内している**ため、まとめて追随させる必要がある。
2. **テーマ実体（SC-13〜16）と `loginTheme` / `accountTheme` の投入**（#438）。
3. **`smtpServer` の投入**（#438。足りないもの 3 点が供給されてから）。
4. **リカバリーコードの実現方式**を Keycloak の版とあわせて確定する（#438）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 先例: [[IADR-0061]]（前回の改名。母集合の取り方をここから実測した）
- 作業仕様書: [`20260815_issue-578_realm-rename-and-auth-policy.md`](../specs/20260815_issue-578_realm-rename-and-auth-policy.md)
- 画面仕様書: [SC-14](../screens/SC-14_otp-mfa.md) ／ [SC-15](../screens/SC-15_password-reset.md)
- テスト仕様書: [SC-14](../tests/SC-14_otp-mfa.md) ／ [SC-15](../tests/SC-15_password-reset.md)
