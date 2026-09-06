---
title: 運用 Runbook — Keycloak smtpServer（SMTP リレー）の設定
type: runbook
status: draft
created: 2026-08-23
updated: 2026-09-06
author: claude
---
<!-- trace:
ids: [SC-10, SC-15, FR-05, FR-09, FR-22]
adrs: [ADR-0026, ADR-0045, ADR-0078]
iadrs: [IADR-0197, IADR-0261, IADR-0332, IADR-0344, IADR-0347, IADR-0403]
specs: [20260823_issue-438_keycloak-theme-and-smtp, 20260831_issue-1102_keycloak-smtp-externalsecret-wiring, 20260902_issue-1144_dev-mail-capture-mta, 20260902_issue-1143_reset-existence-concealment, 20260906_issue-1245_nearby-mta-relay]
issues: [#438, #578, #600, #1102, #1143, #1144, #1245]
-->

# 運用 Runbook: Keycloak smtpServer（SMTP リレー）の設定

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 起点: **#438**（#578 が「足りないもの」として分離した項目。先行する実装 ADR の決定を引き継ぐ）。
>
> **本書は「実環境の値が供給されてから、それを安全に投入する手順」を定める。**
> **値そのものは本書にもリポジトリのどこにも置かない**（メール配信の計画 ADR の決定。CLAUDE.md 禁止事項「機密情報のコミット」）。

> **［2026-09-06 更新］手順が 2 つ減った。** 認証基盤の送出先は**クラスタ内の近接 MTA（キュー付き）に固定**され、
> 実環境の接続条件は**近接 MTA → 上流**の区間へ移った。認証基盤の realm へ実値を反映する手順（旧 §3）と、
> compose 経路の同手順は**退役した** —— 運用者が行うのは **§1（Vault へ値を投入）と §2（同期の確認）だけ**である。
> **認証基盤の送出先はバージョン管理下の宣言が正**であり、再起動・再インポート・後追いの差分適用の
> いずれを経ても近接 MTA を指す。
>
> 🔴 **残るのは §0 の「先に申請を閉じる」だけ**である —— そこだけは認証基盤の realm を触る（`kcadm`）。
> 機械で閉じる門が入るまでの暫定であり、**この 1 箇所を人手に残していること自体が窓である**。

## この手順を実行する条件（いつ走らせるか）

- 組織のメールテナント（go-live では Google Workspace）から SMTP 接続情報（ホスト・送信元アドレス・
  アプリパスワード）が供給されたとき（初回投入）。
- 送信元アドレスの変更・アプリパスワードのローテーションが必要になったとき（再投入）。
- ~~realm を作り直したため送出先が消えたとき。~~ **［2026-09-06］この条件は消えた** —— 送出先は
  バージョン管理下の宣言が持つので、再インポートしても近接 MTA を指したままである。

**実行しなくてよい場合**: 検証だけであれば本手順は要らない。メール配信の計画 ADR の決定（開発環境では
実送信しない）に従い、**開発環境には捕捉用 MTA が dev 既定で立っている**（`deploy/local/infra/mailpit.yaml`）。
`scripts/k8s-local-up.sh` が起動する。**［2026-09-06］realm が指す先は近接 MTA**（`deploy/mail-relay/`）に
変わり、捕捉用 MTA はその**上流**になった —— 経路は `認証基盤 → 近接 MTA → 捕捉用 MTA` である。
**何もしなくても送出は成立し、1 通も外へ出ない**（既定の上流が捕捉用 MTA だからである）。受信したメールは次で読む。

```sh
kubectl -n platform-infra port-forward svc/mailpit 8025:8025   # → http://localhost:8025
node scripts/check-password-reset-mail.js                       # 申請→送出→受信→本文を機械で確かめる
```

🔴 **本手順は「外部の実リレーへ向ける」ための手順である。** 実行すると、その開発環境からのメールは
**外部へ実送信される**。疎通と文面の検証が要る段階に限って行うこと（計画 ADR の同決定の但し書き）。

## なぜ realm.json に直接書かないか

`deploy/keycloak/microservices-platform-realm.json` は **--import-realm で毎回（または初回）読み込まれる
バージョン管理下のファイル**である。`smtpServer.from` / `smtpServer.user` / `smtpServer.password` は
**実環境の秘匿値または個人情報相当の値**であり、ここへ書くと平文コミットになる（メール配信の計画 ADR の決定）。

**`host` / `port` / `starttls` は秘匿値ではない**（メール配信の計画 ADR が接続の書式として確定している値：
`smtp.gmail.com` / `587` / STARTTLS 必須）。**`from` / `user` / `password` を realm.json へ書くことは今後もしない**
——理由は上記のとおりであり、「実環境の値が判明したから書いてよくなる」ものではない
（値の性質が変わらない限り恒久的な方針）。

> **［2026-09-02］realm.json には dev 既定の `smtpServer` が入っている。** 宛先は**クラスタ内の捕捉用 MTA**
> であり、`from` も `noreply@platform.localhost` という**クラスタの外では意味を持たない合成値**である。
> **秘匿値は 1 つも入っていない**（上の恒久方針と矛盾しない —— 禁じているのは**実環境の値**である）。
>
> **［2026-09-06 追記］realm.json の送出先は「近接 MTA の Service 名」になった。** 環境によらず同じ値であり、
> **本手順を実行しても realm は変わらない**（実値は近接 MTA が読む Secret にしか存在しない）。
> 恒久方針はより強く満たされた —— `from` / `user` / `password` は**稼働 realm にも入らない**。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | Vault へ書き込める運用者権限（`secret/msp/keycloak-smtp`）。`platform-infra` namespace の Deployment を再起動できる権限。**［2026-09-06］認証基盤の管理者権限は要らなくなった**（realm を書き換えないため） |
| 必要なツール | `kubectl`（k8s 経路）または `docker compose`（compose 経路）。`vault` CLI は不要（Pod 内 exec で足りる。[`bootstrap.sh`](../../deploy/local/vault/eso/bootstrap.sh) と同じ作法） |
| 供給元の値 | 送信元アドレス・SMTP 認証ユーザー（通常は送信元アドレスと同じ）・アプリパスワード（メール配信の計画 ADR の決定。2 段階認証が前提） |
| 所要時間の目安 | 10 分（Vault seed → ExternalSecret 同期確認 → 近接 MTA の再起動 → 疎通確認） |

## 手順（k8s 経路。`deploy/local/` の dev 環境）

### 0. 🔴 先に申請を閉じる（存在秘匿を割らないため。**省略しないこと**）

**近接 MTA を止める・作り直す間、パスワードリセットの申請を開いたままにしてはならない。**
認証基盤から見た送出先が使えないと**実在する利用者名のときだけ 500** が返り、実在しない利用者名は 200 を返す ——
**その差だけで利用者名を 1 リクエストずつ列挙できる**（稼働環境で実測済み）。

閉じてしまえば、実在／非実在のどちらにも**同じ 400 と同じ本文**が返る（実測済み）。

> **［2026-09-06］この手順の適用範囲は狭くなった。** 上流（組織のメールテナント）の停止では、
> 近接 MTA がキューに受け取って後送するため**申請の応答は変わらない** —— 閉じる必要は無い。
> **閉じるのは近接 MTA 自身を止めるときだけ**である（§4 の再起動は Recreate で一瞬止まる）。
> **この手順はまだ人手である。** 機械で閉じる門は別途実装する（それまでの窓は残る）。

```sh
KC_POD=$(kubectl -n platform-infra get pod -l app=keycloak -o jsonpath='{.items[0].metadata.name}')
kubectl -n platform-infra exec -i "$KC_POD" -- sh -c   '/opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master      --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null    && /opt/keycloak/bin/kcadm.sh update realms/platform -s "resetPasswordAllowed=false"'
```

> **同じ理由で、送出経路が落ちたときも閉じる。** 起動器の到達判定（`node scripts/check-stack-ready.js`）が
> 捕捉用 MTA の停止を検出したら、復旧までの間は上のコマンドで閉じておくこと。
> **閉じている間はリセットが使えない**が、**利用者名が漏れるよりはよい**（fail-closed）。


### 1. Vault へ値を投入する（Secret の値は画面や CLI 履歴に残さない）

`bootstrap.sh` は `secret/msp/keycloak-smtp` を **env 由来 or 空既定**で seed する
（[`deploy/local/vault/eso/bootstrap.sh`](../../deploy/local/vault/eso/bootstrap.sh)）。値を対話的に読み込んで
再実行する（シェル履歴に残る `export` 形は避け、`read -rs` を使う。既存の Vault OIDC 手順と同じ作法）。

```sh
read -rs SMTP_FROM;     export SMTP_FROM
read -rs SMTP_USER;     export SMTP_USER
read -rs SMTP_PASSWORD; export SMTP_PASSWORD
bash deploy/local/vault/eso/bootstrap.sh
unset SMTP_FROM SMTP_USER SMTP_PASSWORD
```

🔴 **`SMTP_HOST` は必ず明示すること。** `bootstrap.sh` の**宛先の既定はクラスタ内の捕捉用 MTA**であり、
外部の実リレーではない（メール配信の計画 ADR の決定「開発環境では実送信しない」を既定で満たすため）。
`SMTP_HOST` を渡さずに `from`/`user`/`password` だけ入れても、**メールは捕捉用 MTA に溜まるだけで外へは出ない**。

```sh
export SMTP_HOST=<供給されたホスト>   # 秘匿値ではない（計画 ADR が書式として確定している値）
```

`SMTP_PORT` / `SMTP_STARTTLS` は**宛先から導出される** —— 捕捉用 MTA 以外を指した時点で
**計画 ADR の確定値（`587` / STARTTLS 有効）が既定になる**。明示的に変えたいときだけ env で渡す。
**STARTTLS を無効のまま外部へ繋ぐ経路は作らないこと**（計画 ADR の決定。平文フォールバックを許さない）。

### 2. ExternalSecret の同期を確認する（適用は起動器が済ませている）

**ExternalSecret は手で適用しない。** 起動スクリプトが `ESO=1` のとき常時適用し、同期の完了まで
待ち合わせる（適用の並び・待ち合わせの根拠は
[`deploy/local/vault/eso/README.md`](../../deploy/local/vault/eso/README.md)）。
**§1 で値を入れ替えたあとに残るのは、同期が済んだことの確認だけである。**

```sh
kubectl -n platform-infra wait --for=condition=Ready externalsecret/keycloak-smtp --timeout=60s
kubectl -n platform-infra get secret keycloak-smtp -o jsonpath='{.data.from}' | base64 -d | wc -c
```

**最後のコマンドが 0 より大きければ**、Vault → k8s Secret の同期は成立している。
**値そのものは表示しない**（長さだけを見る。`password` キーは確認しない）。

> **同期の間隔は 1 時間である**（`refreshInterval: 1h`）。§1 の再 seed 直後に長さが 0 のままなら、
> まだ前の（空の）値を保持している。`kubectl -n platform-infra delete secret keycloak-smtp` で
> ESO に作り直させるか、次の refresh を待つ。
>
> 🔴 **長さが 0 のまま §4 へ進んでも、外向きの差出人は書き換わらない。** 近接 MTA は空の `from` を
> 「実値が未供給」と読み、外向きの差出人写像を張らない（**壊れた写像で起動するよりよい**）。
> 認証基盤側の `from` は合成値のまま変わらないので、**申請が 500 になることはない** ——
> 送出は近接 MTA が受理した時点で成立する。**長さが 0 なら §1 をやり直すこと。**

### 3.（退役）稼働中の realm へ送出先を反映する

> **［2026-09-06］この手順は無くなった。** 認証基盤が指す送出先は**クラスタ内の近接 MTA**（`deploy/mail-relay/`）に
> 固定され、その値は**バージョン管理下の realm 宣言**が持つ。実環境の接続条件（ホスト・ポート・STARTTLS・
> 認証情報・送信元）は**近接 MTA から上流への区間**へ移り、近接 MTA が §1 で投入した Secret を環境変数で読む。
>
> したがって、**運用者が認証基盤の realm へ送出先を書き込む作業は無い**。§1 と §2 を終えたら §4 へ進む。
> （§0 の「申請を閉じる／開き直す」だけは realm を触る。機械の門が入るまでの暫定である。）
>
> 🔴 **realm を手で書き換えないこと。** 後追いの差分適用が宣言（近接 MTA）へ戻すため、手で入れた値は
> 次の適用で消える。これは事故ではなく設計である —— 送出先が宣言で固定されているからこそ、
> **「送出先が未設定のまま申請だけ開いている」状態（実在する利用者名のときだけ 500 が返る組）が構造として作れない。**

### 4. 近接 MTA へ新しい値を届ける（Pod を作り直す）

環境変数として読み込んだ Secret は、**Pod の起動時に一度だけ解決される。** §1 で値を入れ替えても、
動いている近接 MTA は**古い値（＝捕捉用 MTA 宛・空の送信元）のまま**である。起動スクリプトは
同期の完了を待ってからこれを行うが、**§1 を単体で再実行したときは手で行う。**

```sh
kubectl -n platform-infra rollout restart deploy/mail-relay
kubectl -n platform-infra rollout status  deploy/mail-relay --timeout=120s
```

> 🔴 **上流の接続条件が壊れていると近接 MTA は起動しない**（宛先が空、STARTTLS の値が `true` / `false` 以外）。
> **黙って平文で外へ出たり、宛先の MX へ直接配送したりするより、起動しないほうが安全である**という設計である。
> 起動しないときは `kubectl -n platform-infra logs deploy/mail-relay` の 1 行目を読むこと（理由を名指しする）。

## 手順（docker-compose 経路。`deploy/docker-compose.yml` の dev 環境）

> **［2026-09-06］この経路の手順は退役した。** 旧手順は認証基盤の realm へ実値を直接反映するものであり、
> **近接 MTA を経由しない**（＝上流が落ちた瞬間に実在する利用者名だけ 500 になる経路をそのまま残す）。
> 加えて compose 経路には捕捉用 MTA も近接 MTA も居ないため、実行した時点から**外部へ実送信される**。
>
> **疎通と文面の検証は k8s 経路で行うこと。** 近接 MTA は環境非依存の宣言（`deploy/mail-relay/`）として
> 置いてあり、go-live では認証基盤と同じ namespace へ同じものを適用する。

## 確認（この手順が成功したと言える条件）

1. 近接 MTA が新しい上流で起動していること（**値そのものは表示しない**）。

   ```sh
   kubectl -n platform-infra get deploy/mail-relay
   kubectl -n platform-infra logs deploy/mail-relay | grep -i 'forwarding all emails'
   ```

   > 起動ログの 1 行が `[<供給されたホスト>]:<ポート>` を指していれば、上流の差し替えは成立している。
   > **認証基盤側の設定は確認しなくてよい** —— realm はバージョン管理下の宣言が正であり、
   > 手順の前後で 1 バイトも変わらない。

2. 申請 → 送出 → 受信 → 本文までを機械で確かめる。

   ```sh
   node scripts/check-password-reset-mail.js
   ```

3. 🔴 **送出が成立することを確かめてから、申請を開き直す**（§0 で閉じたものを戻す。**順序を逆にしない** ——
   送出が壊れたまま開くと、実在する利用者名のときだけ 500 になり利用者名が漏れる）。

   ```sh
   KC_POD=$(kubectl -n platform-infra get pod -l app=keycloak -o jsonpath='{.items[0].metadata.name}')
   kubectl -n platform-infra exec -i "$KC_POD" -- sh -c      '/opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master         --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null       && /opt/keycloak/bin/kcadm.sh update realms/platform -s "resetPasswordAllowed=true"'
   node scripts/check-password-reset-mail.js   # 開閉と送出先の組・応答の同値性を機械で確かめる
   ```

4. パスワードリセット画面を実運用アカウントで申請し、リセットメールが着信する。

**捕捉用 MTA へ戻すとき**（検証が終わったら戻すこと。戻さないと以後の申請が外部へ実送信され続ける）:
**認証基盤には触らない。** `SMTP_HOST` を渡さずに §1 を再実行し（既定が捕捉用 MTA へ戻る）、§4 で
近接 MTA を作り直す。**戻すときも §0 と同じ順序で** —— 先に閉じ、上流を戻し、成立を確かめてから開く。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| ExternalSecret が `Ready` にならない | Vault に値が未投入（§1 未実施）／`eso-read` policy が `secret/msp/keycloak-smtp` を含んでいない | `vault kv get secret/msp/keycloak-smtp`（Vault Pod 内）で値の有無を確認。policy は `secret/msp/*` を許可済みのため通常は該当しない |
| 近接 MTA が起動しない（CrashLoopBackOff） | 上流の宛先が空／STARTTLS の値が `true`・`false` 以外 | `kubectl -n platform-infra logs deploy/mail-relay` の 1 行目が理由を名指しする。**fail-closed は設計である**（黙って平文で外へ出さない） |
| 近接 MTA は起動するが上流で認証エラー（ログに 535） | アプリパスワードの失効・2 段階認証の設定変更 | 組織のメールテナント側でアプリパスワードを再発行し、§1 → §4 を再実行 |
| 申請は 200 だがメールが届かない | 上流が受け取っていない（キューに滞留している） | **これは設計どおりの挙動である** —— 近接 MTA が受理した時点で申請は成功する。滞留は近接 MTA 側で見る（観測の配線は別途） |
| 差出人が拒否される（ログに 554） | 送信元ドメインの制限に外れた差出人 | 認証基盤側の `from` は合成値で固定されている。近接 MTA の許可ドメインの宣言（`deploy/mail-relay/mail-relay.yaml`）と突き合わせる |
| 送信元アドレスが `noreply@` を期待して見える | 個人 Google アカウントを例外的に使っている場合の既知の制約 | メール配信の計画 ADR §結果「受け入れたリスク」参照。組織テナントへの移行までの既知の制約であり、本手順の不具合ではない |

## 記録

- 実施日・実施者・供給元（組織テナントか個人アカウントか）を監査ログ相当の記録
  （`docs/operations/operations.md` の障害対応記録、または部門の変更管理台帳）へ残す。
- **値そのもの（アドレス・パスワード）は記録に含めない。**

## 限界（この手順で担保できないこと）

- **本手順は「値を投入する」ところまでであり、「値が正しく供給され続ける」ことは担保しない。**
  アプリパスワードの失効・組織テナントへの移行は別途の運用判断が要る（メール配信の計画 ADR §結果 フォローアップ参照）。
- **送信失敗の監視（運用ダッシュボード）は本手順の対象外。**
  🔴 **［2026-09-06］観測点が変わったことに注意すること。** 近接 MTA を挟んだので、
  **認証基盤から見た送出は上流が止まっていても成功する**（キューに入る）。認証基盤の監査ログだけを
  見ていると**上流の停止を見逃す**。見るべきはキューの長さ・滞留時間・後送の失敗率であり、
  その配線（収集とダッシュボードの導線）は**本手順にも本リポジトリの現状にも無い**。
  **本手順を実行しても、送信失敗が自動検知されるようにはならない。**
- 🔴 **近接 MTA 自身が落ちている間の窓は閉じていない。** 近接 MTA が居ない・投函を拒む間は、
  実在する利用者名のときだけ 500 が返る。現状の防ぎ方は §0 の**人手の手順**だけであり、
  機械の門は別途実装する。**「窓は無い」と読まないこと。**
- **メールテナント停止時の代替**（管理者による本人確認済みリセット）は
  `UPDATE_PASSWORD` 必須アクションとして realm.json に投入済みであり、**本手順（SMTP そのものの設定）とは
  独立に機能する。** 本手順が失敗していても、代替手段は影響を受けない。
