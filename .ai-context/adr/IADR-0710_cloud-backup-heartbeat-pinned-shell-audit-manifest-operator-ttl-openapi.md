---
title: IADR-0710 クラウドのバックアップの回の結果を外の check へ送る。手で打つ配備の段はシェルごと宛先のクラスタへ固定する。オーナー用の secret_id を期限つきにし期限を警報で見る。平文の age の識別子の既定を enforce にする。監査の記録の目録を日次でノードの外へ送る。LLM ゲートウェイの OpenAPI の文書は開発の環境でだけ出す
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-21, NFR-05, NFR-09, IADR-0675, IADR-0689, IADR-0703, IADR-0705, IADR-0424]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - ../specs/20261108_backup-alerts-audit-offnode-approle-openapi.md
---

# IADR-0710: クラウドの運用の 5 つの硬化（バックアップの外の確かめ・手で打つ段の固定・監査の目録・オーナーの資格の期限・OpenAPI）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-08
- 決定者: 所有者の裁定（2026-11-07「全部直してから稼働」）・claude（方式）

## 起点・関連

- 要求 **NFR-18**（統制）・**NFR-21**（バックアップ）・**NFR-05**（秘匿）・**NFR-09**（認可）。前提: IADR-0675（バックアップ・Discord・heartbeat）・
  IADR-0689（OpenBao・オーナー用の AppRole）・IADR-0703（宛先のクラスタの確かめ・識別子の扱い）・IADR-0705（外向きの中継・API サーバーの監査・期限の測り）・
  IADR-0424（LLM ゲートウェイの REST の門）。
- 作業仕様書: `.ai-context/specs/20261108_backup-alerts-audit-offnode-approle-openapi.md`。

## コンテキストと課題

- バックアップの失敗・未実行は Job の失敗としてしか見えず、気付く手段がオーナーの手の確かめだけだった（kube-state-metrics は無い）。
- 宛先のクラスタの確かめはスクリプトの中にだけ在り、README の手で打つ段は確かめの外だった。
- 監査の記録はノードの上にしか無く、ノードの root を取った者は書き換えられる。
- オーナー用の AppRole の secret_id は期限も回数の上限も無く、平文の age の識別子は既定で警告して進んだ。
- LLM ゲートウェイは環境を問わず認可なしで OpenAPI の文書を出していた。

## 決定

### 決定 1 — バックアップの回の結果を外の check（check C）へ送る

- 本体（`deploy/local/platform-backup/script/backup.sh`。ローカルと共有）は、`BACKUP_HEARTBEAT_URL_FILE` を与えたときだけ、回の後に結果を送る:
  成功は URL、失敗（どの段の失敗も）は URL/fail。止まった（時間切れの TERM）・走らなかった回は送られず、check の期限（Period 1 日・Grace 6 時間）で下がる。
  外の check を使うのは、クラスタの中の警報（kube-state-metrics を足す）では「クラスタごと止まった」を拾えず、heartbeat と同じ外の確かめに揃えるためである。
- URL は Secret `alertmanager-heartbeat` のキー `backup-ping-url`（OpenBao の同じパス。ESO が在るキーを全部写す）。CronJob はそのキーだけを optional でマウントする
  （ほかの heartbeat の URL はバックアップの Pod に置かない）。キーが無ければ警告して送らない（段: オーナーが check を作るまで）。
- 送るのは rclone の `copyurl --stdout`（イメージに既に在る。HTTPS_PROXY に従い外向きの中継を経る）。上限 20 秒。URL はログに出さず、道具の出力は捨てる。
  送れなくても回の終了コードは変えない（送れないことは check の期限が知らせる）。宛先の名前は heartbeat と同じなので、許可表は足さない。
- 最悪の合計（試験 T-2402-12）に送信の上限を足しても、Job の時間切れより小さい。

### 決定 2 — 手で打つ配備の段は、シェルごと宛先のクラスタへ固定する（`ct_pin_shell`）

- `deploy/cloud/lib/target-cluster.sh` に `ct_pin_shell` を足す。確かめ（`ct_require_target` と同じ 3 つ）は子のシェルで行い（対話のシェルを exit で閉じない）、
  合えばシェルの `kubectl`・`helm` を `--context`・`--kube-context` を付けて呼ぶ関数へ替える。違えば 4 を返し、`kubectl`・`helm` を拒む関数へ替える
  （貼り付けた残りの段が別のクラスタへ流れない）。
- README §3 は、手で打つ最初の段より前にこれを流す。`reconcile_recreate_strategy` は `kubectl` を呼ぶので、固定したシェルではその中も固定される。
  ローカルの起動器（`k8s-local-up.sh`）は同じ関数を使い続けるので、関数そのものには確かめを入れない（ローカルの宛先はクラウドの印を持たない）。

### 決定 3 — オーナー用の secret_id を 90 日の期限つきにし、期限を警報で見る

- role の設定と secret_id の作り方を `vault/lib.sh` の `ob_operator_issue` の 1 つにまとめ、`configure.sh`（初回）と `root-token.sh rotate-operator` が使う。
  `rotate-operator` は role の設定を書き直してから作るので、稼働中の構成の期限なしの secret_id も期限つきの 1 つへ替わる。作った secret_id の期限が設定どおりであることを
  Pod の中で確かめる。
- 期限は `OB_OPERATOR_SECRET_ID_TTL_HOURS=2160`（90 日）。approle の認証の口の最長（既定 768 時間）が期限を頭打ちにするので（実物の OpenBao 2.7.1 で実測）、
  口の最長を同じ値にする（トークンは role の `token_max_ttl=1h` のまま）。
- 回数の上限（`secret_id_num_uses`）は置かない。1 回の手順の中で複数回入る（root の作り直しなど）ため、回数で絞ると手順の途中で切れる。期限と、入るたびの短いトークン（15 分）・
  ループバックへの束縛で抑える。
- 🔴 期限が切れるとオーナー用の資格で入れず、root の作り直しもできない（初期化からのやり直しになる）。そのため期限を ConfigMap
  `platform-infra/openbao-operator-credential` のキー `not-after`（作り始めの時刻＋期限。実際より早いか同じ。値ではない）に記録し、`tls-cert-probe` が
  `credential_not_after_seconds` に出し、残り 30 日（warning）・7 日（critical）・記録なし（測りが動いている間。6 時間）で警報を出す。

### 決定 4 — 平文の age の識別子の既定を enforce にする

- `OB_IDENTITY_POLICY` の既定を `warn` から `enforce` へ替える。包む前の一時の手順だけ、`OB_IDENTITY_POLICY=warn` を明示して流せる（拒みの文が案内する）。
  稼働中の通信には関わらない（オーナーの手元の手順だけが止まる）。

### 決定 5 — 監査の記録の目録を日次でノードの外へ送る

- `deploy/cloud/node/audit-offsite.sh` が、API サーバーの監査（`/var/lib/rancher/k3s/server/logs/audit*.log*`）と OpenBao の監査（Pod のログ
  `/var/log/pods/openbao_openbao-*/openbao/*.log*`）の目録（ファイルごとの大きさと、先頭から大きさまでの sha256）を作り、外の check（check D）の ping の本文として送る。
  目録は前の回の目録の sha256 を持つ（鎖）。外の check の履歴が外の写しになり、ノードの root を取った者は消せない。
- 記録は追記と世代交代（改名）でしか変わらないので、前の目録の各行は今のどれかのファイルの先頭と同じ sha256 を持つ。`verify` がこれを突き合わせ、書き換え・削除を NG にする。
- 中身は送らない（送るのは名前・大きさ・sha256）。URL はノードの 0600 のファイルにだけ在り、curl へは標準入力の設定（`-K -`）で渡す（引数に載せない）。
  systemd の timer（日次・Persistent）が流し、暗号化した領域のマウントを条件にする。

### 決定 6 — LLM ゲートウェイの OpenAPI の文書は開発の環境でだけ出す

- `app.MapOpenApi()` を `app.Environment.IsDevelopment()` のときだけ張る。本番（Production。クラウド・k8s・compose の既定）と試験の器（Testing）では口が無い。
  `ServiceCaller` を付ける案は採らない（実行時に文書を読む呼び出し元が無く、口そのものを無くすほうが狭い）。

## 結果

- 試験: `backup.test.sh`（T-0710-01〜07）・`cluster-target.test.sh`（`ct_pin_shell`・README の順・識別子の既定）・`openbao-scripts.test.sh`（実物の OpenBao で期限）・
  `probe.test.js`（C6）・`deploy-cloud-egress-audit.test.js`（C4。promtool）・`audit-offsite.test.sh`（新規。CI の「Test cloud owner procedures」の段へ足した）・
  `OpenApiExposureTests`（LLM ゲートウェイ）。
- 手順書: `cloud-backup-alerts-heartbeat-runbook.md` §4（check C）・`cloud-apiserver-audit-log-runbook.md` §4（check D と突き合わせ）・
  `cloud-openbao-init-unseal-runbook.md` §6（定期の rotate-operator・期限の警報・識別子の既定）・`cloud-owner-target-cluster-runbook.md` §3（`ct_pin_shell`）。

## 残る制約

1. 監査の記録そのものの写しは外に置かない（目録だけ）。消された記録の中身は戻らない（消されたことは分かる）。中身の写しは、ノードに保管先の資格を置くことになるので採らない。
2. 目録の外の写しの長さは外の check の履歴の保持（無料の枠の件数）に依る。
3. `ct_pin_shell` の固定はそのシェルだけに効く。新しいシェルで最初の行を飛ばせば、確かめの外で打てる（README と手順書が最初の行から流すことを求める）。
4. 期限が切れた後の回復の手順は持たない（初期化からのやり直し）。警報（30 日・7 日）と定期の作り直しで切らさない。
5. 外向きの中継の許可表を送り元ごとに分けるときは、バックアップの Pod の送り元に heartbeat の宛先の名前を許す必要がある。
