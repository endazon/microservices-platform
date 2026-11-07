---
title: IADR-0704 クラウドの基盤の外向きの通信を名前の許可表を持つ中継 1 つに集め、master realm の一時管理者を要る間だけ置き、ESO の名前空間を restricted と到達の制限で先に作り、k3s の API サーバーの監査の記録を足し、サーバ証明書の期限を受け口で測って警報にする
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-16, NFR-21, NFR-05, IADR-0674, IADR-0675, IADR-0689, IADR-0693, IADR-0696, IADR-0697, IADR-0700, IADR-0703]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_cloud-egress-audit-cert-expiry.md
---

# IADR-0704: クラウドの外向きの中継・一時管理者・ESO の名前空間・API の監査・証明書の期限

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定（クラウドの最小構成の稼働前の硬化の 1 組）。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「稼働の前に low もすべて直す」）・統括の指示（外向きの宛先は段階的に・宛先の値が設定に無いものは利用者が埋める・
  監査の記録は k3s の起動の引数で・証明書は OpenBao の CA の作りに揃える）・claude（方式）

## 起点・関連

- 要求 **NFR-18**（統制）・**NFR-16**（通信の暗号化）・**NFR-21**（運用の監視）・**NFR-05**（秘匿は OpenBao）。
- 前提: IADR-0674（クラウドの最小構成）、IADR-0675（警報と heartbeat）、IADR-0689（OpenBao・ESO）、IADR-0693（Keycloak の TLS・入りの名指し）、
  IADR-0696（OpenBao の受け口の TLS。CA 2 年と受け口 1 年を同じ回に作る）、IADR-0697（基盤の Pod の最小権限）、IADR-0700（稼働中の realm へ足す Job）。
- 閉じる残余: IADR-0689 残余 4（ESO の名前空間に NetworkPolicy が無い）、IADR-0693 残余 1・IADR-0696 残余 2（証明書の期限の警報が無い）、
  IADR-0696 残余 3（IADR-0689 残余 4 と同じ）。改めた旨はそれぞれに日付つきで追記した。
- 作業仕様書: `.ai-context/specs/20261107_cloud-egress-audit-cert-expiry.md`（現状・母集合・実測）。

## コンテキストと課題

1. クラスタの外の 443 へ出る Pod（LLM ゲートウェイ・バックアップ・Alertmanager）は、私設の範囲を除く全ての宛先へ出られる（NetworkPolicy は名前を名指せない）。
2. Keycloak の Deployment は常に一時管理者の env を持ち、master realm の一時管理者が残る。master realm の総当たりの検知は既定の無効。
3. ESO の名前空間は helm が作り（`--create-namespace`）、Pod Security のラベルも NetworkPolicy も無い。k3s の API サーバーは監査の記録を残さない。
4. サーバ証明書（Postgres・RabbitMQ・Keycloak・LLM ゲートウェイ）と OpenBao の受け口の証明書の期限の警報が無い。ESO は半年ごとに作り直すが、
   消費側（Postgres・RabbitMQ・LLM ゲートウェイ）は作り直すまで古い証明書を出し続ける。

## 決定

### 決定 1 — クラスタの外の 443 へ出るのは、名前の許可表を持つ中継 1 つだけにする（段: report → enforce）

- 中継 `egress-proxy`（`deploy/cloud/egress/`。Node 標準だけの CONNECT の中継。既存の `node:22-alpine` の digest）を `platform-infra` に置く。
  受けるのは `CONNECT <名前>:443` だけ。IP の字面・443 でないポート・ほかの方法は拒む。TLS は端から端まで（中継は中身を読まない）。
  名前を引いた先が私設・ループバック・リンクローカル・CGNAT の範囲なら、どちらの段でも拒む。
- 許可表は 3 つの和（最後に読んでから 30 秒を過ぎた後の要求で読み直す）: ① 宣言（設定に在る宛先: LLM の提供元〔appsettings の BaseUrl・Anthropic の SDK の既定〕と Discord〔受信先の形〕）、
  ② バックアップの保管先（オーナーが作る ConfigMap `platform-backup-s3-target` の `endpoint` の名前）、③ オーナーが埋める表（ConfigMap `egress-proxy-extra-hosts`。
  heartbeat の宛先など、値が Secret の中にあって設定に無いもの）。
- 3 つの Pod は中継を使う（LLM ゲートウェイ・バックアップは `HTTPS_PROXY`、Alertmanager は `global.http_config.proxy_url`）。出の NetworkPolicy は外の 443 を
  外し、中継の 3128 だけを許す。`ipBlock` で外の範囲を名指す出は中継の Pod だけが持つ（試験が描画で固定する。宛先を問わないポートだけの出は残余 5）。
- 段: 最終の段（`deploy/cloud`）は enforce。稼働中の構成へ入れる間は重ね `deploy/cloud-transition/egress-proxy-report`（中継は report・3 つの直の 443 を戻す）を経る。
  report の行で表の不足を埋めてから最終の段を当てる。重ねが 14 日を過ぎて残れば警報 `EgressProxyReportMode`、enforce で拒めば `EgressProxyDenied`。
- ［2026-11-07 追記］`EgressProxyReportMode` は `for: 14d` で数えない。Prometheus（emptyDir・保持 7 日）か中継が 14 日の間に再起動すると数え直しになり、
  移す段が残ったまま鳴らない。report の段に入った日時をオーナーが ConfigMap `egress-proxy-report-since`（宣言の外。在れば書き直さない）に書き、
  中継が report の段のときだけ `egress_proxy_report_since_timestamp_seconds` に出す。式は「enforce でない かつ（今 − 日時 > 14 日 か、日時の系列が無い）」、
  `for: 1h`。日時が無い・形が違う・1 日より先なら中継は 0 を出す（鳴る側）。`promtool test rules` で、14 日を越える／越えない／日時が無い／enforce／
  Prometheus の再起動の後、を固定する（前の形へ戻すと赤）。

採った理由: NetworkPolicy は名前を名指せない。中継の 1 か所に宛先の名前を集めれば、持ち出しの宛先が表に閉じ、表の外への試みが記録と警報になる。
中継は既存のイメージと標準ライブラリだけで書けるので、新しい外部のイメージ（digest の固定・脆弱性の追随）を増やさない。

採らなかった:
- **Squid などの既製の中継**: 新しいイメージが増える。要るのは CONNECT と名前の完全一致だけで、既製品の設定の面（キャッシュ・認証・ACL の言語）の方が大きい。
- **CNI の FQDN の NetworkPolicy（Cilium など）**: k3s の既定の CNI（flannel）と NetworkPolicy の実装を替えることになり、単一ノードの最小構成の範囲を越える。
- **受容のまま記録だけする**: 所有者の裁定は稼働の前に直すこと。

OpenBao の出（API サーバーの TokenReview。443・6443 のポートだけ）は中継を通さない。宛先はクラスタの外ではなく、API サーバーの IP をノードのループバックで
名指せない事情（IADR-0689 残余 2）は変わらない（受容のまま）。ESO の名前空間の API サーバー向けの出も同じ形（決定 3）で、
どちらも宛先を問わず 443・6443 へ出られる（残余 5）。

### 決定 2 — master realm の一時管理者は、要る間だけ置く。master realm の総当たりの検知を有効にする

- `infra/keycloak.yaml` は `KC_BOOTSTRAP_ADMIN_*` を持たない（realm の取り込みに管理者は要らない）。
- 稼働中の realm へ手を入れるときだけ `deploy/cloud/keycloak-admin/keycloak-admin.sh` で開く（`kubectl set env --from=secret/keycloak-admin` で Secret への参照を足して
  作り直す → master に管理者が居なければ Keycloak が作る → Job が総当たりの検知を有効にする）。作業の後に閉じる（Job が一時管理者を消し、その資格でトークンを
  取れないことを確かめる → env を外して作り直す → 在らないことを確かめる）。閉じる段は始めに env と一時管理者の有無を読み、在る分だけ消すので、
  途中で落ちても流し直せる（一時管理者が既に無いことでは止まらず、有無を確かめられないときだけ止まる）。`check` は閉じ忘れを 3、確かめられないことを 1 で知らせる。
- master realm の総当たりの検知は platform realm の宣言と同じ値（試験が突き合わせる）。
- ［2026-11-07 追記］`keycloak-admin.sh` は、ほかのオーナーの手順と同じく `deploy/cloud/lib/target-cluster.sh`（IADR-0703 決定 2）で宛先のクラスタを確かめてから
  kubectl を使い、以後は確かめた context に固定する（`cluster-target.test.sh` の入口に足した）。
- 管理の面へ届く Pod の範囲（取引ユニットの 11 の Pod・LLM ゲートウェイ）は変えない。管理者が在らない間は、届いても認証が通る資格が無い。

採らなかった: **常設の管理者を別に作る**（資格が常に在る形は変わらない）。**管理の API の機能を切る**（稼働中の realm へ足す Job が使えなくなる）。

### 決定 3 — ESO の名前空間は、chart より先に restricted と到達の制限つきで宣言で作る

- `deploy/cloud/external-secrets/`（Namespace と NetworkPolicy）。PSA は enforce・warn・audit とも restricted。入り・出とも既定で拒否し、出は名前解決・OpenBao の
  受け口（8443）・API サーバー（443・6443。ポートだけ。OpenBao の出と同じ形）、入りは webhook（10250。呼ぶのは API サーバーで送り元を名指せない）だけ。
- 手順書は `--create-namespace` を使わない。chart 2.8.0 の 3 つの Pod が restricted を満たすことは、固定の tgz を描いて試験が確かめる。
- chart 2.8.0 は PushSecret を処理しないなら ClusterPushSecret も処理しないと明示することを求める（無いと描画で止まる）ので、values に足した。

ESO の ClusterRole（全名前空間の Secret を書ける）は変えない（chart の名前空間を絞る口は 1 つの名前空間だけで、基盤・MSP・取引ユニットの 3 つの名前空間に要る）。§残余 2。

### 決定 4 — k3s の API サーバーの監査の記録を、起動の引数（設定の追加）で足し、暗号化した領域に置く

- `deploy/cloud/k3s/config.yaml.d/20-apiserver-audit.yaml`（`kube-apiserver-arg+`）を `/etc/rancher/k3s/config.yaml.d/` に置く。k3s は起動の引数と同じに読む。
  方針 `deploy/cloud/k3s/audit-policy.yaml` と記録は `/var/lib/rancher` の下（LUKS）。保持は 30 日・10 本・100MB。
- 方針は Metadata まで（要求・応答の本文を残さない＝Secret の値を記録に載せない）。Secret・ServiceAccount のトークン・TokenReview・exec / attach / port-forward・RBAC は
  雑音の除外より先に必ず残す。最後は「ほかは Metadata」。
- 新しく作るノードは README §1-4 がインストーラ（k3s を起こす）の前に置く。稼働中のノードはオーナーが手順書で足す（リポジトリからは触らない）。
  `INSTALL_K3S_EXEC` の旗は変えない（既存の試験の固定のまま）。

### 決定 5 — サーバ証明書の期限は、受け口が出している証明書を測って警報にする。OpenBao の受け口は CA から導く

- 測り `tls-cert-probe`（`deploy/cloud/tls-cert-probe/`。Node 標準だけ）が 10 分ごとに 4 つの受け口へ TLS で繋ぎ（Postgres は SSLRequest の後）、出している証明書の
  検証を切らずに（`rejectUnauthorized: true`・基盤の CA・宛先の名前）握手し、通ったときだけ検証を通った証明書の notAfter を出す。握手が検証で落ちたら、その種類
  （`CERT_HAS_EXPIRED`・`UNABLE_TO_VERIFY_LEAF_SIGNATURE`・`ERR_TLS_CERT_ALTNAME_INVALID` など）を `tls_probe_verify_error` に出す。期限切れは notAfter を
  測った時刻（残り 0）とみなし、危急の警報を鳴らす。検証を切った接続で証明書を読むことはしない（期限切れ以外の検証の失敗では残りの日数は出ず、
  `TlsServerCertNotVerified` だけが鳴る）。Secret を読まない（鍵に触れない）。宛先は PKI の role（`lib.sh` の `OB_PKI_ROLES`）と同じ 4 つで、
  名前は ESO の証明書の common_name と同じ（試験が突き合わせる）。
- 受け口を測る理由: Secret を読むと、消費側の作り直しの漏れ（README §6）が見えない。受け口を測れば、ESO の止まり（封印のまま）・発行の失敗・作り直しの漏れの
  どれでも、出している証明書の残りの減りとして見える。
- 警報（Prometheus の `cloud-tls-expiry`）: 残り 150 日で warning・30 日で critical（作り直しの直前の残り 182 日より小さく、平時に鳴らない）。鎖・名前の不一致と
  届かないこと、測れている受け口が 4 に足りないこと、CA の ConfigMap が読めないことも鳴らす。基盤の CA（10 年）は残り 400 日（1 年の証明書が CA を越えない）。
- OpenBao の受け口は ESO の controller だけが届く（IADR-0689 決定 3）ので測りに行かない。IADR-0696 の `tls-cert.sh` は CA（2 年）と受け口の証明書（1 年）を
  同じ回に作るので、受け口の期限を ConfigMap `openbao-ca` の最新の CA の notAfter − 365 日で導く（日数は試験が `tls-cert.sh` と突き合わせる）。
- 測りへの入りは Prometheus の 9465 だけ。受け口の側の入りは別宣言（測りの Pod から各 TLS の口だけ）で、既存の送り元の一覧は書き換えない。

## 結果

- 良い影響: 侵害された Pod から外への持ち出しの宛先が許可表に閉じ、表の外への試みが記録と警報になる。一時管理者の資格が普段は使えない。ESO の Pod の侵害から
  広がる先が OpenBao と API サーバーに絞られ、API サーバーの操作の追跡ができる。証明書の期限切れの前に警報が出る。
- 悪い影響: 中継が止まると外への通信が全部止まる（警報 `EgressProxyDown`・heartbeat の停止で気付く）。宛先を足すときに許可表の更新が要る。一時管理者を開け閉めする
  たびに Keycloak の作り直しが 2 回ある。測りの接続が受け口のログ（RabbitMQ の接続の閉じ）を 10 分ごとに 1 行増やす。
  中継が止まると Alertmanager も Discord へ届かないので、警報 `EgressProxyDown` 自体は Discord に出ない（知らせは heartbeat の停止による外の監視だけになる）。

## 残余

1. 中継は名前で絞るので、許可表の宛先（LLM の提供元・Discord・保管先・heartbeat）そのものを使った持ち出しは止めない（宛先の側の資格が要る）。
2. ESO の controller は全名前空間の Secret を書ける（chart の ClusterRole）。到達と Pod Security は絞ったが、権限は絞っていない。
3. API サーバーの監査の記録はノードの上にだけ在る（外へ送らない）。ノードの root は消せる。
4. クラスタでしか確かめられないこと（移行の門で確かめる）: 中継を経た 3 つの Pod の外への通信、`kubectl set env --from=secret` で Keycloak が一時管理者を作ること、
   API サーバーが設定の追加を読むこと、ESO の webhook が API サーバーから届くこと、測りが 4 つの受け口へ届くこと。
5. **ESO・OpenBao の出はポートだけで、宛先を問わず 443・6443 へ出られる**（API サーバー向けの規則が `to` を持たない）。ESO は全名前空間の Secret を書けるので
   （残余 2）、侵害された ESO の Pod はクラスタの外の 443・6443 へも出られる。試験 E1 が見るのは `ipBlock` で宛先を名指す出だけで、この 2 つの規則は
   「ポートだけの出はこの 2 つだけ」と固定するに留まる（増えれば赤になるが、2 つの中身は絞らない）。
   - 宛先で絞れるかの確かめ（2026-11-07）: k3s の NetworkPolicy の実装（kube-router。iptables の形）は、Pod の出の chain を filter の表の INPUT・FORWARD・OUTPUT から
     呼ぶ（kube-router の `pkg/controllers/netpol/pod.go` の `interceptPodOutboundTraffic`、`npc_iptables.go`。HEAD `6e325beb` で読んだ）。filter の表は kube-proxy の
     DNAT（nat の PREROUTING）の後なので、判定は DNAT の後の宛先で行われると読める。一方、本構成の API サーバーはループバックに bind し
     `--advertise-address 127.0.0.1`（README §1-4）で、DNAT の後の宛先は `127.0.0.1:6443` になる。ループバックの宛先を `ipBlock` で名指して効くこと、
     Pod から API サーバーへ届くこと（IADR-0689 残余 1）は、どちらもクラスタでしか確かめられず、Kubernetes の文書も DNAT の前後のどちらで判定するかを
     定めていない。誤ると ESO の同期が全部止まるので、確かめないまま絞らない。
   - 移行の門で測る候補: 2 つの規則の `to` を `ipBlock: { cidr: 127.0.0.1/32 }`・ポート 6443 だけにし、SecretStore が Ready のままか、
     ESO の Pod から外の 443 へ届かないかを見る。効けば、この残余を閉じる改定で宣言を絞る（値は README §1-4 の `--advertise-address` と同じなので、
     利用者が埋める値は要らない）。
