---
title: IADR-0651 MCP の tools/list_changed は、無状態の Streamable HTTP のまま subscriptions/listen の流れで送る（自前の listen の処理）。セッションの方式は変えない
type: impl-adr
status: Accepted
related_ids: [FR-16, UC-08, ADR-0024, IADR-0269, IADR-0292]
author: claude
created: 2026-10-30
updated: 2026-10-30
related_specs:
  - ../specs/20261030_2142_mcp-tools-list-changed.md
---

# IADR-0651: tools/list_changed の送出（#2142）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-30
- 決定者: claude（#2142。親 #2047 の指摘 L-13）

## 起点・関連

- 起点 issue: #2142。計画 ADR-0024 §2（コア改修なしでの追従）。
- 残件の出所: [IADR-0269](./IADR-0269_mcp-tool-publication-and-service-account-exclusion.md)、[IADR-0292](./IADR-0292_service-mcp-tool-self-declaration.md)。
- 作業仕様書: [20261030_2142_mcp-tools-list-changed](../specs/20261030_2142_mcp-tools-list-changed.md)。

## 背景

- 実効ツール一覧の版は進むが、クライアントへ知らせていなかった。
- MCP 面は Streamable HTTP を**無状態**で動かしている（SDK 2.2.0 の既定）。
  - 無状態では、要求の外から送る通知は捨てられる。
  - SDK が自動で送る list_changed は、`ToolCollection` の変化をセッション全体へ流すもので、無状態では届かない（実測）。

## 決定

1. **セッションの方式は変えない（無状態のまま）。**
2. 2026-07-28 の改訂の `subscriptions/listen` の処理を自前で持ち（`WithSubscriptionsListenHandler`）、その応答の流れで list_changed を送る。
   - 受付の前に購読し、受付を 1 回送り、版が進むたびに送る。
   - 各通知の `_meta` に listen の要求 ID を付ける。
   - 続けて進んだ版は 1 回にまとめる。
3. `capabilities.tools.listChanged = true` を申告する。
4. 合図の元は `ToolCatalog.Changed` とする。版が進んだときだけ呼ぶ。

## 却下した案

- **有状態（`Stateful` / `StatefulForInitializeClients`）へ変えて、セッション全体へ送る**: 複数のレプリカでセッションの付着（affinity）が要り、配備の前提が変わる。2026-07-28 の改訂はセッションを廃した方向でもある。
- **中身の無い `ToolCollection` を置いて `Changed` を鳴らす**: 無状態では、どのクライアントにも届かなかった（実測）。

## 帰結・残余リスク

- 初期化の握手の利用者（2025-11-25 以前の改訂）へは届かない。その利用者は `tools/list` を引き直して知る。届けるには有状態への切り替え（上の却下案）が要る。
- 通知は 1 プロセスの中のカタログの変化だけを運ぶ。mcp-server を複数のレプリカで動かす場合でも、各レプリカが自分の周期で突合して版を進め、自分に繋いでいる listen へ送るので、取りこぼしは周期（既定 300 秒）の遅れに留まる。
