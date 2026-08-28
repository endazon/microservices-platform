---
title: IADR-0294 MFA は「必須アクション＋直接付与の閉鎖」で実効化し、認証フローは宣言しない
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-09, NFR, SC-14, SC-16, SC-17, ADR-0026, ADR-0045]
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
---

# IADR-0294: MFA を実効化し、Keycloak の監査イベントを有効にする

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: claude（実装担当）

## 起点・関連

- 関連する計画書 ID: ADR-0026（§決定「TOTP による MFA を必須とする」）/ ADR-0045 決定 9-b / SC-14 / SC-16 / SC-17
- 関連する実装仕様書: `.ai-context/specs/20260828_issue-438_mfa-enforcement-and-audit-events.md`
- 先行: IADR-0197（realm 改名と認証ポリシーの投入）。**本 IADR はその フォローアップ 5・6 を閉じる**
- 先例: IADR-0075（AST 用サービスクライアント）/ IADR-0133（ABAC dev seed）/ IADR-0284（検索 seed）

## コンテキストと課題

IADR-0197 は ADR-0026 の確定値 8 項目を realm へ投入したが、**同 IADR 自身が 2 つの未達を明記して #438 へ送っていた**。

- **未達 1（フォローアップ 5）**: 「`defaultAction: true` は『MFA 必須』の十分条件ではない」。
  ① realm import で作られる利用者には `defaultAction` が遡及しない（実測: 4 名とも `requiredActions` を持たない）。
  ② Keycloak 既定 `browser` フローの OTP サブフローは Conditional であり、**OTP 未登録者はパスワードだけで通る**。
- **未達 2（フォローアップ 6）**: `eventsEnabled` / `adminEventsEnabled` / `adminEventsDetailsEnabled` /
  `eventsListeners` が**いずれも未設定**。ログイン失敗も管理操作も 1 件も残らない。

さらに本作業の母集合走査で **3 つ目**が出た。**`bff` の `directAccessGrantsEnabled: true`** は
browser フローを丸ごと迂回する —— 利用者名・パスワード・client secret を持つ者は
**OTP を一切問われずにトークンを取得できる**。realm の全 9 client で `true` はこれ 1 つだった。

## 検討した選択肢

### (a) OTP を「必須」にする手段

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A-1 | **`authenticationFlows` を宣言し、browser フローの OTP を REQUIRED にする** | **却下。** realm export に `authenticationFlows` を書いた瞬間、Keycloak は**書かなかった既定フローを一切登録しない**（`requiredActions` とまったく同じ罠。IADR-0197 が既に踏んでいる）。したがって browser / direct grant / registration / reset credentials / clients / first broker login / docker auth の**全フローと全サブフローを手で宣言する**ことになる。**この環境に Keycloak が無く、書いたものを一度も起動できない。** 間違えれば realm import が壊れ、**ログインが全経路で不能になる**。得られる利益に対して失敗の代償が大きすぎる |
| A-2 | **全利用者に `CONFIGURE_TOTP` を必須アクションとして持たせ、Conditional のまま使う** | **採用。** 初回ログインで登録を強制すれば、以後 `Condition - user configured` は常に真になり、**Conditional OTP は毎回発火する**。結果として browser 経路の MFA は必須になる。realm JSON の 1 キーで済み、静的に検査できる |

**A-2 が残す穴を 2 つ塞ぐ。**

- **穴 1**: 利用者が自分の OTP 資格情報を消せば、登録前の状態へ戻れる。→ **`delete_credential` を `enabled: false` にする。**
  ADR-0026 は再発行を SC-16／管理者側に置いており、自己都合の削除口は計画上も要らない。
- **穴 2**: 直接付与（password grant）は browser フローを通らない。→ **`bff` の `directAccessGrantsEnabled` を `false` にする。**

### (b) 直接付与を閉じると壊れるもの

母集合を引き直したところ（**1 回目は `--include=*.js` を落として `.js` を取りこぼした。規則 3 の破り**）、
`platform` realm に対して password grant を使う経路は **dev の投入器 2 本**だけだった。

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | 投入器のために `bff` の直接付与を残す | **却下。** 迂回口を運用の都合で開けたままにすることになる。そもそも**穴 2 を塞がなくても投入器は壊れる** —— `CONFIGURE_TOTP` が未消化の利用者に対し、Keycloak は password grant を `invalid_grant: Account is not fully set up` で拒む |
| B-2 | 投入器を必須アクションの対象外の利用者で動かす | **却下。** 「MFA を免除された人のアカウント」を作ることであり、最も権限の高い口に穴を残す |
| B-3 | **投入器をサービスアカウント（client_credentials）へ移す** | **採用。** 投入器は機械であり、**機械に第二要素は無い**。主体を人から機械へ移せば MFA の対象そのものから外れる。先例は IADR-0075（`ai-stock-trading-kb-writer`） |

🔴 **B-3 は副次的にもっと大きな問題を解いている。** 投入器が realm の変更に追随できなかったのは
**これで 3 回目**である —— ①#933（パスワード値の一斉変更）②#439（client の confidential 化）
③#438（MFA 必須化）。**人の資格情報を借りている限り、認証を強くするたびに投入器が壊れる。**
①②は「値を直す」で済んだが③は済まない。**主体を変えることが根治である。**

### (c) MFA を掛けたログイン導線を、どう自動検証し続けるか

`scripts/verify-oidc-edge-flow.sh` は認可コード + PKCE をログインフォーム越しに通し切る検証器で、
`integration-stack.yml`（develop への push と日次。**PR では起動しない**）が実行する。
OTP の段が挟まると、この検証器は止まる。

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C-1 | 検証器が止まることを受け入れ、issue に残す | **却下。** `integration-stack.yml` は PR で起動しないため、**PR は緑のまま develop が壊れる**。壊れると分かっていて出す形になる |
| C-2 | 検証用の利用者だけ MFA を免除する | **却下。** 「検証できないから統制を外す」であり本末転倒。B-2 と同じ形 |
| C-3 | **検証器の側が第二要素を出す** | **採用。** MFA を掛けた導線の検証は、検証する側が OTP を出せて初めて成立する |

## 決定

**決定 1**: 対話ログインする realm 利用者 4 名（`admin` / `poc-user` / `poc-operator` / `developer`）へ
`requiredActions: ["CONFIGURE_TOTP"]` を付与する。**サービスアカウントには付けない** ——
対話ログインしないため、必須アクションが残るとトークン取得が `Account is not fully set up` で失敗する。
判定は **`serviceAccountClientId` の有無**で行う（`service-account-` という名前の慣習に依存しない。
名前は人が付けるもので、機械が保証する事実ではない）。

**決定 2**: **`authenticationFlows` は宣言しない**（A-1 却下）。既定フローの Conditional OTP を、
決定 1 の強制登録と組み合わせて使う。**この決定は「実機で確かめられないものを書かない」ことに拠っており、
Keycloak を起こせる環境が整ったら A-1 を再評価してよい**（下記 §結果 の残件）。

**決定 3**: **realm の全 client で直接付与を無効にする**（`bff` の `directAccessGrantsEnabled` を `false` へ）。
併せて **`delete_credential` を `enabled: false`** にし、登録済み OTP を利用者自身が消せないようにする。

**決定 4**: **dev の投入器 2 本をサービスアカウントへ移す**。realm へ機密クライアント `abac-seeder`
（`serviceAccountsEnabled: true` / `standardFlowEnabled: false` / `directAccessGrantsEnabled: false`）を足し、
service-account 利用者に `platform-admin`（投入先 `/authz/*` が AdminOnly）と、
**`admin` と同じ ABAC 属性**（`clearance=restricted` / `department=engineering`）を持たせる ——
属性を落とすと `seed-search-documents.js` が投入する文書の帰属が変わる。
`scripts/seed-abac-policies.js` から **`passwordFromRealm` を削除する** ——
人のパスワードを読む口が残っていると、次の誰かがまたそこへ戻る。

**決定 5**: **Keycloak の監査イベントを有効にする**。`eventsEnabled` / `eventsListeners: ["jboss-logging"]` /
`enabledEventTypes`（29 種）/ `adminEventsEnabled` / `adminEventsDetailsEnabled` / `eventsExpiration`。
🔴 **保持期間（`eventsExpiration`）は 30 日とし、FR-19 の「監査ログ 3 年」を Keycloak DB へ持たせない。**
3 年分の認証イベントを Keycloak の RDB に溜めると肥大し、Keycloak 自身の可用性を損なう。
**`jboss-logging` で出したものをログ基盤側が保持する**という分担にする
（ログ基盤側の 3 年保持は本 IADR の射程外。§結果 の残件）。

**決定 6**: **検証器が TOTP を計算する**（C-3）。`scripts/lib/totp.js`（RFC 6238・外部依存ゼロ）を新設し、
`verify-oidc-edge-flow.sh` が OTP の段を自分で通す。初回登録画面（`login-config-totp`）は
画面に出ているシークレットを読み、2 回目以降（`login-otp`）は `OIDC_TOTP_SECRET` で受け取る。
🔴 **これは認証の実装ではない。検証器の側が第二要素を出すための計算だけを持つ**
（本番の OTP 検証は Keycloak が行う。ADR-0026 は認証を IdP へ寄せると確定している）。

**決定 7**: 上記すべてを **`check-realm-constraints.js` の検査 5** で静的に固定する。
自己試験は**陽性対照 1 件＋変異 9 件**を持つ（正例だけでは検出力を測れない）。

## 理由

- 決定 2 の核心は「**書いたものを一度も起動できないなら、失敗の代償が小さい方を採る**」である。
  A-1 は正面から見れば正しいが、間違えたときに壊れるのが**全経路のログイン**であり、
  この環境では壊れたことにすら気付けない。A-2 は同じ結果（OTP が毎回要求される状態）へ、
  はるかに小さい面積で到達する。
- 決定 4 は「人の資格情報を機械に借りさせない」である。3 度の drift はいずれも同じ根から出ている。
- 決定 6 は「統制を掛けたら、検証の側もその統制を通れるようにする」である。
  ここを怠ると、次に困った人が**検証を通すために統制を外す**。

## 結果

- 良い影響:
  - **ADR-0026 の「TOTP 必須」が browser 経路で実効になる。** 計画 決定 28「MFA なしでの稼働は採らない」に対する
    IADR-0197 §未達の記述が解消する。
  - **MFA の迂回口（直接付与）が realm から消えた。** 検査 5 が再発を止める。
  - **ログイン失敗・管理操作が記録される。** ADR-0045 決定 9-b ①（申請者・承認者・実行者）の前提が立つ。
  - 投入器が **realm の認証強化に引きずられなくなった**（3 度目の drift の根治）。
- 悪い影響 / トレードオフ:
  - **dev のログインに一手間増える**（初回に TOTP 登録、以後は毎回 6 桁）。`deploy/local/README.md` に明記した。
  - `delete_credential` を閉じたため、**利用者が自分で OTP を貼り直せない**。再発行は管理者側（SC-16）に寄る。
  - **検証器が Keycloak のログイン画面の HTML 構造に依存するようになった**（`kc-totp-secret-key` の id、
    `totp` / `otp` のフィールド名）。Keycloak を上げるとここが最初に壊れる。
- 🔴 **この環境で検証できていないこと**（「統制を定めた」と「統制が働いている」を書き分ける）:
  - **実 Keycloak での疎通は 1 度も行っていない。** 出せた証拠は realm JSON の静的不変条件と、
    検査 5 の検出力（実データへの変異 3 件がすべて exit 1）、および `totp.js` の RFC 6238 ベクタ一致（5/5）である。
    HTML の抽出は**この形だろうという fixture** に対してしか通していない。
  - IADR-0197 が求めた「`kcadm.sh` で `developer` の実ログインを 1 回試せば決着する」は**未実施**。
- フォローアップ:
  - **実機での 1 回の確認**（上記）。`integration-stack.yml` の次の develop 実行が最初の実測機会になる。
  - **A-1（明示フローで OTP を REQUIRED 化）の再評価**（決定 2）。Keycloak から realm を export できる環境が要る。
  - **ログイン画面 HTML への依存を減らす**（決定 6 のトレードオフ）。
  - **監査ログの 3 年保持**（決定 5）はログ基盤側の課題として残る。

## 関連

- Supersedes: なし
- Superseded by: なし
