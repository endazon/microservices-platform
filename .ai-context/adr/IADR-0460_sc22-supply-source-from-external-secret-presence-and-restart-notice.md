---
title: IADR-0460 SC-22 の供給元は同期先 ExternalSecret の有無から境界層が判定して 3 値（screen / git / unknown）で返し、画面は送る前に消費側の再起動を項目ごとに告げる
type: impl-adr
status: Accepted
related_ids: [SC-22, FR-05, NFR-18, ADR-0095, ADR-0104, IADR-0433, IADR-0453, IADR-0456]
author: claude
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0104_sc22-item-kinds-and-dual-path-for-env-ids.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
related_specs:
  - ../specs/20260925_1502_sc22-supply-source-and-restart-notice.md
---

# IADR-0460: SC-22 の供給元の判定と、消費側の再起動の告知

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（#1502。計画 ADR-0104 決定 2・4 の実装）

## 起点・関連

- 関連する計画書 ID: SC-22 主要素 1（供給元の列）・§アクション（書き込み後の同期と再起動）／FR-05／NFR-18
- 関連する計画 ADR: **ADR-0104** 決定 1（両経路・効くのは配備時のスイッチが選んだ一方）・決定 2（供給元を出す。画面は推測しない）・決定 4（消費側が再起動する旨を出す）、ADR-0095 決定 3（BFF は値を読まない）
- 関連する実装 ADR: **IADR-0456** 決定 4（ExternalSecret への force-sync と `resourceNames` 限定の Role）・決定 5（Reloader）、IADR-0433 決定 1（BFF に Vault の read を与えない）、IADR-0453 決定 4・5（状態の 3 値・失敗を空に縮退させない）
- 関連する実装仕様書: `.ai-context/specs/20260925_1502_sc22-supply-source-and-restart-notice.md`

## コンテキストと課題

ADR-0104 決定 1 は環境固有 ID を画面と Git（Helm values）の両経路で設定できるとし、決定 2 で**両経路を認めた代償として**「画面は『いま効いている供給元』を出す。推測せず、配備時のスイッチが決めた事実を出す。Git 経路のときは書いても効かないことを先に伝える（書き込みは拒否しない）」と定めた。決定 4 は「再起動の制約が定まるまでは、画面に『消費側が再起動する』旨を出して利用者に判断させる」と定めた。

着手時の実装（`origin/develop` a24e6258）には、供給元の欄も、送る前の再起動の告知（鍵の生成の確認を除く）も無かった（#1502 の表）。

判断が要ったのは 2 点である。**(A) 「配備時のスイッチが決めた事実」をどこから読むか**、**(B) 「消費側が再起動する」を何に基づいて書き分けるか**。

## 検討した選択肢

### A. 供給元の判定

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A1 | BFF の構成値（`SecretItems:SupplySource:<item>` を配備スクリプトが注入） | 🔴 **スイッチの写しであって事実ではない。** MSP と AST は別の release であり、写しと実際の AST の描画が食い違っても BFF は気づけない。決定 2 が禁じた「画面の表示と実際の供給が食い違う」を構造的に許す |
| A2 | allowlist に経路を宣言する | 🔴 同上（しかも環境ごとに違う値を Git に固定する） |
| A3 | **同期先の ExternalSecret を Kubernetes API で `get` し、在れば screen・404 なら git・それ以外は unknown** | AST の `ast-secrets` は `externalSecrets.enabled && appSecrets.enabled` のときだけ描画され、Discord ID の読み先を Secret へ切り替えるのも**同じ述語**である（AST の IADR-0341 決定 2・3）。有無はスイッチの結果そのもの。**権限は IADR-0456 決定 4 の Role の `get` で足り、増やさない** |
| A4 | 消費側の Deployment の env を読んで、どちらの経路を参照しているかを見る | Deployment の `get` 権限を BFF に足す必要がある。読み先の解釈（env 名と secretKeyRef の対応）を BFF が持つことになり、AST の描画の詳細に結合する |

### B. 消費側の再起動の告知

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B1 | 全項目に同じ文言（「再起動されることがある」） | 🔴 OpenD（Reloader の対象外。AST の IADR-0341 決定 4）が消費する moomoo の 2 項目で「自動で作り直される」と誤解させ得る。現にログイン情報の書き込みでは手動の再起動が要ることを画面が一言も伝えていなかった |
| B2 | BFF が Reloader の配備を検出して返す | Reloader の配備は名前空間の外（`reloader`）で、検出には追加の権限が要る。決定 4 は「制約が定まるまで利用者に判断させる」だけを求めており、検出までは要らない |
| B3 | **画面の語彙に項目ごとの作り直され方（自動 / OpenD 手動）を持ち、自動の文言は「自動再起動を配備した環境の場合」と限定して書く** | 表示の関心として画面に置く（項目の表示名・用途と同じ場所）。表に無い項目は「再起動されることがある」とだけ書き、断定しない |

## 決定

**A3 と B3 を採用する。**

### 決定 1: 供給元は同期先 ExternalSecret の有無から境界層が判定し、3 値で返す

- `GET /bff/secrets` の各行に `supplySource`（`screen` / `git` / `unknown`）を足す（`SecretItemSupplySources`。既定値つきで後方互換、BFF は常に埋める）。
- 判定器 `ExternalSecretPresenceReader` は同期依頼と**同じ構成・通信路・名乗り**（`ExternalSecretSync:*`・`KubernetesApi` クライアント・Pod の SA トークン・SA の CA で TLS 検証）を使い、`GET /apis/external-secrets.io/v1/namespaces/{ns}/externalsecrets/{name}` を投げる。
  - 2xx → `screen`／**404 → `git`**（ExternalSecret が無い。CRD 自体が無い＝ESO 未配備の 404 も同じく「画面の経路は効いていない」）／それ以外（構成無効・トークン不読・401・403・5xx・不達・時間切れ）→ `unknown`。
  - 🔴 **`unknown` を他の 2 値に畳まない。** 本番像（`externalSecretSync.enabled=false` が既定）では全項目 `unknown` になる。**これは正しい** —— BFF は配備の事実を読めないので、読めないと言う。
- 🔴 **ExternalSecret の本文は読み捨て、Secret にも Vault にも触れない。** 使う動詞は既存 Role の `get` だけ（`resourceNames` 限定）。
- 判定は**一覧を返すと決まった後**（ロール判定・保管先の確認・「1 件も取れなければ 503」の判定の後）に置き、項目ごとに並べて問い合わせる
  （1 件の遅延が一覧全体を項目数倍に延ばさない。1 件の上限は `TimeoutSeconds`）。🔴 metadata の読み取りより前に置くと、保持中のトークンで
  ログイン確認を飛ばして保管先に届かない場合（IADR-0453 決定 5）に Kubernetes API へ触れてから 503 を返す（#1502 の初版で実測）。
- 画面は受け取った値だけを出し、無い・未知の値は「確認できない」（推測しない）。`git` の項目は送信を**拒否せず**、送る前と後に「反映されない」を出し、保存後は同期の成否の文言に替えて「反映されない」を出す（同期先が無いので依頼は通らず、「定期同期を待つ」と書くと反映されるかのように読める）。

### 決定 2: 送る前に、消費側の再起動を項目ごとに告げる（画面の語彙に置く）

- `secretConsumerRestart(item)`: 外部 LLM・メール送信・Wiki 同期・株式自動売買の app-secrets → `automatic`（IADR-0456 決定 5 の注釈・AST の `reloader.enabled` の描画）／moomoo のログイン情報・OpenD の RSA 鍵 → `manual-opend`（手動の再起動コマンドと再認証の可能性を書く）／表に無い項目 → 「再起動されることがある」。
- `automatic` の文言は「**自動再起動を配備した環境の場合。配備していない環境では再起動するまで反映されない**」と限定する（Reloader はローカルの ESO=1 にしか入れていない。IADR-0456 §結果）。
- 供給元が `git` の項目では出さない（Secret が変わらないので再起動も起きない）。鍵の生成の確認（IADR-0456 決定 3）は残す。

## 理由

- **決定 2 の文言（ADR-0104）は「推測するな」であり、構成値の写しは推測の一形態である。** 写しは、写した時点の意図であって、いま描画されているものではない。有無の `get` は描画の結果を直接読む。
- **権限を 1 つも増やさずに済む。** IADR-0456 決定 4 が force-sync のために `get` / `patch` を `resourceNames` で限って与えており、`get` はその中にある。
- **3 値目を持つのは計画の 2 値（画面 / Git）を覆すためではない。** 判定できないときに片方へ倒すと、それ自体が推測になる（決定 2 の禁止に当たる）。
- 再起動の告知を画面に置いたのは、告知が表示の関心だからである（決定 4 は検出を求めていない）。配備の事実に依存する部分は文言で限定し、断定しない。

## 結果

- **良い影響**:
  - 画面から書いた値が効くかどうかを、書く前に項目ごとに読める（ADR-0104 決定 2）。AST_ESO=0 の配備では app-secrets と moomoo の 3 項目が「Git」と出る。
  - moomoo のログイン情報を書くと OpenD の手動再起動が要ることを、画面が送る前に伝えるようになった。
- **悪い影響・トレードオフ**:
  - 一覧のたびに項目数（6）の `get` が Kubernetes API へ飛ぶ。
  - 本番像では全項目「確認できない」になる（同期の構成が無効のため）。本番で出すには `externalSecretSync.enabled` と `apiServerEgress.cidrs`（IADR-0456 §結果）が要る。
  - 株式自動売買の名前空間の Role（`deploy/local/vault/eso/rbac-bff-externalsecret-sync.yaml`）は ESO=1 のときだけ適用される。適用されていない配備では `get` が 403 になり「確認できない」に倒れる（「Git」とは言わない）。
  - 初期ロードが +4,074 B（Lingui カタログの文言。`scripts/chunk-budget-baseline.json` に記録）。
- **フォローアップ**:
  1. 稼働クラスタでの判定の実測（SC-22 テスト仕様書の手動の項。T-40 と同じ場）。
  2. ADR-0104 決定 3 の公開鍵の表示は本 IADR の射程外（置き場の設計と、公開鍵の登録が要るという前提の確認が要る。#1502 の PR 本文）。
  3. 再起動を**いつ**行ってよいかの制約と、同期を促す UI の形（ADR-0104 フォローアップ 2。計画で未定）。定まったら決定 2 の文言を改める。

## 関連

- Supersedes: なし（IADR-0456 に同日付の追記を置いた —— 同 決定 4 の Role の `get` を一覧が使うようになったこと）
- Superseded by: なし
