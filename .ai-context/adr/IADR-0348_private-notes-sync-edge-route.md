---
title: IADR-0348 /private-notes/sync/* はエッジ host のパス前置で DocumentService へ直接通し、本番像は opt-in（既定 off）にする
type: impl-adr
status: Proposed
related_ids: [FR-19, FR-20, UC-11, SC-20, NFR-09, NFR-11, ADR-0005, ADR-0021, ADR-0032, ADR-0037, ADR-0047, ADR-0084, IADR-0076, IADR-0078, IADR-0270, IADR-0317, IADR-0338]
author: claude
created: 2026-09-03
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
issue: "#1154"
---

# IADR-0348: 同期プロトコルのエッジ公開（受け口・露出範囲・fail-safe の向き）

- 状態: Proposed
- 日付: 2026-09-03
- 決定者: claude（実装判断）／起点 issue #1154（親 #1098・`IADR-0338` フォローアップ 1）

## 起点・関連

- 関連する計画書 ID: FR-20（前提 FR-19）/ UC-11 / SC-20 / NFR-11 /
  `ADR-0021`（エッジ ＝ Istio Ingress Gateway・Traefik は無効化）/ `ADR-0037` 決定 1・課題 2 /
  `ADR-0032`（BFF セッション）/ `ADR-0047` / 08_data-egress-policy §個人資料の同期に関する例外（許容条件 2・3）
- 関連する実装仕様書: `.ai-context/specs/20260903_issue-1154_private-notes-sync-edge-route.md`
- 前提 IADR（覆さない）: `IADR-0270` 決定 3（同期トークンは不透明トークンで DocumentService が自前検証）/
  `IADR-0338` 決定 4（BFF ではなく `/private-notes/sync/*` を Bearer で直接叩く）/
  `IADR-0076`・`IADR-0078`（エッジの `/bff` と catch-all）/ `IADR-0017`・`IADR-0026`（内部サービスの非公開と多層防御）/
  `IADR-0091`・`IADR-0317`（ローカルエッジ・Istio への移行）

## コンテキストと課題

`IADR-0338`（#1098・PR #1156）は Obsidian プラグインの第 1 段を起こす過程で、**自らの決定 4 が
穴を発見した**: 現行のエッジは `/bff` と catch-all（SPA）の **2 本しかルートを持たず**、
`/private-notes/sync/*` は外へ出ていない。サーバ側の端点は実装済みで、プラグインも動くが、
**配備済みクラスタに対しては到達できない**。同 IADR はこれをフォローアップ 1 として `deploy/` 領域の
後続 issue（#1154）へ切った。本 IADR はその 1 件を閉じる。

実測（2026-09-02。#1154 本文）:

```console
$ curl --cacert root-ca.pem https://localhost/                                -> 200
$ curl --cacert root-ca.pem https://localhost/bff/private-notes/sync/manifest -> 404   # BFF に口は無い（設計どおり）
$ kubectl -n microservices-platform port-forward svc/document-service 18093:8080
$ curl http://127.0.0.1:18093/private-notes/sync/manifest                     -> 401   # 実体には口がある
```

決めることは 3 つある —— **①どの host / path で受けるか ②露出をどこまでに絞るか ③既定をどちら向きに倒すか**。
**サーバ側の契約（`/private-notes/sync/*`）は変えない**（#1154 の制約）。

## 検討した選択肢と決定

### 決定 1: 受け口は **同一エッジ host のパス前置 `/private-notes/sync/`**、rewrite 無し、行き先は `document-service:8080`

| 案 | 評価 |
| --- | --- |
| **A. 同一 host のパス前置（採用）** | `/bff` と同型で、**公開パスと契約パスが同一**になる。プラグインの設定値は基底 URL（`https://<edge>`）だけで済み、`endpoint.ts` の正規化（末尾 `/` を落として `/private-notes/sync/...` を連結する）がそのまま効く。追加の資材はルート 1 本と NetworkPolicy 1 本 |
| B. 専用 host（`sync.<domain>` / `sync.localhost`） | 分離は強いが、Certificate の `dnsNames`（`edge-certificate.yaml` と `edge-certificate-istio.yaml` の**両方**。`k8s-local-up.test.js` が一致を強制する）・CoreDNS の転送先・VirtualService を 1 式ずつ増やす。**パス前置で得られる分離と同じもの**に 3 箇所の追随義務を足すだけである。host 単位でレート制限や WAF を分けたくなったときに引き直す |
| C. 中立パス（`/sync/…`）＋ rewrite | `templates/edge.yaml` は「`/bff` は rewrite を張らない」を明示的な契約にしている。中立パスにすると公開パスと通信仕様書のパスが二重になり、文書が 2 つのパス空間を説明することになる。プラグイン側の設定も「基底 URL」で説明できなくなる |
| D. BFF に薄い中継を置く | 不採用。`ADR-0032` は BFF の資格情報を HttpOnly セッション Cookie ＋ CSRF ヘッダに定めており、Bearer の別系統を通すと「BFF は Cookie セッションだけ」という境界が崩れる。`IADR-0338` 決定 4 が既に採らないと決めており、覆す理由が出ていない |

- 順序は **`/bff` の後・catch-all の前**。Istio のルートは先勝ちで、catch-all より後ろに置くと SPA に吸われる。
- 前置文字列は**テンプレートに直書きし、values の knob にしない**。これは環境ごとに変える値ではなく
  **サーバ契約そのもの**であり、knob にすると「打ち間違いで露出面が広がる」経路を作るだけである。

### 決定 2: 露出は **`/private-notes/sync/` 配下だけ**。エッジで JWT 検証は要求しない

- 前置は**末尾スラッシュ込み**である。`/private-notes`（一覧）・`/private-notes/devices`（端末登録）・
  `/private-notes/quotas/*`・`/documents` は当たらず、catch-all（画面配信）へ落ちる。
  08_data-egress-policy 許容条件 2（同期用資格情報のスコープ限定）を**経路の側でも**守る形である。
  **「落ちた先が 404 になる」わけではない**（下記「実測」の 🔴）。
- **エッジに `RequestAuthentication` / `AuthorizationPolicy` を置かない。** 同期トークンは JWT ではなく
  不透明トークンで、DocumentService が `SyncTokens.HashOf` のハッシュ照合で検証する（`IADR-0270` 決定 3）。
  エッジで JWT を要求すると**正当な要求が 401 で落ちる**。エッジは L7 のルーティングだけを担い、
  deny-by-default（欠落・不正・期限切れ・失効をすべて同じ 401 にする）は端点側が持つ。
- **PeerAuthentication も変更不要である。** istio-ingressgateway はメッシュ内（principal を持つ）なので、
  namespace の STRICT はそのままで Envoy 間 mTLS で入る。`ADR-0021` が「入口が mesh ネイティブなら
  mTLS 境界問題は構造的に発生しない」と書いたとおりで、`/bff` が既にこの経路で通っている。
  **実測でも変更していない**（下記「実測」の 6）。
- `IADR-0017`（内部サービスはホスト非公開）との整合: 公開するのは**サービスではなく 1 つの端点群**であり、
  その端点群は JWT 経路と資格情報系統が異なり、所有者の個人資料に構造的に閉じている
  （`ObsidianSyncEndpoints` のすべての照会が `PrivateNote.OwnerId` を通る）。多層防御は NetworkPolicy
  （決定 4）で維持する。

> **［2026-09-27 追記 / #1606］「`AuthorizationPolicy` を置かない」は改めた。** 置かないとしたのは
> **JWT を要求する方針**（`RequestAuthentication` と組む ALLOW）であり、その理由（正当な同期要求が 401 で落ちる）は今も正しい。
> 一方、決定 4 の NetworkPolicy は gateway の Namespace から 8080 番**全体**を開けるので、経路の制限は
> VirtualService の振り分け 1 枚だけに頼っていた（共有のゲートウェイへ別の VirtualService が付けば `/documents` へ届き得る）。
> そこで **JWT を要求しない、経路だけの DENY** を同じ条件で足した。判断の全体は下の「追記: エッジの主体を経路で絞る」にある。

### 決定 3: 本番像の既定は **off（opt-in）** —— fail-safe は「気付ける方向」へ倒す

`values.yaml` に `edge.privateNotesSync.enabled: false` を置く。

- **どちら向きに壊れるかで決めている。**
  - 経路が**無い**側の失敗は**利用者に見える**。プラグインが即座に失敗し、#1154 そのものの形で表面化する。
  - 経路が**余分にある**側の失敗は**誰にも気付かれない**まま、内部サービスの端点群が外に面する。
  - egress 統制は default-deny（08_data-egress-policy）である。**見えない方向へは倒さない。**
- 露出面そのものの危険度は低い（端点は自前で 401・所有者スコープ）。しかし **fail-safe は
  「露出しても平気か」ではなく「間違いに気付けるか」で決める**。
- 代償は「配備の運用者が knob を 1 つ立てる」ことだけで、立て忘れは可視な失敗になる。

### 決定 4: NetworkPolicy は既存 2 本と同型の 3 本目を、**同じ条件でだけ**開ける

`networkPolicy.enabled` かつ `edge.enabled` かつ `edge.privateNotesSync.enabled` のときだけ
`allow-edge-ingress-to-document-service` を描画する（`edge.gateway.namespace` → `app: document-service` の
当該ポートのみ）。`allow-edge-ingress-to-bff` / `-to-frontend` と同じ型である。

- **VirtualService が描画されても実到達しない**という壊れ方は既に 2 回踏んでいる（`IADR-0076` / `IADR-0078`）。
  同型の 3 回目を作らない。
- `podSelector` は route の行き先と**同じ単一情報源**（`edge.privateNotesSync.service`）から描画するので、
  knob を変えたときにドリフトしない。

### 決定 5: ローカル overlay は無条件に持ち、`values-local.yaml` へは knob を書かない

- `deploy/local/edge-istio/virtualservice-app.yaml`（ADR-0021 / #782 の overlay）は**条件を持たない**。
  overlay 自体が opt-in（`ISTIO=1` かつ `LOCALEDGE=1`）であり、ここは FR-20 を実測する環境である。
- `values-local.yaml` は `edge.enabled: false`（Istio 未導入の経路B 前提）なので、そこへ
  `edge.privateNotesSync` を書いても **1 バイトも描画されない不活性な設定**になり、後で静かに腐る。
  代わりに `edge:` ブロックのコメントへ「ローカルの実エッジは overlay 側であり、edge 配下の knob を
  ここへ書き足さないこと」を残した。

## 受け入れた限界

- **Traefik 経路（`deploy/local/edge/`）には同ルートを足さない。** `ADR-0021` が無効化を決めた側であり、
  退役方向の資材を太らせない。`istio-edge-down.sh` で切り戻した状態では同期経路が無くなるが、
  **無くなる方向は 404 であって開く方向ではない**（決定 3 と同じ向き）。
- 本 PR は**経路だけ**を足す。レート制限・接続元の絞り込み（`ADR-0021` のフォローアップ「レート制限」）は
  エッジ全体の課題であり、この 1 本だけに先行して入れない。

## 実測（稼働 k3s・2026-09-03）

エッジは istio-ingressgateway（`ADR-0021` / `IADR-0317`）。**`curl -k` は使っていない。** `--cacert` に
`local-edge-root-ca` を渡し、Windows の curl（schannel）が私設 CA の失効情報を引けないため
`--ssl-no-revoke` を併用した —— **これは失効照会だけを止めるもので、チェーンとホスト名の検証は効いている**
（`--cacert` を外すと exit 60 で落ちることを対で確かめた）。同期トークンは PR #1156 の手順を踏襲し、
Admin REST API で一時ユーザーと一時 direct-grant クライアントを作って発行し、**終了時に両方削除**した
（Keycloak pod で `kcadm.sh` は exec していない）。生出力は PR 本文にある。

| # | 測ったこと | 結果 |
| --- | --- | --- |
| 1 | `GET https://localhost/private-notes/sync/manifest`（有効な同期トークン） | **200**（資料 1 件の JSON） |
| 2 | 同じ URL をトークン**無し**で | **401**（陰性対照） |
| 3 | 同じ URL を**でたらめな**トークンで | **401**（陰性対照） |
| 4 | `https://localhost/private-notes` / `/private-notes/devices` / `/documents` | **画面（200 `text/html`）**。API へは届いていない（下記 🔴） |
| 5 | `/private-notes/sync/../devices`（正規化）と `/private-notes/syncX`（前置の境界） | どちらも画面へ落ちる（前置から抜けられない） |
| 6 | `dist/cli.mjs` を**エッジ URL**に向けて pull | exit 0・`個人資料/msp-1154/edge.md` を書き下ろし。2 回目は `upToDate:1`（冪等） |
| 7 | PeerAuthentication / AuthorizationPolicy | **変更していない**（STRICT のまま。適用したのは VirtualService 1 件だけ） |
| 8 | 一時ユーザー・一時クライアント・検証用の資料・端末 | **全件削除済み**（残 0 を確認） |

🔴 **#1154 の受け入れ基準が書いた「404 のまま」は成立しない。** catch-all の行き先である画面配信の
nginx は history fallback（`try_files … /index.html`）で**どのパスにも 200 を返す**ため、公開していない
API パスは 404 ではなく**画面**になる。**「404 が返る」を陰性対照にすると測れない。** 代わりに
**陽性対照と対で**測った —— 同じ `/private-notes/devices` を port-forward で直に叩くと **401**（JSON）に
なる。すなわちその端点は実在し、応答の形が違う。エッジ経由で画面が返るのは、**要求が DocumentService に
一度も届いていない**ことの証拠である。

## 理由

- **決定 1** は「公開パスと契約パスを分けない」という `/bff` の既存規律をそのまま延長したものである。
  分けると、通信仕様書・プラグインの設定説明・エッジのマニフェストの 3 箇所が別々のパスを持ち、
  どれか 1 つが遅れて腐る。
- **決定 2** は #1154 の「決めること 2」への直接の回答であり、egress 許容条件 2 を**認可だけでなく経路でも**
  担保する。末尾スラッシュ 1 文字が露出範囲を決めるので、静的検査（`k8s-local-up.test.js`）で固定した。
- **決定 3** は「壊れ方の可視性」で選んでいる。露出の危険度で選ぶと、危険度の見積もりが甘い日に既定が開く。
- **決定 4** は同型の事故（VirtualService はあるのに L3/L4 で塞がれる）を 3 回目にしないためである。

## 結果

- 良い影響:
  - **FR-20 が配備済みクラスタで成立する。** プラグインは port-forward なしでエッジ URL に向けて同期できる
  - 露出面が `/private-notes/sync/` に閉じていることが、実測（404）と静的検査の両方で固定された
  - 本番像は既定で何も出さないので、Obsidian 同期を使わない配備の攻撃面は変わらない
- 悪い影響・トレードオフ:
  - 配備ごとに knob を 1 つ立てる運用が増える（立て忘れは可視な失敗になる）
  - Traefik へ切り戻すと同期経路が消える（上記「受け入れた限界」）
  - 同期トークンは Bearer で平文のまま載るので、**https でない配備では成立しない**。プラグイン側は
    loopback 以外の http を拒む（`IADR-0338` 決定 4 / `endpoint.ts`）ので、設定を誤っても平文では出ない
- フォローアップ:
  1. エッジのレート制限（`ADR-0021` フォローアップ。同期経路だけでなくエッジ全体の課題）
  2. 本番配備での knob 有効化手順の Runbook 化（`IADR-0338` フォローアップ 3 の配布運用と併せて）
  3. `IADR-0338` フォローアップ 2（第 2 段: push / delete / 競合解決 UI）は本 IADR の射程外

## 追記: エッジの主体を経路で絞る（2026-09-27 / #1606）

> **［2026-09-27 追記 / #1606］** #1603 の監査が見つけた多層防御の隙間を閉じる。作業仕様書は
> `.ai-context/specs/20260927_issue-1606_private-notes-sync-edge-authz.md`。**新しい IADR は立てない** ——
> 本 IADR の決定 2（エッジに方針を置かない）と決定 4（NetworkPolicy）の補正であり、同じ knob の同じ条件で描くため。
>
> **決定 6: `edge.enabled` かつ `edge.privateNotesSync.enabled` のとき、DocumentService に `AuthorizationPolicy`（DENY）を 1 枚置く。**
>
> ```yaml
> selector: { matchLabels: { app: <edge.privateNotesSync.service> } }
> action: DENY
> rules:
>   - from: [{ source: { namespaces: [<edge.gateway.namespace>] } }]
>     to:   [{ operation: { notPaths: ["/private-notes/sync/*"] } }]
> ```
>
> - **ALLOW ではなく DENY にした。** ワークロードを選ぶ ALLOW が 1 枚でもあると、当たらない要求はすべて拒否に変わる。
>   DocumentService の呼び出し元は名前空間の中（BFF / GraphService / McpServer の REST 8080 と gRPC 8081）だけでなく、
>   **別名前空間の AST**（information-collection / report の KB 保存。AST はサイドカー注入が既定オフ＝平文で principal を持たない）と
>   **kubelet のプローブ**が居る。ALLOW で「名前空間の主体」＋「ゲートウェイ × sync」と列挙すると AST とプローブが黙って 403 になり、
>   呼び出し元が増えるたびに列挙を直す義務も生じる。DENY は当たった要求だけを落とすので、**from × to の外の呼び出しは 1 本も変わらない。**
> - **from はゲートウェイの主体（principal）ではなく、ゲートウェイの Namespace にした。** NetworkPolicy が開けた L3 の穴は
>   Namespace 単位（`edge.gateway.namespace`）であり、同じ単一情報源で絞れば穴の範囲をそのまま L7 で覆える。principal を名指しすると
>   service account 名（istioctl は `istio-ingressgateway-service-account`、helm の gateway chart はリリース名）と trust domain に
>   依存し、違えば**方針が黙って何も落とさない**（fail-open）。gateway の Namespace に DocumentService の正当な呼び出し元は
>   ゲートウェイ × sync 以外に居ない。
> - **to は `notPaths: ["/private-notes/sync/*"]` を直書きする**（route の前置 + `*`。決定 1 と同じく knob にしない）。
>   `/private-notes/sync-settings/`・スラッシュ無しの `/private-notes/sync`・gRPC（8081）の面も当たる＝落ちる。
>   **メソッドは絞らない** —— route もメソッドを絞っておらず、許すメソッドの正は端点側の契約にある。
> - **mTLS の影響**: STRICT（既定）では平文は PeerAuthentication が先に落とすので、本方針が見るのは mTLS の要求だけである。
>   ゲートウェイの Envoy → サイドカーは auto mTLS（と DestinationRule の ISTIO_MUTUAL）で必ず mTLS になり、`source.namespace` が採れる。
>   PERMISSIVE では平文の要求に `source.namespace` が無いので本 DENY は当たらない —— **平文の呼び出し元（サイドカーの無い AST 等）を
>   巻き込まない代わりに、gateway の Namespace に居るサイドカー無しの Pod からの平文は本門を素通りする。** これは PERMISSIVE の既知の限界として受け入れ、
>   閉じるのは STRICT である（平文を `notPrincipals: ["*"]` で落とすと、PERMISSIVE の間に平文で来る AST の KB 保存まで落ちる）。
> - **既定は無効のまま**なので、既定の values と `values-local.yaml` の描画は変わらない（#1606 の PR で `helm template` の
>   バイト一致を確かめた）。ローカルの overlay（`deploy/local/edge-istio/`）には足していない —— 決定 5 のとおり overlay は
>   FR-20 を実測する opt-in 環境であり、稼働中の PoC を変えないため（必要になれば overlay 側の別 issue で足す）。
> - 固定: `scripts/helm-private-notes-sync-authz.test.js` が実際に `helm template` で描き、呼び出し元の一覧へ Istio の評価規則で当てて
>   「ゲートウェイ × sync だけが通り、ゲートウェイ × それ以外は落ち、他は 1 本も切れない」を確かめる（経路の制限を外す・広げる、
>   from を名前空間の内側へ向ける、ALLOW に変える、の 4 変異がすべて赤になる）。

## 関連

- Supersedes: なし
- Superseded by: なし
- **`IADR-0338` フォローアップ 1 を解消する**（同 IADR 決定 4 の 🔴「エッジ公開は後続 issue」と
  結果欄の「配備済みクラスタへは到達できない」は、本 IADR の着地をもって解消した。**凍結記録なので
  同 IADR 本文は書き換えない**——解消の記録はこちらが持つ）
- 実装 issue: #1154（本体）/ #1098（親）/ #451（祖）
