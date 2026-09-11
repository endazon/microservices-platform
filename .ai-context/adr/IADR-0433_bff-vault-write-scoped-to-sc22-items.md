---
title: IADR-0433 BFF の Vault 書き込みは項目ごとの許可で与え、値を読み返せない形にする
type: impl-adr
status: Accepted
related_ids: [SC-22, FR-05, NFR-11, NFR-18, ADR-0040, ADR-0042, ADR-0095, ADR-0032, ADR-0007]
author: claude
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# IADR-0433: BFF の Vault 書き込みは項目ごとの許可で与え、値を読み返せない形にする

- 状態: Accepted
- 日付: 2026-09-11
- 決定者: claude（#1411。計画 ADR-0095 決定 3 の実装設計）

## 起点・関連

- 関連する計画書 ID: SC-22（秘密情報・接続設定の管理。**未実装・モックアップ未受領**）／FR-05／NFR-18（シークレット管理）／NFR-11（全経路 HTTPS）
- 関連する計画 ADR: ADR-0095 決定 3・決定 1（境界は「Git に置けるか」）／ADR-0032（BFF セッション方式）／ADR-0040 決定 3・ADR-0042 決定 4（射程は Headlamp に確定した）
- 関連する実装 ADR: IADR-0096〜IADR-0099（Vault ＋ ESO による secret 供給）／IADR-0094（Vault の Keycloak OIDC 認証）／IADR-0251・IADR-0273（BFF セッション）
- 関連する実装仕様書: `.ai-context/specs/20260911_issue-1411_sc22-console-fallback-and-bff-vault-write.md`
- 起票: #1411（環流 planning#599 の裁定）

## コンテキストと課題

ADR-0095 決定 3 は「**BFF が Vault へ書く。ESO は読み取り専用の同期を維持する**」と定め、
さらに「**書き込みの射程を項目で限る。BFF が Vault の任意のパスへ書けるようにしない**」と
条件を付けた。同 ADR の「統制と現在の実現手段」の表は、この統制について
🔴 **「決定 3 を実装する時点で同時に配備しなければ、射程の無い書き込み権限が先に生まれる」**
と警告している。

一方 **SC-22 のモックアップは未受領であり、画面には着手できない**（利用者裁定 2026-07-30）。
そこで本 ADR は、**画面が来たときに「射程の無い権限」を先に作らずに済むよう、
権限の形だけを先に決める**。

決めなければならないのは次の 6 点である。

1. Vault policy の書き方（どのパスに、どの capability を与えるか）
2. BFF が Vault に対して名乗る手段（k8s auth のロールと束縛先）
3. 「SC-22 が扱う項目の集合」を**どこが持つか**
4. ESO の権限を変えるか
5. 監査イベントの形（値を残さずに何を残すか）
6. 端点の契約（画面が呼ぶ口の形）

### 現状の実測（`origin/develop` `cee62d58`）

- ESO 用の policy は `deploy/local/vault/eso/policy-eso-read.hcl` にあり、**`read` と `list` だけ**を
  `secret/data/msp/*` ／ `secret/data/ai-stock-trading/*` に与えている。**write は存在しない。**
- k8s auth のロールは `bootstrap.sh` が `auth/kubernetes/role/eso` を 1 つだけ作り、
  ESO の ServiceAccount（`external-secrets/external-secrets`）に束縛している。
- 🔴 **BFF の Deployment は `serviceAccountName` を持たない** —— `deploy/helm/` を走査しても
  1 件も出ない。つまり名前空間の `default` ServiceAccount で動いている。
- ESO が同期する MSP の項目は **28 件**、AST 側は KV **3 件**である（走査は作業仕様書の「走査した母集合」）。
- Vault は `hashicorp/vault:1.16` である（KV v2 の `patch` に対応する）。

## 検討した選択肢

### 1. policy のパスの書き方

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | `secret/data/msp/*` に `create` / `update` | 🔴 **ADR-0095 決定 3 の「項目の集合の外へ書けない」が成立しない。** 28 項目すべてが書ける |
| B | 項目ごとに**完全一致のパス**を 1 つずつ書く | 項目が増えるたびに policy を足す必要があるが、**外へ出られないことが policy の字面で読める** |
| C | 接頭辞を分ける（`secret/data/msp/screen/*` へ引っ越す） | 引っ越しに ESO の全 ExternalSecret の書き換えが伴う。**稼働中の供給経路を触る**ので割に合わない |

### 2. 「値を読み返せない」の実現

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | `read` も与え、BFF 側のコードで読まないようにする | 🔴 **統制がコードの自制になる。** 権限が残るのでレビューでしか守れない |
| B | `read` を与えず `create` / `update` だけ与える | `vault kv put` は通るが、**複数プロパティを持つ KV で他のプロパティが消える**（`put` は全置換） |
| C | `read` を与えず `create` / `update` / **`patch`** を与える | KV v2 の HTTP PATCH は**部分更新であり read を要さない**。他のプロパティを保ったまま 1 つだけ書ける |

### 3. 項目集合の出所

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | Vault に `list` して列挙する | 🔴 **`list` を与えると「項目の集合の外」を知れてしまう。** 決定 3 と向きが逆である |
| B | BFF のコードに配列で書く | 変更にデプロイが要り、policy との突合が人の目になる |
| C | **リポジトリのデータファイル 1 つ**を単一情報源にし、policy・BFF・Runbook がそこを引く | 3 つの読み手が同じ字面を見る。差分がレビューに出る |

### 4. BFF の名乗り

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | `default` ServiceAccount に束縛する | 🔴 **名前空間の全 Pod が同じ権限を得る。** `vault-auth-rbac.yaml` が同じ理由で `default` を避けている先例がある |
| B | BFF 専用の ServiceAccount を作り、それに束縛する | Deployment に 1 行足すだけで、権限の主体が名前で読める |
| C | 静的トークンを Secret で渡す | 回転できず、Secret を読めた者が Vault を書ける。IADR-0096 が k8s auth を選んだ理由に反する |

## 決定

**1-B / 2-C / 3-C / 4-B を採用する。** 加えて ESO は変更しない。

### 決定 1: policy は項目ごとの完全一致パスで書き、`read` を与えない

```hcl
# 例（実際の項目は deploy/bootstrap/sc22-secret-items.json が持つ）
path "secret/data/msp/llm-provider-credentials" {
  capabilities = ["create", "update", "patch"]
}
path "secret/metadata/msp/llm-provider-credentials" {
  capabilities = ["read"]
}
```

- 🔴 **ワイルドカードを使わない。** `secret/data/msp/*` と書いた瞬間に決定 3 の統制は消える。
- 🔴 **`secret/data/<item>` に `read` を与えない。** 値を読み返す経路を権限の層で断つ。
  「読まないコードを書く」ではなく「読めない権限を渡す」。
- **`secret/metadata/<item>` の `read` だけを与える。** KV v2 のメタデータは
  **版番号・作成時刻・更新時刻を持ち、値を持たない。** SC-22 の「投入済みかどうかだけを表示する」
  「いつ更新したかを表示する」は**これで足りる**。
- **`list` はどの階層にも与えない**（`secret/metadata/msp/` の `list` も含む）。
  一覧は allowlist から作るのであって、Vault から作らない。
- **`delete` / `destroy` も与えない。** SC-22 に削除の要件は無い。

### 決定 2: 書き込みは KV v2 の部分更新（`patch`）で行う

- 🔴 **`put`（全置換）を既定にしない。** `msp/keycloak-smtp` は `host` / `port` / `starttls`（構成）と
  `from` / `user` / `password`（秘密）が**同じ KV に同居している**。`put` は前者を消す。
  `ai-stock-trading/app-secrets` も同型である（15 プロパティのうち 7 つだけが対象）。
- KV v2 の HTTP PATCH は **`patch` capability を要し、`read` を要さない。**
  これが決定 1 の「`read` を与えない」と両立する唯一の書き方である。
- **Vault 1.16 で使える**（`patch` は 1.9 以降）。

### 決定 3: 項目集合の単一情報源は `deploy/bootstrap/sc22-secret-items.json` である

- 読み手は 3 つ: **(a) policy を起こす者**（決定 1 のブロックを項目数ぶん生成する）、
  **(b) BFF の起動時 allowlist**、**(c) 運用 Runbook の対象表**。
- 🔴 **BFF がこのファイルを読めなかったら起動しない**（fail-closed）。
  「読めなかったから全部許す」も「読めなかったから空にする」も採らない ——
  前者は統制の消失、後者は**画面が静かに全項目 400 を返す**（原因が分からない）。
- ファイルは `items[]`（allowlist）のほかに `deferred[]` と `excluded[]` を持つ。
  🔴 **allowlist は `items[]` だけである。** 他の 2 つは「なぜ入っていないか」の記録であり、
  **BFF はそれらを読まない**（読むと「載っているから許す」という誤実装の入口になる）。

`items[]` に入れた項目と、外した理由:

| 区分 | 対象 | 判断 |
| --- | --- | --- |
| `items[]` | `msp/llm-provider-credentials` / `msp/keycloak-smtp`（`from` `user` `password` のみ）／`msp/wikijs-sync` ／ `ai-stock-trading/app-secrets` の外部 API キーと通知の webhook / token 7 件 | **外部から供給され、人が持ち込むほかに入る道が無い。** ADR-0095 決定 1 が名指しする種別である |
| `deferred[]` | OIDC クライアントシークレット群と east-west gRPC の s2s 資格情報（計 18 件）／`ai-stock-trading/moomoo`・`moomoo-rsa` | 🔴 **realm の宣言と同値でなければならず、片側だけ書くと認証が静かに壊れる。** 認証基盤への書き込みと**対**で設計する必要がある。`moomoo-rsa` は値がファイル形（PEM）で UI の作法が別である |
| `excluded[]` | `msp/postgres` / `postgres-app` / `rabbitmq` / `rabbitmq-app` / `keycloak-admin` / `minio-credentials` / `wikijs-db` | 🔴 **稼働中のデータストアが既存パスワードで初期化済みである。** Vault 側だけ書き換えると ESO が誤った資格情報を配って認証が壊れる。**回転は画面の 1 欄では成立しない。恒久的に対象外とする** |

### 決定 4: BFF 専用の ServiceAccount を作り、k8s auth ロールをそれに束縛する

```
vault write auth/kubernetes/role/bff-secret-writer \
  bound_service_account_names=bff \
  bound_service_account_namespaces=microservices-platform \
  policies=bff-secret-write ttl=1h
```

- 🔴 **`default` に束縛しない。** 現状 BFF は `default` で動いているため、そのまま束縛すると
  **名前空間の全 Pod が秘密を書けるようになる。** ServiceAccount を新設し、
  BFF の Deployment に `serviceAccountName` を足すことを本決定の一部とする。
- **ロールは `eso` と別に作る。** 同じロールへ policy を足すと、ESO が書けるようになる（決定 5 に反する）。
- TTL は既存の `eso` ロールに合わせて 1 時間とする。

### 決定 5: ESO は読み取り専用のまま。`policy-eso-read.hcl` を変更しない

ADR-0095 決定 3 がそう定めている。**供給（Vault → k8s Secret）と投入（画面 → Vault）は別の経路であり、
別の主体が別の権限で行う。** 片方に両方を持たせない。

### 決定 6: 監査イベントは既存の監査ログ機構に相乗りし、値に関わる一切を残さない

`IAuditLogger`（`Audit=true` の構造化ログ。`Shared.Infrastructure/Foundation/Audit`）へ次の形で出す。
構成情報 API の監査（`action` / `subject` / `outcome` / `detail`）と同じ 4 項目に揃える。

| 属性 | 値 |
| --- | --- |
| `action` | `secret.item.update` |
| `subject` | 利用者名（BFF セッションの主体） |
| `outcome` | `granted` / `denied` |
| `detail` | 項目名・プロパティ名・書き込み後の版番号（`secret/metadata` から得る）。`denied` のときは理由（`not-in-allowlist` / `forbidden`） |

- 🔴 **値・値の長さ・値のハッシュ・値の先頭数文字のいずれも入れない。**
  長さは推測の手掛かりになり、ハッシュは辞書攻撃の対象になる。
- **`denied` も記録する。** allowlist 外への試行が残らないと、統制が働いた事実も残らない。

### 決定 7: 端点の契約（**素描。確定はモックアップ受領後**）

| 項目 | 形 |
| --- | --- |
| 口 | `PUT /bff/secrets/{item}` |
| 認可 | `AdminOnly`（既存の `PlatformAuthPolicies.AdminOnly`。ADR-0095 は「運用者とシステム管理者に限る」と書いており、**運用者を含めるかはモックアップ時に確定する**） |
| 本体 | プロパティ名と値の対（1 回の呼び出しで 1 項目） |
| allowlist 外 | **400**。🔴 **404 にしない** —— 存在秘匿の対象ではなく「入力が不正」であり、項目名の一覧は画面が既に持っている |
| 応答 | 更新後の版番号と更新時刻。**値は返さない** |
| 読み出し口 | **作らない。** `GET /bff/secrets/{item}` は存在しない。一覧（`GET /bff/secrets`）は allowlist と metadata から作り、値を含まない |

### いま決めたことと、モックアップ受領後に決めること

| いま決めた（本 ADR） | モックアップ受領後に決める |
| --- | --- |
| policy のパスと capability（決定 1・2） | 一覧画面に出す列（項目名・投入済みか・最終更新日時 の 3 つで足りるか） |
| 項目集合の単一情報源とその中身（決定 3） | **UI の粒度**（KV 単位か、プロパティ単位か）。`keycloak-smtp` と `app-secrets` は 1 KV に複数プロパティを持つ |
| BFF の名乗りと束縛先（決定 4） | `deferred[]` の 20 件を扱うか。扱うなら認証基盤への対の書き込みをどう設計するか |
| ESO を変えないこと（決定 5） | 確認ダイアログの有無・入力の再入力確認の有無 |
| 監査イベントの形（決定 6） | `AdminOnly` に運用者を含めるか |
| 端点の形と「読み出し口を作らない」こと（決定 7） | 端点の本体スキーマの確定・エラー本文の文言 |

🔴 **本 ADR は配備しない。** policy も ServiceAccount も**画面と同時に配備する** ——
ADR-0095 の統制の表が「決定 3 を実装する時点で同時に配備しなければ、射程の無い書き込み権限が
先に生まれる」と警告しているためである。**本 ADR が置くのは形だけである。**

## 理由

- **ワイルドカードを使わない理由は、統制が「設定の字面で読めること」にあるからである。**
  `secret/data/msp/*` と `items[]` の組み合わせでも、BFF が正しく実装されていれば結果は同じになる。
  🔴 **だが「BFF が正しく実装されていれば」という条件が付く時点で、それは権限による統制ではない。**
- **`read` を落として `patch` を足したのは、「読めない」と「他を壊さない」を同時に満たす書き方が
  それしか無いからである。** `read` を残す案は統制をコードの自制に落とし、`put` だけの案は
  同居するプロパティを消す。**2 つの制約が交差する点が 1 つしか無かった。**
- **項目集合をファイルに置いたのは、読み手が 3 つあるからである。** policy と BFF とだけなら
  コードでも足りたが、**Runbook（人が読む手順書）が同じ集合を引く**。3 者が同じ字面を見る形にすると、
  **allowlist を広げる変更が必ずレビューの差分に出る。**
- **`excluded[]` を「まだ決めていない」ではなく「恒久的に対象外」としたのは、
  実際に壊れる筋道が具体的に分かっているからである。** データストアは既存パスワードで初期化済みであり、
  Vault 側の値を変えてもストアのパスワードは変わらない。**画面から入れられるように見えることが、
  そのまま事故になる。**
- **`deferred[]` を `excluded[]` と分けたのは、こちらは設計すれば解けるからである。**
  認証基盤への対の書き込みを設計すれば画面から扱える。**解ける問題と解けない問題を同じ箱に入れない。**
- **専用 ServiceAccount にしたのは、`default` への束縛が「BFF に権限を与える」ではなく
  「名前空間に権限を与える」になるからである。** 先例（`vault-auth-rbac.yaml` が vault 専用 SA を作り
  `default` を避けた）と同じ判断である。
- **監査に `denied` を含めたのは、統制は働いたときにも痕跡を残すべきだからである。**
  `granted` だけを記録すると、**攻撃の試行と「何も起きていない」が同じ見え方になる。**

## 結果

- **良い影響**:
  - 画面の実装に着手する時点で、**権限の形について決めることが残っていない。**
  - **「射程の無い書き込み権限が先に生まれる」という ADR-0095 の警告に対する具体的な防ぎ方**
    （配備は画面と同時）が記録に残った。
  - 項目集合が走査で確定し、**28 ＋ 3 のうち画面で扱うのは 4 KV・13 プロパティだけ**と分かった
    （`items[]` の `properties` を数えた値。`deferred[]` は Vault パスで 20 件、`excluded[]` は 7 件）。
    画面の設計が扱う対象が 1 桁小さくなる。
  - 同じ集合を退避手順（Runbook）も引くため、**画面経路とコンソール経路が同じ境界を持つ。**
- **悪い影響・トレードオフ**:
  - 🔴 **policy が項目数ぶん長くなる。** 項目が増えるたびに policy を足す作業が発生する
    （生成器を書く余地はあるが、いまは項目が 4 つなので書かない）。
  - 🔴 **`patch` は KV v2 と Vault 1.9 以降に依存する。** シークレットストアを替えるとこの決定は成り立たない。
  - **`deferred[]` の 20 件は、画面ができても当面コンソール経路に残る。** 退避手段が「例外」ではなく
    「その 20 件の唯一の経路」であり続ける期間がある。
  - **本 ADR は 1 行も実装していない。** 形だけを決めたので、**画面の実装時に「決めたとおりに作られたか」を
    確かめる者が要る**（policy の字面と `items[]` の突合は機械化できるが、本 PR では置いていない）。
- **フォローアップ**:
  1. モックアップ受領後、SC-22 の画面仕様書（`docs/screens/`）を起こす。上表「モックアップ受領後に決める」を埋める。
  2. policy（`deploy/local/vault/eso/policy-bff-secret-write.hcl` 相当）・ServiceAccount・
     `bootstrap.sh` のロール追加を、**画面・端点と同じ PR で**配備する。
  3. `items[]` と policy の突合（allowlist に載っていてパスが無い／その逆）を機械検査にする。
     🔴 **同型の事故が 2 回起きたら**が検査器追加の条件であるため、いまは置かない（本項は記録である）。
  4. `deferred[]` の扱いを計画へ問う（**モックアップ受領時に一括して問う**。いま問うと裁定が二重になる）。

## 関連

- Supersedes: なし
- Superseded by: なし
