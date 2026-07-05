---
title: 作業仕様書 — 推移依存の脆弱性を CI で定期スキャン（#61 の後継）
type: work-spec
status: review
related_ids:
  - NFR
author: claude
created: 2026-07-05
updated: 2026-07-05
plan_refs:
  - "../../CLAUDE.md（自動化・検証・安全 / CI ゲート）"
related_specs:
  - ../security/security.md
  - ../operations/operations.md
related_adrs:
  - ../adr/IADR-0017_transitive-vulnerability-scan.md
related_prs:
  - "#61"
# 注: IADR-0016（PR #61・OpenApi 推移ピン）は未マージのため develop に未存在。相互参照は prose 内で #61 として記載。
---

# 作業仕様書: 推移依存の脆弱性を CI で定期スキャンする

## 目的

PR #61（`Microsoft.OpenApi` を推移的ピンでパッチ版 2.1.0 へ固定し NU1903/GHSA-v5pm-xwqc-g5wc を解消）の
フォローアップとして、**推移依存を含む既存の全依存**を CI で継続的に脆弱性走査する仕組みを恒久化する。

起点: NFR（セキュリティ / CI・CD）、CLAUDE.md「自動化・検証・安全（CI ゲート）」。

## 背景（#61 で残した課題）

PR #61 の注意書きで「既存推移依存の脆弱性検出（`dotnet list package --vulnerable --include-transitive` の
CI 定期実行）は別 Issue で対応を推奨」とされていた。当初の理由は「`.github/workflows/` の編集が Claude
GitHub App の権限で不可」であったが、**本作業はローカルの Claude Code 環境で実施しており当該権限制約は
無い**ため、フォローアップを別 Issue に切り出さず本 PR で直接実装する。

### 既存構成のギャップ（確認済み）

| 検査 | 対象範囲 | ギャップ |
| --- | --- | --- |
| `security.yml` `dependency-review` | **PR で新規に導入される依存の差分のみ** | マージ後に公開された advisory を取りこぼす。既存の推移依存は検査対象外 |
| `security.yml` `secret-scan`（gitleaks） | 秘密情報のみ | 脆弱性は対象外 |
| `codeql.yml` | コードの SAST | 依存パッケージの既知脆弱性は対象外 |

`dependency-review` は PR 差分しか見ないため、NU1903 のように「既にマージ済みの推移依存に対して
**後から** advisory が公開される」ケースを構造的に検出できない。これを埋めるのが本作業。

## 対応方針

`security.yml` に **`vulnerable-scan` ジョブ**を追加する。

- 実行内容: `dotnet restore KnowledgePlatform.slnx` → `dotnet list KnowledgePlatform.slnx package
  --vulnerable --include-transitive`。CPM（`src/Directory.Packages.props`）の中央定義に対して走査する。
- **判定**: `dotnet list package --vulnerable` は脆弱性が有っても exit 0 を返すため、出力を解析し、
  深刻度列（`Critical`/`High`/`Moderate`/`Low`）が現れたら `exit 1` で CI を失敗させる。
- **トリガー**: `schedule`（毎週月曜 03:00 UTC）を主目的とし、`push: [develop, main]` と `pull_request`
  でも二重に検査する。定期実行が「変更が無くても新規 advisory を既存依存に対して検出する」核心。
- あわせて `security.yml` の `push` トリガーを `[main]` → `[develop, main]` に整合させる
  （既定ブランチが `develop` であり、`main` 限定では develop の push で発火しない。IADR-0015 と同方針）。

深刻度別の失敗基準は現状「1件でも検出したら失敗（fail-closed）」とする。運用で誤検知・抑制が必要になれば
`NuGetAudit`/`NuGetAuditLevel`（プロジェクト側の監査設定）での閾値調整を別途検討する（残課題）。

## 実装物（本 PR）

- `.github/workflows/security.yml`
  - `on:` に `schedule`（`cron: "0 3 * * 1"`）を追加、`push` を `[develop, main]` に整合。
  - `vulnerable-scan` ジョブを追加（restore → `dotnet list package --vulnerable --include-transitive` →
    深刻度検出で `exit 1`）。
- `docs/adr/IADR-0017_transitive-vulnerability-scan.md`（本作業の実装判断）。
- `docs/adr/README.md` の一覧へ IADR-0017 を追記。

## 受け入れ基準

- [x] `security.yml` に推移依存の脆弱性スキャンジョブを追加した。
- [x] スキャンは `--include-transitive` を付け、既存の推移依存も対象にしている。
- [x] `dotnet list package --vulnerable` の exit 0 問題に対応し、深刻度検出時に CI を失敗させる判定を入れた。
- [x] `schedule` トリガーで、変更が無くても定期的に既存依存へ新規 advisory を照合する。
- [x] `security.yml` の `push` トリガーを develop 運用へ整合した（`[develop, main]`）。
- [x] 重要な実装判断を IADR-0017 に記録した。
- [ ] **CI 実走での検証**: 本ブランチの `security.yml` が発火し、`vulnerable-scan` が緑（または脆弱性検出で
      正しく赤）になることを確認する。本作業環境は .NET SDK/ネットワーク無効のため `dotnet restore`/`list` を
      実走できず、ジョブの静的定義のみをコミットしている。実測は CI に委ねる。

## 残課題・フォローアップ

- **#61（IADR-0016）のマージ順序**: 本 IADR は #61 が採番する IADR-0016 の後継として IADR-0017 を用いる。
  #61 が未マージのうちに本ブランチが先行マージされると develop 上で 0016 が一時的に欠番になる。
  #61 → 本 PR の順でマージするのが望ましい。
- **深刻度閾値の調整**: 現状は fail-closed（1件でも失敗）。誤検知や未修正 advisory の一時抑制が必要になれば
  `NuGetAuditLevel` 等での閾値運用を検討する。
- **-warnaserror の CI 導入**（#61 注意書き）: 「警告ゼロ」実測後に別途対応する（本作業のスコープ外）。
