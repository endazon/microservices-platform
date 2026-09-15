---
title: IADR-0454 SC-22 は現在版が削除された項目へ書かずに 409 で次の一手を示し、PUT 本文を 64 KiB で打ち切って解釈失敗も監査し、最終更新者は版と作成時刻の両方で突き合わせる
type: impl-adr
status: Accepted
related_ids: [SC-22, FR-05, NFR-18, ADR-0095, IADR-0433, IADR-0453]
author: claude
created: 2026-09-15
updated: 2026-09-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
related_specs:
  - ../specs/20260915_issue-1467_sc22-audit-followups.md
---

# IADR-0454: SC-22 の削除済みの版への書き込み・PUT 本文の上限・古い最終更新者

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-15
- 決定者: claude（#1467。MSP#1466 のフェーズ末監査の非ブロッキング指摘 2・4・5）

## 起点・関連

- 関連する計画書 ID: SC-22（秘密情報・接続設定の管理）／FR-05／NFR-18
- 関連する計画 ADR: ADR-0095 決定 3（値を読み出せない・項目の集合の外へ書けない）・決定 4（一括再投入をしない）
- 関連する実装 ADR: **IADR-0433**（権限の形。本 ADR は 1 つも広げない）／**IADR-0453**（決定 3・5・7・10。本 ADR はフォローアップ 5〜7 を埋める）
- 関連する実装仕様書: `.ai-context/specs/20260915_issue-1467_sc22-audit-followups.md`
- 起票: #1467

## コンテキストと課題

IADR-0453 は SC-22 の画面 → BFF → Vault を決め、PR #1466 で着地した。別文脈のエージェントによるフェーズ末監査が、
**直さなくても安全は崩れないが、運用で困る**穴を 3 つ残した（IADR-0453 フォローアップ 5〜7）。

1. **現在版がソフト削除された KV への書き込み**（監査指摘 2）。一覧は「未設定」と出すのに、保存すると
   `PATCH` 404 → `POST cas=0` → 403 → **502 `vault-rejected`** になる。実 Vault は metadata が在る path への `POST` を
   `update` として権限判定し、IADR-0453 決定 10 が `update` を外したためである。**画面の利用者は原因も次の一手も分からない。**
   さらに FakeVault はこの状況を写しておらず（削除済みでも `PATCH` が成功する）、**試験では 502 すら再現しなかった。**
2. **`PUT /bff/secrets/{item}` の本文に上限が無い**（監査指摘 4）。本文は最小 API の暗黙バインドで**ロール判定より前**に解釈され、
   上限は Kestrel 既定の 30 MB。解釈の失敗（不正 JSON・Content-Type 違い）はフレームワークが 400 / 415 を返し、**監査に残らない**。
3. **metadata を削除して作り直すと版が 1 から振り直される**（監査指摘 5）。最終更新者は記録の版と `current_version` だけで
   突き合わせているため、**古い記録（版 1・別人）が作り直し後の版 1 に付き得る**。IADR-0453 決定 3 の「誤った名前は出ない」が破れる。

🔴 **いずれも Vault の権限を広げて解いてはならない**（IADR-0433 決定 1・IADR-0453 決定 10）。

## 検討した選択肢

### A. 削除済みの版への書き込み

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A1 | policy に data の `update` を戻し、`POST` で書く | 🔴 **IADR-0453 決定 10 を覆す。** `update` は KV の全置換を許し、同居する構成値・realm と対の値を BFF のトークンで消せる |
| A2 | policy に `secret/undelete/<path>` の `update` を足し、BFF が削除を取り消してから `PATCH` する | 🔴 権限を広げる。加えて、**誰かが意図して削除した値（漏えいした鍵など）を画面の保存が黙って復活させる**。削除の意図を人が確かめる段が消える |
| A3 | `POST` の 403 を「削除済み」と読む | 403 は policy と allowlist の食い違いでも起きる。**原因の違う 2 つを同じ文言にし、誤った次の一手を示す** |
| A4 | **`PATCH` の 404 の後に metadata（`read` 済み・値を持たない）を読み、削除・破棄なら `POST` を送らずに区別した結果を返す** | 権限は変わらない。原因を確定してから言える。403 を踏まないのでトークンの取り直しも起きない |

状態コード:

| 案 | 評価 |
| --- | --- |
| 502 のまま（type だけ変える） | 502 は「上流が異常な応答をした」の意味。Vault は正しく振る舞っている |
| 400 / 422 | 入力は正しい。同じ入力が、項目の状態が変われば通る |
| **409** | **「対象の現在の状態が要求と衝突する。状態を解消すれば通る」**。運用者が版を戻せば同じ要求が成功する形と一致する |

### B. 本文の上限と解釈失敗

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B1 | Kestrel の `MaxRequestBodySize` を端点で絞る（`IHttpMaxRequestBodySizeFeature`・`[RequestSizeLimit]` メタデータ） | 拒否は**本文を読んだ瞬間の例外**としてハンドラの外で起き、**監査に残らない**。**TestServer はこの上限を強制しない**ので試験で確かめられない |
| B2 | 暗黙バインドのまま、フィルタで `Content-Length` を見る | `Content-Length` を持たない送り方（chunked）で素通りする。解釈失敗は依然として監査に残らない |
| B3 | **暗黙バインドをやめ、ロール判定・allowlist の後に本文を手で「上限 ＋ 1 バイトまで」読み、アプリの JSON 設定で解釈する** | 送り方に依らず止まる。拒否をすべてハンドラの中で監査できる。**TestServer でも同じ経路が走る** |

上限の状態コード:

| 案 | 評価 |
| --- | --- |
| 400 | 中身の誤りと区別がつかない |
| **413** | 転送の大きさの拒否として HTTP の意味どおり。Kestrel の既定の上限が返す状態コードとも揃う |

### C. 古い最終更新者

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C1 | 記録のキーに TTL を置く | 🔴 **現在版のままの正しい名前も期限で消える**（SC-22 の列が時間とともに空になる）。期限内の作り直しは防げない |
| C2 | Vault の `custom_metadata` に書き手を置く | 🔴 metadata の書き込み権限が要る（IADR-0453 決定 3 が退けた案） |
| C3 | **記録の `UpdatedAt` と metadata の `versions[current].created_time` の一致も条件にする** | 権限は変わらない。作り直した版 1 は作成時刻が違うので一致しない。記録の `UpdatedAt` は書き込み応答の `created_time`（metadata に保存される値そのもの）で、既に持っている |

## 決定

**A4 ＋ 409 / B3 ＋ 413 / C3 を採用する。**

### 決定 1: 現在版が削除・破棄された KV へは書かず、409 `current-version-deleted` で次の一手を示す

- `VaultMetadataState` に **`Deleted`**（metadata は在り、現在版の `deletion_time` が埋まっているか `destroyed` が真）を足す。
  `Absent` は「KV が無い・`current_version` が 0」だけを意味するようにする。**一覧は `Deleted` を従来どおり `notSet` と出す**（状態の値域・契約は変えない）。
- `VaultWriteOutcome` に **`CurrentVersionDeleted`** を足す。`WritePropertyAsync` は `PATCH` の 404 の後に metadata を読み:
  `Absent` → `POST` ＋ `cas=0`（従来どおり）／`Deleted` → **`POST` を送らず** `CurrentVersionDeleted`／
  `Present`（間に作られた・戻された）→ `PATCH` を 1 度だけやり直す／`Unavailable` → `Unavailable`。
- 端点は **409**、problem type `urn:microservices-platform:secret-items:current-version-deleted`、title「この項目の現在の版は保管先（Vault）で削除されているため、画面から書き込めません。」。
  監査は `failed`・`reason=current-version-deleted`（許可・拒否のどちらでもない保管先側の状態。IADR-0453 決定 7 の `failed` と同じ扱い）。書き込み記録は作らない。
- SPA は 409 を「現在の版が削除されている／画面からは書けない／Runbook の手順でコンソールから版を復元してから更新し直す／値は保存されていない」と出す。**境界層の title を直接出さない**（en ロケールで日本語が混ざる）。
- **運用者の次の一手**（運用 Runbook「失敗したときの分岐」）: 削除なら `vault kv undelete -versions=<現在版>`、破棄なら `vault kv rollback -version=<破棄されていない版>` で版を戻し、**値は画面から入れ直す**（監査に残るため）。
  🔴 **戻す操作は人がコンソールの権限で行う。** 削除には意図があり得る（漏えいした鍵を消した等）ため、画面の保存で黙って戻さない（A2 を退けた理由）。
- FakeVault は実 Vault を写す: 削除・破棄された現在版への `PATCH` は **404**、metadata が在る KV への `POST` は `cas` に関係なく **403**。

### 決定 2: PUT 本文は 64 KiB で打ち切り、Content-Type・大きさ・解釈の失敗をすべて監査つきで返す

- 暗黙バインド（`UpdateSecretItemRequest? body`）をやめる。ハンドラは **ロール判定 → allowlist → 本文**の順に進む（非権限者に本文を解釈させない）。
- 本文の扱い（すべて `denied`。🔴 **本文・例外メッセージ・長さはログにも監査にも出さない**）:

  | 状況 | 応答 | 監査の理由 |
  | --- | --- | --- |
  | Content-Type が JSON でない | 415 | `unsupported-media-type` |
  | `Content-Length` が上限超 | 413（本文を読まない） | `body-too-large` |
  | 読んだバイト数が上限超（`Content-Length` 無しの送り方を含む。上限 ＋ 1 バイトで読むのをやめる） | 413 | `body-too-large` |
  | JSON として解釈できない・`null` | 400（ValidationProblem・`body`） | `invalid-body` |

- 解釈はアプリの JSON 設定（`Microsoft.AspNetCore.Http.Json.JsonOptions`）で行い、暗黙バインドと同じ名前の対応・大文字小文字の扱いを保つ。
- **上限は 64 KiB（65,536 バイト）**。最悪の場合の本文は、値 8,192 文字 × 6 バイト（JSON で 1 文字が最も長くなる `\uXXXX`。
  System.Text.Json は既定で非 ASCII を、ブラウザの `JSON.stringify` は制御文字と孤立サロゲートをこの形にする）＝ 49,152、
  理由 500 × 6 ＝ 3,000、プロパティ名 256、JSON の枠と整形の空白 1,024 で**約 53.4 KB**。**64 KiB に収まり約 12 KiB の余裕がある。**
  上限を超える本文は、入力規則を満たさない値か、契約に無い余分な内容を必ず含む。SPA は入力規則で送る前に止めるため、画面の利用者は 413 を踏まない。
- 🔴 **CSRF の扱いは変えない**（`CsrfHeaderMiddleware` は端点より前で動く）。Content-Type が JSON であることの要求も、暗黙バインドが持っていたものを保つ。

### 決定 3: 最終更新者は版と作成時刻の両方が一致するときだけ出し、TTL は置かない

- 一覧は **`record.Version == current_version` かつ `record.UpdatedAt == versions[current].created_time`（同じ瞬間）** のときだけ `lastUpdatedBy` を返す。
- 記録の `UpdatedAt` は書き込み応答の `data.created_time`。応答に時刻が無く BFF の時計で埋めた場合、または metadata の時刻を解釈できない場合は一致せず「記録なし」になる —— **誤った名前を出さない側に倒れる**（IADR-0453 決定 3 の方針）。
- **TTL を置かない**。キーは `items[]` の数（4）しかなく、書き込みごとに上書きされる。古さの害（作り直し後の誤帰属）は時刻の突き合わせで閉じ、TTL は正しい名前を消すだけである。

## 理由

- **権限を 1 つも広げずに 3 件とも閉じられる**。削除済みの判定・作成時刻の突き合わせは、既に持つ metadata の `read` だけで足りる。本文の上限は BFF の中の話である。
- **原因を確定してから言う**（決定 1）。403 から推測すると、policy の食い違いという別の原因に「版を戻せ」と言ってしまう。
- **拒否は監査に残らなければ統制にならない**（決定 2）。IADR-0433 決定 6 は拒否も記録すると定めており、フレームワークの外で起きる拒否はその穴になる。試験で確かめられない仕組みは、壊れても気付けない。
- **「誤った名前を出さない」を条件の追加で守る**（決定 3）。期限（TTL）は古さの近似でしかなく、作成時刻は同一性そのものである。

## 結果

- **良い影響**:
  - 削除済みの項目で保存したとき、画面が原因と次の一手を示す。監査からも `current-version-deleted` で抽出できる。
  - 非権限者の大きな本文は解釈されない。本文の解釈失敗も監査に残る。
  - 最終更新者の誤帰属の経路が 1 つ減る。
  - FakeVault が実 Vault の削除済みの版と `update` の欠如を写すようになり、同型の穴を試験が拾える。
- **悪い影響・トレードオフ**:
  - 削除済みの項目を画面だけでは直せない（コンソールで版を戻す 1 手が要る）。**意図した設計**である（決定 1）。
  - 削除済みでない KV でも `PATCH` 404 の後に metadata を 1 回余分に読む（KV が無いときの作成経路だけ）。
  - 前後に大量の空白を持つ理由など、**入力規則は満たすが 64 KiB を超える本文**は 413 になる。SPA は理由を trim して送るため起きない。
  - 監査の拒否・失敗の理由の値域が 4 つ増える（`current-version-deleted` / `body-too-large` / `invalid-body` / `unsupported-media-type`）。
- **帰結（コードは変えない）**: `update` を持たない policy の下では、`cas=0` 作成の競合（`PATCH` 404 の後に誰かが先に作った）は
  400 ではなく 403 として現れ、502 `vault-rejected` になる。作成と作成が数十ミリ秒で重なる稀な場合であり、再送すれば `PATCH` で通る。
- **フォローアップ**:
  1. 稼働クラスタの Vault で、書き込み応答の `created_time` と metadata の `versions[n].created_time` が同じ文字列であることを確かめる（SC-22 テスト仕様書 T-40 と同じ場で行う）。食い違えば最終更新者は常に「記録なし」になる（誤った名前は出ない）。
  2. 本文の手読みは **UTF-8 として解釈する**ため、`Content-Type: application/json; charset=utf-16` など UTF-8 以外の charset を名乗る本文は 400（`invalid-body`）になる（PR #1469 のフェーズ末監査が実測。暗黙バインドは charset で変換していた可能性があるが develop では測っていない）。SPA とブラウザは UTF-8 で送るため実害は無いと判断し、コードは変えない。UTF-8 以外の送り手が現れたら charset を読んで変換するか 415 で明示的に断る。

## 関連

- Supersedes: なし（IADR-0453 を覆さない。同 ADR のフォローアップ 5〜7 を埋める）
- Superseded by: なし
