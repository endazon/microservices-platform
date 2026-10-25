---
title: 作業仕様書 — setup-dotnet の dotnet-version を厳密な版 10.0.403 へ替え、全ワークフローで揃っていることを試験で見る（#2266）
type: spec
status: done
related_ids: [NFR, IADR-0638]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2266"
---

# 作業仕様書 — setup-dotnet の厳密な版（#2266）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。コーディネーターの依頼（P1・最優先）による hotfix である。

## 事象と原因（コーディネーターの実測）

- `dotnet-version: "10.0.x"` を指定すると、setup-dotnet は install script を `--channel 10.0` で呼ぶ。版の解決は aka.ms を経由し、`dotnetorigin.blob.core.windows.net` を引く。
- そこが 2026-10-26 19:09Z ごろから HTTP 409「Public access is not permitted on this storage account」を返す。develop を含む全 PR の CI が赤になる。
- 厳密な版（`--version 10.0.403`）では、primary の `builds.dotnet.microsoft.com`（200）と `ci.dot.net` を引くので通る（`dotnet-install.sh --dry-run` で確認済み）。
- 10.0.403 は、前回の CI が解決していた SDK である。

## 受け入れ基準

- [x] `.github/workflows/` の setup-dotnet の全 11 か所が `dotnet-version: "10.0.403"` である
- [x] 全箇所が同じ厳密な版であることを `scripts/scripts.repo.test.js` が見る。x 指定・値の不一致・版の欠落・`global-json-file` を変異試験で検出する
- [x] 「aka.ms の復旧を確認したら x 指定へ戻すかを判断する」を issue と IADR-0638 に残す
- [x] この PR の CI 自体が、SDK を導入できることを確かめる（push の CI）

## 母集合（規則 9: 誤りの側の文字列で全文を走査した）

`git grep -n -E "10\.0\.x|dotnet-install|--channel|aka\.ms/dotnet|dotnetorigin|dotnet-version"` で引いた（submodule `src/ai-stock-trading` は除く）。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `.github/workflows/` の `dotnet-version: "10.0.x"` 11 か所（ci.yml 5・claude-code-review・claude-coding・codeql・copilot-setup-steps・integration・security） | **直す** | 本件の原因 |
| `scripts/setup.sh` の `dotnet-install.sh --channel` | 外す（報告のみ） | SessionStart hook である。dotnet が既にある環境では導入を飛ばし、失敗しても継続する。CI を止めない |
| `README.md`・`docs/how-to/local-development.md` の「.NET SDK 10.0.x」 | 外す | 手元の要件（10.0 系のどれでもビルドできる）の記述で、いまも正しい。CI の導入の指定ではない |
| `.ai-context/` の過去の記録 | 外す | 凍結記録 |
| `src/coverage-floor.json` の「SDK 10.0.400 を導入し」 | 外す | 当時の実測の記録 |

## 値の単一情報源（検討）

- `global-json-file` は使えない。global.json は `8.0.0` + `rollForward: latestMajor` で、SDK の版を決めない（`8.0.x` が入る）。
- workflow の `env` は workflow ごとに閉じる。7 つの workflow をまたいで 1 か所に置く手段は無い（reusable workflow へ寄せる作り替えは hotfix の範囲を超える）。
- そこで、各所に同じ値を書き、揃っていることを `scripts.repo.test.js` で見る。先例は k3d の版である（`integration-stack.yml` と `cutover-rehearsal.yml` の `K3D_VERSION` を同じ試験ファイルが突き合わせる）。
- 既存の検査器で `dotnet-version` を見るものは無い。`check-action-versions.js` は action のメジャー版を、`check-ai-workflow-config.js` は setup-dotnet と dotnet の許可の対を見るだけである。

## 戻す条件

- aka.ms の `--channel 10.0` の解決が 409 を返さなくなったことを確認したら、x 指定へ戻すかを判断する（IADR-0638 決定 3）。

## AST（submodule）の同型

- 報告のみ。本 PR では触らない（AST は別途判断）。
