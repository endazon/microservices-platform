---
title: IADR-0501 SC-22 の群「データソースの資格情報」は基盤の BFF が群の仕組み（専用接頭辞の 1 階層・管理者だけ・値を返さない）を持ち、成員と参照の配置は knowledge の BFF モジュールがポートで供給する。書いた後に値の無いキーにだけ参照を置き、供給元は設定の参照の有無で判定する
type: impl-adr
status: Accepted
related_ids: [FR-01, UC-04, SC-06, SC-22, NFR-18, ADR-0042, ADR-0095, ADR-0104, ADR-0110, ADR-0126, IADR-0044, IADR-0117, IADR-0295, IADR-0403, IADR-0433, IADR-0453, IADR-0454, IADR-0456, IADR-0460, IADR-0493, IADR-0495]
author: claude
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0126_datasource-credentials-as-sc22-group-admin-write-runtime-supply.md 決定 1〜5
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-22（2026-10-03 追加の群・アクセス制御）/ §SC-06（2026-10-03 追記の導線）
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md 決定 3（補完）
  - planning:projects/microservices-platform/07_adr/ADR-0110_sc22-supplier-three-values-no-public-key-restart-confirmed-at-write.md 決定 1・3（この群に限って部分改定）
related_specs:
  - ../specs/20261003_458_connector-secret-vault-reference.md
---

# IADR-0501: SC-22 の群「データソースの資格情報」（#458 段 S2・S3）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-06
- 決定者: claude（計画 ADR-0126 の裁定〔planning#716・利用者裁定 2026-10-03〕の実装。作業仕様書 20261003_458 §S2・S3 の追記）

## 起点・関連

- 起点 issue: #458（残射程 a「コネクタ資格情報の Vault 化」の段 S2・S3）
- 関連する計画書 ID: FR-01・UC-04・SC-06・SC-22・NFR-18
- 関連する計画 ADR: **ADR-0126**（本 IADR の上位。決定 1〜4 を実装する）・ADR-0095（決定 3 の補完）・ADR-0110（決定 1・3 をこの群に限って部分改定）・ADR-0104・ADR-0042
- 関連する実装 ADR: [[IADR-0493]]・[[IADR-0495]]（読み取り側。前提）、[[IADR-0403]] 決定 8（段 1 を改める）、[[IADR-0433]]（静的な項目の書き込み policy。群について追随）、
  [[IADR-0460]]（供給元の判定。群について追随）、[[IADR-0453]]・[[IADR-0454]]・[[IADR-0456]]（静的な項目の端点の規則を流用）、[[IADR-0117]]（ユニット外参照）
- 採番: 着手時の develop の最大は 0497 で、**0498〜0500 は並行作業が予約していた**ため取らず 0501 とした。予約分（#1754・#1753・#1757）はその後 develop へ入り、本ブランチを rebase した時点で欠番は無い。
- 基点コミット: `origin/develop` `c63351de`

## コンテキストと課題

計画 ADR-0126 は、データソースの資格情報を SC-22 の「データソースの資格情報」の**群**から投入すると裁定した。群の項目はデータソースの登録で増え、
無効化で消える。書き込みは**専用接頭辞（権限の層）と登録済みのデータソース ID（BFF のコード）の両方**で限り、**書けるのは管理者だけ**、
供給元は ExternalSecret の有無ではなく設定の参照の有無で判定し、再起動の確認は出さない。実装側で決めることは 5 つある。

1. **どこに置くか。** SC-22 の端点・Vault クライアント・監査は基盤の BFF（`Platform.Bff/Foundation`）にある。登録済みのデータソースを知っているのは
   knowledge ユニット（DataSourceService）である。**基盤は可変ユニットを参照できない**（`src/README.md` 依存規則・[[IADR-0117]]）。
2. **Vault の権限の形。** 既存の BFF の policy は項目ごとの完全一致パスで、ワイルドカードを禁じている（[[IADR-0433]] 決定 1）。
3. **書いた値をコネクタが使うには、データソースの設定に参照（`vault:datasource/<ID>#<キー>`）が要る。** 誰がいつ置くか。
4. **供給元の材料。** データソースの応答は秘密キーの値を一律に `***` へ伏せる（[[IADR-0295]]）ので、参照か平文かを BFF は応答から読めない。
5. **画面の作り**（群の表・ロールの出し分け・SC-06 の導線）。

## 検討した選択肢

### 置き場所

| | A. 基盤の BFF に群の仕組み、knowledge がポートで成員を供給（**採用**） | B. knowledge の BFF モジュールに端点を置き、Vault 書き込みのポートを Shared に出す | C. 基盤の BFF から DataSourceService を直接呼ぶ |
| --- | --- | --- | --- |
| 依存規則 | 守る（ポートは `Platform.Shared.Infrastructure`、実装は knowledge、合成点は BFF ホスト） | 守る | 🔴 破る（基盤コードが可変ユニットの契約を知る） |
| 秘密の扱い（本文の上限つき手読み・監査・値を出さない）の重複 | 無い（静的な項目の手続きをそのまま使う） | 🔴 knowledge 側へ複写が要る | 無い |
| Vault の書き込み口の露出 | 基盤の内側に留まる | 🔴 Shared へ「任意のパスへ書ける」口が出る | 基盤の内側 |

### 参照を置く時機（作業仕様書 §窓。規則 11 の 3 形を実測）

| 形 | 値なしの行（増える側） | 平文の行（減る側） |
| --- | --- | --- |
| Vault へ書くだけ（参照を置かない） | 🔴 書いた値が使われない | ○ |
| 常に参照を置く（平文を上書き） | ○ | 🔴 画面が平文を黙って移送する（ADR-0126 決定 4「画面以外」と食い違う） |
| **Vault へ書いた後、値なしのキーにだけ置く**（**採用**） | ○ | ○ |

### 供給元の材料

1. **DataSourceService に群専用の一覧（キーごとの `reference` / `other` / `absent`）を足す**（**採用**） — 値も参照の文字列も返さず、事実の符号だけを返す。
2. 応答のマスクを緩めて参照だけを素通しする — 🔴 Vault のパス構造を応答に出す（作業仕様書 §未決事項 3 の既定「伏せる」に反する）。

## 決定

### 決定 1: 群の仕組みは基盤の BFF、成員は knowledge がポート `ISecretItemGroupSource` で供給する

- ポート `Platform.Shared.Infrastructure.Foundation.Ports.Secrets.ISecretItemGroupSource`（成員の列挙・参照の配置の 2 操作。値はポートを通らない）。
- 実装 `Knowledge.Bff.Endpoints.Secrets.DataSourceCredentialGroupSource`（群名 `datasource-credentials`）。DataSourceService の 2 端点（決定 3）を、
  利用者の Authorization を伝播して呼ぶ（`DataSourceBffEndpoints` と同じ）。取れなければ例外を外へ出さず null を返す。
- 合成点は BFF ホストの `Program.cs`（`AddDataSourceCredentialGroup()`。前例 `AddKnowledgeUsageEventReporting()`）。
- 端点 `SecretItemGroupBffEndpoints`（`GET /bff/secrets/groups/{group}`・`PUT /bff/secrets/groups/{group}/{memberId}`）は基盤の BFF に置き、
  静的な項目の手続き（ロール判定を監査つきでハンドラ内に置く・本文は上限つきで手読み・Vault の結果の写像・値を出さない）を共有する
  （`SecretItemBffEndpoints` の補助を `internal` にして使う）。

### 決定 2: 群の宣言と Vault の権限

- 群の宣言は `deploy/bootstrap/sc22-secret-items.json` の `groups[]`（`group`・`vaultPathPrefix`・`writers`）。BFF の起動時に読み、**fail-closed**
  （未知のキー・接頭辞が 1 セグメントでない・`items[]` のパスの先頭セグメントと交わる・`writers` が `admin` 以外・重複は起動しない）。
- policy は新しいファイル `policy-bff-secret-group-write.hcl`（`bff-secret-group-write`）。`secret/data/<接頭辞>/+` に `create`・`patch`、
  `secret/metadata/<接頭辞>/+` に `read` だけ。🔴 **`*` ではなく `+`（1 セグメント）** —— 接頭辞の下の入れ子へは書けない。
  data の `read`・`update`・`list`・`delete` は無い。metadata の `read` は静的な項目と同じ形で、主要素 3（未設定）と最終更新日時に要る（値は持たない）。
- role は既存の `bff-secret-writer`（SA `bff`）に並べて付ける（`policies=bff-secret-write,bff-secret-group-write`）。**静的な項目の policy は変えない**
  （完全一致・ワイルドカードなしの試験はそのまま）。字面は `SecretItemGroupVaultPolicyTests` が `groups[]` との完全一致で固定する。
- 🔴 **登録済みの ID は BFF のコードで限る**（ADR-0126 決定 2 が受容したとおり、接頭辞の内側では権限で限れない）。BFF は成員 ID を
  `^[a-z0-9][a-z0-9-]{0,63}$` に限り（`/`・`..`・大文字を Vault のパスへ入れない）、ポートが返す成員に無い ID は 404 で Vault へ届かない。

### 決定 3: DataSourceService に群の後段 2 端点を足す（データソースの API は秘密を受け取らない）

- `GET /datasources/credentials`（管理者・運用者）: **有効で**コネクタが資格情報のキーを宣言するデータソースだけ。キーごとに
  `reference`（自分の正規の参照 `vault:datasource/<ID の D 形式>#<キー>` を持つ）／`other`（平文・別の場所を指す参照）／`absent`（値なし）。
  **値も参照の文字列も返さない。**
- `PUT /datasources/{id}/credentials/{key}/reference`（**管理者だけ**。多層防御 [[IADR-0044]]）: **本文なし。** `absent` のときだけ正規の参照を置き、
  `reference` は何もしない、**`other` は置き換えない**（応答 `other`）。無効・未登録・資格情報を使わない種別は 404、宣言しないキーは 400。
- 正規の参照の組み立て（`ConnectorSecretReference.CanonicalFor`）と接頭辞（`DedicatedPathPrefix`）は Domain の 1 箇所に置き、
  Vault 解決器の `PathPrefix` もそれを指す（BFF が書くパスと参照が 1 文字でもずれると、書いた値が使われない）。
- 🔴 **[[IADR-0403]] 決定 8 段 1（データソースの API が秘密を受け取り Vault へ書く）は採らない**（ADR-0126 決定 1。投入の面は SC-22）。

### 決定 4: 書き込みの順序と供給元

- BFF の `PUT`: 管理者の判定 → 群 → 成員 ID の形 → **登録済み**（ポートの一覧）→ 本文 → プロパティ（成員が宣言するものだけ）→ 値・理由 → **Vault へ 1 プロパティ** →
  書き込み記録（キーは `<群>/<成員 ID>`。静的な項目名と交わらない）→ 監査 → **参照の配置**（別の監査行 `secret.group.reference`）。
  🔴 **Vault が先、参照が後**（逆だと書き込みの失敗時に値の無い Vault を指す参照が残る）。参照を置けなくても 200（書き込みは成立）で、供給元は `unknown`。
  ExternalSecret の同期依頼も再起動の確認も無い（消費側は次の同期の開始時に読む。[[IADR-0493]]）。
- 供給元（契約の値は既存の 3 値のまま）: 成員のキーのどれかが `other` → `git`（表示「画面以外」）、それ以外 → `screen`（表示「画面（実行時に取得・次の同期から効く）」）。
  🔴 **`absent` も `screen`** —— 書けば参照が置かれて次の同期から効くので、「書いても効かない」ではない。後段の未知の符号は `git` へ倒す（「画面」と誤認させない）。
  成員を取れなければ一覧も書き込みも 502（空の群へ縮退させない）。
- 一覧の `writable` は `AdminOnly` を満たすか（画面の出し分け用。実効境界は `PUT`）。

### 決定 5: 画面

- SC-22（`/admin/secrets`）の静的な項目の表の下に群の表（項目名＝データソース名と種別と `datasource/<ID>`・最終更新日時と状態・最終更新者・供給元・操作）。
  **「更新」と操作の列は `writable` のときだけ**（運用者には出さず、管理者だけが書ける旨を注記する）。更新フォームはマスク入力＋確認入力 2 度で、**確認ダイアログ（再起動の確認）を置かない。**
- ルートの検索パラメータ `?datasource=<ID>`（`validateSecretsSearch`）で群の当該行を強調し、書ける利用者には更新フォームを開く。群に無い ID は注記で伝える。
- SC-06 の各行（有効で、ファイルサーバー以外）に導線 —— 管理者「認証情報を設定」・運用者「認証情報の状態」→ `/admin/secrets?datasource=<ID>`。入力欄は置かない。
  注記を「認証情報は『秘密情報・接続設定の管理』画面で設定します」へ改める。
- 呼び出しは orval 生成フック（`useBffSecretItemGroupList` / `useBffSecretItemGroupUpdate`）だけ。翻訳カタログは ja / en を再生成してコミットした。

## 理由

- **群の統制（値を出さない・本文の上限・拒否の監査）を 1 箇所に保つため**に、基盤の BFF に群の仕組みを置いた。knowledge に置くと秘密の扱いが 2 箇所に分かれ、
  片方だけが古くなる（本リポが繰り返し踏んだ型）。依存規則はポートで守った。
- **接頭辞を `+` にしたのは、権限の層で限れる範囲を最大にするため**である。`*` だと `datasource/<ID>/<任意>` へも書け、コネクタの読み手（`datasource/*`）が
  それを読み得る。成員 ID は常に 1 セグメント（GUID）なので `+` で足りる。
- **参照を置く判断を DataSourceService に置いたのは、`Config` の意味を知るのがそこだけだから**である（BFF は値の有無も読めない）。
- **値なしを「画面」とするのは、事実を出すため**である（ADR-0104 決定 2）。値なしの行は、画面から書けば次の同期から効く。

## 結果

- **良い影響**: データソースの資格情報を、管理者が画面から Vault へ入れられるようになった（新規のデータソースは平文を一度も持たずに済む）。
  投入の面は SC-22 の 1 つのまま。SC-06 の確定（更新は管理者限定）は崩れない。
- **悪い影響 / トレードオフ**
  - 🔴 **接頭辞の内側では、登録済みの ID への限定はコードの検査に依る**（ADR-0126 §結果 と同じ）。BFF のトークンで `datasource/<任意の 1 セグメント>` へ書ける。
  - 🔴 **既存の平文の行は「画面以外」のまま**で、画面で書いても使われない（段 S4 まで）。
  - 🔴 **無効化したデータソースの値は Vault に残る**（BFF にも datasource-service にも削除の権限が無い。ADR-0126 フォローアップ 4）。
  - 🔴 **本番は Vault を読まない**（helm の `services.datasource.vault.address: ""`）。群で書いた値が本番で効くのは配備の値を改めた後（フォローアップ 3）。
  - 群の一覧は成員ごとに Vault の metadata を 1 回引く（N 件で N 往復。静的な項目と同じ形。データソースの数は管理者が登録する程度で小さい）。
  - 書き込みのたびに後段の一覧を 1 回引く（登録済みの検査）。
- **フォローアップ**
  1. 段 S4: 既存の平文を Vault へ移して参照へ置き換える一回きりの移送（運用者のトークン。BFF 経由ではない）・移送期間フラグと平文の行の件数の readiness 検査・
     移送後の `Config` の秘密キーへの平文の書き込みの拒否（作業仕様書 §窓 1 の表 3 列を `[Fact]` で実測する）。
  2. 本番で Vault を読む配備の値（ADR-0126 フォローアップ 3）。
  3. 無効化したデータソースの値の扱い（ADR-0126 フォローアップ 4。計画・実装の両方）。
  4. 稼働クラスタでの疎通（群の policy の `+` が実 Vault で 1 セグメントに効くこと・metadata の `read` で状態が出ること）は未実測（#458 の残射程 4 と同じ場）。

## 関連

- Supersedes: なし（[[IADR-0403]] 決定 8 段 1 は日付つき追記で本 IADR を指す。本文は書き換えない）
- Superseded by: なし
- 追随を記録した IADR: [[IADR-0403]] 決定 8 段 1・[[IADR-0433]]・[[IADR-0460]]・[[IADR-0495]]（いずれも日付つき追記）
