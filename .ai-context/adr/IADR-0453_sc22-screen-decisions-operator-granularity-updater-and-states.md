---
title: IADR-0453 SC-22 は運用者を含め、プロパティ 1 つずつ書き、最終更新者は BFF が書いた版にだけ付け、状態は metadata から 3 値で出す
type: impl-adr
status: Accepted
related_ids: [SC-22, FR-05, NFR-11, NFR-18, ADR-0032, ADR-0042, ADR-0095, IADR-0009, IADR-0030, IADR-0096, IADR-0251, IADR-0433]
author: claude
created: 2026-09-14
updated: 2026-09-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md
  - planning:projects/microservices-platform/07_adr/ADR-0042_ops-management-ui-production.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# IADR-0453: SC-22 は運用者を含め、プロパティ 1 つずつ書き、最終更新者は BFF が書いた版にだけ付け、状態は metadata から 3 値で出す

- 状態: Accepted
- 日付: 2026-09-14
- 決定者: claude（#1411。IADR-0433 が「モックアップ受領後に決める」とした論点を、画面の実装時点で確定する）

## 起点・関連

- 関連する計画書 ID: SC-22（秘密情報・接続設定の管理）／FR-05／NFR-18／NFR-11
- 関連する計画 ADR: ADR-0095 決定 1〜4（**着手可否の注記が「モックアップの受領を着手の条件にしない」と明記**）／ADR-0042 決定 2（公開範囲は運用者・システム管理者）／ADR-0032（BFF セッション）
- 関連する実装 ADR: **IADR-0433**（権限の形。本 ADR は同 ADR を覆さず、先送り分を埋める）／IADR-0096（Vault ＋ ESO の k8s auth）／IADR-0030（運用者ロール）／IADR-0009（存在秘匿）／IADR-0251（BFF セッションの Redis）
- 関連する実装仕様書: `.ai-context/specs/20260914_issue-1411_sc22-secret-injection-screen.md`
- 起票: #1411

## コンテキストと課題

IADR-0433 は Vault policy・k8s auth ロール・allowlist・監査・端点の形を決め、
**「モックアップ受領後に決める」**として次を残した。

| IADR-0433 が残した論点 | 本 ADR |
| --- | --- |
| `AdminOnly` に運用者を含めるか | 決定 1 |
| UI の粒度（KV 単位かプロパティ単位か） | 決定 2 |
| 一覧に出す列（項目名・投入済みか・最終更新日時の 3 つで足りるか） | 決定 3・決定 4 |
| 確認ダイアログの有無・再入力確認の有無 | 決定 6 |
| 端点の本体スキーマ・エラーの形 | 決定 5・決定 7 |
| `deferred[]` を扱うか | 決定 8 |

ところが計画 ADR-0095 は**「本 ADR はモックアップの受領を着手の条件にしない —— 画面の要件は本 ADR と SC-22 の記述で定まる」**と
明記している。**「モックアップ待ち」は計画の側では解けていた。** 本 ADR は SC-22 の文言を一次情報として、残りを決める。

加えて、SC-22 の文言そのものが IADR-0433 の統制と 2 か所で**擦れる**。

1. SC-22 は列に **「最終更新者」** を求める。🔴 **Vault KV v2 の metadata は更新者を持たない。**
   `custom_metadata` に書けば持てるが、それには `secret/metadata/<item>` への `create` / `update` が要り、
   IADR-0433 決定 1（metadata は `read` だけ）を広げる。
2. SC-22 は「**値が入っていない項目を『未設定』として出す**」と求める。🔴 **プロパティに値が入っているかは data を読まないと分からない。**
   data の `read` を与えないことが IADR-0433 決定 1 の統制の中心である。

## 検討した選択肢

### A. 運用者を含めるか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A1 | `AdminOnly`（システム管理者のみ） | 🔴 **SC-22「運用者・システム管理者ロール限定」と ADR-0042 決定 2 に反する** |
| A2 | 既存の `ConfigViewer`（admin ＋ operator）を流用する | 集合は同じだが、**閲覧の名前に書き込みを相乗りさせる**。SC-11 の公開範囲を狭めたとき、秘密の書き込みまで黙って動く |
| A3 | **新ポリシー `SecretItemWriter`（admin ＋ operator）** | 集合は A2 と同じで、**変える理由が別々に来ても片方だけ動かせる** |

`OperatorRole` の定義コメントは「構成閲覧のみ。管理系操作は不可」と書いている（IADR-0030 時点）。
しかし辺の型辞書の管理（SC-09 の書き込み）は既に admin ＋ operator であり、**このコメントは実態より狭い**。
**計画（SC-22）が運用者を明示している以上、コメントの側を実態と計画へ揃える。**

### B. UI の粒度

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B1 | KV 単位で全プロパティの入力欄を並べる | 🔴 `ast-app-secrets` は 7 つ書ける。**全部を一度に送る形は「一括再投入」と同じ形**になり、1 つ直すために他を空で上書きする事故の入口になる（ADR-0095 決定 4） |
| B2 | **KV を 1 行に出し、更新はプロパティを 1 つ選んで 1 つだけ送る** | SC-22「1 項目ずつ更新する」を最も狭く読む。`PUT` 1 回 ＝ 1 プロパティ ＝ 監査 1 行 |
| B3 | プロパティを 1 行ずつ一覧に出す | 13 行になり、同じ KV の行が並ぶ。**KV 単位でしか取れない状態（決定 4）がプロパティ行に重複して見え、プロパティ単位で判定しているかのように読める** |

### C. 最終更新者の出所

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C1 | Vault の `custom_metadata` に更新者を書く | 🔴 metadata への書き込み権限が要る（`max_versions` や `delete_version_after` も変えられる）。IADR-0433 決定 1 を広げる |
| C2 | 監査ログ（ログ基盤）から引く | BFF にログ基盤の読み取り口が無い。**新しい依存と新しい読み取り権限**を足すことになる |
| C3 | **BFF が書いた版を Redis に記録し、Vault の現在版と一致するときだけ出す** | Vault の権限は変えない。**版で突き合わせるので、コンソールや bootstrap が後から書いた版に画面の利用者名が付くことは無い**（一致しない → 「記録なし」） |
| C4 | 列を置いて常に「記録なし」と出す | 正直だが、SC-22 の列が恒久的に空になる |

### D. 状態の出し方

| 案 | 内容 | 評価 |
| --- | --- | --- |
| D1 | data を読んでプロパティごとに空欄を判定する | 🔴 **IADR-0433 決定 1 の統制を捨てる**（値を読み返せる権限を BFF に渡す） |
| D2 | **metadata だけで KV 単位の 3 値を出す** | 404 ＝ 未設定／版あり ＝ 設定済み／取れない ＝ 取得できない。**プロパティ単位の空欄は分からない**ことを画面に書く |

## 決定

**A3 / B2 / C3 / D2 を採用する。** 加えて次を決める。

### 決定 1: 到達できるのは運用者とシステム管理者。新ポリシー `SecretItemWriter` で判定する

- `PlatformAuthPolicies.SecretItemWriter` ＝ `RequireRole(platform-admin, platform-operator)`。
- 画面: `RequireRole anyOf=[admin, operator]`。権限外は NotFound を描き、**画面チャンクも一覧も取りに行かない**。左ナビ「運用」グループ。
- BFF: 群に `RequireAuthorization()`（未認証 401）、**ロールはハンドラ内で評価して拒否を監査してから 403**。
  `RequireAuthorization(policy)` を群に付けると、ミドルウェアが先に 403 を返して**拒否が監査に残らない**（IADR-0433 決定 6「denied も記録する」）。
- 🔴 **404 で存在を秘匿しない。** 管理 API の存在は秘匿の対象ではない（`BFF_bff-surface.md` §横断の規約 1「その他の管理系」と同じ）。
  秘匿するのは画面の側（NotFound とメニュー非表示）である。

### 決定 2: 一覧は KV 1 行、更新はプロパティ 1 つずつ

- `PUT /bff/secrets/{item}` の本文は `{ property, value, reason? }`。**1 回の呼び出しで書くのは 1 プロパティだけ**。
- 画面のフォームは「書けるプロパティ」（`items[].properties`）だけを選択肢に出す。`notWritable` は出さず、送られても BFF が 400 で拒む。
- 🔴 **一括再投入のボタン・複数プロパティを一度に送る口を作らない**（画面にも契約にも）。

### 決定 3: 最終更新者は「BFF が書いた版の記録」と Vault の現在版が一致するときだけ出す

- 書き込みが成功したら、`IDistributedCache`（BFF セッションと同じ Redis）へ
  `bff:sc22:write-record:<item>` ＝ `{ version, updatedBy, property, updatedAt }` を置く。
- 一覧では metadata の `current_version` と記録の `version` が**一致するときだけ** `lastUpdatedBy` を返す。一致しなければ null（画面は「記録なし」）。
- 🔴 **記録の読み書きの失敗は握る。** 書き込みは Vault で既に成立しており、記録が置けないことを理由に 5xx を返すと
  「書けたのに失敗と表示される」ことになる。**記録が消えた場合は「記録なし」へ倒れる**（誤った名前は出ない）。
- **正本は監査ログである。** 本記録は画面に 1 列を出すための付随物であり、監査の代わりにしない。
- 最終更新日時は記録ではなく **Vault の metadata**（現在版の `created_time`）から出す。コンソールで書いた版にも日時は付く。

### 決定 4: 状態は metadata から KV 単位の 3 値で出し、プロパティ単位は判定しないと明示する

| metadata の結果 | 状態 | 画面 |
| --- | --- | --- |
| 404 | `notSet` | 状態バッジ「未設定」 |
| 200、現在版が削除・破棄されていない | `set` | 最終更新日時 |
| 200、現在版が削除・破棄されている | `notSet` | 状態バッジ「未設定」 |
| 403・5xx・不達・応答を解釈できない | `unavailable` | 状態バッジ「取得できない」 |

- 🔴 **「設定済み」は「KV に版がある」の意味である。プロパティに空でない値が入っていることは意味しない。**
  bootstrap（`deploy/local/vault/eso/bootstrap.sh`）は `anthropic-api-key=''` のように**空文字で seed する**ため、
  bootstrap 直後の KV はすべて「設定済み」と出る。**画面の注記でこれを明示する。**
- 状態は色だけで示さない（`StatusBadge` が色 ＋ アイコン ＋ テキストを強制する）。

### 決定 5: Vault が無い・届かないときは 503 で失敗を見せる

| 状況 | 一覧 | 更新 | 監査 |
| --- | --- | --- | --- |
| `Vault:Address` が未設定（Vault を配備していない構成） | **503**（`vault-not-configured`） | **503** | `failed` |
| ログインが不達・拒否される | **503**（`vault-unavailable`） | **503** | `failed` |
| 個別の metadata が取れない | 200。その行だけ `unavailable` | — | — |
| **全項目の** metadata が取れない（保持中のトークンでログイン確認を飛ばした後に Vault が落ちた等） | **503**（`vault-unavailable`） | — | `failed` |
| 書き込みを Vault が拒否した（policy と allowlist の食い違い等） | — | **502**（`vault-rejected`） | `failed` |

- 🔴 **一覧を空配列で返さない。** 「扱う項目が 0 件」と「保管先に届かない」を同じ見た目にしない。
- 🔴 **更新を黙って成功扱いにしない。**

### 決定 6: 確認は「2 度目の入力」だけにし、確認ダイアログは置かない

- 値と確認入力はどちらもマスクし、**一致しない・空のとき送信ボタンを無効にして理由を出す**（SC-22 入力規則）。
- **確認ダイアログは置かない。** KV v2 は旧版を保持し、誤った値は書き直しで戻せる。
  一方、無効化（SC-17）や完全削除（SC-19）のような取り返しのつかない操作ではない。2 度入力がそのまま確認である。

### 決定 7: 契約とエラーの形

- `GET /bff/secrets` → `SecretItemStatusDto[]`（`item` / `vaultPath` / `properties` / `status` / `currentVersion` / `lastUpdatedAt` / `lastUpdatedBy`）。**値の項目を持たない。**
- `PUT /bff/secrets/{item}` → `SecretItemWriteResultDto`（`item` / `property` / `version` / `updatedAt`）。**値を返さない。**
- 400（RFC7807 ValidationProblem）: allowlist 外の項目（**404 にしない**。IADR-0433 決定 7）／書けないプロパティ／空の値・8192 文字超／理由 500 文字超。
  🔴 **エラー本文に値を入れない**（長さも入れない）。
- 監査 `outcome` は `granted` / `denied` に **`failed`** を足す（Vault 側の失敗。許可・拒否のどちらでもないため）。
  `detail` は `item=… property=… version=… reason="…"`（**利用者の入力した理由は二重引用符で囲み、`\` と `"` をエスケープする**。
  入力に `version=99 item=postgres` のような文字列を入れても、`key=value` を読む側が監査行を取り違えないため）。
  拒否・失敗の理由（機械語・引用符なし）は `not-in-allowlist` / `property-not-writable` /
  `invalid-value` / `invalid-reason` / `forbidden` / `vault-not-configured` / `vault-unavailable` / `vault-rejected`。
  一覧は `secret.item.list`（`granted` / `denied` / `failed`）。
- 書き込みは **KV v2 の `PATCH`（`Content-Type: application/merge-patch+json`）**。KV が存在しない（404）ときだけ
  **`POST` ＋ `options.cas=0`**（存在しないときだけ作る。既存の KV を全置換しない）。`cas=0` が競合で失敗したら `PATCH` を 1 度だけやり直す。

### 決定 8: `deferred[]` / `excluded[]` は扱わない

IADR-0433 決定 3 のまま。**本 ADR は `items[]` を 1 行も動かさない。** 画面が扱うのは 4 KV・13 プロパティである。

### 決定 9: allowlist はイメージへ同梱し、配備は画面と同時に行う

- `Platform.Bff.csproj` が `deploy/bootstrap/sc22-secret-items.json` を `Content` としてリンクし、出力ディレクトリへ置く。
  Dockerfile は同じファイルを COPY する。**写しを作らない**（helm の `files/` に置くと 2 つ目の真実になる）。
- helm は BFF 専用 ServiceAccount `bff` を作り、BFF の Deployment が `serviceAccountName: bff` で使う（IADR-0433 決定 4）。
- Vault の policy（`policy-bff-secret-write.hcl`）と role（`bff-secret-writer`）は `bootstrap.sh` が入れる。
  **policy の path 集合が `items[]` と一致しワイルドカードを含まないことを xUnit で固定する**
  （IADR-0433 フォローアップ 3 は「同型の事故 2 回」を待つとしていたが、**配備と同じ PR で policy を初めて書く**以上、
  書いた瞬間から突合できる形で置く。検査器（`scripts/`）ではなく既存のテストスイートに載せ、CI の新設はしない）。

### 決定 10: data の `update` を与えない（IADR-0433 決定 1 の capability 列を狭める）

- `policy-bff-secret-write.hcl` の data パスは **`create` / `patch` だけ**にする（metadata は `read` のまま）。
- BFF の書き込み経路は **`PATCH`（`patch`）と、KV が無いときの `POST` ＋ `cas=0`（`create`）の 2 つしかない**（決定 7）。
  `update` を要する経路はコードに無い。
- 🔴 **`update` を残すと、BFF のトークンで `POST` による KV の全置換ができる。** `msp/keycloak-smtp` の `host` / `port` / `starttls`
  （構成）や `ai-stock-trading/app-secrets` の `*-auth-client-*`（realm と対）を消せてしまい、ADR-0095 決定 4 が防ぐ事故の形になる。
  「コードが全置換しない」で守るのは、IADR-0433 決定 1 が退けた「統制がコードの自制になる」形である。
- **帰結**: KV の現在版がソフト削除された状態（metadata は在る）では、`cas=0` の作成が `update` を要して 403 になり、
  画面は 502（`vault-rejected`）を出す。変更前も同じ状態は `cas=0` 失敗 → `PATCH` 404 で 502 だったため、**利用者から見た結果は変わらない**
  （区別した文言はフォローアップ 5）。
- 経緯: PR #1466 のフェーズ末監査（別文脈のエージェント）が「HCL の注記が存在しない経路を理由にしている」と検出した。
  IADR-0433 には同日付の追記ブロックを置いた。

## 理由

- **運用者を含めたのは、計画が名指ししているからである**（決定 1）。実装側のコメント（「管理系操作は不可」）は計画より古く、実態とも食い違っていた。
- **プロパティ 1 つずつにしたのは、事故の形がそこにあるからである**（決定 2）。ADR-0095 の起点は「1 つ直すために全部を賭けた」ことであり、
  KV 単位の入力欄は画面の上で同じ賭けを再現する。
- **最終更新者に Vault の権限を足さなかったのは、統制の字面を守るためである**（決定 3）。
  metadata の書き込みは「値」ではないが、**版の保持数や自動削除を変えられる**。1 列のために統制の説明を 1 行増やすより、
  BFF が自分で書いた事実だけを版と組にして持つほうが狭い。**版で突き合わせる**ことで、記録が古くなっても誤った名前は出ない。
- **状態を KV 単位に留めたのは、プロパティ単位の判定が「読める権限」と同値だからである**（決定 4）。
  SC-22 の要求を満たしきれない部分は、**満たしたふりをせず画面と本 ADR に書く**。
- **503 にしたのは、Vault が無い構成（`VAULT=1` なし）が普通に存在するからである**（決定 5）。
  そこで一覧が空で返ると、利用者は「扱う項目が無い」と読む。

## 結果

- **良い影響**:
  - 秘密情報の投入が画面から完結し、**運用者へ kubeconfig を配る理由が 1 つ減る**（ADR-0095 の効果測定の対象）。
  - BFF の書き込み権限は `items[]` の 4 KV に限られ、**その事実が policy の字面とテストの両方で読める**。
  - BFF は `default` ServiceAccount を離れ、Vault の role はそれにだけ束縛される。
- **悪い影響・トレードオフ**:
  - 🔴 **「設定済み」はプロパティに値が入っていることを保証しない**（決定 4）。bootstrap 直後は全項目が「設定済み」と出る。
  - 🔴 **最終更新者はコンソール・bootstrap で書いた版には付かない**（決定 3）。Redis が消えれば画面で書いた版にも付かなくなる。
  - BFF が Redis を**セッション以外の用途**にも使うようになる（キーの接頭辞 `bff:sc22:` で分ける）。
  - 監査の `outcome` の値域が 3 値になる（`failed`）。監査を抽出する側のクエリが 2 値を前提にしていれば追随が要る。
- **フォローアップ**:
  1. 🔴 **計画への問い（planning#631 で起票した・2026-09-15。フェーズ末監査の必須指摘により当初の「起票しない」を改めた）**: SC-22 主要素 3「値が入っていない項目を『未設定』として出す」は、
     IADR-0433 決定 1（data の `read` を与えない）の下では **KV 単位でしか満たせない**。
     (a) KV 単位で足りるとするか、(b) bootstrap が空文字で seed するのをやめて「未設定＝KV が無い」を成り立たせるか
     （ESO の同期先 Secret が作られず消費側が起動しない問題と対になる）、(c) data の read を与えるか、の裁定が要る。
     > ［2026-09-15 追記 / #1411］**planning#631 で選択肢 (a) と裁定された（2026-09-15・オーナー）。** SC-22 主要素 3 は**項目（KV）単位**で満たす。
     > プロパティ単位の空欄は画面に出さない。画面の「設定済みは各プロパティに空でない値が入っていることを保証しない」注記は残す。
     > **Vault の権限は変えない**（本決定 4・IADR-0433 決定 1 のまま）。注記は既に在るため**コードは変えない**。本フォローアップは閉じた。
  2. `deferred[]`（20 件）を画面で扱うか（IADR-0433 フォローアップ 4 のまま）。
  3. 退避手段の使用記録（ADR-0095 フォローアップ 4。Runbook は引き続き issue コメントへ記録する）。
  4. 監査の抽出クエリ（可観測性基盤）が `outcome=failed` を拾うか確かめる（本 PR では抽出側を触っていない）。
  5. **KV の現在版がソフト削除された状態での書き込み**は 502（`vault-rejected`）になり、原因が画面から分からない（決定 10 の帰結。監査指摘 2）。
     区別した結果と文言を返し、FakeVault に「削除済み版への PATCH が 404」を再現させる。
     > ［2026-09-15 追記 / #1467］**IADR-0454 決定 1 で片付けた。** `PATCH` 404 の後に metadata を読み、現在版が削除・破棄なら
     > `POST` を送らずに **409 `current-version-deleted`**（監査 `failed`）を返す。権限は広げず、運用者はコンソールで版を戻してから画面で書き直す。
     > 本決定 10 の「帰結」に書いた 502 はこの状態では出なくなった。
  6. **PUT 本文の大きさに上限が無い**（Kestrel 既定 30 MB。ロール判定より前に本文を解釈する）。
     端点に小さい上限を置くか、本文を手で束縛して解釈失敗も監査に残す（監査指摘 4。TestServer は本文上限を強制しないため試験の形も併せて決める）。
     > ［2026-09-15 追記 / #1467］**IADR-0454 決定 2 で片付けた。** 本文はロール判定・allowlist の後に手で **64 KiB** まで読み、
     > 超過 413（`body-too-large`）・不正 JSON 400（`invalid-body`）・JSON でない 415（`unsupported-media-type`）をすべて `denied` で監査する。
     > 本決定 7 の拒否理由の列挙はこの 3 つと `current-version-deleted` だけ増えた。
  7. **metadata を削除して作り直すと版が 1 から振り直され**、古い Redis の記録が別人の「最終更新者」を出し得る（監査指摘 5）。
     記録の `UpdatedAt` と metadata の `created_time` も突き合わせる。
     > ［2026-09-15 追記 / #1467］**IADR-0454 決定 3 で片付けた。** 本決定 3 の条件へ「記録の `UpdatedAt` ＝ 現在版の `created_time`」を足した。TTL は置かない。

## 関連

- Supersedes: なし（IADR-0433 を覆さない。同 ADR「モックアップ受領後に決める」の欄を埋める。**ただし決定 10 で IADR-0433 決定 1 の capability 列から data の `update` を外した**＝部分的に狭める改定であり、IADR-0433 に同日付の追記を置いた）
- Superseded by: なし
