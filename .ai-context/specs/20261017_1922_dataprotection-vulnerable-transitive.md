---
title: develop の Security「Vulnerable transitive dependencies」が System.Security.Cryptography.Xml / .Pkcs 10.0.10 の advisory で落ち続けるのを、DataProtection 一族を 10.0.13 へ上げて直す（#1922）
type: spec
status: done
related_ids: [NFR, ADR-0032, IADR-0251]
author: claude
created: 2026-10-17
updated: 2026-10-17
issue: "#1922"
---

# 仕様書: DataProtection の推移依存の脆弱性（#1922）

## 起点となる計画書（トレーサビリティ）

- `NFR`（無採番）。計画の非機能要件表のセキュリティ区分（NFR-09〜18・28）に**依存ライブラリの脆弱性管理に当たる番号が無い**
  （認証・HTTPS・パスワード・ロック・セッション・ライフサイクル・暗号化・越境・運用の口・ログの完全性）。近い番号を付けない
  （traceability.md「起点 ID の種別」の 2）。同型の先行例は #61 / #80（Microsoft.OpenApi のピン）。
- 変えない前提: ADR-0032 / IADR-0251 決定 5（BFF の DataProtection 鍵リングを Redis で共有する）。パッケージの**系列は変えず**パッチだけ上げる。
- 起点: #1922（2026-10-14 以降、develop への push ごとに Security の `Vulnerable transitive dependencies` が失敗。コメント 30 件）。

## 原因（再現）

最新の失敗 run 38903394442（`7fa45d22`）は step 7「List vulnerable packages (incl. transitive)」で落ちている（steps は `gh api .../runs/38903394442/jobs`）。
ログ本文は取れないため、worktree で `security.yml` と同じ 3 ソリューション（`src/ai-stock-trading` / `src/knowledge` / `src/platform` の `backend/backend.slnx`）を
`dotnet restore` → `dotnet list <slnx> package --vulnerable --include-transitive` で再現した。

- 該当は **`Platform.Bff.Tests` の 1 プロジェクトだけ**（`Platform.Bff` 自体はフレームワーク参照で解決され表に出ない）。
- 経路: `Microsoft.AspNetCore.DataProtection.StackExchangeRedis` 10.0.10 → `Microsoft.AspNetCore.DataProtection` 10.0.10（中央の推移的ピン）
  → `System.Security.Cryptography.Xml` 10.0.10 → `System.Security.Cryptography.Pkcs` 10.0.10。
- advisory（NuGet の脆弱性データ `api.nuget.org/v3/vulnerabilities` で範囲を確認）:

| パッケージ | advisory | 深刻度 | 影響範囲（10.x） |
| --- | --- | --- | --- |
| System.Security.Cryptography.Xml | GHSA-r955-8q5j-3h83 / GHSA-7hqp-7c5m-4vq6 / GHSA-c399-9853-6jmj | High | 10.0.0〜10.0.12 |
| System.Security.Cryptography.Pkcs | GHSA-846f-f764-76mx | High | 10.0.0〜10.0.12 |
| System.Security.Cryptography.Pkcs | GHSA-2fmv-rv2c-59x7 | Moderate | 10.0.0〜10.0.12 |

## 方式

- `src/Directory.Packages.props` の `Microsoft.AspNetCore.DataProtection`（推移的ピン）と `Microsoft.AspNetCore.DataProtection.StackExchangeRedis` を
  **10.0.10 → 10.0.13** へ上げる。10.0.13 の nuspec は `System.Security.Cryptography.Xml` 10.0.13 に依存し、Xml 10.0.13 は Pkcs 10.0.13 を引く（いずれも範囲外）。
- **Xml / Pkcs を個別にピンしない。** 親を上げれば足りる（変異 2 で実測）。ピンを増やすと次の更新で追随先が増える。
- 2 つを同値に保つ理由: 同じ dotnet/aspnetcore の同一パッチ一族であり、推移的ピンだけを古くすると restore が NU1109 で落ちる（変異 1）。
- IADR は起こさない。#61 と同じ手順（中央定義でパッチ版へ上げる）であり、新しい方針の判断を含まない。
- `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.10 は advisory に当たらないため触らない（射程外）。

## 受け入れ基準と写像

| 受け入れ基準 | 写像（証跡） |
| --- | --- |
| `security.yml` の検査を手元で再現し 0 件 | 3 ソリューションの `dotnet list ... --vulnerable --include-transitive` 出力で advisory URL 0 件（修正前 5 件）。84 行すべて「has no vulnerable packages」 |
| 解決版が修正版である | `Platform.Bff.Tests/obj/project.assets.json` の libraries が DataProtection / Abstractions / StackExchangeRedis / Xml / Pkcs いずれも 10.0.13 |
| 影響ユニットの build / test / format | `dotnet build src/platform/backend/backend.slnx` 0 warning / 0 error。`dotnet test` は Docker を要する `BffSessionStoreValkeyTests` 5 件以外は通過（手元に Docker が無い。CI で走る）。`dotnet format --verify-no-changes` 通過 |
| 変異 1: 推移的ピンだけ 10.0.10 に戻す | restore が NU1109（`StackExchangeRedis 10.0.13 -> DataProtection (>= 10.0.13)` に対し 10.0.10）＋ NU1902/NU1903 が再出 |
| 変異 2: StackExchangeRedis だけ 10.0.10 に戻す | advisory 0 件（ピンが効く）。直すのはピンであり、StackExchangeRedis は一族の版合わせ |

## 母集合（規則 9・10）

- 誤りの側の文字列で走査した: `git grep -n -E 'Cryptography\.(Xml|Pkcs)|DataProtection.*10\.0\.1[0-3]|10\.0\.10' -- ':!src/ai-stock-trading' ':!CHANGELOG.md'`。
  - 当たり: `src/Directory.Packages.props` の 3 行（DataProtection 2 行は直した。`Caching.StackExchangeRedis` 10.0.10 は脆弱でないため据え置き）。
  - `.ai-context/specs/20260822_issue-439_bff-session-token-handler.md` の「DataProtection 10.0.10 は既に宣言済み」は当時の記録（凍結）であり直さない。
- AST（submodule）は自前の `Directory.Packages.props` を持ち、走査で 0 件。触らない。
- 新たに誤りになる自分の記述（規則 10）: 無し（版を書いたのは props のコメントだけで、値と同じ 10.0.13）。
