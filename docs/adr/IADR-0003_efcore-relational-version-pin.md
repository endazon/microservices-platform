---
title: IADR-0003 EFCore.Relational のバージョン直接ピン（MSB3277 解消）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0002
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/01_architecture-overview.md"
issue: "#34"
---

# IADR-0003: EFCore.Relational のバージョン直接ピン（MSB3277 解消）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-06-27
- 決定者: claude（実装）

## 起点・関連

- 起点 Issue: #34
- 関連する実装仕様書: `../specs/20260627_chore_efcore-relational-version-pin.md`
- 関連する計画書 ID: ADR-0002（DB per Service。各 API が EFCore + Npgsql を使う前提）

## コンテキストと課題

DB を持つ 4 つの API プロジェクト（Authorization / DataSource / Document / Wiki）の
ビルドで `MSB3277` が発生する。`Microsoft.EntityFrameworkCore.Relational` が中央
パッケージ管理で直接ピンされておらず、版の異なる 2 経路で推移的に取り込まれるため。

- `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.2 → `Relational` 10.0.4
- `Microsoft.EntityFrameworkCore.Design` 10.0.9 → `Relational` 10.0.9

MSB3277 は警告だが、参照解決が曖昧になり実行時に意図しない版がバインドされ得る。

## 検討した選択肢

- (a) `Npgsql.EntityFrameworkCore.PostgreSQL` のダウングレード / `Design` の版調整で
  推移依存を一致させる。
- (b) `Microsoft.EntityFrameworkCore.Relational` を中央管理に追加し 10.0.9 に直接ピン、
  衝突するプロジェクトに `PackageReference` を加える。
- (c) MSB3277 を抑制（`MSBuildTreatWarningsAsErrors` / `NoWarn`）して放置する。

## 決定

**(b)** を採用する。`src/Directory.Packages.props` に
`Microsoft.EntityFrameworkCore.Relational` 10.0.9 の `PackageVersion` を追加し、
衝突する 4 API プロジェクトに `PackageReference`（直接ピン）を加える。

## 理由

- EFCore 本体（10.0.9）と `Design`（10.0.9）に Relational を揃えるのが最も自然で、
  上位版（10.0.9）は Npgsql 10.0.2 が要求する下位版（10.0.4）を満たすため安全。
- (a) は EFCore 周辺の版全体に波及しコストが高い。(c) は競合を隠蔽するだけで、
  実行時バインドのリスクが残る。
- 直接ピンは中央パッケージ管理（CPM）の方針に沿い、版を単一箇所で管理できる。

## 結果

- 良い影響: MSB3277 が解消し、Relational が全プロジェクトで 10.0.9 に確定する。
  参照解決が決定的になり、実行時バインドのリスクが消える。
- 影響範囲: 変更はパッケージ参照のみ。コード・スキーマ・マイグレーションに差分なし。
- テストプロジェクト: API への `ProjectReference` 経由で 10.0.9 を継承するため
  .csproj 変更は不要（`Design` は `PrivateAssets=all` で非推移）。
- フォローアップ: 将来 EFCore / Npgsql を更新する際は Relational のピンも合わせて見直す。

## 関連

- Supersedes: なし
- Superseded by: なし
