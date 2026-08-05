---
title: SearchRequest に検索モードを追加する（3 値・既定ハイブリッド）
type: spec
status: done
related_ids: [FR-03, UC-01, SC-02, ADR-0009]
author: Claude
created: 2026-08-06
updated: 2026-08-06
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - ../api/openapi.yaml
  - ../adr/IADR-0131_openapi-as-bff-contract-source.md
---

# 仕様書: 検索モード（3 値）の追加（issue #531）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-03（キーワードと自然文の双方で横断検索できる。ベクトル検索＋全文検索のハイブリッド）
- ユースケース（UC）: UC-01（横断検索）
- 画面（SC）: SC-02（検索結果一覧の検索モード切替）
- 関連 ADR（計画）: ADR-0009（ベクトル DB の抽象化）
- 裁定: **質問票 第12回 Q4**（2026-08-05）／環流元 planning#197 ／実装 issue [#531](https://github.com/endazon/microservices-platform/issues/531)

## 目的・背景

計画は検索モードの切替を SC-02 に置くが、契約（`SearchRequest`）に載っていなかった。
裁定で **3 値**（`hybrid`〔既定〕/ `keyword` / `semantic`）に確定した。

> **2 値（キーワード｜意味）にしてはならない。** 現行は常時ハイブリッドで動いており、
> 当初計画の 2 値を入れると**利用者はハイブリッドを選べなくなり機能後退になる**（裁定 Q4）。

`IVectorStore` は `SearchAsync`（ベクトル）と `KeywordSearchAsync`（全文）を**既に別メソッドで持つ**ため、
足りないのは呼び出し側から指定する経路だけである。

## 対象範囲

- 対象:
  - `Knowledge.Contracts` の `SearchRequest.Mode` と値集合 `SearchModes`（3 値）
  - `HybridSearchService` の分岐（keyword = 全文のみ／semantic = ベクトルのみ／hybrid = 従来の RRF 融合）
  - BFF `/bff/search` のモード透過
  - OpenAPI（`docs/api/openapi.yaml`）の `SearchRequest.mode`
  - 単体テスト（分岐・縮退・既定・deny-by-default の非バイパス）
- 対象外:
  - **SPA 側の UI**（SC-02 の切替コントロール）。本 issue は契約と配線が射程であり、
    画面は #452 系の画面実装で扱う（契約が載ったことで着手可能になる）
  - 並び順（#532）・更新日時（#536）。**別 issue**（ただし #532 と #536 はセットで裁定されている）

## 設計

| 決定 | 内容 |
| --- | --- |
| 値集合 | `hybrid`（既定）/ `keyword` / `semantic` の **3 値**。`SearchModes` の `const` で持つ |
| 型 | **`enum` にしない**（IADR-0131 決定 5 と同じ理由。閉じた `enum` は後段の値追加を SPA 側で破壊的にする） |
| 既定値 | `Mode = null`（既定値つきで追加する。**既定値の無いメンバー追加は契約上の破壊的変更**） |
| 未知値 | `SearchModes.Normalize` が `hybrid` へ**縮退**させる。旧クライアント・誤入力で検索が壊れない |
| 候補数 | 単系統では `topK` をそのまま使う（融合しないため広く取る意味が無い。hybrid は従来どおり `topK * 4`） |
| BFF | モードは**そのまま透過**する。`Scope` と違い信頼性の問題が無い——モードは絞り込みの**種類**であって権限ではない |
| 埋め込み | `keyword` では**埋め込みを生成しない**（無駄な LLM 呼び出しを避ける） |

## 受け入れ基準

- [x] `SearchRequest` に検索モードがあり、値集合が **3 値**である（2 値ではない）
- [x] 既定はハイブリッドで、**未指定の呼び出しは従来と同じ挙動**（後方互換）
- [x] `keyword` は全文検索のみ、`semantic` はベクトル検索のみを呼ぶ
- [x] 未知の値・空文字はハイブリッドへ縮退する
- [x] モード指定が **ABAC の deny-by-default を迂回しない**
- [x] BFF がモードを後段へ透過する
- [x] OpenAPI に `mode` が載っている（契約の単一情報源）

## テスト方針

`HybridSearchServiceTests` に 8 ケース（記録用スタブで**どちらの系統が呼ばれたか**を観測する）。

- 既定 = 両系統／`keyword` = 全文のみ＋埋め込み 0 回／`semantic` = ベクトルのみ
- 単系統は候補を広げない（`topK` そのまま）／hybrid は `topK * 4`
- 未知値 3 種（`null` / `""` / `"unknown-mode"`）が hybrid へ縮退
- 大小文字無視
- **スコープ未解決なら系統を一切呼ばない**（モードで deny-by-default を迂回できない）
- 値集合そのものの回帰（**3 値であること**・既定が hybrid であること）

## 計画書との差異

- 差異: なし（裁定 Q4 のとおり 3 値で実装した）

## 未決事項

- SPA の SC-02 に切替 UI を出すのは画面側の作業（本 issue の対象外）。
  契約が載ったので、`pnpm run codegen` で生成フックに `mode` が現れる。

## 検証

```console
$ dotnet build src/knowledge/backend/backend.slnx                    # ビルドに成功しました
$ dotnet test .../RetrievalService.Api.Tests.csproj                  # 合格 27（うち本作業 8）
$ node scripts/check-contract-schema.js                              # baseline と一致（非破壊 2 件を --update で反映）
```

契約差分は**非破壊 2 件**（`SearchRequest.Mode` のメンバー追加・`SearchModes` の型追加）で、
破壊的変更は 0 件だった。
