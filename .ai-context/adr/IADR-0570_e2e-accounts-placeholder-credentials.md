---
title: IADR-0570 試験アカウント e2e-* は realm の宣言にプレースホルダだけを置き、Secret e2e-accounts（OpenBao → ESO）の値を後追い Job と取り込み元の 1 つの口で差し込む。差し込めない利用者は作らない
type: impl-adr
status: Accepted
related_ids: [NFR-10, NFR-18, ADR-0026, ADR-0124, IADR-0294, IADR-0369, IADR-0518, IADR-0546, IADR-0569]
author: claude
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0026（認証は IdP・TOTP の MFA 必須）
  - planning:projects/microservices-platform/07_adr/ADR-0124（対になる秘密）
related_specs:
  - ../specs/20261021_2024_e2e-accounts-realm-eso.md
---

# IADR-0570: 試験アカウント e2e-* の資格情報の差し込み（#2024）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-21
- 決定者: claude（#2024。設計の正本は #2013 のコメント 3 §1）

## 起点・関連

- 起点 issue: #2024（親 #2013 の E-30）。
- 関連する計画書 ID: NFR-10、NFR-18。計画 ADR: ADR-0026、ADR-0124。
- 関連する実装判断: [IADR-0294](./IADR-0294_mfa-enforcement-and-audit-events.md)（MFA の実効）・[IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md)（後追い Job と境界）・
  [IADR-0518](./IADR-0518_realm-import-secret-with-env-client-secrets.md)（取り込み元の Secret）・[IADR-0546](./IADR-0546_account-console-declared-builtin-clients-and-seed-self-service-roles.md)（自己管理のロール）・
  [IADR-0569](./IADR-0569_e2e-layers-and-live-opt-in.md)（E2E の層と live の opt-in）。
- 作業仕様書: [20261021_2024_e2e-accounts-realm-eso](../specs/20261021_2024_e2e-accounts-realm-eso.md)。

## コンテキストと課題

稼働中の PoC へ E2E を流すため、役割ごとの試験アカウント 6 名（`e2e-user` / `e2e-operator` / `e2e-admin` / `e2e-noaccess` / `e2e-revocable` / `e2e-trading-owner`）を realm `platform` に置く。
MFA は外さず、OTP を宣言で事前登録する（初回の登録画面が自動の試験を止めるため）。資格情報の値はリポジトリに置けない（公開リポジトリ・NFR-18）。

realm の宣言は 2 つの経路で Keycloak へ届く。どちらも宣言をそのまま送る作りだった。

1. `--import-realm`（空の PVC の初回）。取り込み元は `k8s-local-up.sh` の [3/7] が作る Secret `keycloak-realm-import`。Keycloak の取り込みは `${…}` を Keycloak の env で置き換え、**解決できないものは文字列のまま残す**。
2. 後追い Job（`reconcile-realm.js`）。無い seed 利用者を宣言の本文のまま `POST /users` する。

どちらもプレースホルダを残したまま利用者を作ると、**リポジトリに公開されている文字列（`${E2E_USER_PASSWORD}`）がパスワードになる。**

順序の事実もある。新規クラスタでは [3/7] と最初の後追いが ESO より先に走るので、その時点で値の Secret はまだ無い。

## 検討した選択肢

1. 取り込み元は外部の `envsubst` で置き換え、後追いは別に実装する。`envsubst` は未定義の変数を空文字へ置き換える（空パスワードの宣言ができ、import はポリシー違反で丸ごと落ちる）。Windows の Git Bash に無いことがある。判断が 2 か所に分かれる。採らない。
2. Keycloak の Pod へ Secret を env で渡し、取り込みの置き換えに任せる。未解決のまま残る挙動は変わらず、後追いの経路は塞がらない。採らない。
3. Vault の種（`bootstrap.sh`）で乱数を入れ、常に作る。種の集合は SC-22 の許可リストとの一致を `SecretItemBootstrapSeedTests` が固定しており、試験アカウントは SC-22 の項目ではない。採らない。
4. **差し込みを `reconcile-realm.js` の純関数 1 つに置き、後追い（Job の env）と取り込み元（`--render-import`）の両方で使う。差し込めない利用者は宣言から外す。** 採る。

## 決定

### 決定 1 — 宣言はプレースホルダだけを持つ

- `users[].credentials[]` のパスワードの `value` に `${E2E_<ROLE>_PASSWORD}`、OTP（`type: otp`・`value` なし）の `secretData` に `{"value":"${E2E_<ROLE>_TOTP_SECRET}"}` を置く。`credentialData` は realm の otpPolicy と同じ（totp・6 桁・30 秒・HmacSHA1）。
- `requiredActions` に `CONFIGURE_TOTP` を置かない。`check-realm-constraints.js` の検査 5 は「`CONFIGURE_TOTP` を持つか、宣言で TOTP の OTP 資格情報を持つ」を MFA の実効とみなす（既定 browser フローの Conditional OTP は、OTP を持つ利用者に必ず OTP を問う）。
- `e2e-trading-owner` だけが realm ロール `trading-owner` を持つ（AST のロールは realm `platform` に在る）。AST の realm には足さない。

### 決定 2 — 差し込みの口は 1 つ（`resolveE2eCredentials`）。差し込めない利用者は作らない

- 置き換えるのは資格情報の `value` / `secretData` に現れる `${E2E_[A-Z0-9_]+}` だけ。`E2E_` で始まらない名前は置き換えない（宣言で Job の env〔管理者のパスワード等〕を利用者へ写させない）。資格情報に置き換えない `${` が残る利用者も作らない。資格情報の外にプレースホルダがあれば宣言の誤りとして例外にする。
- 値の検査: 空でない・制御文字なし・`${` を含まない。パスワードは realm の `passwordPolicy` の `length` と `regexPattern` を満たす（違反は import 全体を落とし、後追いでは 400 で up を止めるため、その利用者だけを外す）。TOTP の秘密は英数 20〜64 文字。
- 1 つでも欠ける・落ちる利用者は宣言から外す。`plan` に現れないので drift にも数えない（Secret が無いクラスタで G9 を赤くしない）。外した利用者は名前と理由だけを出す。値は出さない。
- 作った後の e2e 利用者は人の seed 利用者と同じく実行時が所有する（資格情報を比べない・戻さない。IADR-0369 の境界）。回転は KV を変え、利用者を消して作り直す（`deploy/local/README.md` の手順）。

### 決定 3 — 値の供給経路と置き場

- OpenBao の KV `secret/msp/e2e-accounts`（プロパティ `e2e-<role>-password` / `e2e-<role>-totp-secret`）→ ExternalSecret → Secret `platform-infra/e2e-accounts`（キー `E2E_<ROLE>_…`。`creationPolicy: Owner`）。
  KV のパスは既存の `msp/*` に揃え、policy `eso-read` を広げない（#2013 の設計の `microservices-platform/e2e-accounts` から変えた）。Secret は読み手（Job・[3/7]）の namespace に置く（設計の `microservices-platform` から変えた）。
- 後追い Job は `envFrom`（optional）で読む。[3/7] は Secret が在ればキーをサブシェルの変数へ読み、env として `reconcile-realm.js --render-import` へ渡す（値はプロセスの引数に載らない）。そのため `k8s-local-up.sh` はホストの `node` を要する（無ければ止める）。
- ESO ブロックは ExternalSecret を常時 apply し、同期したら後追いをもう一度走らせる（新規クラスタの初回で e2e を作るため。失敗は WARN で、G9 が落とす）。
- KV へは運用者が `scripts/gen-e2e-accounts.js`（宣言のプレースホルダから 12 個を導き乱数で作る）の出力を `vault kv put … -` へ流して入れる。Vault の種には入れない。KV が無い間、試験アカウントは作られない。
- TOTP の秘密は Keycloak がそのまま持つ生の文字列で、認証アプリ・E2E が使う base32 はその UTF-8 バイト列の base32 である。

## 結果

- realm JSON・例示・ConfigMap `keycloak-realms` に資格情報の値が載らない。取り込み元にも後追いの本文にもプレースホルダが残らない（`k8s-local-up.test.js`・`keycloak-realm-reconcile.test.js` が変異つきで固定）。
- `VAULT=1 ESO=1` のクラスタで KV を入れれば、次の up（または force-sync と後追い）で 6 名が出来る。KV が無ければ何も起きない。
- E2E の runner は `kubectl -n platform-infra get secret e2e-accounts` で値を取れる。

### 残るもの

- 回転は利用者の削除と作り直しで行う（後追いは既存の利用者の資格情報を書き換えない）。削除は管理コンソールの手作業である。
- `k8s-local-up.sh` がホストの Node.js を要するようになった（README の必要ツールに足した）。
- 稼働の Keycloak での実測（6 名の作成・OTP でのログイン）はこの PR では行っていない。PoC での確認手順を README と PR に書いた（後続の E-31 が live の setup で使う）。

## 関連

- #2024・#2013・#2025
