---
title: IADR-0016 Microsoft.OpenApi の推移的ピンによる脆弱性（NU1903）解消
type: impl-adr
status: Accepted
related_ids:
  - NFR
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - planning:projects/microservices-platform/06_technical/01_architecture-overview.md
issue: "#61"
---

# IADR-0016: Microsoft.OpenApi の推移的ピンによる脆弱性（NU1903）解消

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-04
- 決定者: claude（実装）

## 起点・関連

- 起点 Issue: #61（親: #48）
- 関連する実装仕様書: `../specs/20260704_NFR_openapi-vulnerability-pin.md`
- 関連する計画書 ID: NFR（セキュリティ）、`docs/DEFINITION_OF_DONE.md`「安全」
- 関連 IADR: IADR-0003（EFCore.Relational の版ピン）

## コンテキストと課題

`dotnet restore` で `NU1903: Package 'Microsoft.OpenApi' 2.0.0 has a known high
severity vulnerability`（GHSA-v5pm-xwqc-g5wc, High）が出る。`Microsoft.OpenApi` は
中央パッケージ管理（CPM）で直接ピンされておらず、
`Microsoft.AspNetCore.OpenApi 10.0.9` → `Microsoft.OpenApi 2.0.0` の推移経路で
脆弱版が取り込まれていた。影響は `Microsoft.AspNetCore.OpenApi` を参照する
全 API/BFF プロジェクト（10 件）と、それらを参照する IntegrationTests に及ぶ。

## 検討した選択肢

- (a) 影響する 10 プロジェクトの `.csproj` に `PackageReference Include="Microsoft.OpenApi"`
  を個別追加（IADR-0003 と同じ手法）。
- (b) CPM で推移的ピン（`CentralPackageTransitivePinningEnabled`）を有効化し、
  中央 `PackageVersion` に `Microsoft.OpenApi` のパッチ版を追加して単一箇所で固定。
- (c) `Microsoft.AspNetCore.OpenApi` 本体を、パッチ版 OpenApi を引く新版へ更新。
- (d) NU1903 を `NoWarn` で抑制して放置。

## 決定

**(b)** を採用する。`src/Directory.Packages.props` の `PropertyGroup` に
`<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>`
を追加し、`<PackageVersion Include="Microsoft.OpenApi" Version="2.1.0" />` を追加する。
これにより全消費側で `Microsoft.OpenApi` が 2.0.0 → 2.1.0（パッチ版）に固定される。

## 理由

- 影響が 10 プロジェクト＋テストに及ぶため、(a) は編集箇所が多く、将来の
  プロジェクト追加で追従漏れが起きやすい。(b) は単一箇所で完結し自動追従する。
- 推移的ピンは NuGet が「脆弱な推移依存の解消」に推奨する標準手法であり、CPM の
  「版を中央 1 箇所で管理する」方針とも整合する。
- (c) は `Microsoft.AspNetCore.OpenApi` の新版有無・互換性に依存し不確実。
  (d) は脆弱性を隠蔽するだけで DoD「安全」を満たさない。

## 結果

- 良い影響: NU1903 が解消し、`Microsoft.OpenApi` が全プロジェクトで 2.1.0 に確定。
  変更はパッケージ参照のみで、コード・API スキーマ・マイグレーションに差分なし。
- 影響範囲: 推移的ピン有効化により、中央 `PackageVersion` を持つ他パッケージも
  推移経路に現れれば中央版に固定される。既存版は据え置きのため影響は限定的だが、
  CI ビルドで版衝突・警告が出ないことを確認する。
- 検証: 本作業環境は .NET SDK / ネットワークが無効のため `dotnet restore` を実走
  できず、NU1903 の消失は CI（`ci.yml`）ログで確認する。パッチ版 2.1.0 で NU1903 が
  残る場合は advisory 記載の最小パッチ版へ調整する。
- フォローアップ: `security.yml`（dependency-review）は PR 差分のみ検査し既存推移
  依存を取りこぼすため、`dotnet list package --vulnerable --include-transitive` の
  CI 定期実行を別 Issue で追加する（ワークフロー編集は本 PR の権限外）。

## 関連

- Supersedes: なし
- Superseded by: なし
