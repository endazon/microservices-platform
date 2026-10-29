---
title: 作業仕様書 — AI 提案の却下で、両端の本文指紋を GraphService の複製（BodyHash）から控える（#2144 の指紋の是正）
type: spec
status: done
related_ids: [FR-18, ADR-0033, IADR-0300, IADR-0276]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2144"
---

# 作業仕様書 — 却下時の本文指紋（#2144）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `e760d82e`）。

## 射程

- #2144 には 2 つの論点がある。本 PR は **却下時の本文の指紋の是正だけ**を扱う。
- 類似度の方式（語の共起と、計画 FR-18 の「埋め込み」との差異）は、planning#837 へ環流済みで、裁定待ちである（issue のコメント 2026-10-23）。本 PR では触らない。

## 現状（develop で確かめた）

- `Reject/Endpoint.cs` は、指紋を要求本文（`RejectAiSuggestionRequest`）からだけ取る。
- BFF は本文を送らない（IADR-0300 決定 3。SPA は指紋を持てない。IADR-0276 決定 2）。そのため、控える指紋は常に null だった。
- 解除の発火は外側の門（`GraphDocumentSyncConsumer` の「本文指紋の変化」）で決まるので、変わらない。
- 一方で `ReinstatedReason` は、初回の解除で常に `both` になっていた（null と現在値の比較で、両端とも「変わった」になる）。
- IADR-0300 決定 3 は、「指紋を後段が自分の複製から読む形にすれば消える差」として、追随を予告している。

## 方針

- 却下の口で、両端の文書の `BodyHash` を GraphService の複製（`Documents`）から読み、それを控える。
  - タグの提案には先が無いので、先の指紋は null のままにする。
- 複製に指紋が無い（取り込み前・本文なし）ときだけ、要求本文の値を使う。内部の呼び出し元との互換のためである。
- BFF は変えない。本文を送らない形のまま、コメントだけを追随させた。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n "TryReject("` で走査した。呼び出しは却下の口の 1 か所だけである。`SourceFingerprint` / `TargetFingerprint` を読むのは、解除（`TryReinstate`）とその試験だけである。
- **規則 10**: 次の 2 か所を、この変更に合わせて書いた。
  - BFF の却下の口のコメント（「帰結は IADR-0300 決定 3」の後に追記）
  - テスト仕様書 FR-18 の T-11-b
- IADR-0300 は凍結記録なので書き換えない。本仕様書が追随の記録である。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 却下時に本文の指紋が保存され、本文が変わったときに再提示の判定に使われる | `AiSuggestionEndpointsTests.Rejecting_without_a_body_records_both_fingerprints_from_the_replica`。本文なしで却下すると、両端の指紋が控えられる。先だけが変わったときの解除の理由が `target` になる | 赤（実測。控えた指紋が null） |
