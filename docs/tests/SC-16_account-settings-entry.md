---
title: SC-16 アカウント設定への導線 テスト仕様書
type: test-spec
status: draft
created: 2026-08-04
updated: 2026-08-21
author: Claude
---
<!-- trace:
ids: [SC-16]
adrs: [ADR-0031]
iadrs: [IADR-0124]
specs: [01_screens, 20260804_issue-490_spa-router-shell, IADR-0124_tanstack-router-unit-composition]
issues: []
-->

# テスト仕様書: SC-16 アカウント設定への導線

> **本リポジトリは SC-16 の画面そのものを実装しない。** SC-16（アカウント設定）は
> Keycloak のアカウントコンソール（テーマ）であり、`auth.example.co.jp` の別ホストで配信される
> （05_screens §共通シェル（計画リポ）
> 「適用除外（SC-13〜16）」）。本書が写像するのは **SPA 側が持つ導線**——共通シェルの
> ユーザーアイコンから SC-16 へ遷移できること——だけである。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-16 アカウント設定**（本リポジトリの担当は導線のみ）
- 計画の該当記述: 05_screens §共通シェル「**ユーザーアイコン（アバター）**: 共通シェルの上部右端に、
  共通シェルを適用する全画面（SC-01〜12・SC-17〜21）で表示し、クリックで SC-16 アカウント設定へ遷移する」
- 関連 ADR: ADR-0031（計画リポ）（SPA スタック）／
  IADR-0124: TanStack Router とユニット合成の両立（型付きルート木・旧契約ブリッジ・型登録の実装形）（共通シェルの実装範囲）

## テスト対象・範囲

| 対象 | 範囲 |
| --- | --- |
| 共通シェル（`platform/frontend/src/foundation/ui/Layout.tsx`）のユーザーアイコン | **本書の対象** |
| SC-16 の URL 組み立て（`accountConsoleUrl`。実行時 config の `oidc.authority` から導出） | **本書の対象** |
| SC-16 の画面そのもの（プロフィール・パスワード・OTP・セッション管理） | 対象外（Keycloak テーマ） |
| 認証フロー（ログイン・MFA・リセット。SC-13〜15） | 対象外（第 3 段 / #439・Keycloak テーマ） |

## テストケース

| # | 観点 | 期待 | 実装 |
| --- | --- | --- | --- |
| 1 | 共通シェルにユーザーアイコンが出る | アクセシブル名「アカウント設定（<ユーザー名>）」のリンクが 1 つある | `Layout.test.tsx` |
| 2 | 遷移先が Keycloak アカウントコンソールである | `href` が `.../account` で終わる | `Layout.test.tsx` |
| 3 | 遷移先をビルドへ焼き込まない | 実行時 config の `oidc.authority` から組み立てる（末尾スラッシュの有無を吸収する） | `Layout.test.tsx`（`accountConsoleUrl` の純関数テスト） |

## 前提・注記

- SC-16 は共通シェルの適用外（左ナビ・AI チャットパネル・パンくずを持たない）であり、
  SPA のルータでは扱わない。したがって導線は `<Link>`（内部遷移）ではなく `<a href>`（外部遷移）である。
- 接続先（`oidc.authority`）は実行時 config（`public/config.js`）で注入する。環境ごとに異なるため
  テストでは URL の**組み立て規則**のみを固定し、具体的なホスト名は固定しない。
- 第 3 段（#439 / ADR-0032（計画リポ））で
  BFF セッション方式へ移ると、authority の取得経路が変わり得る。そのとき本書のケース 3 を見直す。
