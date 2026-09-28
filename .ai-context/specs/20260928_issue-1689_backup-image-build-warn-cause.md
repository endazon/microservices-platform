---
title: platform-backup イメージのビルド失敗の WARN を、ビルドのログから原因別に案内する（#1689）
type: spec
status: done
related_ids: [NFR-21, ADR-0008, IADR-0471, IADR-0066]
author: claude
created: 2026-09-28
updated: 2026-09-28
issue: "#1689"
---

# 仕様書: platform-backup イメージのビルド失敗の WARN を原因別にする（#1689）

## 起点（トレーサビリティ）

- 要求: NFR-21（障害検出 —— 失敗を利用者に正しく知らせる）
- 計画 ADR: ADR-0008（ローカル配備）
- 関連 IADR: IADR-0471（platform-infra の暗号化バックアップ・同梱イメージ。LOCAL_ONLY_IMAGES のビルド失敗は起動を止めず WARN）、IADR-0066（ローカルイメージ供給）
- 起点 issue: #1689。新しい実装判断（IADR）は起こさない —— IADR-0471 の「WARN を出して続ける」の文言の是正であり、止める／続けるの判断は変えない。

## 目的・背景

2026-09-28 の稼働クラスタでの起動で、`k3d-local/platform-backup:…` のビルドが
`error getting credentials - err: exit status 22` で失敗した（資格情報ヘルパーの失敗）。
`scripts/k8s-local-images.sh` の WARN は原因を「age の版が Alpine で上がった可能性が高い」と決め打ちし、
Runbook §6（イメージの版を上げる）へ導く。原因が違うのに版を上げる手順へ進ませるのは誤誘導である。

## 母集合（規則 9: 誤りの側の文字列で走査した。`origin/develop` = `6bb387df`）

走査: `grep -rn "age の版が Alpine で上がった\|可能性が高い"`・`grep -rn "イメージの版を上げる\|ビルドに失敗しました\|WARN を出して続ける\|build-local (platform-backup)"`
（`src/`〔submodule〕と `.ai-context/specs/` を除く）。

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `scripts/k8s-local-images.sh` | **変更** | WARN の本体。ビルドのログを採り、原因別に案内する |
| `scripts/lib/backup-image-build-cause.sh` | **新規** | ログの分類（純粋な判定）。シェル本文の grep 検査は「文字列が在ること」しか見ないため、判定は関数に閉じて試験から直接呼ぶ |
| `scripts/k8s-local-up.test.js` | **変更** | #1564 の WARN の既存試験の場所。docker スタブにログを出させ、3 分岐の案内を固定する |
| `docs/operations/platform-infra-backup-runbook.md` | **変更**（失敗の分岐の表の 1 行・§6 の冒頭 1 文） | 「WARN が出ていれば §6（age の版が…可能性が高い）」が同じ誤誘導（規則 10） |
| `scripts/README.md` | **変更**（lib の行を足す） | scripts の一覧 |
| `.ai-context/adr/IADR-0471_*.md` | 変更しない | 凍結記録。止めずに続ける判断は変わらない |
| `.github/workflows/images.yml` | 変更しない | CI の厳格な赤（build-local）は別経路で、案内の文言を持たない |
| `deploy/local/platform-backup/image/Dockerfile` | 変更しない | 失敗の文言の出どころ（読むだけ） |

## 失敗の文言（実物を確かめた）

- 資格情報ヘルパー: docker-credential-helpers `client/client.go` の `error getting credentials - err: …, out: …`（issue の実ログと一致）。
- レジストリの認証・到達: BuildKit / containerd の `failed to authorize` / `unauthorized` / `authentication required` /
  `pull access denied` / `toomanyrequests` / `failed to resolve source metadata` / `failed to do request` /
  `dial tcp` / `i/o timeout` / `no such host` / `TLS handshake timeout` / `connection refused`。
  busybox wget（ミラーへの到達）の `bad address '…'` / `download timed out`。
- 版の解決（apk-tools の実物）: `src/commit.c` の `unable to select packages:`、`src/app_fetch.c` の
  `unable to select package (or its dependencies)`。
  Dockerfile は age を apk の索引ではなくミラーのファイル（`age-<版>.apk`）で取るため、上流が `-rN` を上げた日の
  実際の失敗は busybox wget の `server returned error: HTTP/1.1 404 Not Found`（`networking/wget.c`）である。これも版の分岐に入れる。
  ベースの Alpine とブランチの食い違い（Dockerfile 自身の `ベースの Alpine（…）が ALPINE_BRANCH=… と違います`）も §6 の対象なので版の分岐に入れる。
- **sha256 の不一致（busybox `sha256sum` の `FAILED` / `computed checksums did NOT match`）は版の分岐に入れない。**
  同じ版のファイルの中身が変わったことを意味し、版を上げる手順で上書きすべきものではない。判別不能として扱う。

## 設計

1. `scripts/lib/backup-image-build-cause.sh` に `backup_image_build_cause <ログのファイル>` を置き、
   `registry` / `version` / `unknown` のどれか 1 語を標準出力へ出す。判定の順は registry → version → unknown
   （資格情報・到達の失敗はベースのメタデータの段で起き、RUN まで進まない。両方が載ることは無い）。
   ログが読めなければ `unknown`（断定しない）。
2. `k8s-local-images.sh` は LOCAL_ONLY_IMAGES のビルドの出力を `2>&1 | tee <一時ファイル>` で画面へ流しつつ採る
   （`pipefail` によりビルドの失敗はそのまま失敗になる）。失敗したら分類を配列に持ち、WARN で原因別に案内する。
   一時ディレクトリは EXIT の trap で消す。lib が読めなければ exit 3（live-opt-in.sh と同じく黙って続けない）。
3. 案内:
   - registry: 資格情報ヘルパーかレジストリ・ミラーへの到達の失敗であり age の版の問題ではない、と告げ、
     ① Docker Hub へログインし直す ② 資格情報ヘルパー（`~/.docker/config.json` の `credsStore` / `credHelpers`）を
     確かめ、コンテナランタイム（Rancher Desktop / Docker Desktop）を再起動する ③ プロキシ・DNS を確かめる、の手順と、
     直したあとの作り直し（Runbook §1 の 5）を示す。§6 へは導かない。
   - version: 従来どおり §6 へ導く（断定を「版の解決に失敗した」へ改める）。
   - unknown: 原因を断定せず、上に出たビルドのログを見るよう案内し、Runbook の「失敗したときの分岐」を示す。

## 受け入れ基準（→ 試験）

| # | 基準 | 試験（`scripts/k8s-local-up.test.js`） |
| --- | --- | --- |
| A1 | `error getting credentials` のログで、資格情報・到達の案内（ログイン・ヘルパー・再起動）を出し、§6 へ導かない | 起動器を docker スタブの下で走らせ、stderr を見る |
| A2 | 版の解決の失敗（wget の 404）のログで §6 へ導き、資格情報の案内を出さない | 同上（既存の #1564 の試験を版の分岐として締める） |
| A3 | どちらにも当たらないログで、原因を断定せず（§6 も資格情報も出さず）ログを見るよう案内する | 同上 |
| A4 | 分類器が各分岐の実物の文言（apk の 2 形・wget の 404・資格情報・到達）を正しく分け、sha256 の不一致と空・不在のログを unknown にする | lib を bash から直接呼ぶ表の試験 |
| A5 | 起動を止めない（既存の #1564 の試験）ことは 3 分岐とも変わらない | 3 分岐の実行の終了コードと helm upgrade の有無 |

## 検証

- `node scripts/k8s-local-up.test.js`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `node scripts/check-trace-blocks.js`
- `node scripts/check-commit-messages.js --range=origin/develop..HEAD`
- 変異（コミット後に 2 件以上）: 分類器の registry 分岐を潰す／WARN の分岐を version 固定に戻す、で赤になること。

## 範囲外

- 実際のイメージのビルド・稼働クラスタでの実行（スタブで固定する）。
- 資格情報ヘルパー自体の修復・Rancher Desktop の設定変更。
