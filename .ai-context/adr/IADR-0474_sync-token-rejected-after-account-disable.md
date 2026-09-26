---
title: IADR-0474 同期トークンの検証で、所有者のアカウントが有効かを同期要求ごとに利用者名簿へ訊き、有効と確かめられたときだけ通す（計画 ADR-0114 の方式）
type: impl-adr
status: Accepted
related_ids: [FR-20, SC-17, SC-20, UC-11, NFR-14, NFR-09, ADR-0114, ADR-0096, ADR-0037, ADR-0026, ADR-0032, ADR-0029, ADR-0075, IADR-0270, IADR-0428, IADR-0431, IADR-0401, IADR-0379, IADR-0419, IADR-0056]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0114_sync-token-rejected-after-account-disable.md
  - planning:projects/microservices-platform/07_adr/ADR-0096_private-note-disposal-after-view-window.md
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
related_specs:
  - ../specs/20260926_issue-1532_sync-token-rejected-after-disable.md
---

# IADR-0474: 同期トークンの検証で、所有者のアカウントが有効かを同期要求ごとに名簿へ訊く

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（#1532。計画 ADR-0114 決定 3 が方式を実装判断へ委ねた）

## 起点・関連

- 関連する計画書 ID: FR-20、SC-17、SC-20、UC-11、NFR-14（アカウント無効化時の全セッション即時失効）
- 関連する計画 ADR: **ADR-0114**（決定 1 無効化の後の最初の要求から 401／決定 2 判定できなければ通さない／
  決定 3 方式は IADR で閉じ、再有効化の扱いと依存の向きを書く／決定 4 go-live の前提）、
  ADR-0096 フォローアップ 2、ADR-0037 フォローアップ 3、ADR-0026・ADR-0032（即時失効）、ADR-0029・ADR-0075（east-west gRPC）
- 関連する実装 ADR: IADR-0270 決定 3（同期トークンの検証）、IADR-0428（無効化と保持起点）、
  IADR-0431（DocumentService が名簿の `enabled` を読む口・退職者削除）、IADR-0401 決定 2（名簿の狭い読み口）、
  IADR-0379 決定 4（利用者トークンを east-west へ載せない）、IADR-0419（document-service の s2s 資格情報）、
  IADR-0056（platform → 可変ユニットの依存禁止）
- 関連する実装仕様書: `.ai-context/specs/20260926_issue-1532_sync-token-rejected-after-disable.md`

## コンテキストと課題

SC-17 の無効化は「Keycloak の `enabled=false`・Keycloak のセッション失効・保持起点の刻印」の 3 段だけで、
同期トークン（DocumentService の `SyncDevices`）に触れない。同期トークンの検証（`ResolveDeviceAsync`）は
端末の失効日時と期限しか見ない。無効化した本人は最大 30 日、Obsidian へ個人資料を同期し続けられた（#1532）。

計画 ADR-0114 は結果（無効化の後の最初の要求から 401・判定できなければ通さない）を固定し、方式を委ねた。
決めることは 4 つである: 方式（案 A / B / A＋B）、判定できない場合の境界、再有効化の扱い、配備の配線。

## 検討した選択肢

### 方式

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A** | 同期要求ごとに利用者名簿（`platform.authz.v1.UserDirectory/GetUserAttributes`）で有効かを確かめる | **採用**。無効化の直後から効く。無効化側（platform）に手を入れない。**読み口は既存**（IADR-0431 で `found` / `enabled` が足されている）で、proto も認可サービスも変えない |
| B | 無効化のイベント（`Shared.Contracts` ＋ Wolverine）を DocumentService が受け、端末を全失効 | 不採用。**イベントが届くまで開いたまま**で、決定 1 の「最初の要求から」を満たすには at-least-once と再送を作り込んでも**届くまでの窓が残る**。platform が可変ユニット向けの契約を新たに発行することになる |
| A＋B | A で塞ぎ、B で失効を記録する | 不採用。A だけで決定 1・2 を満たす。B の利点（SC-20 に失効として表示される）は計画が求めていない（計画外の機能追加） |

### 名簿の答えの持ち方

| 案 | 評価 |
| --- | --- |
| **新しいポート `IOwnerAccountDirectory`（`Unknown=0 / Enabled / Disabled / NotFound`）** | **採用** |
| 既存の `IOwnerRetentionDirectory` に述語を 1 つ足して流用 | 不採用。同じ rpc を読むが**倒す向きが逆**である（あちらは「分からなければ消さない」、こちらは「分からなければ通さない」）。試験のスタブの既定値（あちらは「引けなかった」）も縮退の意味も片方に引きずられる |
| bool（有効か否か） | 不採用。「無効化された」と「分からなかった」を畳み、ログと試験で区別できなくなる |

### キャッシュ

| 案 | 評価 |
| --- | --- |
| **持たない（1 要求 1 往復）** | **採用**。決定 1 の「最初の要求」を TTL の分だけ遅らせる形は即時にあたらない（ADR-0114 決定 1 の 🔴） |
| 短い TTL のキャッシュ | 不採用。TTL の間は無効化した本人の同期が通る |

## 決定

### 決定 1: 検証の 1 か所に、名簿の門を足す（案 A）

- `ObsidianSyncEndpoints.ResolveDeviceAsync`（同期トークンを検証する**唯一**の箇所。5 端点が呼ぶ）で、
  端末が有効（未失効・期限内）と確定した**後に**、所有者 ID で `IOwnerAccountDirectory.GetStateAsync` を引く。
  **`Enabled` のときだけ端末を返し、それ以外は `null`（＝同じ 401）。**
- 🔴 **名簿は端末の検証を通った要求でだけ引く。** トークンの無い・不正・期限切れ・失効の要求で認可サービスへの
  往復を生ませない（無資格の呼び出しが名簿の負荷を操れない）。
- 🔴 **口は `HttpContext.RequestServices` から引く**（端点の引数にしない）。新しい端点が引数を書き忘れても
  門を素通りできない。
- 応答は既存どおり区別しない 401 である（欠落・不正・期限切れ・失効・無効化・判定不能）。

### 決定 2: 判定できないときは通さない（ADR-0114 決定 2 の境界）

| 名簿の答え | 状態 | 同期 |
| --- | --- | --- |
| `found=true, enabled=true` | Enabled | **通す（唯一）** |
| `found=true, enabled=false` | Disabled | 401 |
| `found=false` | NotFound | 401（有効だと確かめられない） |
| `RpcException`（全 status）・s2s トークン取得失敗 | Unknown | 401 |
| **5 秒の上限を超えた**（`GrpcOwnerAccountDirectory.LookupTimeout`） | Unknown | 401 |
| 口が未構成（`Services:AuthorizationServiceGrpc` 無し）→ `UnavailableOwnerAccountDirectory` | Unknown | 401 |

- `Unknown` を列挙の 0 に置く —— 既定値・写し損ねが「通す」へ倒れない。
- `enabled` を知らない古い認可サービスが応答すると proto3 の既定 `false` になり Disabled ＝ 401。
  **配備の順序を誤っても通さない側へ倒れる。**
- 上限を置いたのは、チャネルに期限が無いからである。認可サービスが応答しないと、同期要求がその間止まり続ける
  （ADR-0114 決定 2 は「時間切れ」を判定できない場合として名指しした）。要求自身が取り消された場合は
  Unknown に畳まず取り消しを伝える（時間切れと中断をログの上で混ぜない）。
  🔴 本番のチャネル（`CreatePlatformChannel`）は `ThrowOperationCanceledOnCancellation` が既定の false であり、
  取り消しを `RpcException(Cancelled)` で投げる。共有クライアントはそれを `null` に畳むので、実装は
  **`null` を受けたときに要求の `ct` が取り消されていれば `OperationCanceledException` を投げ直す**
  （当初の実装はこの形を見落としており、本番では Unknown ＝ 401 になっていた。安全側ではあったが記述と違った。
  #1579 の監査で指摘。試験 T-AS-16 は両方の形を測る）。
- 失敗時のログは呼び出し元に合わせる。共有クライアントに同期用の読み口 `GetAccountStatusAsync` を足し
  （rpc と応答の写しは退職の窓の読み口と同じ）、同期の経路の失敗を「退職の窓…削除しません」と書かない。

### 決定 3: 再有効化すると、未失効・期限内の端末は再び使える（ADR-0114 決定 3 の 1 点目）

- **案 A の帰結である。** 無効化は端末を失効させない（`RevokedAt` を書かない）ので、SC-17 で再有効化すると、
  その時点で期限内かつ本人が失効させていない同期トークンは**次の同期要求から再び通る**。
- 無効化中にトークンの期限（発行から 30 日）が過ぎた端末は戻らない。本人が SC-20 で再発行する。
- 管理者と利用者が読めるよう、画面仕様書（SC-17）と機能仕様書（FR-20）に書いた。
  ADR-0096 が唯一の救済とする「再有効化で保持の起点を消す」と向きが揃う（再有効化は無効化の前の状態へ戻す）。
- 試験で固定した: 再有効化の後に同じトークンが 200、かつ端末の `RevokedAt` が立っていないこと。
  **これが崩れる（案 B へ寄る）なら、本決定と両仕様書を改めること。**

### 決定 4: 依存の向きは knowledge → platform だけ（ADR-0114 決定 3 の 2 点目）

- DocumentService（knowledge）が `Platform.Shared.Contracts`（proto）と `Platform.Shared.Infrastructure`
  （`UserDirectoryGrpcClient`）を使う。**ユニット外参照は許可された Shared の 3 プロジェクトの範囲に収まる。**
- AuthorizationService（platform）は何も変えない。**platform → knowledge の依存は作っていない。**
- 呼び出しは s2s の資格情報（`document-service` client・`platform-service` ロール。IADR-0419 で配線済み）で行い、
  利用者の資格情報を east-west へ載せない（IADR-0379 決定 4）。

### 決定 5: 配備で口を配線する

- 🔴 **document-service には helm（`services.document.extraEnv`）にも compose にも
  `Services__AuthorizationServiceGrpc` が無かった。** 決定 2 の縮退により、配線しないと**全配備で同期が 401** になる。
  同じ変更で両方へ `http://authorization-service:8081` を足した（他の 6 サービスと同じ値）。
- 付随する効果: **IADR-0431 の退職者削除（ADR-0096 決定 1）も配備で動き始める。** 同じ構成キーで口が選ばれるため、
  これまで配備では縮退（常に「引けなかった」＝ 1 件も削除しない）のまま動いていた。削除の述語
  （無効化済み ∧ 起点から 30 日経過）は変えていない。**所有者（利用者）は実働化を意図どおりと確認した**（2026-09-26）。

### 決定 6: 実働化する退職者削除の安全を試験で固定する（#1579 の監査）

削除は取り返せない（ADR-0057 決定 2）。決定 5 で初めて配備で動く口に、次を足した。

- **gRPC 写像の試験**（`GrpcOwnerRetentionDirectoryTests`）。従前は 1 本も無く、`_ => NotEvaluable` を
  `_ => Elapsed` に変える変異が既存の 596 件をすべて通り抜けた。その 1 行は、窓の項目を知らない古い認可サービス
  （found=true・enabled=false・eligibility=Unspecified がすべて既定値）による**一斉削除**を止める唯一の門である。
  「消さない」枝（未指定・未知の値・古い応答・窓の中・在籍中・名簿に居ない・輸送の失敗・応答なし・未構成）を
  1 本ずつ置き、陽性対照（名簿が明示的に Elapsed と答えた無効化済みの所有者だけが対象）と対にした。
- **列挙 `OwnerRetentionEligibility` の 0 を `NotEvaluable` にした**（従前は `Elapsed` が 0 で、既定値が「削除」を意味した）。
  列挙は DocumentService の中だけで使い、永続化も線上の表現も持たない（線上は proto で、名前で写す）ので互換は壊れない。
- **退職の窓の照会にも 5 秒の上限**を掛けた（応答しない認可サービスで日次の定期処理全体が止まらないように）。超えたら「引けなかった」＝削除しない。
- **削除の前に件数を 1 行ログへ残す**（所有者の総数・対象の数・判定できなかった数。所有者 ID は出さない）。
- 🔴 **1 周期あたりの削除人数の上限は置かない。** 上限を置くと、退職が集中した周期で対象者の削除が翌日以降へ
  ずれ込み、計画の窓（ADR-0096 の 30 日）を実装が黙って延ばすことになる。誤削除の防御は述語と写像の側
  （既定値・未知の値・古い応答を消さない側へ倒す）で行い、その各枝を変異試験で裏取りした。

## 理由

- **案 A を選んだのは、決定 1 の「最初の要求から」を、届くまでの窓を持たずに満たせる唯一の案だからである。**
  B はイベントが届くまで開いたままで、A と組まない限り決定 1 を満たさない。組むなら A だけで足りる。
- **読み口が既にあった。** IADR-0431 が退職の窓のために `enabled` を面へ出しており、proto・認可サービス・
  s2s の資格情報のいずれも増やさずに済む。
- **fail-closed は計画が固定した結果であり、実装の選択ではない**（ADR-0114 決定 2）。本 IADR が決めたのは、
  その境界（NotFound・時間切れ・未構成・古い配備を含める）である。

## 結果

- 良い影響: ADR-0114 決定 1・2 が実装され、NFR-14 の同期トークンについての未達が解ける条件が揃う（計画側の記録は
  ADR-0114 フォローアップ 2）。ADR-0096 決定 3（窓の間の権能は閲覧のみ）が本人の側でも働く。
- 悪い影響・トレードオフ:
  - 🔴 **同期 1 要求につき名簿 1 往復**（認可サービス → Keycloak の by-username 照会）。プラグインの同期 1 巡は
    manifest ＋ 資料ごとの push / pull であり、資料の数だけ往復が増える。計画が受け入れた負荷である
    （ADR-0114 §結果）。重くなったら測り直す。
  - 🔴 **認可サービス（または Keycloak）が落ちている間、すべての利用者の同期が止まる**（ADR-0114 決定 2 の代償）。
    プラグインは 401 を「同期トークンが無効です」と表示するため、利用者からは期限切れと区別がつかない。
  - 管理者が本人の端末を失効させる経路は依然として無い（ADR-0114 実測 3）。本決定は失効ではなく拒否で塞ぐ。
  - **実 Keycloak・実クラスタでの疎通は測っていない。** 手順は `docs/how-to/obsidian-plugin-device-check.md` §5。
- **変異による裏取り**（宣言ではなく実測。1 か所ずつ書き換えて実行。当初の 11 種は新しい試験 20 件、監査後の 5 種は同期・写像・退職者削除の試験 49 件、M6 は DocumentService の全 613 件）:

  | 変異 | 赤 |
  | --- | --- |
  | 門を外す（`return device;`） | **6**（無効化の次の要求・5 端点・再有効化・判定不能・名簿に居ない・未構成の配備） |
  | Unknown を通す | **2**（判定不能・未構成の配備） |
  | NotFound を通す | **1**（名簿に居ない） |
  | 端末の有効性より先に名簿を引く | **1**（端末が有効と確定しない要求では名簿を引かない） |
  | 未構成の縮退が Enabled を返す | **2**（縮退の単体・未構成の配備の結合） |
  | gRPC 実装: null → Enabled | **5**（輸送の失敗 4 status・`RpcException(Cancelled)` の時間切れ） |
  | gRPC 実装: NotFound → Enabled／NotFound を Disabled へ畳む | **1**／**1**（名簿に居なければ NotFound） |
  | gRPC 実装: `enabled` を無視 | **1**（無効化されていれば Disabled） |
  | gRPC 実装: 上限を外す | **2**（時間切れの 2 形。🔴 当初は試験に期限が無く、赤ではなく**ホストごと止まった** —— 試験へ `Timeout` を足して赤で止まる形にした） |
  | gRPC 実装: 要求自身の取り消しも Unknown へ畳む | **1**（要求そのものが取り消されたら取り消しをそのまま伝える） |
  | ［#1579 監査後に追加］gRPC 実装: `null` を受けたとき要求の取り消しを投げ直さない | **1**（同上・本番の `RpcException(Cancelled)` の形） |
  | ［同］退職の窓: 未知・未指定 → Elapsed（監査の M6。**全 613 件で実行**） | **3**（未指定・未知の値・古い認可サービスの応答）。従前は 0 件 |
  | ［同］退職の窓: 列挙の 0 を Elapsed へ戻す | **5**（既定値・未指定・未知の値・古い応答・起点なしの結合） |
  | ［同］退職の窓: 上限を外す | **2**（応答なしの 2 形。試験の `Timeout` で赤） |
  | ［同］退職の窓: 述語から `Found` を外す | **2**（名簿に居ない所有者の単体・結合） |

- フォローアップ:
  1. 計画へ: 本 IADR の着地を ADR-0114 フォローアップ 2（NFR-14 の未達の解消の記録）へ環流する。
  2. 稼働クラスタで「無効化 → 同期 401 → 再有効化 → 200」を実測する（how-to §5）。

## 関連

- Supersedes: なし
- Superseded by: なし
