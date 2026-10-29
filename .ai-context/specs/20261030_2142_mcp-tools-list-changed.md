---
title: 作業仕様書 — MCP の実効ツール一覧の版が進んだとき、subscriptions/listen の流れで notifications/tools/list_changed を送る（#2142）
type: spec
status: done
related_ids: [FR-16, UC-08, ADR-0024, IADR-0651, IADR-0269, IADR-0292]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2142"
---

# 作業仕様書 — tools/list_changed の送出（#2142）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `e760d82e`）。

## 現状（develop と SDK 2.2.0 で確かめた）

- `ToolCatalog.Version` は内容が変わったときだけ進む。しかし送出の実装は無い（IADR-0269 と IADR-0292 の残件）。
- ツールの一覧は、動的ハンドラ（`WithListToolsHandler`）が要求ごとに組んでいる。SDK が自分で list_changed を送るのは `McpServerOptions.ToolCollection` の `Changed` だけである。
- **MCP 面は Streamable HTTP を無状態で動かしている**（SDK 2.2.0 の既定は `HttpServerSessionMode.Stateless`）。
  - 無状態では、要求の外から送る通知（セッション全体への list_changed）は捨てられる。
  - 通知が届く道は、2026-07-28 の改訂の `subscriptions/listen`（クライアントが開いて保つ要求）の応答の流れだけである。
- 実測（使い捨ての試験。コミットしない）:
  1. 中身の無い `ToolCollection` を置いて `Changed` を鳴らす案では、届かなかった。
     - 2025-11-25 の握手のクライアントにも、2026-07-28 のクライアントが listen を開いた場合にも届かない。
     - 無状態では、`capabilities.tools.listChanged` も申告されない。
  2. 自前の `subscriptions/listen` の処理（`WithSubscriptionsListenHandler`）では、2026-07-28 のクライアントに届いた。

## 方針

- `ToolCatalog` に `Changed` イベントを足す。版が進んだときだけ、新しい版で 1 回呼ぶ。
- `subscriptions/listen` の処理を自前で持つ（`ToolListChangedSubscription`）。手順は SDK の `SubscriptionsListenHandler` の契約どおりである。
  1. 受付の前に `Changed` を購読する。
  2. 受付の通知（`notifications/subscriptions/acknowledged`）を 1 回送る。扱う種類は tools の list_changed だけである。
  3. 版が進むたびに list_changed を送る。各通知の `_meta` に listen の要求 ID を付ける。
  4. 切断（取り消し）で購読を外して抜ける。
- `capabilities.tools.listChanged = true` を申告する。
- **セッションの方式は変えない**（無状態のまま）。有状態へ変えると、複数のレプリカでセッションの付着が要り、配備の前提が変わる。初期化の握手の利用者（2025-11-25 以前）へは送れないが、その利用者は `tools/list` を引き直して知る。これを残余として IADR-0651 に書く。
- 一覧そのもの（利用者ごとの ABAC と公開構成）は従来どおり `tools/list` が組む。通知は「引き直してよい」の合図だけで、中身を運ばない。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n "list_changed\|listChanged"` で走査した。
  - 当たりは IADR-0269・IADR-0292 の残件（凍結記録。書き換えない。IADR-0651 が後継を記録する）と、テスト仕様書 FR-16 の「未実施・残件」である。
  - テスト仕様書の残件は取り消し線にし、追記を足した。
- **規則 10**: テスト仕様書の L-1〜L-3 と、実装マッピングの 2 行を、この変更に合わせて書いた。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 公開ツールの集合が変わったとき、接続中（listen 中）のクライアントへ list_changed が届く | `ToolListChangedNotificationTests.公開ツールの集合が変わるとlistenしているクライアントへlist_changedが届く`（実物の SDK のクライアント・本物のトークン） | 赤（自前の listen の処理を外す変異で赤） |
| 陰性の対照: 版が進まなければ届かない | `版が進まない突合ではlist_changedは届かない` | 緑 |
| 版が進んだときだけ合図する | `ToolCatalogTests.版が進んだときだけChangedを呼ぶ` | 赤（コンパイル不可） |

## ［2026-10-30 追記 / #2315 の監査］レプリカ間の一貫性・長命の接続の上限・認可の失効

- 事実の整理と判断は IADR-0651 の「決定 5」と「事実の整理」に書いた。
- 実装した上限:
  - listen の寿命は最長 10 分。寿命で閉じた後の開き直しは認証を通り直す。
  - 同時本数は主体ごと 4 本、全体 256 本。
- 試験:
  - `listenは最長の寿命で閉じる`
  - `主体ごとの同時listenは上限を超えると断る`
  - `ToolListChangedListenLimitsTests`
- **red→green**: 上限の判定と寿命の打ち切りを外す変異で 2 件が赤、戻すと緑になった。
- ［#2315 のレビュー］試験の listen は、開いたことを受付（`notifications/subscriptions/acknowledged`）の受信で待つ。従前は `Task.Delay(500)` で待っており、混んだ CI で取りこぼし得た。
- ［#2315 のレビュー］tools を求めない listen は、受付の後にすぐ閉じる。試験は `toolsを求めないlistenは受付の後にすぐ閉じる` で、無期限に待つ形へ戻す変異で赤になる。
