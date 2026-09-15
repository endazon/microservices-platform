---
title: IADR-0456 SC-22 はプロパティの種別（値・パスワードの MD5・RSA 鍵の生成）を allowlist に持ち、書き込み後に ExternalSecret へ即時同期を依頼し、消費側は Reloader で作り直し、bootstrap は画面の KV を無いときだけ作る
type: impl-adr
status: Accepted
related_ids: [SC-22, FR-05, NFR-18, ADR-0095, IADR-0096, IADR-0103, IADR-0433, IADR-0453, IADR-0454]
author: claude
created: 2026-09-15
updated: 2026-09-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
related_specs:
  - ../specs/20260915_issue-1477_screen-only-poc-setup.md
---

# IADR-0456: SC-22 のプロパティの種別・即時同期・Reloader・seed-if-absent

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-15
- 決定者: claude（#1477。利用者指示「PoC の立ち上げをすべて画面から行えるようにする」。AST 側は AST#795 が同じ契約を実装する）

## 起点・関連

- 関連する計画書 ID: SC-22（秘密情報・接続設定の管理）／FR-05／NFR-18
- 関連する計画 ADR: ADR-0095 決定 1（Git に置けないもの）・決定 3（BFF が書く・ESO は読み取り専用）・決定 4（一括再投入をしない）
- 関連する実装 ADR: **IADR-0433**（権限の形。本 ADR は data の `read`・`update`・ワイルドカードを 1 つも足さない）／**IADR-0453**・**IADR-0454**（画面と端点）／IADR-0096（Vault ＋ ESO）／IADR-0103（env は Pod 起動時に 1 度だけ解決される）
- 関連する実装仕様書: `.ai-context/specs/20260915_issue-1477_screen-only-poc-setup.md`
- 起票: #1477 ／ 計画への確認: planning#635（射程と画面設計の追随。**裁定を待たず実装する**のは利用者指示による。裁定が異なれば戻す）

## コンテキストと課題

PoC（AST#342）の立ち上げには、画面の外で行う作業が 4 つ残っていた（#1477 の「現状の穴」）。

1. SC-22 で書いた値が Pod に届くまで最大 1 時間（ESO `refreshInterval: 1h`）。env で読む消費側は再起動も要る。
2. moomoo のログイン情報・OpenD の RSA 鍵が allowlist の `deferred[]`（IADR-0433 決定 3 は「ファイル形（PEM）で UI の作法が別」を理由にした）。
3. Discord の環境固有 ID（guild / channel / 許可ユーザー / 対応表）を入れる画面が無い。
4. Vault の seed が `ai-stock-trading/*` を持たない。加えて、**bootstrap は SC-22 の KV を毎回全置換しており、`k8s-local-up.sh` を再実行すると画面で入れた値が消えていた**（着手前の実測）。

MSP と AST が同じ Vault パス・プロパティ・Secret 名を共有する契約は #1477 本文の表にあり、本 ADR はその MSP 側の実装判断である。

## 検討した選択肢

### A. 変換・生成をどこに宣言するか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A1 | BFF のコードにプロパティ名で分岐を書く（`login-pwd-md5` なら MD5） | 🔴 allowlist（単一情報源）と別の場所に「どう書くか」が生まれ、policy・Runbook の読み手から見えない |
| A2 | **allowlist の `properties[]` の要素にオブジェクト形 `{ name, kind, sensitive }` を許す** | 既存の文字列要素はそのまま読める。種別が単一情報源に並び、差分がレビューに出る |
| A3 | 種別ごとに別の配列（`md5Properties` 等）を足す | 1 つのプロパティの宣言が 2 か所に割れ、交差の検査が増える |

### B. パスワード（`login-pwd-md5`）

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B1 | 画面が MD5 を計算して送る | 平文は BFF に届かないが、**ブラウザの実装が変換の正本になる**（検証も監査もサーバ側でできない）。画面を経由しない呼び出しは任意の文字列を「MD5」として書ける |
| B2 | **BFF が平文を受けて MD5（UTF-8・小文字 hex 32 桁）へ変換して書く** | 変換の正本がサーバの 1 か所。平文は要求本文にだけ現れ、既存の「値を出さない」統制（IADR-0433 決定 6）の中に入る |
| B3 | 平文を Vault に置き、消費側（OpenD）で変換する | 🔴 契約（#1477）が `login-pwd-md5` を指定している。平文の保存が増える |

### C. RSA 鍵（`opend_rsa.pem`）

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C1 | PEM を貼り付ける大きなテキスト欄 | 🔴 利用者の端末で鍵を作り、クリップボードとブラウザに鍵が残る。値の上限（8192 文字）とも別の作法が要る |
| C2 | ファイルのアップロード | 同上。鍵がどこで作られたかを画面が保証できない |
| C3 | **BFF が生成して書く。要求に値を持たない** | 鍵は BFF のメモリにしか現れない。応答・監査・ログのどこにも出ない形を型で閉じられる |

### D. 即時同期

| 案 | 内容 | 評価 |
| --- | --- | --- |
| D1 | ESO の `refreshInterval` を短くする | 全 ExternalSecret の Vault への問い合わせが増える。書かれていない時間にも走り続ける |
| D2 | BFF が k8s Secret を直接書く | 🔴 ADR-0095 決定 3（供給は ESO が読み取り専用で行う）に反する。BFF に Secret の書き込み権限が要る |
| D3 | **書き込み成功後に ExternalSecret へ `force-sync: <unix 秒>` を merge-patch する（ESO が同期し直す）** | 同期するのは ESO のまま。BFF の権限は ExternalSecret の注釈だけで、`resourceNames` で名前を限れる |

### E. 消費側の再起動

| 案 | 内容 | 評価 |
| --- | --- | --- |
| E1 | BFF が Deployment を `rollout restart` する | BFF に Deployment の `patch` 権限が要る。画面の書き込みと Pod の作り直しが同じ主体に寄る |
| E2 | 消費側を env ではなくファイルのマウントで読むように変える | 全消費側のコード変更。AST 側にも波及する |
| E3 | **Stakater Reloader（ESO=1 のローカル配備で導入）が Secret の変更を見て、注釈を持つ Deployment を作り直す** | 消費側は注釈 1 行。見る名前空間を限定できる |

### F. seed が画面の値を消す問題

| 案 | 内容 | 評価 |
| --- | --- | --- |
| F1 | env を渡し忘れないよう Runbook に書く | 🔴 2026-09-10 の事故（Runbook 冒頭）と同じ形の「手順で守る」統制 |
| F2 | **KV が無いときだけ作る（`vault kv put -cas=0`）。在るときは env が空でないプロパティだけ部分更新する** | 再実行が安全になる。明示した env での上書きは残る |

## 決定

**A2 / B2 / C3 / D3 / E3 / F2 を採用する。**

### 決定 1: allowlist の `properties[]` に種別と `sensitive` を持ち、各項目に同期先の ExternalSecret を宣言する

- 要素は文字列（種別 `value`・秘密）か `{ "name", "kind"?, "sensitive"? }`。`kind` は `value` / `md5-from-password` / `generate-rsa-pkcs1`。
- 🔴 **fail-closed**（IADR-0433 決定 3 のまま起動しない）: 未知の `kind`・オブジェクトの未知のキー（綴り違い）・真偽値でない `sensitive`・**`value` 以外への `sensitive: false`**（パスワードと生成鍵を「秘密でない」と宣言させない）・`externalSecret` の欠落・書式違反・重複。
- 項目は 6 KV・書けるプロパティ 21（`ast-app-secrets` に Discord ID 4 件と SEC EDGAR の User-Agent 1 件、`deferred[]` から `ast-moomoo`・`ast-moomoo-rsa` を移す）。
- 一覧の応答に `propertyDetails`（名前・種別・秘密か）を足す（契約の追加。既定値つきで後方互換）。画面は種別で入力の形を選び、**宣言が無い・未知の種別は「値・秘密」（マスクと確認入力）へ倒す**。
- `sensitive: false`（Discord ID・SEC EDGAR の User-Agent）でも**書き込み専用なのは同じ**（読み出す口は無い）。画面は平文で入力させ、確認入力を求めない。

### 決定 2: `md5-from-password` は BFF が平文を MD5（UTF-8・小文字 hex 32 桁）へ変換して書き、平文もハッシュも出さない

- 要求は `{ property: "login-pwd-md5", value: <平文> }`。入力規則は `value` と同じ（1〜8192 文字）。
- 🔴 平文・MD5 のどちらも応答・監査・ログに出さない（長さも出さない。IADR-0433 決定 6）。作った値は変数にも保持せず、Vault への要求本文にだけ渡す。
- MD5 は**消費側（moomoo OpenD）の設定形式の要求**であり、MSP の中で認証や完全性の判定に使うものではない（解析器の CA5351 はこの理由で抑止する）。

### 決定 3: `generate-rsa-pkcs1` は値を受けず、BFF が RSA 1024 bit の PKCS#1 PEM を生成して書き、鍵をどこにも返さない

- 要求は `{ property: "opend_rsa.pem", value: "" }`（**要求の型は変えない**。空でない値は 400 `invalid-value` —— 持ち込んだ鍵を書かない）。
- 鍵長 1024 は OpenD の要件（CA5385 はこの理由で抑止する）。生成器は使い終えたら破棄する。
- 画面は値の欄を持たず、「生成」を 1 度押すと**生成し直すと OpenD に登録済みの鍵との対応が失効する**旨と、**OpenD は Reloader の対象外なので `kubectl -n ai-stock-trading rollout restart deploy/opend` で手動で再起動し、そのとき SMS / 画像の認証を再び求められ得る**旨（PR #1478 監査 D2）の確認を出し、「生成して書き込む」でだけ送る（IADR-0453 決定 6「確認ダイアログは置かない」の例外。値の書き直しと違い、戻せない副作用が外にある）。

### 決定 4: 書き込み成功後に ExternalSecret へ force-sync を依頼し、失敗しても書き込みを失敗にしない

- 順序: Vault への書き込み成功 → 書き込み記録 → 監査 `secret.item.update granted` → `PATCH /apis/external-secrets.io/v1/namespaces/{ns}/externalsecrets/{name}`（`application/merge-patch+json`・`{"metadata":{"annotations":{"force-sync":"<unix 秒>"}}}`）。
- 名乗りは Pod の ServiceAccount トークン（Vault の k8s auth と同じ）、TLS は SA の `ca.crt` で検証する（検証を外さない）。所在は `KUBERNETES_SERVICE_HOST` / `PORT`。構成 `ExternalSecretSync:Enabled`（チャート `services.bff.externalSecretSync.enabled`。**本番像の既定 false**、ローカル true）。
- 🔴 **依頼の失敗は 200 のまま `syncRequested: false`**。監査は**別の行** `secret.item.sync`（`granted` / `failed`、理由 `sync-request-failed` / `sync-not-configured`、detail は項目名と `ns/name` だけ）。書き込みの監査行の outcome を汚さない。書き込みが失敗したら依頼しない。
- RBAC: Role `bff-externalsecret-sync`（`external-secrets.io` の `externalsecrets` に `get` / `patch`、**`resourceNames` で限定**）＋ RoleBinding（SA `microservices-platform/bff` だけ）。MSP の名前空間はチャート、platform-infra と ai-stock-trading は `deploy/local/vault/eso/rbac-bff-externalsecret-sync.yaml`（`k8s-local-up.sh` の ESO=1）。**名前空間ごとの名前の集合が items[] の `externalSecret` と完全一致することを xUnit で固定する**（Vault policy と同じ考え方）。
- NetworkPolicy: `networkPolicy.enabled` かつ同期有効かつ **`apiServerEgress.cidrs` を宣言したときだけ** BFF → API サーバ（443・6443）の Egress を描く。既定の宛先は置かない（「どこへでも 443」の穴にしない）。

### 決定 5: env で読む消費側は Stakater Reloader が作り直す（ローカルは ESO=1 で導入）

- chart `stakater/reloader` **2.2.17**・image **v1.4.22** を pin する（ESO の chart を pin したのと同じ理由）。
- 🔴 `reloader.watchGlobally=false` ＋ `reloader.namespaces={microservices-platform,platform-infra,ai-stock-trading}`（各名前空間の Role。クラスタ全体の Secret を読ませない）。`ai-stock-trading` の名前空間は Role の置き場として `k8s-local-up.sh` が冪等に作る。撤去は `k8s-local-down.sh`（Rancher Desktop 経路）が release と名前空間 `reloader` を消す（PR #1478 監査 D6）。
- 注釈 `secret.reloader.stakater.com/reload`: llmgateway-service（`llm-provider-credentials`）・wiki-service（`wikijs-sync`）は `values-local.yaml`（チャートの汎用テンプレートに `deploymentAnnotations` を足す）、mail-relay（`keycloak-smtp`）は `deploy/mail-relay/mail-relay.yaml`。AST の消費側は AST#795。
- **keycloak-smtp の反映**: 消費側は #1245 以降 **mail-relay（env）** であり Keycloak ではない。Reloader が mail-relay を作り直す。**Keycloak は作り直さない**（realm の `smtpServer` は mail-relay を指す宣言固定で、秘密は持たない）。

### 決定 6: bootstrap は画面が書く KV を無いときだけ作り、在れば env が空でないプロパティだけ部分更新する

- 対象は items[] のうち seed するもの: `msp/llm-provider-credentials`・`msp/wikijs-sync`・`msp/keycloak-smtp`（`host` / `port` / `starttls` は構成なので在っても毎回その値へ揃える）・`ai-stock-trading/app-secrets`。
- 作成は `vault kv put -cas=0`（Vault 側でも「無いときだけ」）を `vkv_exists` の「無い」側の分岐に置く。部分更新は値を stdin で渡し、空なら何もしない。失敗（現在版が削除されている等）は警告にして bootstrap を止めない。
- `ai-stock-trading/app-secrets` の seed: `*-auth-client-*` 8 件は **MSP realm（`deploy/keycloak/microservices-platform-realm.json`）の機密クライアントと同値**、画面から書ける 12 件は空文字。**在れば触らない**（env での上書きも持たない —— 投入面は画面）。
  ただし在る KV にも `*-auth-client-*` 8 件は**プロパティが無いものだけ**同じ値で足す（`vkv_patch_if_missing`。画面が先に 1 プロパティだけ書くと BFF がその 1 件だけの KV を作り、auth キーが欠けて AST のサービス間トークン取得が止まるため。PR #1478 監査 D4）。
- `ai-stock-trading/moomoo` / `moomoo-rsa` は seed しない（未設定のあいだ OpenD は Secret 不在で待機する＝fail-closed）。
- 形（items[] のパスへの put はすべて分岐の中・`-cas=0`、app-secrets のキー集合と realm との一致、moomoo を seed しない）は xUnit で固定する。

## 理由

- **権限は 1 つも広げていない。** Vault は完全一致パスを 2 KV 足しただけ（data に `create` / `patch`、metadata に `read`）。k8s は ExternalSecret の注釈だけを名前で限る。値を読む口は Vault にも k8s にも無い。
- **変換と生成をサーバに置いたのは、「値を出さない」統制を 1 か所で守るため**である（決定 2・3）。ブラウザで変換・生成すると、統制の正本がクライアントに移る。
- **同期と再起動を別の主体に分けたのは、画面の書き込みが Pod を直接操作しないため**である（決定 4・5）。BFF は「ESO に同期を頼む」ところまでで止まり、作り直すのは Reloader である。
- **seed-if-absent は、画面を投入面にした時点で必要になった前提**である（決定 6）。全置換の seed と画面の投入は同じ KV を奪い合い、後から走ったほうが勝つ。

## 結果

- **良い影響**:
  - PoC の秘密情報（外部 API キー・Discord ID・moomoo・OpenD の鍵）を画面だけで投入でき、数秒で Pod まで届く（ESO=1 のローカル配備）。
  - `k8s-local-up.sh` を再実行しても画面の値が消えない。
  - 同期先・RBAC の名前・policy のパス・seed のキーが allowlist と試験で結ばれ、どれか 1 つだけを変えると落ちる。
- **悪い影響・トレードオフ**:
  - 🔴 **本番像では同期を有効にしても、`apiServerEgress.cidrs` を環境ごとに与えないと NetworkPolicy で依頼が届かない**（`syncRequested: false` に倒れる）。チャートは宛先の既定を持たない。
  - 🔴 **Reloader はローカルの ESO=1 にしか入れていない。** 本番の消費側の作り直しは別途決める（本 ADR の射程外）。
  - 監査のアクションが 1 つ増える（`secret.item.sync`）。
  - 生成は画面の確認 1 回で OpenD の鍵の対応を失効させられる（運用者ロールに限る・監査に残る）。
  - bootstrap は KV が在る限り env 未指定のプロパティを戻さない。**意図して空へ戻すには画面かコンソールで書く**。
- **帰結として記録する食い違い（コードは変えない）**:
  - AST の `k8s-local-deploy.sh` は `kb-auth-client-secret` / `llm-auth-client-secret` の既定が**空**である。本 ADR の seed は契約（realm と同値）に従い MSP realm の値を入れる。AST 側の既定は AST#795 の範囲。
  - AST の `ast-secrets` は `sec-edgar-user-agent` も読むが、当初の契約の `app-secrets` の表に無かった。**画面から入れられないと ESO 所有の経路で SEC EDGAR だけが収集対象から外れる**（AST#796 の監査指摘）ため、契約へ加え、allowlist に秘密でない値（`sensitive: false`）として置き、seed は空文字とした（書ける 12 件）。
- **フォローアップ**:
  1. planning#635 の裁定（ADR-0095 決定 1 の射程に Discord ID が入るか・SC-22 の画面設計の追随）。裁定が異なれば本 ADR を改定する。
  2. 稼働クラスタでの疎通（SC-22 テスト仕様書 T-40 と同じ場）: force-sync で数秒以内に Secret が変わること、Reloader が消費側を作り直すこと、生成した鍵を OpenD が読めること。
  3. 本番の消費側の作り直し（Reloader を本番像に入れるか、別の方式か）。

## 関連

- Supersedes: なし（IADR-0433 決定 3 の分類表と IADR-0453 決定 6・8 に同日付の追記を置いた）
- Superseded by: なし
