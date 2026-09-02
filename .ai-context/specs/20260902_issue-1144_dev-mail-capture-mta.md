---
title: 開発環境の捕捉用 MTA（Mailpit）を dev 既定で配備し、送出の成立を開発環境で作る
type: spec
status: draft
related_ids: [SC-15, SC-10, FR-22, NFR, ADR-0026, ADR-0045, IADR-0261, IADR-0332]
author: Claude（実装）
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
---

# 仕様書: 開発環境の捕捉用 MTA（Mailpit）を dev 既定で配備する（issue #1144）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-22（利用者通知）。非機能要件（セキュリティ: 認証・認可）
- ユースケース（UC）: UC-05
- 画面（SC）: SC-15（パスワードリセット）／SC-10（運用ダッシュボード・送信失敗の観測）
- 関連 ADR: ADR-0045（メール送信基盤。**決定 9** が本件の起点。決定 2-b / 5 / 6 / 7 / 10 が制約）／ADR-0026
- 先行する実装 ADR: IADR-0261（テーマと smtp 注入方式）／IADR-0332（ExternalSecret の起動器配線）
- 計画書リンク: `projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md`

## 目的・背景

ADR-0045 **決定 9**（`Accepted`）は「**開発環境では実送信しない。k3s 上に捕捉用 MTA（Mailpit 等）を置き、
送信内容を画面で確認できるようにする。本番のメールテナントを開発環境から指さない**」と確定している。
**その実体がリポジトリに 1 つも無い**（issue #1144 の実測。`deploy/` 配下 0 件・陽性対照 `qdrant` 16 件）。

結果として 2 つのことが同時に起きている。

1. **開発環境に「メールを送る先」が無い。** `from` に実値を入れた瞬間、既定の宛先は外部の本番リレー
   （`smtp.gmail.com:587`）になる —— 決定 9 が禁じている側へ倒れる。
2. **`docs/tests/SC-15_password-reset.md` の T-10 / T-16 が「手動（実環境）」で止まっている。**
   捕捉先が無いので、メール本文（リンクと有効期限のみ。決定 7）を機械が検証できない。

### 着手前の実測（陽性・陰性の対）

```console
### 陰性: deploy/ 配下の捕捉用 MTA（走査は 2026-09-02 に再実行した）
$ git ls-files | grep -v '^src/ai-stock-trading/' \
    | xargs grep -lni -E 'mailpit|mailhog|maildev|smtp4dev|papercut'
.ai-context/adr/IADR-0332_keycloak-smtp-externalsecret-wiring.md
.ai-context/specs/20260816_issue-600_fr22-in-app-notifications.md
.ai-context/specs/20260831_issue-1102_keycloak-smtp-externalsecret-wiring.md
docs/operations/keycloak-smtp-relay-setup-runbook.md
docs/screens/SC-15_password-reset.md
  → いずれも「無い」と述べている散文であって、配備物ではない（deploy/ 配下は 0 件）

### 陽性対照: 同じ走査器・同じ範囲で実在する配備物
$ grep -rlni 'qdrant' deploy/ | wc -l   → 16
```

```console
### 稼働 k3s（2026-09-02）: realm の送出設定
$ kcadm.sh get realms/platform --fields realm,resetPasswordAllowed,smtpServer
{ "realm": "platform", "resetPasswordAllowed": true, "smtpServer": { } }

### 同上: 申請の応答（#1143 の陰性対照でもある）
$ reset-probe.sh poc-user           → http_code=500  「申し訳ございません」
$ reset-probe.sh no-such-user-zzz   → http_code=200  「アカウントにログイン」
```

## 対象範囲

- **対象**
  - `deploy/local/infra/` へ捕捉用 MTA（Mailpit）の Deployment / Service を**dev 既定**で置く
  - dev 既定の送出先を捕捉用 MTA へ向ける（`realm.json` の `smtpServer` ＋ `bootstrap.sh` の `SMTP_*` 既定）
  - `scripts/k8s-local-up.sh` の起動段・`scripts/check-stack-ready.js` の到達判定
  - 送出の成立とメール本文（決定 7）を機械で確かめる検査器（T-10 の一部 / T-16 の自動化）
  - 「dev 既定が外を向いていない」ことの静的検査（受け入れ基準 5）
  - `docs/operations/keycloak-smtp-relay-setup-runbook.md` / `docs/tests/SC-15_password-reset.md` /
    `docs/screens/SC-15_password-reset.md` / `deploy/local/README.md` / `docs/operations/operations.md` の追随
- **対象外**
  - **SC-15 の存在秘匿の破れ（500 / 200 の差）は直さない。** #1143 の射程である（IADR-0332 決定 5 の分割）。
    本 PR は**その測定台**（捕捉用 MTA と申請フローの器）までを作る。
  - **docker-compose 経路への捕捉用 MTA の配備。** 決定 9 は「**k3s 上に**」と明示している。
  - **NotificationService（FR-22）の SMTP 送出。** 実装は `UnconfiguredSmtpEmailTransport` だけで、
    実 SMTP トランスポート自体が存在しない（#600・未着手）。**dev 既定が外を向いている箇所は無い**ので、
    本件の受け入れ基準 5 の母集合には入らない。
  - **実環境（go-live）の値の投入。** 利用者裁定 2026-08-15「設定手順の整備までが限度」の内側に留める。
    **本 PR が増やす秘匿値は 0 個**である。

## 設計

### D1. 捕捉用 MTA は dev 既定（opt-in にしない）

決定 9 の「開発環境では実送信しない」は**無条件**である。opt-in ゲートに載せると、**ゲートを立てない人の
既定は外を向いたまま**であり、決定 9 の弱い版（守る人だけが守る）にしかならない。
→ `deploy/local/infra/mailpit.yaml` を base の kustomization に入れ、`up` で常に立てる。

永続化オーバーレイ（`deploy/local/infra-persistence`）には**加えない**。捕捉したメールは使い捨てであり、
`up` のたびに空から始まるほうが検査の前提が単純になる（Mailpit の既定はインメモリ）。

### D2. dev 既定の送出先を捕捉用 MTA へ向ける（2 つの供給源）

dev 既定は 2 箇所にあり、**両方を向け直さないと片方から外へ出る**。

| 供給源 | 変更 | 増える秘匿値 |
| --- | --- | --- |
| `deploy/keycloak/microservices-platform-realm.json` の `smtpServer`（新規） | `host: mailpit.platform-infra.svc.cluster.local` / `port: 1025` / `from: noreply@platform.localhost` / `auth: false` / `starttls: false` / `ssl: false` | **0 個**（合成の dev アドレス。クラスタ外へは 1 通も出ない） |
| `deploy/local/vault/eso/bootstrap.sh` の `SMTP_HOST` / `SMTP_PORT` / `SMTP_STARTTLS` 既定 | 同じ in-cluster 宛先へ | 0 個（`from`/`user`/`password` は**空のまま**） |

- **`realm.json` へ書いてよいのは `host`/`port`/`from`/`auth`/`starttls`/`ssl` だけである。** runbook §なぜ
  realm.json に直接書かないか が禁じているのは `from`/`user`/`password` の**実環境の値**であって、
  **クラスタ内でしか意味を持たない合成値ではない**。同節の「将来 host/port/starttls だけを静的に投入する
  余地はある」を、dev 既定として行使する。
- **`from` を Vault 側で空のまま残す**のは、IADR-0332 決定 4 と runbook §2 の「長さが 0 なら §3 へ進むな」を
  壊さないためである。ESO 経路は**実リレーの値を入れるための経路**であり、dev 既定の経路ではない。

### D3. STARTTLS は「捕捉用 MTA 宛のときだけ」外す（fail-safe な既定の導出）

ADR-0045 **決定 5**（STARTTLS 必須・証明書検証を無効化しない）の理由は「リセットリンク（認証資格）が
**経路上で読めてはならない**」である。捕捉用 MTA は**読ませるために置く装置**であり、経路はクラスタ内の
Pod ネットワークに閉じ、**外へは 1 通も出さない**。ここで STARTTLS を課すと、自己署名証明書を Keycloak に
信頼させる配線が増えるだけで、守るものが無い。
→ **捕捉用 MTA 宛に限り平文とする**（決定 5 の明示的な適用外。IADR に記録する）。

🔴 **ただし `SMTP_STARTTLS` の既定値を素朴に `false` へ書き換えてはならない。** そうすると、実リレーへ
向けるために `SMTP_HOST`/`SMTP_FROM`/… だけを渡した運用者が **STARTTLS 無しで外部へ繋ぐ**（決定 5 の破れ）。
→ 既定を**導出**する: `SMTP_HOST` が捕捉用 MTA のままなら `1025` / `false`、**それ以外の宛先なら `587` / `true`**。
env で明示すればいつでも上書きできる。

### D4. 閲覧 UI はエッジへ出さない（`kubectl port-forward` で開く）

`qdrant.localhost` と同型のエッジ Ingress は**置かない**。Mailpit の UI は**認証を持たず**、
その中身は**パスワードリセットリンク＝認証資格**である（決定 5 の理由がそう述べている）。
qdrant の先例が公開しているのは埋め込みであって資格情報ではない。**認証の無い資格情報ストアをエッジへ
出すのは、本 issue の射程外の新しい統制判断**になる。決定 9 の「画面で確認できる」は、Service の HTTP ポートと
**運用者が明示的に開く** `kubectl port-forward` で満たす。IADR にフォローアップとして残す。

### D5. 起動器と到達判定

- `scripts/k8s-local-up.sh` `[4/7]`: `rollout status deploy/mailpit` を infra の並びへ加える。
  最終案内へ捕捉用 MTA の開き方（port-forward）を 1 行足す。
- `scripts/check-stack-ready.js` に **G8** を足す:
  - (a) `platform-infra/mailpit` の Deployment が在る（**無ければ失敗**。ゲートが無いので notice で逃がさない）
  - (b) Mailpit の HTTP API が**ループバックで**応答する（`GET /api/v1/info`）。G7 と同じ作法で、
    エッジにも port-forward にも依存しない（Mailpit の image は alpine/busybox ＝ `sh` と `wget` を持つ・実測済み）
  - **realm の実行時 `smtpServer.host` は見ない。** 運用者が決定 9 の但し書き（疎通と文面の検証）で
    実リレーへ向けている状態と区別できず、正当な状態を赤にしてしまう。

### D6. 受け入れ基準 5（外部送信禁止の機械化）は**静的検査**に置く

「開発環境の設定を実リレーへ向ける変更を入れると落ちる」は、**リポジトリのファイルの変更**を捕まえる
検査でなければならない（クラスタの実行時状態を見ると D5 の理由で正当な状態を赤にする）。
→ `scripts/check-realm-constraints.js` に **ADR-0045 決定 9 の門**を足す。同検査器は既に realm.json を
ADR-0026 / ADR-0045 決定 9-b と突き合わせており、`--self-test` に**変異させると必ず落ちる**ことを
書ける器を持っている（受け入れ基準 5 の「陽性対照つき」はここで満たす）。

門の内容（**列挙を持たない**: 許可ホストの名は 1 箇所＝ `deploy/local/infra/mailpit.yaml` の Service 名から走査して得る）:

1. `realm.json` に `smtpServer` が在り、その `host` が **in-cluster の捕捉用 MTA**（`<svc>.<ns>.svc…` 形）である
2. `bootstrap.sh` の `SMTP_HOST` 既定が同じ宛先である
3. どちらも**クラスタ外のホスト名**（`.` を含み `.svc` で終わらない・`localhost` でもない）を向いていない

### D7. T-10 / T-16 の自動化（受け入れ基準 4）

`scripts/check-password-reset-mail.js`（新規）:

1. 対象 realm と Keycloak のエッジ URL を**走査して**得る（realm 名は `deploy/keycloak/*-realm.json`、
   URL は Deployment の `KC_HOSTNAME_URL`。`check-stack-ready.js` G4 と同じ単一情報源）
2. Mailpit の件数を控える → SC-15 の `reset-credentials` フローで**実在する利用者名**を申請する
   （エッジ経由・**クラスタの CA を検証に使う**。`-k` 相当は使わない）
3. Mailpit の API を**ループバックで**読み、増えた 1 通を取る
4. **T-16**: 本文がリセットリンク（`action-token`）と**有効期限**を含み、
   **決定 7 が禁じる余分な情報**（資料タイトル・検索語・回答本文に相当するもの）を含まないこと
5. **T-10 の前半**（送出が成立すること）: 申請の応答が 200 であること
   —— **T-10 の本体（存在秘匿＝実在／非実在で区別できないこと）は #1143 が同じ器へ足す**
6. `--self-test`: 判定関数（純関数）を変異入力で落とす（陽性対照）

## 受け入れ基準

- [ ] **1. 捕捉用 MTA が開発環境に居る** —— `kubectl -n platform-infra get deploy,svc` に `mailpit` が
      Running で現れ、SMTP 受信ポート（1025）と閲覧 UI / API（8025）を持つ
- [ ] **2. 開発既定が外へ出ない** —— `SMTP_*` を何も指定せずに `bootstrap.sh` が seed した `host` が
      in-cluster の捕捉用 MTA であり、`smtp.gmail.com` ではない。実リレーへ向くのは env 明示時だけ
- [ ] **3. 送出が開発環境で成立する** —— realm の `smtpServer` が捕捉用 MTA を指す状態でパスワードリセットを
      申請すると、Mailpit に **1 通**届き、本文にリセットリンクと有効期限が含まれる。**外部へは 1 通も出ない**
- [ ] **4. テストが自動化される** —— `docs/tests/SC-15_password-reset.md` の T-10 / T-16 が「手動（実環境）」から
      自動へ変わり、捕捉用 MTA の API を読んで本文を検証する検査器が実在する
- [ ] **5. 外部送信禁止が機械で守られる** —— dev 既定を実リレーへ向ける変更を入れると検査が落ちる（陽性対照つき）

## テスト方針

| 受け入れ基準 | 写像先 | 陽性対照 / 陰性対照 |
| --- | --- | --- |
| 1 | 稼働 k3s での実測 ＋ `scripts/k8s-local-up.test.js`（`rollout status deploy/mailpit` の発行） | 陰性: 配備前は `deploy,svc` に現れない（着手前の実測） |
| 2 | `scripts/check-realm-constraints.js` の新設門（`bootstrap.sh` の既定） ＋ 稼働 k3s の Vault seed 実測 | `--self-test` で `smtp.gmail.com` へ戻す変異を必ず落とす |
| 3 | `scripts/check-password-reset-mail.js`（稼働クラスタ） | 陽性: 向けた realm で 1 通届く／陰性: 向けていない realm では届かない |
| 4 | `docs/tests/SC-15_password-reset.md` の T-10 / T-16 を自動へ、T-17 / T-18 を新設 | 同上 |
| 5 | `scripts/check-realm-constraints.js --self-test` | 変異（realm.json の host を外部へ / `bootstrap.sh` の既定を外部へ）で必ず fail |

## 母集合（是正の追随先。規則 9・10 に従い**走査してから**挙げた）

```console
$ git ls-files | grep -v '^src/ai-stock-trading/' \
    | xargs grep -lni -E 'smtp\.gmail\.com|mailpit|mailhog|maildev|smtp4dev|papercut'
```

| ファイル | 追随 | 理由 |
| --- | --- | --- |
| `deploy/local/vault/eso/bootstrap.sh` | **する** | dev 既定の付け替え（D2 / D3） |
| `docs/operations/keycloak-smtp-relay-setup-runbook.md` | **する** | 「host/port/starttls は上書き不要」が偽になる。§実行しなくてよい場合・§確認・§なぜ realm.json に書かないか |
| `docs/screens/SC-15_password-reset.md` | **する** | 未決事項「捕捉用 MTA が未配備」／§メール送出は成立していない（**dev では成立する**へ） |
| `docs/tests/SC-15_password-reset.md` | **する** | T-10 / T-16 の区分と未決事項 |
| `deploy/local/README.md` | **する** | `platform-infra` の構成要素の列挙に `mailpit` が要る（`deploy/local/infra` 参照の走査で得た） |
| `scripts/test-traceability-allowlist.json` | **する** | 「SC-15 のメール経路は `smtpServer` 未設定のため**原理的に検証できない**」が偽になる（`smtp` 走査で得た）。**SC-15 の allowlist からの削除はしない** —— 同検査器の母集合は `src/` のテストであり、本 PR の検査器は `scripts/` に在るためである |
| `scripts/README.md` / `.github/workflows/integration-stack.yml` | **する** | 検査器の一覧と門の数（`check-stack-ready` の「門は 7 つ」・self-test 件数）が古くなる |
| `docs/operations/operations.md` | **しない** | `deploy/local/infra` を引いてはいるが、参照は永続化オーバーレイと可観測性の文脈のみで、`platform-infra` の構成要素を列挙していない（走査後に本文を確認した） |
| `.ai-context/adr/IADR-0332_*.md` / `.ai-context/specs/2026083*_*.md` / `.ai-context/specs/20260816_*.md` | **しない** | **確定済みの凍結記録**（traceability.repo.md「Superseded / Deprecated な ADR を引用するときの書式」の凍結の射程）。本文を後から書き換えない |
| `docs/how-to/plan-id-range-history-annex.md` | **しない** | 別件（SMTP は ID レンジの話として現れるだけ・走査の副産物） |

**この変更で新たに誤りになる自分の記述（規則 10）**: 「`deploy/` 配下に捕捉用 MTA は 0 件」「開発環境には
メールを送る先が無い」「T-10 / T-16 は原理的に検証できない」。いずれも上表の live 文書側にあり、**凍結記録側は
測定時点の事実として正しいまま**なので触らない。

## 計画書との差異

- 差異: **あり（1 件・射程を限った適用外）**。ADR-0045 決定 5（STARTTLS 必須）を**捕捉用 MTA 宛に限り
  適用しない**（D3）。理由と、実リレー宛では既定が `true` のままであること（fail-safe な導出）を実装 ADR に残す。
  **決定 5 そのものを改めない** —— 改めるなら計画 ADR が要る。
- 差異なし: 決定 9（dev 既定・k3s 上・画面で確認）／決定 2-b（実リレーの書式は env 明示時に維持）／
  決定 6（秘匿値は Vault のまま・本 PR は 0 個増）／決定 7（本文はリンクと有効期限のみ・T-16 で検査）／
  決定 10（**egress を増やさない**。捕捉用 MTA はクラスタ内で閉じ、既定を外向きから内向きへ**減らす**変更である）

## 未決事項

- **Mailpit の UI をエッジへ出すか**（D4）。出すなら SSO か閉域の統制が要る。本 PR は出さない。
- **docker-compose 経路**の捕捉用 MTA（決定 9 は k3s のみを述べている）。本 PR は触らない。
- **NotificationService（FR-22）の実 SMTP トランスポート**は未実装（#600）。捕捉用 MTA が居るので、
  実装時に dev の送出先はそのまま使える。
