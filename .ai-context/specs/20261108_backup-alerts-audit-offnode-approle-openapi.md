---
title: 作業仕様書 — クラウドのバックアップの外の生存の確かめ・手で打つ配備の段の宛先の固定・監査の記録の目録のノードの外への送り・オーナー用の資格の期限と識別子の既定・LLM ゲートウェイの OpenAPI の文書を本番で出さない
type: spec
status: done
related_ids: [NFR-18, NFR-21, NFR-05, NFR-09, IADR-0710, IADR-0675, IADR-0689, IADR-0703, IADR-0705]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - 20261105_2402_cloud-backup-alerts-heartbeat.md
  - 20261107_backup-owner-ops-hardening.md
  - 20261107_cloud-egress-audit-cert-expiry.md
---

# 作業仕様書 — クラウドの運用の 5 つの硬化

> 着手 2026-11-08（develop `3ba6611f1`）。判断の記録は IADR-0710。稼働中のクラスタには何もしない。秘密の値は扱わない
> （外の check の URL・資格の作り直しは、オーナーが手元とノードで流す）。

## 起点

利用者の裁定（2026-11-07）「全部直してから稼働」。クラウドの最小構成（`deploy/cloud/`）と LLM ゲートウェイについて、次の 5 点を硬くする（どれも重大度は低い）。

1. 日次のバックアップが失敗しても・走らなくても、警報も外の生存の確かめも鳴らない。
2. 配備の手順（`deploy/cloud/README.md` §3）で手で打つ `kubectl`・`helm` が、宛先のクラスタの確かめの外にある。
3. API サーバーと OpenBao の監査の記録が、ノードの上にしか無い。
4. オーナー用の AppRole の secret_id に期限が無い。平文の age の識別子の既定が「警告して進む」。
5. LLM ゲートウェイが本番でも OpenAPI の文書を認証なしで出す。

## 現状（着手前に develop で確かめたもの）

| 項目 | develop の状態 | 根拠 |
| --- | --- | --- |
| 1 | 残る | `deploy/cloud/backup/cronjob-postgres.yaml` は成功の印を外へ送らない。`observability/prometheus.yaml` にバックアップの規則が無く、kube-state-metrics も無い |
| 2 | 残る | README §3 の `kubectl apply`・`helm upgrade`・`kubectl wait` は素で打つ。`ct_require_target` はスクリプトの中にだけ在る。`scripts/lib/recreate-strategy.sh` は素の `kubectl` を呼ぶ |
| 3 | 残る | `k3s/config.yaml.d/20-apiserver-audit.yaml` はノードの `/var/lib/rancher/k3s/server/logs/`、`vault/openbao.yaml` の監査は stdout（ノードの `/var/log/pods`） |
| 4 | 残る | `vault/configure.sh` の role は `secret_id_ttl=0 secret_id_num_uses=0`。`vault/lib.sh` の `OB_IDENTITY_POLICY` の既定は `warn` |
| 5 | 残る | `src/platform/backend/Services/LlmGateway/Program.cs` の `app.MapOpenApi()` は環境を問わず認可なし |

## 母集合（規則 9・10。誤りの側の文字列で走査した）

- 1: `git grep -n 'COMPLETE\|heartbeat' -- deploy/cloud deploy/local/platform-backup docs/operations` → バックアップの本体は `deploy/local/platform-backup/script/backup.sh`
  （ローカルと共有）、クラウドの CronJob は `cloud/backup/cronjob-postgres.yaml` の 1 つ。heartbeat の URL は Secret `alertmanager-heartbeat`（ESO の `dataFrom.extract`。
  在るキーを全部写す）。宛先の名前は外向きの中継の許可表 `egress-proxy-extra-hosts` に在る。［2026-11-08 追記］表は送り元ごとの行なので、送り元 `backup` の行が要る（`cloud-egress-proxy-runbook.md` 1-1）。
- 2: `git grep -n '^kubectl\|^helm\|reconcile_recreate_strategy' -- deploy/cloud/README.md docs/operations` → README §3 の段と、PSA の確かめの 2 行。
  `reconcile_recreate_strategy` の呼び手は README §3 と `scripts/k8s-local-up.sh`（ローカル。変えない）。
- 3: `git grep -n 'audit-log-path\|audit "file"' -- deploy/cloud` → 上の 2 つだけ。
- 4: `git grep -n 'secret_id_ttl\|OB_IDENTITY_POLICY' -- deploy docs scripts` → `configure.sh`（role の作成）・`root-token.sh`（rotate-operator は role を書かない）・
  `lib.sh`・`cluster-target.test.sh`・`openbao-scripts.test.sh`（平文の使い捨ての識別子で流す）・手順書 `cloud-openbao-init-unseal-runbook.md`。
- 5: `git grep -n 'MapOpenApi'` → 13 のサービス。クラウドへ配備するのは LLM ゲートウェイだけ（`values-cloud.yaml`）。本作業は指摘のとおりゲートウェイだけを替える
  （ほかのサービスはクラウドに無い）。実行時に `/openapi/v1.json` を読む呼び出し元は無い（`git grep -n '/openapi'`。エッジの試験はエッジの面の話）。

## 方針（決定は IADR-0710）

1. **バックアップの回の結果を外の check（Healthchecks.io の check C。Period 1 日・Grace 6 時間）へ送る**。成功は URL、失敗は URL/fail。止まった・走らなかった回は
   送られず期限で下がる。URL は Secret `alertmanager-heartbeat` のキー `backup-ping-url` をそのキーだけ optional でマウントして渡す。キーが無ければ警告して送らない（段）。
   URL はログに出さず、送る道具（rclone の copyurl。HTTPS_PROXY に従う）の出力は捨てる。送れなくても回の終了コードは変えない。
2. **手で打つ段は、同じシェルで `ct_pin_shell` を流してから打つ**。合えばシェルの `kubectl`・`helm` を確かめた context に固定する関数に替え、違えば 4 を返して
   シェルの `kubectl`・`helm` を拒む関数に替える（シェルは閉じない）。`reconcile_recreate_strategy` は `kubectl` を呼ぶので、固定したシェルではその中も固定される
   （ローカルの起動器の使い方は変えない）。
3. **オーナー用の secret_id を 90 日の期限つきにする**。role の設定と作り方を `lib.sh` の 1 つ（`ob_operator_issue`）にまとめ、`configure.sh` と `rotate-operator` が使う
   （稼働中の構成の期限なしの secret_id は、`rotate-operator` が期限つきへ替える）。認証の口の最長（既定 768 時間）が期限を頭打ちにするので、口の最長も合わせる。
   期限を ConfigMap `platform-infra/openbao-operator-credential` に記録し、`tls-cert-probe` が系列に出し、30 日・7 日・記録なしで警報を出す。
   回数の上限は置かない（1 回の手順の中で数回入るため）。
4. **平文の age の識別子の既定を enforce にする**。包む前の一時の手順だけ `OB_IDENTITY_POLICY=warn` を明示して流せる（拒みの文が案内する）。
5. **監査の記録の目録（ファイルごとの大きさと先頭の sha256。前の回の目録の sha256 で鎖）を、ノードの systemd の timer で日次に外の check（check D）の本文として送る**。
   中身は送らない。URL はノードの 0600 のファイルにだけ在り、curl へは標準入力の設定で渡す。外の写しと今のファイルの先頭を突き合わせる `verify` を持つ。
6. **LLM ゲートウェイの OpenAPI の文書は Development でだけ出す**（Production・Testing では口を張らない）。

## 受け入れ基準と試験

| # | 受け入れ基準 | 試験（陰性対照） |
| --- | --- | --- |
| AC1 | 成功の回は URL へ 1 回、失敗の回（保管先へ送れない・受取人が無い）は URL/fail へ 1 回送る。URL をログに出さない。送れなくても成功は成功。URL が無い・空・http なら警告して送らない。URL のファイルを与えなければ何もしない | `backup.test.sh` T-0710-01〜06（本体の送信の呼び出しを外すと 8 件が赤） |
| AC2 | クラウドの CronJob は `backup-ping-url` だけを optional でマウントし、そのパスを渡す。最悪の合計は送信の上限を入れても時間切れより小さい | `backup.test.sh` T-0710-07・T-2402-12・T-2402-16 |
| AC3 | `ct_pin_shell` は、合えば手で打つ `kubectl`（パイプ・source した関数の中も）・`helm` を確かめた context に固定し、違えば（別のクラスタ・トンネルでない・context が無い）4 を返してシェルを閉じず、以後の `kubectl`・`helm` を拒む。README §3 は手で打つ最初の段より前に流す | `cluster-target.test.sh`（固定を外す・拒みを外すと赤。README の行を消すと赤） |
| AC4 | `configure.sh` と `rotate-operator` が作る secret_id の期限は 2160 時間で、role の設定も同じ。期限なしの secret_id は `rotate-operator` の後に残らない。期限を ConfigMap に記録する | `openbao-scripts.test.sh`（実物の OpenBao 2.7.1。口の最長を合わせないと 768 時間で頭打ちになり赤になることを確かめた） |
| AC5 | 記録の期限を `credential_not_after_seconds` に出し、形の違う記録は系列を作らない。警報は残り 30 日・7 日・記録なし（測りが動いている間）で鳴り、それ以外で鳴らない | `probe.test.js` C6・`deploy-cloud-egress-audit.test.js` C4（promtool。期限を閾値より短くする・測りの条件を外すと赤） |
| AC6 | 平文の識別子は既定で止まり（kubectl を使わない）、warn を明示したときだけ警告して進む | `cluster-target.test.sh`（識別子の節） |
| AC7 | 目録は種類ごとの大きさと先頭の sha256 を持ち、中身を持たない。送るとき URL は引数にも出力にも出ない。URL が無い・0600 でない・https でなければ送らない。前の回の sha256 で鎖になる。verify は追記・世代交代の後も前の行を見つけ、書き換え・削除を NG にする | `audit-offsite.test.sh`（書き換え・削除が陰性対照） |
| AC8 | LLM ゲートウェイの `/openapi/v1.json` は Production・Staging・Testing で 404（資格情報があっても）、Development で 200 | `OpenApiExposureTests`（Program.cs の条件を外すと 3 件が赤） |

## 利用者の作業（リポジトリでは直せない）

- check C・check D を Healthchecks.io に作り、URL を入れる（`cloud-backup-alerts-heartbeat-runbook.md` §4・`cloud-apiserver-audit-log-runbook.md` §4）。
- 稼働中の OpenBao で `root-token.sh … rotate-operator` を 1 回流し、期限つきの資格へ替える（`cloud-openbao-init-unseal-runbook.md` §6）。以後は 90 日の前に流す。
- age の識別子を包む（同手順書「識別子を守る」）。包むまでは `OB_IDENTITY_POLICY=warn` を明示しないと手順が止まる。

## 残すもの

- 監査の記録そのものの写しはノードの外に置かない（目録だけ）。中身の写し（バックアップと同じ age の暗号化・Object Lock の保管先）は、ノードに保管先の資格を置くことになるので、
  本作業では採らない（IADR-0710 の残る制約）。
- 外向きの中継の許可表は送り元ごとの行なので、送り元 `backup` に heartbeat の宛先の名前を許す行を、オーナーが表に足す（利用者の作業。`cloud-egress-proxy-runbook.md` 1-1）。
