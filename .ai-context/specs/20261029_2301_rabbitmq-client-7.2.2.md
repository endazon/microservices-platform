---
title: 作業仕様書 — RabbitMQ.Client を 7.2.2 へ上げ、開き損ねた接続が漏れる欠陥を外す（#2301）
type: spec
status: done
related_ids: [NFR, ADR-0027, IADR-0233, IADR-0648]
author: claude
created: 2026-10-29
updated: 2026-10-29
issue: "#2301"
---

# 作業仕様書 — RabbitMQ.Client 7.2.2（#2301）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。develop `b59bae67`）。AST#1491（AST の IADR-0595）と同じ形で進める（コーディネーターの指示）。

## 現状（実測）

- MSP は `RabbitMQ.Client` を直接参照しない。`WolverineFx.RabbitMQ` 6.24.4 の推移で **7.1.2** を解決している。
  - `dotnet list <slnx> package --include-transitive` で確認。platform 6 プロジェクト・knowledge 11 プロジェクト、いずれも 7.1.2。
- 7.1.2 の `AutorecoveringConnection.CreateAsync` は、開き損ねた接続を閉じない。自動回復が裏で繋ぎ直しを続ける。
  - Wolverine はブローカが未準備のあいだ起動を再試行する（5 秒ごと・最大 20 回）。再試行 1 回につき 1 本ずつ接続が漏れる。
  - 根拠: AST の調査（逆コンパイル・上流 rabbitmq-dotnet-client#1857〔7.2.0 で是正。issue rabbitmq-dotnet-client#1856〕・CHANGELOG）。AST#1491 と AST の IADR-0595 にある。
- 実測（本件の試験・修正前）: 偽のブローカ（ハンドシェイクで切る）へ自動回復つきで開き損ねると、1.5 秒の間に接続の試みが 15 回になった。
- `src/Directory.Packages.props` は `CentralPackageTransitivePinningEnabled=true` である。行を足せば推移の版も差し替わる。

## 方針（判断は IADR-0648）

- `src/Directory.Packages.props` に `RabbitMQ.Client` 7.2.2 の `PackageVersion` を足す（推移ピン留め）。Wolverine の版は変えない。
- 回帰試験は AST の `RabbitMqFailedOpenConnectionLeakTests` と同形にし、`Platform.Shared.Infrastructure.Tests` に置く（Wolverine の RabbitMQ 配線の試験と同じ場所）。
- 7.1.2 → 7.2.2 に破壊的変更は無い（AST の確認: CHANGELOG v7.2.0〜v7.2.2、Wolverine.RabbitMQ が参照する型・メンバの解決）。本件ではビルド（`-warnaserror`）と全単体試験で確かめる。

- **AST の試験との違い（1 点）**: AST の形は「開いた直後の試みが 1 回」を前提として表明していた。CPU が詰まった環境では、7.2.2 でも開く処理の中で 2 回試みる（4 コアに 8 本の負荷を掛けて 6 回とも 2・その後は増えない、を実測。負荷の無い全試験の並走でも 1 回赤になった）。
  本件では前提を「1〜2 回」に緩め、**開き損ねた後に増えないこと**（300 ms 待って落ち着いた数から、さらに 1.5 秒で増えない）を表明にした。7.1.2 では 4 → 19 で赤になる（実測）。

## 受け入れ基準

- [x] MSP の全サービスで `RabbitMQ.Client` の解決版が 7.2.2（platform 6・knowledge 11 プロジェクト）
- [x] 偽のブローカの試験で、開き損ねた後の接続の試みが 1 回のまま（修正前は 15 回で赤）
- [ ] Integration と E2E Live が緑（PR では起動しない。コーディネーターの手動起動で確かめる）

## 母集合（規則 9）

- `git grep -n "RabbitMQ.Client"`（`*.cs`・`*.csproj`・`*.props`）→ 直接参照 0。言及は Wolverine の配線の注記（`WolverineExtensions.cs` の amqps の証明書名の注記「7.1.2 ＋ 6.24.4 で実測」）と、試験の `RabbitMQ.Client.ConnectionFactory` の読み出し。
  - 証明書名の注記は実測した版の記録であり、版を上げても主張（名前の不一致を既定で受け入れる）を試験が守る。注記は「7.1.2 で実測」のまま残し、7.2.2 でも同じ試験が緑であることを確かめる（下記）。
- AST（submodule）は自分の `Directory.Packages.props` を持つ。AST#1491 が 7.2.2 へ上げる。MSP の solution から見た解決版は上の一覧どおり 7.2.2 である。

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
