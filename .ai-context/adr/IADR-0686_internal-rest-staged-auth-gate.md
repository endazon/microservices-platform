---
title: IADR-0686 内部の AI 分析と Wiki の REST の群に、段階つきの共有の認証の門（Auth:InternalGate:Mode。段 1 は報告、段 2 は 401）を掛ける（#2430）
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-09, ADR-0084, ADR-0032, IADR-0335, IADR-0424]
author: claude
created: 2026-11-06
updated: 2026-11-06
related_specs:
  - ../specs/20261106_2430_internal-rest-auth-gate.md
---

# IADR-0686: 内部の REST の群の段階つきの認証の門（#2430）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。番号は仮（マージ直前に develop の最大＋1 へ改める）。

- 状態: Accepted
- 日付: 2026-11-06
- 決定者: claude（#2430。コーディネーターの指示 2026-11-06）

## 起点・関連

- 起点 issue: #2430。
- 前提:
  - 計画 ADR-0084 決定 1（門は端点または端点群に掛ける）・ADR-0032（エッジは BFF）。
  - IADR-0335（未認証は認可サービスへ問い合わせる前に空へ倒す）。
- 作業仕様書: `.ai-context/specs/20261106_2430_internal-rest-auth-gate.md`。

## コンテキスト

- `/analysis/*` と `/wiki/*` は群の門を持たない。未認証の扱いはハンドラの中の短絡に任されていた。
- 素の `RequireAuthorization()` を足すと、未認証の応答が 200 から 401 へ変わり、稼働の呼び手を止め得る。

## 決定

### 決定 1 — 共有の門 `InternalAuthGate.RequireAuthenticatedOrReport(groupName)` を群の端点フィルタとして掛ける

- 置き場は `Platform.Shared.Infrastructure/Foundation/Extensions`（両ユニットから使う。ユニット外の参照は許された共有物だけ）。
- 認証済みなら素通しする。未認証なら、計器 `auth.internal_gate.anonymous.total`（群・段）に数え、Warning を出す（群ごとに 5 分に 1 行）。
- 群の名前は固定の文字列だけを渡す（利用者由来の値を属性にしない）。

### 決定 2 — 段は `Auth:InternalGate:Mode`。既定は `Report`

- `Report`（段 1）: 数えるだけで、ハンドラへ進める。応答は従来どおり（IADR-0335 の短絡が働く）。
- `Enforce`（段 2）: 401 で拒み、ハンドラへ入らない。
- 段の値は**起動時に**検証し、不正なら起動を止める（`AddInternalAuthGate` の ValidateOnStart。門を使うサービスの Program.cs が呼ぶ）。報告だけへ黙って倒さず、最初の要求で 500 にもしない。

### 決定 3 — 段 2 へ切り替える前提

- 段 1 のあいだに `auth_internal_gate_anonymous_total` が 0 のままであることを確かめる（BFF は常に利用者のトークンを運ぶ）。

## 結果・残余リスク

1. 段 1 のあいだは、未認証の要求もハンドラまで届く（従来どおり、閲覧できるものは無い）。
2. ［2026-11-06 是正 / AI レビュー］段の値の不正は起動時に止める（当初は最初の未認証の要求で例外にしていた）。門を使うサービスが `AddInternalAuthGate` を呼び忘れると既定（Report）で動くので、両サービスの起動の試験（不正な値で止まる）で配線を固定した。
3. 他の群（グラフ・ダッシュボード・フィードバック）は端点ごとの門のままで、本 IADR の門は掛けない（作業仕様書 §母集合）。
