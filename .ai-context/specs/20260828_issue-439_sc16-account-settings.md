---
title: 作業仕様書 — SC-16（アカウント設定）の実測と射程確定（#439 残スコープ）
type: spec
status: done
related_ids:
  - NFR
  - SC-16
  - ADR-0026
  - ADR-0032
  - IADR-0197
  - IADR-0251
  - IADR-0261
  - IADR-0273
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
related_specs:
  - "20260823_issue-438_keycloak-theme-and-smtp.md"
  - "20260828_issue-438_keycloak-theme-k8s-local.md"
  - "20260823_issue-439_bff-session-completion.md"
---

# 仕様書: SC-16（アカウント設定）の実測と射程確定

## 0. 結論（先に書く）

**SC-16 に対して新規に実装するものは無い。** 計画 SC-16 は **Keycloak アカウントコンソールに
テーマを適用して提供する**画面であり、**自前 SPA では実装しない**と計画側で確定している。
実体（テーマ・realm 設定・SPA 側の導線・画面仕様書・テスト仕様書）は **#438 と #439 で実装済み**である。

本作業の成果は次の 2 点に閉じる。

1. **k8s ローカルのテーマ自動配線が完了した（2026-08-28）事実に、画面仕様書 4 件を追随させる。**
2. **導線テストの検出力の穴を、変異試験で見つけて塞ぐ**（§5.1）—— 既存 2 ケースは
   「シェルが URL を直書きする」変異を取り逃がしていた。

🔴 **本作業に着手した時点の指示は「SC-16（アカウント設定画面）を実装せよ」であった。**
原文を読んだ結果その前提が成り立たないと判定したので、実装せず本書に判定根拠を残す
（`CLAUDE.md` 禁止事項「計画外の機能追加」）。

## 1. 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-16（アカウント設定）
- 関連 ADR: 認証 UX とアカウント管理（`ADR-0026`）／SPA 認証の BFF セッション方式（`ADR-0032`）
- 非機能要件: セキュリティ（セッション管理・パスワードポリシー）。`NFR-14` に対応する
- issue: #439（SPA 認証の BFF セッション方式移行の完了。go-live ブロッカー）

## 2. 計画 SC-16 の原文（隣接クローンで実読・要約からの継承をしない）

`/home/user/project-planning/projects/microservices-platform/05_screens/01_screens.md` の
§SC-16 節（実読）と、同書の関連注記・`ADR-0026` を読んだ。**計画が確定している内容**は次のとおり。

| 計画の記述 | 本作業への含意 |
| --- | --- |
| 「Keycloak Account Console に**テーマを適用して提供する**」 | 画面本体は Keycloak 既定機能。自前実装の対象ではない |
| 共通シェル: **適用外**（認証基盤ホスト） | SPA のルータ・左ナビ・パンくずの対象外。ルートを増やさない |
| ルート: `auth.example.co.jp` の `/realms/platform/account` | **SPA の配信ホストではない**。TanStack Router に載らない |
| 主要素: プロフィール／パスワード変更／OTP デバイス管理／アクティブセッション一覧 | いずれも Keycloak アカウントコンソールの既定機能 |
| 導線: 共通シェルのユーザーアイコンから遷移する | **本リポジトリの担当はこの導線だけ** |
| 表示: 共通シェル適用外のためアバターを表示しない（モック間相違の確定 ③） | SC-16 側に SPA 部品を足さない |
| 制約: ロール・ABAC 属性は本人から変更不可（SC-17 の管理者操作のみ） | 本画面へ権限編集を足さない |

さらに同書の注記（2026-07-24・SC-13〜17 のモック精密整合）が
**「SC-13〜16 は Keycloak テーマとして実装」**と明記している。

## 3. 現状の実測（走査で確かめた。記憶で挙げない）

| 確認項目 | 実測結果 | 出典 |
| --- | --- | --- |
| `src/*/frontend/src/features/` に SC-16 の実装が在るか | **無い。かつ在るべきでない。** `platform/frontend/src/features/index.ts` は合成点のみ、`knowledge/frontend/src/features/` は sc01〜sc11・sc18〜sc21 で SC-16 は無い | `ls src/*/frontend/src/features/` |
| Keycloak アカウントテーマの実体 | **在る。** `deploy/keycloak/themes/platform/account/{theme.properties,resources/css/platform.css}`（`parent=keycloak` 継承・CSS のみ追加） | `find deploy/keycloak/themes -type f` |
| realm がテーマを指しているか | **指している。** `"accountTheme": "platform"`（`loginTheme` も同じ） | `deploy/keycloak/microservices-platform-realm.json:6` |
| 宣言と実体の突合検査 | **在る。** `check-realm-constraints.js` 検査 4 が `loginTheme`/`accountTheme` の解決可能性を見る（テスト仕様書 `docs/tests/SC-13_login.md` の T-01〜T-08 が写像。trace ids に SC-16 を含む） | `scripts/check-realm-constraints.js:35,599` |
| BFF の認証端点 | **`/login` `/logout` `/me` の 3 本のみ**（指示の実測どおり）。`/me` は身元とロールと `logoutUrl` だけを返し**トークンを返さない** | `AuthBffEndpoints.cs` |
| セッション一覧・個別失効の口 | **BFF に無い。かつ在るべきでない**（§4 参照）。`Foundation/Authz/` に在るのは `BffScopeResolver.cs` のみで、チケットストアの索引は subject 単位 | `ls src/platform/backend/Shared/*/Foundation/Authz/`、`IADR-0251` 決定 4 |
| 共通シェルからの導線 | **在る。** `Layout.tsx` の `accountConsoleUrl()` が実行時 config の `oidc.authority` から `.../account` を組み立て、`<a href>` で外部遷移する。テスト 3 件が固定 | `Layout.tsx:25-30,74-84`、`Layout.test.tsx:160-176` |
| 画面仕様書 | **在る**（`status: completed`） | `docs/screens/SC-16_account-settings.md` |
| テスト仕様書 | **在る**（導線側）。画面本体の静的検査は SC-13 のテスト仕様書が兼ねる | `docs/tests/SC-16_account-settings-entry.md` |

### 3.1 issue #439 側の残スコープに SC-16 は含まれない

#439 のスコープ行は「**SC-16（アカウント設定）のセッション管理との整合**」であり、
「SC-16 を画面として実装する」ではない。整合は実装済みである ——
`/me` が配る `logoutUrl` は `/logout` の `sid` 一致検査を通る形で組み立てられている
（`AuthBffEndpoints.cs` の該当コメントが SC-16 整合と明記）。

#439 の 2026-08-23 コメントが挙げる残件 4 件（`platform-spa` public client の撤去・
`backchannel.logout.url` の本番上書き・DataProtection 鍵リングの複数レプリカ検証・
`roles.ts` の JWT 復号フォールバック）に **SC-16 由来のものは 1 件も無い**。

## 4. 射程の線引き（作らないものと、その理由）

### 4.1 作らない: SPA のアカウント設定画面

計画が「自前 SPA では実装しない」と確定している。作れば計画違反かつ
`CLAUDE.md` 禁止事項の「計画外の機能追加」に当たる。ルートも増やさない
（共通シェル適用外・別ホストのため、そもそも SPA のルート木に居場所が無い）。

### 4.2 作らない: 「この端末だけログアウト」UI

🔴 **`IADR-0273` 決定 2 が失効の単位を subject（その利用者の全セッション）と決めており、
sid 索引を増やす案は明示的に捨てられている。** 画面から個別失効を作ることは同決定を覆す。
**新 IADR か裁定が要る**ため実装しない（§7 へ申し送る）。

なお計画 SC-16 の「個別サインアウト」は **Keycloak アカウントコンソールの既定機能**が
Keycloak 自身のセッション管理に対して提供するものであり、**BFF のチケットストアの索引単位とは別の層**である。
計画の要求は Keycloak 側で満たされており、BFF に個別失効の口を足す必要は無い。

### 4.3 作らない: BFF のセッション一覧端点

上と同じ理由。一覧は Keycloak アカウントコンソールが提供する。BFF に足すと
「SPA はトークンを扱わない」（`ADR-0032` / Token Handler）境界の内側に
セッション管理 UI を持ち込むことになり、計画の構造と逆を向く。

### 4.4 作らない: SC-16 単独のテスト仕様書

`docs/screens/SC-16_account-settings.md` §未決事項が残件として挙げていたが、**実測の結果これは不要と判定した。**
本リポジトリが持つ SC-16 の実体は「テーマ」と「導線」の 2 つだけで、
**どちらも既にテスト仕様書を持つ** —— テーマの宣言と実体の突合は `docs/tests/SC-13_login.md`
（`accountTheme` を検査対象に含み、trace ids に SC-16 を持つ）、導線は
`docs/tests/SC-16_account-settings-entry.md`。3 冊目を作ると同じ内容が 2 箇所に分かれ、片方が腐る
（`CLAUDE.md`「2 箇所に置くと片方が古くなる」）。**この判定を §未決事項へ書き戻す。**

## 5. 本作業で実際に直すもの（追随 1 件）

`.claude/rules/traceability.repo.md`「是正・追随の母集合の取り方」規則 9（誤りの側の文字列で全文書を走査）
に従い、**誤りの側**＝*k8s ローカルのテーマ配線が未組み込みだという記述*を対象に走査した。

```console
$ grep -rn "未組み込み\|テーマ自動配線\|自動解決\|keycloak-theme-platform" --include=*.md .
$ grep -rn "ConfigMap の手動作成\|手動作成が必要\|docker-compose 環境では有効" --include=*.md .
```

`scripts/k8s-local-up.sh` の `[3/7]` は `keycloak-theme-platform` ConfigMap を**既に生成している**
（`20260828_issue-438_keycloak-theme-k8s-local.md` で完了・本作業の base に入っている）。
したがって次の記述は**現状と食い違う**。

| ファイル | 状態 | 対応 |
| --- | --- | --- |
| `docs/screens/SC-13_login.md` §未決事項 | 「未組み込み」のまま。SC-14 を「同じ残件」として引くが、その SC-14 は完了と書いている（**参照先が矛盾**） | **対象**。完了へ更新 |
| `docs/screens/SC-14_otp-mfa.md` 冒頭ブロック引用 | 「k8s ローカル環境は ConfigMap の手動作成が必要」。**同一文書の §未決事項は「自動配線済み」と書いており自己矛盾**（前作業の部分的な是正漏れ） | **対象**。完了へ更新 |
| `docs/screens/SC-15_password-reset.md` §未決事項 | SC-13 と同型 | **対象**。完了へ更新 |
| `docs/screens/SC-16_account-settings.md` §未決事項 | SC-13 と同型。あわせて §4.4 の判定を書き戻す | **対象**。完了へ更新 |
| `.ai-context/specs/20260823_issue-438_keycloak-theme-and-smtp.md` §7 残件 1 | 当時の記録として正しい | **対象外**。確定済み記録は書き換えない（`.claude/rules/traceability.repo.md` 凍結節） |
| `.ai-context/adr/IADR-0261` フォローアップ 1 | 同上。決定内容の変更ではない | **対象外**（同上。後続仕様書が一方向で引いている） |
| `deploy/local/README.md` / `deploy/local/infra/keycloak.yaml` | 前作業で更新済み。かつ `deploy/**` は本作業の禁止領域 | **対象外** |
| `docs/functional/FR-20`・`docs/api/FR-20`・`docs/tests/FR-20`・`docs/screens/SC-20`・`IADR-0270`・`20260823_issue-451` | 「自動解決」の語が当たるが**版競合の自動解決**の話であり本件と無関係 | **対象外**（語の同音衝突） |
| `deploy/local/edge/README.md` | 「自動解決」は `*.localhost` の名前解決の話 | **対象外**（同上） |

除外に理由が無いものは無い（規則 6）。

規則 10（この変更で新たに誤りになる自分の記述を引き直す）: 本作業は「残件が消えた」方向の更新のみで、
新たに条件付きになる記述は生じない。SC-14 の §未決事項が既に持つ「実クラスタでの見た目確認のみ環境待ち」
という限定を、他 3 件でも同じ語で揃える（**完了と書いて実機確認まで済んだと読ませない**）。

### 5.1 導線テストの検出力の穴（変異試験で発見・塞いだ）

§4 の結論は「本リポジトリが SC-16 について持つ実体は**テーマと導線だけ**であり、どちらも既に
テストがある」に依っている。**その依り所自体を変異試験で検証した。**

| # | 変異 | 結果 |
| --- | --- | --- |
| 1 | `accountConsoleUrl` から `/account` を落とす | **落ちた**（2 件）。導線の遷移先は守られている |
| 2 | シェルが `accountConsoleUrl(appConfig()...)` を経由せず **URL を直書き**する | 🔴 **生き残った** |
| 3 | realm の `accountTheme` を実体の無い名前へ差し替える | **落ちた**。`check-realm-constraints.js` が `accountTheme` を名指しで検出 |

**変異 2 が通ってしまう理由**: 既存ケース 2 は `href` の**末尾が `/account`** であることしか見ておらず、
ケース 3 は純関数 `accountConsoleUrl` を**単体で**呼ぶだけである。末尾が `/account` の文字列を
直書きすれば、両方とも緑のまま通る。**`CLAUDE.md`「接続先はビルドに焼き込まず実行時 config で注入する」
が破られても誰も気付かない状態だった。**

**対処**: 注入した `oidc.authority` を差し替えて **描画された href が追随すること**を見るケースを
1 件足した（`Layout.test.tsx`）。再度変異 2 を当てて**このケースだけが落ちる**ことを実測した。
テスト仕様書（`docs/tests/SC-16_account-settings-entry.md`）へケース 4 として写像した。

**新しい端点・画面・ルート・翻訳キーは増やしていない。** 既存の受け入れ基準
（ビルドへ焼き込まない）に対する検出力を回復させただけである。

## 6. 受け入れ基準

- [x] 計画 SC-16 の原文を実読し、主要素・ルート・共通シェル適用可否を本書へ転記した
- [x] SPA・BFF・テーマ・realm・導線・仕様書の現状を走査で実測し、表に残した
- [x] 「作らないもの」を理由つきで確定した（§4）
- [x] 追随の母集合を誤りの側の文字列で走査し、除外理由を全件書いた（§5）
- [x] 画面仕様書 4 件の記述を現状へ揃えた。`updated:` を前進させた
- [x] `check-trace-blocks` / `check-doc-links` / `check-doc-updated` / `check-adr-numbering` /
      `REQUIRE_REPO_TESTS=1 scripts.test.js` が緑
- [x] 導線テストの検出力を変異試験で実測し、生き残った変異を塞いだ（§5.1）
- [x] **新規のルート・端点・画面・翻訳キーを 1 つも増やしていない**（増やさないことが本作業の結論）

## 7. 計画書との差異・申し送り

- **差異: なし。** 計画 SC-16 の確定内容と実装は一致している。環流 issue の起票は要らないと判定した。
- **裁定が要る事項（実装しない）**: SC-16 の「個別サインアウト」を **BFF/SPA 側で**提供する案は
  `IADR-0273` 決定 2（失効は subject 単位・sid 索引を増やさない）を覆す。**新 IADR か計画側の裁定が要る。**
  ただし計画の要求自体は Keycloak アカウントコンソールの既定機能で満たされており、
  **現時点で不足は無い**（§4.2）。
- **実機確認は残る**: テーマの見た目（配色・ロゴ）と実際のログイン往復は実 Keycloak が要る。
  CI は静的検査のみを行う（`docs/tests/SC-13_login.md` §対象外）。
- **IADR-0292 は使わなかった。** 記録すべき新しい実装判断が無いためである ——
  「SPA では実装しない」は計画側 `ADR-0026` と `docs/screens/SC-16_account-settings.md` に既にあり、
  3 冊目を作ると腐る側が増える。**番号は未使用のまま空いている。**

## 8. 触ったファイル

- `.ai-context/specs/20260828_issue-439_sc16-account-settings.md`（本書・新規）
- `docs/screens/SC-13_login.md` / `SC-14_otp-mfa.md` / `SC-15_password-reset.md` / `SC-16_account-settings.md`
- `docs/tests/SC-16_account-settings-entry.md`（ケース 4 の追加）
- `src/platform/frontend/src/components/ui/Layout.test.tsx`（テスト 1 件の追加。**製品コードは変更していない**）
