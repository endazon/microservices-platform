---
title: IADR-0648 RabbitMQ.Client を CPM の推移ピン留めで 7.2.2 へ上げ、開き損ねた接続が裏で自動回復を続けて起動の再試行のたびに漏れる欠陥を外す
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0027, IADR-0233]
author: claude
created: 2026-10-29
updated: 2026-10-29
related_specs:
  - ../specs/20261029_2301_rabbitmq-client-7.2.2.md
---

# IADR-0648: RabbitMQ.Client を 7.2.2 へ上げて、開き損ねた接続の漏れを外す（#2301）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-29
- 決定者: claude（#2301。AST#1491 と同じ形。依頼はコーディネーター 2026-10-29）

## 起点・関連

- 起点 issue: #2301。調査の出どころは AST#1482 / AST#1491（AST の IADR-0595）。
- 計画: ADR-0027（メッセージングは Wolverine）。
- 作業仕様書: [20261029_2301_rabbitmq-client-7.2.2](../specs/20261029_2301_rabbitmq-client-7.2.2.md)。

## コンテキスト

- MSP は `RabbitMQ.Client` を直接参照せず、`WolverineFx.RabbitMQ` 6.24.4 の推移で 7.1.2 を解決していた。
- 7.1.2 の `AutorecoveringConnection.CreateAsync` は、回復を購読してから内側の接続を開く。開くのに失敗しても自分を閉じない。
  - 開き損ねの shutdown（Library・541）が自動回復を起動する。
  - 呼び出し元（Wolverine の起動の再試行。5 秒ごと・最大 20 回）は次の試行で新しい接続を作る。その裏で捨てられた接続が繋ぎ直しを続け、ブローカの準備が整った時点で一斉に繋がる。
  - AST の PoC では接続が 115〜131 本（平常 22 本）になった。7.2.x は開き損ねた接続を閉じて破棄する（AST が両版を逆コンパイルして確認）。
- Wolverine は接続の自動回復を必ず有効にする（`RabbitMqTransport.configureDefaults`）。設定で避けられない。

## 決定

- `src/Directory.Packages.props` に `RabbitMQ.Client` 7.2.2 を足す。`CentralPackageTransitivePinningEnabled` により全サービスの推移の版が差し替わる。Wolverine の版は変えない。
- 欠陥の再現を単体の試験で固定する（偽のブローカでハンドシェイクを切る。開き損ねた後に繋ぎ直しが起きないこと。7.1.2 では 1.5 秒で 15 回）。版を戻すと赤になる。
- **理由は不具合の是正であり、脆弱性（CVE）ではない。** 上流の修正は 7.2.0 の rabbitmq-dotnet-client#1857（issue rabbitmq-dotnet-client#1856）。
- 7.1.2 → 7.2.2 に本リポの使い方を壊す変更は無い（AST の確認を引用: CHANGELOG v7.2.0〜v7.2.2 は公開 API の追加と不具合の是正。依存パッケージの組は同じで、アセンブリの版は 7.0.0.0 のまま。Wolverine.RabbitMQ が参照する型・メンバ 142 件が 7.2.2 で解決する）。本リポでは platform・knowledge の `-warnaserror` ビルドと全単体試験で確かめた。

## 却下した案

- **ブローカの準備を待ってから Wolverine を開始する**: 準備の判定は接続を開くことになり、同じ欠陥に乗る。運用中の初回の開き損ねにも効かない。
- **Wolverine の再試行を止める（1 回で落として Pod の再起動に任せる）**: 漏れはプロセスの終了で消えるが、起動の回復力を落とし、原因（ライブラリの欠陥）は残る。
- **WolverineFx.RabbitMQ を上げる**: 6.24.x の最新でも推移の下限は 7.1.2 のままで、欠陥を外す保証にならない。Wolverine の版は 5 パッケージを同値に保つ規則（IADR-0233 ほか）があり、変更の範囲が広がる。

## 結果・残余

- 起動の再試行で接続が漏れない。実ブローカでの本数は Integration・E2E Live と PoC の再起動で確かめる。
- `WolverineFx.RabbitMQ` を上げて推移の版が 7.2.2 以上になったら、ピン留めの行は外してよい（外したら `dotnet list package` で解決版を確かめる）。
- AST の `Directory.Packages.props` にある「基盤と版が食い違う」注記は、本件のマージ後に外せる（AST へ知らせる）。
