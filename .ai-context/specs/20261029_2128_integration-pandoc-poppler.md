---
title: 作業仕様書 — integration.yml に pandoc / poppler-utils を入れ、ConversionService の外部ツール依存の試験 9 件を実走させる（#2128 段 1）
type: spec
status: done
related_ids: [NFR, FR-12, ADR-0090, IADR-0232, IADR-0320, IADR-0356, IADR-0507]
author: claude
created: 2026-10-29
updated: 2026-10-29
issue: "#2128"
---

# 作業仕様書 — 回収実行で外部ツール依存の試験を実走させる（#2128 段 1）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。develop `dc720cd6`）。親は #2047（指摘 M-25）。
> **本件は段 1 だけを扱う。** 段 2（PR ゲートの脚・必須チェックの変更）は利用者裁定が要るので触らない。

## 現状（実測）

- ConversionService の試験のうち、実 `pandoc`・実 `pdftotext` を要する 9 件は、ツールが無いと `Assert.SkipUnless` で Skipped になる。
  - `PandocConversionServiceTests.cs` の `SkipUnless(PandocAvailable())`: 4 件。
  - `PdfTextLayerConverterTests.cs` の `SkipUnless(PdfToTextAvailable())`: 5 件。
- `ci.yml`・`integration.yml` のどちらも pandoc / poppler を入れていない（`git grep -n "pandoc\|poppler" .github/workflows` で 0 件）。そのため 9 件はどこでも実走しない（監査時の run 39538321988 で `Skipped: 9`）。
- 本番のイメージ（`ConversionService/Dockerfile`）は noble の `pandoc` と `poppler-utils` を入れている。ubuntu-latest のランナーも noble である。

## 方針

- `integration.yml` の試験の step より前に、`pandoc` と `poppler-utils` を apt で入れる step を置く。
  - 入れたあと `pandoc --version` と `pdftotext -v` を走らせる。**入らなければ step ごと赤にする**（`|| true` を付けない）。黙って入らないと 9 件が Skipped に戻り、緑のまま回収が空になる。
  - 版は本番イメージと同じ配布元（noble の apt）に揃う。
- `ci.yml`（PR）には入れない。逆向きの 4 件（`SkipWhen`。ツールが**無い**ときの失敗・縮退を見る試験）は PR 側で走り続ける。両方の向きがどこかで実走する形になる。
  - integration では逆向きの 4 件が Skipped になる。これは設計どおりで、PR 側が回収している。
- 回帰の防止: `scripts.repo.test.js` に配線の試験を足す。導入の step が試験の step より前に在ること、`|| true` などで失敗を握り潰していないこと、`pandoc` と `pdftotext` の確認を走らせていることを見る。

## 受け入れ基準

- [x] integration.yml が試験の前に pandoc / poppler-utils を入れ、入らなければ赤になる（配線の試験。修正前は赤）
- [ ] integration.yml の実行で ConversionService の 9 件が Skipped ではなく実走する（PR では integration.yml が起動しない。コーディネーターの手動起動の結果で確かめる）
- 段 2 は本件の対象外（裁定待ち）

## 母集合（規則 9）

- `git grep -n "SkipUnless\|SkipWhen" -- src/*/backend` のうち外部ツールの有無で分岐するもの → ConversionService の 2 ファイルだけ（SkipUnless 9・SkipWhen 4）。他の `Skip` は Docker（Testcontainers）など別の前提で、本件の対象外。
- `git grep -n "apt-get" .github/workflows` → `e2e-live.yml`（libnss3-tools）・`frontend.yml`（Playwright の --with-deps）。pandoc / poppler は 0 件。

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
