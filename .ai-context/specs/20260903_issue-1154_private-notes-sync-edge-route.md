---
title: 作業仕様書 — /private-notes/sync/* をエッジ（Istio Ingress Gateway）から外へ出す
type: spec
status: done
related_ids: [FR-19, FR-20, UC-11, SC-20, NFR-11, ADR-0021, ADR-0037, ADR-0032, ADR-0047, IADR-0270, IADR-0338]
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
issue: "#1154"
---

# 作業仕様書: `/private-notes/sync/*` のエッジ公開（#1154）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-20（Obsidian 双方向同期。前提 FR-19）
- ユースケース（UC）: UC-11
- 画面（SC）: SC-20（同期端末とトークンの発行元。本 PR では触らない）
- 非機能（NFR）: NFR-11（平文 HTTP を残さない）
- 関連 ADR: `ADR-0021`（エッジ ＝ Istio Ingress Gateway・Traefik は無効化）/ `ADR-0037` 決定 1・課題 2 /
  `ADR-0032`（BFF セッション。本経路は BFF に載せない）/ `ADR-0047`（ローカルエッジ証明書）/
  08_data-egress-policy §個人資料の同期に関する例外（許容条件 2・3）
- 前提 IADR（覆さない）: `IADR-0270` 決定 3（同期トークンは DocumentService が自前で検証する別系統）/
  `IADR-0338` 決定 4（BFF ではなく `/private-notes/sync/*` を Bearer で直接。**エッジ公開は後続 issue**）/
  `IADR-0076`・`IADR-0078`（`/bff` と catch-all の 2 本）/ `IADR-0091`・`IADR-0317`（ローカルエッジ）

## 目的・背景

`IADR-0338`（#1098・PR #1156 で着地）が Obsidian プラグインの第 1 段を起こしたが、**同 IADR 決定 4 が
自ら発見した穴**として「配備済みクラスタのエッジは `/private-notes/sync/*` を外へ出していない」を
フォローアップ 1 に残した。本作業はその 1 件だけを閉じる。

現行のエッジのルートは 2 本しかない（本番チャート `templates/edge.yaml` / ローカル overlay
`deploy/local/edge-istio/virtualservice-app.yaml` とも）:

1. `/bff`・`/bff/*` → `bff-service`（rewrite 無し）
2. catch-all `/` → `frontend-service`（SPA）

同期プロトコルは 2 の catch-all に吸われて SPA の nginx が 404 を返す。**サーバ側の契約は変えない。**

## 対象範囲

- **対象**: エッジ（Istio Gateway / VirtualService）に `/private-notes/sync/` の 1 本を足すこと。
  本番チャート・ローカル overlay の両方。NetworkPolicy の穴（本番像のみ）。静的検査。文書の追随。
- **対象外**:
  - サーバ側の契約（`/private-notes/sync/manifest` / `notes/{id}` / `notes` / `notes/{id}/delete`）。1 バイトも変えない
  - プラグインのコード（接続先は設定値。経路が決まれば設定を入れ替えるだけ）
  - BFF への中継の追加（`ADR-0032` の境界。`IADR-0338` 決定 4 が既に不採用としている）
  - Traefik 経路（`deploy/local/edge/`）への同ルートの複製。`ADR-0021` が無効化を決めた側であり、
    退役方向の資材を太らせない（下記「受け入れた限界」）
  - PeerAuthentication / AuthorizationPolicy の変更（**不要である**ことを実測で確かめる。下記）
  - SC-20（トークン発行 UI）と第 2 段（push / delete / 競合解決）

## 走査で引いた母集合（規則 9・10）

**誤りの側の文字列で走査してから挙げる**（記憶で挙げない）。走査日 2026-09-03・`develop` `3d0a7048`。

```console
$ grep -rn "外へ出" --include=*.md --include=*.yaml --include=*.ts .   # 「エッジが外へ出していない」の側
$ grep -rn "port-forward した文書サービス\|port-forward svc/document-service" --include=*.md --include=*.ts .
$ grep -rn "catch-all\|/bff と\|2 本" --include=*.md deploy/ docs/
```

| ファイル | 何が誤りになるか | 本 PR での扱い |
| --- | --- | --- |
| `docs/api/FR-20_obsidian-sync.md` §接続先（78–80 行） | 「配備済みクラスタのエッジは `/private-notes/sync/*` を外へ出していない」 | **更新する**（issue の宣言ファイル領域） |
| `docs/functional/FR-20_obsidian-sync.md`（23 行） | 未達リストの「②同期プロトコルをエッジで外へ出す経路」 | **更新する**（②を落とす。①第 2 段は残る） |
| `docs/functional/FR-20_obsidian-sync.md`（101 行） | 🔴「接続先の公開経路が無い」 | **更新する**。**初回の走査（「外へ出」）では捕まらなかった** —— 語が違う（規則 9 の「軸を 1 本で終わらせない」に反していた）。「公開経路 / 到達できな / 届かない」で引き直して発見した |
| `docs/security/security.md`（119・255 行） | 「外部からの入口は **BFF（エッジ）に一本化**」「内部サービスを host 公開しない。エッジ(BFF)で JWT 認証」 | **更新する**（規則 10。是正**後**の語で引き直して初めて出た。例外 1 本を明記する） |
| `docs/how-to/obsidian-plugin-install.md`（24・71 行） | 「いま実際に届く接続先は port-forward した文書サービスだけ」 | **更新する**（issue 手順 5） |
| `deploy/local/edge-istio/README.md`（22 行） | overlay の表が `virtualservice-app.yaml` を「`/bff`→bff、残り→frontend」と書く | **更新する** |
| `deploy/istio/README.md`（81 行） | 「Gateway 2 本 ＋ VirtualService 9 本」 | **更新しない**。既存 VirtualService に http ルートを 1 本足すだけで、**VirtualService の本数は変わらない**（導出値は数え直した: 9 のまま） |
| `deploy/local/edge/README.md`（23 行） | Traefik overlay の説明 | **更新しない**（Traefik へは同ルートを足さない。対象外） |
| `.ai-context/adr/IADR-0338_*.md` 決定 4・結果・フォローアップ 1 | 「エッジ公開は後続 issue」「配備済みクラスタへは到達できない」 | **書き換えない**（凍結記録。`traceability.repo.md`）。**後継 IADR が本 issue で解消したことを持つ** |
| `.ai-context/specs/20260902_issue-1098_*.md`（68・94 行） | 同上 | **書き換えない**（確定済み記録） |

陰性側の確認（規則 10。**本 PR で新たに誤りになる自分の記述を、是正後の語で引き直す**）:
`/private-notes/sync` を含む追跡下ファイルを全件走査し、経路の有無に言及するものが上表で尽きることを確かめる。

## 設計

### 決定 A: 受け口は **同一エッジ host のパス前置 `/private-notes/sync/`**、rewrite 無し、行き先は `document-service:8080`

| 案 | 評価 |
| --- | --- |
| **A. 同一 host のパス前置（採用）** | `/bff` と同型で、**契約パスと公開パスが同一**になる。プラグインの設定値は基底 URL（`https://<edge>`）だけで済み、`endpoint.ts` の正規化（末尾 `/` を落として `/private-notes/sync/...` を連結）がそのまま効く。Envoy のルートは先勝ちで、前置は**この 1 本に閉じる** |
| B. 専用 host（`sync.<domain>`） | 分離は強いが、Certificate の `dnsNames`（`edge-certificate.yaml` と `edge-certificate-istio.yaml` の**両方**。静的検査が一致を強制する）・CoreDNS の転送先・VirtualService を 1 式増やす。**パス前置で得られる分離と同じ**ものに、3 箇所の追随義務を足すだけである。host 単位でレート制限や WAF を分けたくなったときに引き直す |
| C. 中立パス（`/sync/…`）＋ rewrite | `/bff` が「rewrite を張らない」を明示的な契約にしている（`templates/edge.yaml` 冒頭）。プラグイン側から見える公開パスと通信仕様書のパスが二重になり、文書が 2 つのパス空間を説明することになる |
| D. BFF に薄い中継 | 不採用。`ADR-0032` は BFF の資格情報を HttpOnly セッション Cookie ＋ CSRF ヘッダに定めており、Bearer の別系統を通すと「BFF は Cookie セッションだけ」という境界が崩れる。`IADR-0338` 決定 4 が既に採らないと決めている |

- 前置は `/private-notes/sync/`（**末尾スラッシュ込み**）。`/private-notes`・`/private-notes/devices`・
  `/private-notes/quotas/*`・`/documents` は当たらず、catch-all → SPA の 404 のままになる。
- 前置文字列は**テンプレートに直書きし、values の knob にしない**。これは環境ごとに変える値ではなく
  **サーバ契約そのもの**であり、knob にすると「打ち間違いで露出面が広がる」経路を作るだけである。
- 順序: `/bff` の後・catch-all の前に置く。Istio は先勝ちなので catch-all より前でなければ効かない。

### 決定 B: エッジで JWT 検証を要求しない（RequestAuthentication / AuthorizationPolicy を足さない）

同期トークンは**不透明トークン**で、DocumentService が `SyncTokens.HashOf` でハッシュ照合して検証する
（`IADR-0270` 決定 3 / `ObsidianSyncEndpoints.ResolveDeviceAsync`）。JWT ではないので、エッジに
`RequestAuthentication` を置くと**正当な要求が 401 で落ちる**。エッジは L7 のルーティングだけを担い、
資格情報の判断は DocumentService に委ねる（deny-by-default は端点側が持つ。欠落・不正・期限切れ・失効は
すべて同じ 401）。

**エッジ → DocumentService の mTLS は変更不要**である（実測で確かめる）: istio-ingressgateway は
メッシュ内（principal を持つ）なので、namespace の `PeerAuthentication` が STRICT のままでも
Envoy 間の mTLS で入る。`ADR-0021` が「入口が mesh ネイティブなら mTLS 境界問題は構造的に発生しない」と
書いたとおりで、`/bff` が既にこの経路で通っている。**PeerAuthentication / AuthorizationPolicy は触らない。**

### 決定 C: fail-safe の向き —— 本番チャートの既定は **off**（opt-in）、ローカル overlay は無条件に on

- `values.yaml` に `edge.privateNotesSync.enabled: false` を足す。
  - **どちら向きに壊れるかで決める。** 経路が無い側の失敗は**利用者に見える**（プラグインが即座に失敗し、
    #1154 そのものの形で表面化する）。経路が余分にある側の失敗は**誰にも見えない**まま内部サービスの
    端点群が外に面する。egress 統制は default-deny（08_data-egress-policy）であり、
    **見えない方向へ倒さない**のが fail-safe である。
  - 露出そのものの危険度は低い（端点は自前で 401・所有者スコープ）が、fail-safe は
    「露出しても平気か」ではなく「間違いに気付けるか」で決める。
- `values-local.yaml` には knob を**書かない**。同ファイルは `edge.enabled: false`（Istio 未導入の経路B 前提）
  なので、書いても**1 バイトも描画されない不活性な設定**になり、後で静かに腐る。代わりに既存の `edge:`
  ブロックのコメントへ「ローカルの実エッジは `deploy/local/edge-istio/` overlay である」旨の 1 行を足す。
- ローカル overlay（`virtualservice-app.yaml`）は**無条件にルートを持つ**。overlay 自体が opt-in
  （`ISTIO=1` かつ `LOCALEDGE=1`）であり、ここは FR-20 を実測する環境である。

### 決定 D: NetworkPolicy は本番像でのみ穴を開ける（`/bff`・frontend と同型）

`networkPolicy.enabled` かつ `edge.enabled` かつ `edge.privateNotesSync.enabled` のときだけ、
`allow-edge-ingress-to-document-service` を描画する（`edge.gateway.namespace` → `app: document-service` の
当該ポートのみ）。既存 2 本（bff / frontend）と同じ型で、3 つ目の条件が増えるだけである。
ローカルは `networkPolicy.enabled: false` なので描画されない。

### 受け入れた限界

- **Traefik 経路（`deploy/local/edge/`）には同ルートを足さない。** `ADR-0021` は Traefik の無効化を決めており、
  `istio-edge-down.sh` の切り戻し先は「元の 2 本のエッジ」へ戻る。切り戻した状態では同期経路が無くなるが、
  **無くなる方向は 404 であって開く方向ではない**（fail-safe）。
- 本番像の既定が off なので、**配備の運用者が 1 つ knob を立てる**必要がある。`values.yaml` のコメントと
  `docs/how-to/obsidian-plugin-install.md` に書く。

## 変更するファイル

| ファイル | 変更 |
| --- | --- |
| `deploy/helm/microservices-platform/templates/edge.yaml` | `/private-notes/sync/` ルート（`/bff` の後・catch-all の前） |
| `deploy/helm/microservices-platform/values.yaml` | `edge.privateNotesSync`（enabled/service/port）。既定 false |
| `deploy/helm/microservices-platform/templates/networkpolicy.yaml` | `allow-edge-ingress-to-document-service` |
| `deploy/local/values-local.yaml` | `edge:` ブロックのコメント 1 行（値は変えない） |
| `deploy/local/edge-istio/virtualservice-app.yaml` | 同ルート（無条件） |
| `deploy/local/edge-istio/README.md` | ルート表の追随 |
| `scripts/k8s-local-up.test.js` | 不変条件テスト（下記） |
| `docs/api/FR-20_obsidian-sync.md` / `docs/functional/FR-20_obsidian-sync.md` / `docs/how-to/obsidian-plugin-install.md` / `docs/security/security.md` | 母集合の表のとおり |
| `.ai-context/adr/IADR-0348_*.md` ＋ `.ai-context/adr/README.md` | 実装 ADR と索引 |

## 受け入れ基準（issue #1154 の Given-When-Then を転記）

- [x] Given 稼働クラスタのエッジ / When `GET https://localhost/private-notes/sync/manifest` を有効な同期トークンで呼ぶ / Then **200**
- [x] Given 同じ経路をトークン無し／でたらめなトークンで呼ぶ / Then **401**（陰性対照）
- [x] Given `/private-notes`・`/private-notes/devices` 等の JWT 経路と `/documents` / When エッジから呼ぶ /
      Then **API へ届かない**（露出面が広がっていない）
- [x] Given プラグイン（`dist/cli.mjs`） / When 接続先をエッジ URL にして pull する / Then ファイルが書き下ろされる
- [x] Given 本番チャート / When `helm template` する / Then 既定（knob off）ではルートも NetworkPolicy も描画されない
- [x] Given `node scripts/check-deploy-manifests.js` / Then chart と overlay がレンダリングでき、スキーマに適合する

🔴 **受け入れ基準③④の「404 のまま」は測れない形だった（issue 本文の想定誤り）。** catch-all の行き先である
画面配信の nginx は history fallback で**どのパスにも 200（`text/html`）を返す**ので、公開していない API
パスは 404 ではなく**画面**になる。「404 が返ること」を陰性対照にすると、**変更前も変更後も 404 は返らない**
ため何も測っていない。**陽性対照と対で測る形へ言い直した** —— 同じ `/private-notes/devices` を
port-forward で直に叩くと **401**（JSON）になる。端点は実在し応答の形が違うので、エッジ経由で画面が返るのは
「要求が DocumentService に一度も届いていない」ことの証拠である。

## テスト方針

`scripts/k8s-local-up.test.js` の `#782 / ADR-0021` ブロックへ**静的な不変条件**を足す（実クラスタの疎通は
実測が持つ。この検査は「壊すと落ちる門」である）:

1. overlay と本番チャートの**両方**が `/private-notes/sync/` を `document-service` へ振っている
2. そのルートが**catch-all（`prefix: /`）より前**に置かれている（先勝ちで効かなくなるのを止める）
3. **露出面が広がっていない**（陰性の静的対照）: どちらのマニフェストも `/private-notes`（sync 以外）・
   `/private-notes/devices`・`/documents` への route を持たない
4. 本番チャートの knob 既定が `false`（fail-safe の向きを固定する）
5. NetworkPolicy が同じ 3 条件で描画され、`document-service` の Pod だけに限定されている

## 実測（証跡は PR 本文へ）

稼働 k3s（Rancher Desktop・エッジは istio-ingressgateway）。**`curl -k` は使わない**。エッジ CA を
`--cacert` で渡す。同期トークンの発行は PR #1156 の実測手順を踏襲する（Admin REST API で一時ユーザーと
一時 direct-grant クライアントを作り、**終了時に両方削除**する。**Keycloak pod で `kcadm.sh` を exec しない**）。

実測で踏んだ罠（2026-09-03）:

1. **Windows の curl は schannel で、私設 CA だと `--cacert` だけでは exit 60**（`the revocation status is
   unknown`）になる。`--ssl-no-revoke` を足す。**これは失効照会だけを止めるもので、`-k` とは違う** ——
   `--cacert` を外すと 60 で落ちることを対で確かめ、チェーン検証が効いていることを示した。
2. **realm の既定必須アクション（`CONFIGURE_TOTP`）は、作成要求の `requiredActions: []` を上書きして付く。**
   そのままだと password grant が `invalid_grant "Account is not fully set up"` になる。**作成後に
   PUT で空へ戻す**（before / after を出力に残す）。
3. 陰性対照が「404」では測れない（上記 🔴）。

## 計画書との差異

- 差異: なし。`ADR-0021`（エッジ＝Istio Gateway）・`ADR-0037` 課題 2（別系統の資格情報）・
  08_data-egress-policy 許容条件 2・3（スコープ限定・提供手段）のいずれとも整合する。

## 未決事項

- 本番像の knob 既定を off にしたことで、配備ごとに立てる運用が要る。運用手順の正本化（Runbook 化）は
  配布のリリース資産化（`IADR-0338` フォローアップ 3）と併せて後続で扱う。
