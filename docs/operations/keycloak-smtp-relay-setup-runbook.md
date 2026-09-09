---
title: 運用 Runbook — Keycloak smtpServer（SMTP リレー）の設定
type: runbook
status: draft
created: 2026-08-23
updated: 2026-09-09
author: claude
---
<!-- trace:
ids: [SC-10, SC-15, FR-05, FR-09, FR-22]
adrs: [ADR-0006, ADR-0026, ADR-0045, ADR-0078]
iadrs: [IADR-0197, IADR-0261, IADR-0329, IADR-0332, IADR-0344, IADR-0347, IADR-0369, IADR-0404, IADR-0421]
specs: [20260823_issue-438_keycloak-theme-and-smtp, 20260831_issue-1102_keycloak-smtp-externalsecret-wiring, 20260902_issue-1144_dev-mail-capture-mta, 20260902_issue-1143_reset-existence-concealment, 20260906_issue-1245_nearby-mta-relay, 20260907_issue-1245_reset-gate, 20260909_issue-1245_mail-relay-observation]
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
> ~~🔴 **残るのは §0 の「先に申請を閉じる」だけ**である。~~
> **［2026-09-07 更新］§0 も人手ではなくなった。** 常駐の門が近接 MTA へ投函できない状態を検知して
> 申請を自動で閉じ、回復したら宣言どおりに戻す。**運用者が realm を触る場面は、門自身が止まっている
> ときだけ**になった（§0）。🔴 **窓は縮んだが閉じてはいない** —— 検知して閉じるまでの間は残る。

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

### 0. 申請を閉じるのは**機械**である（人手は門が止まっているときの予備）

**近接 MTA を止める・作り直す間、パスワードリセットの申請を開いたままにしてはならない。**
認証基盤から見た送出先が使えないと**実在する利用者名のときだけ 500** が返り、実在しない利用者名は 200 を返す ——
**その差だけで利用者名を 1 リクエストずつ列挙できる**（稼働環境で実測済み）。
閉じてしまえば、実在／非実在のどちらにも**同じ 400 と同じ本文**が返る（実測済み）。

> **［2026-09-07 更新］この手順は人手から機械へ移った。** `platform-infra` の常駐の門（`reset-gate`）が
> 近接 MTA へ**本物の送信取引**を周期的に打ち、**投函できなければ申請を自動で閉じる**。
> 回復すると**連続した成功のあとに宣言どおりへ戻す**。
> **したがって §4 の再起動でも、近接 MTA の停止でも、運用者が手で閉じる必要は無い。**
>
> **人手のコマンド（下）を打つのは次の 2 つだけである。**
>   1. **門自身が動いていない**とき（Pod が居ない・資格情報が失効して 401 を打ち続けている）。
>   2. **門より早く閉じたい**とき（門の周期を待たずに、その瞬間に閉じたい）。
>
> 🔴 **窓は縮んだだけで、閉じてはいない。** 門が検知して閉じるまでの間（プローブの周期 ＋
> プローブのタイムアウト ＋ 反映の往復）は、実在する利用者名だけが 500 になる。
> **「窓は無い」と読まないこと。**

門の状態は稼働 realm の属性に残る（`reset-gate.state` が `closed` なら門が閉じている）。
状態と理由は次で読める。

```sh
kubectl -n platform-infra get deploy reset-gate                 # 門が居るか
kubectl -n platform-infra logs deploy/reset-gate --tail=20      # close / reopen とその理由
```

> 🔴 **`check-password-reset-mail.js` は門が閉じていると赤になる。** 存在秘匿としては健全だが、
> **近接 MTA へ投函できていない**という意味だからである（門は投函できないときにしか閉じない）。
> 門が閉じていることを**期待する**実行だけが `EXPECT_GATE_CLOSED=1` を立てる。

**人手で閉じる（上の 1・2 のときだけ）**:

```sh
KC_POD=$(kubectl -n platform-infra get pod -l app=keycloak -o jsonpath='{.items[0].metadata.name}')
kubectl -n platform-infra exec -i "$KC_POD" -- sh -c   '/opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master      --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null    && /opt/keycloak/bin/kcadm.sh update realms/platform -s "resetPasswordAllowed=false"'
```

> 🔴 **人手で閉じた状態を門は開け直さない。** 門が開けるのは「**門自身が閉じた**」と記録が残っている
> ときだけである（realm の属性を見て判断する）。上のコマンドで閉じたら、**開けるのも人手**である
> （`resetPasswordAllowed=true` を同じ形で打つ）。realm の後追いの差分適用も、門の記録が無い閉鎖は
> 宣言へ戻す —— つまり**次の後追いで開く**ことがある。意図して閉じたままにしたいなら、
> **宣言（realm JSON）側を `false` にする**こと（門も後追いも、宣言より開く側へは動かない）。


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
| 申請は 200 だがメールが届かない | 上流が受け取っていない（キューに滞留している） | **これは設計どおりの挙動である** —— 近接 MTA が受理した時点で申請は成功する。滞留は近接 MTA 側で見る（§キューの観測） |
| 差出人が拒否される（ログに 554） | 送信元ドメインの制限に外れた差出人 | 認証基盤側の `from` は合成値で固定されている。近接 MTA の許可ドメインの宣言（`deploy/mail-relay/mail-relay.yaml`）と突き合わせる |
| 送信元アドレスが `noreply@` を期待して見える | 個人 Google アカウントを例外的に使っている場合の既知の制約 | メール配信の計画 ADR §結果「受け入れたリスク」参照。組織テナントへの移行までの既知の制約であり、本手順の不具合ではない |

## キューの観測（上流が止まっているかを、ここでしか見られない）

🔴 **近接 MTA を挟んだ以後、上流が止まっていても認証基盤から見た送出は成功する。**
**認証基盤の監査ログだけを見ていると、上流の停止に気付けない。** 見るのはキューである。

**［2026-09-09 追加］キューの観測は配備済みである。** 近接 MTA の Pod にサイドカーが同居し、
キュー長と滞留時間を可観測性コレクタ経由で時系列基盤へ流す。
**指標の定義・アラート・限界は[近接 MTA のキューの可観測性仕様書](../observability/mail-relay-queue-metrics.md)が正本である。**

### 何を見るか

| 見るもの | 正常 | 異常のとき何が起きているか |
| --- | --- | --- |
| `deferred` のキュー長 | **0** | 0 でない間、**メールは届いていない**（1 度以上失敗して再送待ち）。上流の停止・認証失敗・宛先拒否 |
| `deferred` の最古メッセージの齢 | 0 | **1800 秒（30 分）で破棄される。** 破棄されたメールは**跡形も残らない** |
| 全キューを読めたか（`postfix_up`） | **1** | 0 は「キューが空」ではなく**測れていない**。サイドカーか spool の異常 |

**時系列は Grafana の Platform Overview（下段）で見る。** キューが 0 でない状態が 5 分続けば
アラートが鳴り、20 分を超えると深刻度が上がる（**破棄の 5 分前**）。

🔴 **しきい値は実測前の暫定値である。** 稼働環境で測って見直すこと（計画が「実測してから定める」と定めている）。

### キューを直接読む（Grafana が見られないとき）

```sh
kubectl -n platform-infra exec deploy/mail-relay -c postfix -- postqueue -p | tail -5
kubectl -n platform-infra logs deploy/mail-relay -c postfix --tail=50 | grep -i 'status=deferred\|status=bounced'
kubectl -n platform-infra port-forward deploy/mail-relay 9154:9154   # → http://localhost:9154/metrics
```

> `status=deferred` の行に**上流が返した理由**（接続拒否・535 認証失敗・554 差出人拒否など）が出る。
> **キューの数字は「失敗していること」しか言わない。理由はログにしかない。**

### 滞留を解消する

1. **原因を先に直す**（§失敗したときの分岐）。上流が復旧すれば、後送は自動で行われる。
2. 復旧を確かめてから、**再送を今すぐ試みる**（次の再送を待たない）:

   ```sh
   kubectl -n platform-infra exec deploy/mail-relay -c postfix -- postqueue -f
   ```

3. 🔴 **キューを捨てる（`postsuper -d`）のは最後の手段である。** 捨てたメールは届かず、
   利用者からは「リセットメールが来ない」としか見えない。捨てる前に上を試すこと。

> 🔴 **キューは Pod の再作成で失われる**（永続化していない。載るのは寿命 30 分のメールだけである）。
> **近接 MTA を作り直すと、滞留していたメールは消える。**

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
  見ていると**上流の停止を見逃す**。見るべきはキューの長さ・滞留時間・後送の失敗率である。
  ~~その配線（収集とダッシュボードの導線）は**本手順にも本リポジトリの現状にも無い**。~~
  **［2026-09-09 更新］配線は入った**（§キューの観測）。**ただし本手順を実行することとは無関係に働く** ——
  観測は近接 MTA の配備に付いてくるものであり、本手順（上流の値の投入）が有効にするものではない。
  🔴 **「後送の失敗率」は率として測れていない。** 破棄されたメールはキューから消え、事後に数えられない
  （代わりに**破棄される前**に鳴らしている）。詳細と限界は可観測性仕様書が持つ。
  🔴 **しきい値は実測前の暫定値である。**
- 🔴 **近接 MTA 自身が落ちている間の窓は閉じていない。** 近接 MTA が居ない・投函を拒む間は、
  実在する利用者名のときだけ 500 が返る。~~現状の防ぎ方は §0 の**人手の手順**だけであり、
  機械の門は別途実装する。~~ **［2026-09-09 更新］機械の門は §0 のとおり配備済みで、窓は
  「プローブ周期 ＋ タイムアウト ＋ 反映の往復」まで縮んだ。** **「窓は無い」と読まないこと。**
  🔴 **門自身が落ちている間は窓が開いたままであり、その不在を鳴らす計器はまだ無い**（残件）。
- **メールテナント停止時の代替**（管理者による本人確認済みリセット）は
  `UPDATE_PASSWORD` 必須アクションとして realm.json に投入済みであり、**本手順（SMTP そのものの設定）とは
  独立に機能する。** 本手順が失敗していても、代替手段は影響を受けない。
