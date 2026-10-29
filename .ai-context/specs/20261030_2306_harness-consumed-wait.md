---
title: 作業仕様書 — DocumentNormalized の in-memory ハーネスの試験が、非活動の窓で消費の待ちを打ち切られて負荷時に落ちるのを、効果か失敗を待つ形にする（#2306）
type: spec
status: done
related_ids: [NFR, FR-01, FR-12, ADR-0057]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2306"
---

# 作業仕様書 — ハーネスの消費待ち（#2306）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `7c71742d`）。

## 現状（実測）

- CI run 40902248997（#2305 の knowledge 3/3）で `NormalizedAssetLedgerTests.資産の無い文書は空配列になる` が落ちた。内容は `harness.Consumed.Any<DocumentNormalized>()` が False（所要 2 s）。#2303 の作業中にも、手元で knowledge の全試験を並走させたときに同じ試験が 1 回赤になった。
- `Consumed.Any<T>()` は、MassTransit のハーネスの**非活動タイムアウト**が切れると「まだ来ていない」を「来ない」として False を返す。同じファイルの「再正規化」の注記が既に同じ性質を書いており、2 通目の待ちを避けていた。1 通目でも、配送・消費が窓を超えれば同じく落ちる。
- 再現の試み:
  - CPU に負荷を掛けた単独の実行（4 コアに 12 本、10 回）と、DocumentService の全試験の実行（8 本、3 回）では赤にならなかった（間欠）。
  - 決定的な再現として、ハーネスの非活動の窓を 1 ms にした（「混んで窓を超えた」世界）。従来の待ちは 3 回とも赤（`Consumed.Any` が False）、新しい待ちは緑。

## 方針

- 待ちの条件を「消費の効果（台帳の文書が条件を満たす）か、失敗（`Fault<DocumentNormalized>` の発行）」にする。上限（60 秒）までの間、条件が成り立てば即座に抜ける。固定の待ちではなく、窓も延ばさない。
- 共通の helper `DocumentNormalizedHarnessWait.UntilDocumentAsync` に置き、同型の 3 ファイルで使う。
  - 再正規化（前提の行が既に在る）では、「当たった」（題名が変わった）を条件にする。
- `DiagramCodingLimitsTests`（ConversionService）は同型ではない。ハーネスを使わず、受け口の ct が取り消される時刻を `TaskCompletionSource` で待つ（既に条件の待ち）。負荷（12 本）で 5 回走らせて赤にならなかったので、本件では触らない。

## 受け入れ基準

- [x] 非活動の窓を超えた世界で、従来の待ちは赤、新しい待ちは緑（試験 `配送が非活動の窓を超えても消費の効果を待てる`）
- [x] CPU に負荷を掛けて（4 コアに 12 本）5 回続けて、対象の 9 件が緑

## 母集合（規則 9）

- `git grep -n "Consumed.Any<"`（試験）→ `NormalizedAssetLedgerTests`（3 か所）・`NormalizedBodyPresenceTests`（1）・`NormalizedExposureWithdrawalTests`（1）。すべて直した。
- `Published.Any<DocumentNormalized>`（ConversionService の 2 か所）は、発行を await した後に「発行済み」を見るものである。窓の内に既に載っているので、この型の事故の対象ではない（外す）。

## 規則 11（窓）

- 窓（ハーネスの非活動タイムアウト）を扱う是正である。形は「窓に依らない条件で待つ」にした。どちらかの端を見る形ではなく、窓を使わない形である。
- プローブは 2 つ置いた。1 つは窓を超えた世界（増える側。新しい待ちでも緑）、もう 1 つは通常の世界（既存の 8 件。緑）。表に代えて、上の 2 つの実測を記録する。
