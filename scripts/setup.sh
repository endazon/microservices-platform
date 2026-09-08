#!/usr/bin/env bash
# 開発環境セットアップ（SessionStart hook / devcontainer postCreate から実行される）。
# 目的: AI / 開発者が「ビルド・テストを実走できる」状態を用意する。
# 技術非依存の安全設計: 該当しないスタックでは何もせず正常終了する（exit 0）。
# スタックに合わせて必要なセットアップを追記すること（既定は C#/.NET 例）。
set -u

log() { printf '[setup] %s\n' "$1"; }

# --- C# / .NET（例・既定） ---
# NFR: 計画 06_technical/08_data-egress-policy.md（fixed）§非LLM外部送信の統制 が「.NET SDK / OSS
# ツール類の既定テレメトリをオプトアウトする」を課す。.devcontainer/devcontainer.json の remoteEnv は
# **devcontainer で起動したときだけ**効くのに対し、下のブロックは素のコンテナを狙って SDK を入れ
# `dotnet --version` と restore を初回実行する（＝テレメトリの対象）。PATH 追加は 2 経路あり、
# 素のコンテナに最初から dotnet が在る第 3 の経路では PATH 追加自体が起きないため、
# **最初の dotnet 実行より前**へ 1 度だけ置いて 3 経路すべてを覆う。
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# 【在れば使う → 無ければ入れる】(issue #824)
# devcontainer の image 宣言が効くのは devcontainer で起動したときだけで、SessionStart hook から
# 走る素のコンテナには dotnet が無いことがある。PR #823 は、それを「着手不可」の根拠に取り違えて
# 大玉 17 件を誤判定した（IADR-0180）。環境を用意するのは devcontainer と本スクリプトの両方である。
#
# 【必ず fail-open にする】ネットワーク不通・ダウンロード失敗でセットアップを止めない。入らなければ
# 従来どおり「dotnet が無いので restore をスキップ」に落ちるだけで、退行はしない。**インストールの
# 成否は終了コードではなく、入った実体（$HOME/.dotnet/dotnet）が在るかで判定する** —— 出力を sed へ
# 継ぐと `||` はパイプ最終段を見るため、この面での終了コード判定は死んだコードになる。
# **同型の罠を書いた「下の pin 検査の注記」は、もう本ファイルに無い** —— planning pin 鮮度検査は
# ADR-0048 決定 2 / IADR-0228 で撤去済みで、**復活させない**（CLAUDE.md 禁止事項）。注記だけが
# 宙に浮いていたので、参照先ではなく罠そのものをこの場で書き切る形へ直した（#826 の残骸）。
#
# 【版を直書きしない】チャネルは src/Directory.Build.props の <TargetFramework> から導出する
# （net10.0 -> 10.0）。ここへ版を直書きすると、それ自体が次の追随漏れ点になる
# （.claude/rules/traceability.repo.md 規則 10）。**これは突合検査器ではない** ——
# 不一致を検出して落とすのではなく、構成上そもそも不一致になりようがなくする導出である。
# 導出できなければインストールしない（勝手な既定版を打たない）。
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export PATH="$HOME/.dotnet:$PATH"
  log "既存の $HOME/.dotnet を PATH へ追加しました"
fi

if ! command -v dotnet >/dev/null 2>&1; then
  dotnet_channel=""
  if [ -f src/Directory.Build.props ]; then
    dotnet_channel=$(sed -n 's|.*<TargetFramework>net\([0-9][0-9.]*\)</TargetFramework>.*|\1|p' \
      src/Directory.Build.props 2>/dev/null | sed -n '1p')
  fi

  if [ -z "$dotnet_channel" ]; then
    log "dotnet が無く、src/Directory.Build.props から版を導出できないため導入をスキップ（継続）"
  elif ! command -v curl >/dev/null 2>&1; then
    log "dotnet も curl も無いため導入をスキップ（継続）"
  else
    log ".NET SDK $dotnet_channel の導入を試みます（失敗しても継続）"
    installer="$(mktemp)"
    if curl -fsSL --max-time 120 https://dot.net/v1/dotnet-install.sh -o "$installer" 2>/dev/null; then
      # 【停止も止める】curl --max-time が守るのは installer 本体（数十 KB）の取得だけで、
      # 実体の SDK（約 240MB）を落とすのは下の bash である。**不通・失敗ではなく「低速で
      # 止まらず流れ続ける」場合**は上の fail-open の対象外で、SessionStart hook を
      # 無期限にハングさせ得る。timeout で上限を切る（GNU coreutils）。
      timeout 600s bash "$installer" --channel "$dotnet_channel" --install-dir "$HOME/.dotnet" --no-path 2>&1 \
        | sed 's/^/[setup] /'
      if [ -x "$HOME/.dotnet/dotnet" ]; then
        export PATH="$HOME/.dotnet:$PATH"
        log "dotnet $("$HOME/.dotnet/dotnet" --version 2>/dev/null || echo '版不明') を導入しました"
      else
        log ".NET SDK の導入に失敗しました（継続。restore はスキップされる）"
      fi
    else
      log "dotnet-install.sh を取得できませんでした（オフライン等。継続）"
    fi
    rm -f "$installer"
  fi
fi

# --- ユニット submodule（#1349） ---
# NFR, IADR-0056 / IADR-0058 / IADR-0117: `src/<unit>` は submodule で入る。
# **これは restore の前提である** —— `Platform.Bff.csproj` が submodule 内の
# `AiStockTrading.Bff.Endpoints` を ProjectReference するため、未取得だと
# **platform の slnx は restore もビルドもできない**（#1349 / planning#574 監査 B-7）。
#
# 🔴 **restore ループより前に置く。** 後ろに置くと、その回の restore は失敗したままである。
#
# 🔴 **パスを直書きしない。** `.gitmodules` から導出する —— 直書きするとそれ自体が次の
# 追随漏れ点になる（.claude/rules/traceability.repo.md 規則 10。上の「版を直書きしない」と同じ理由）。
# 導出は CI の `Fetch unit submodules (src/*, public, non-recursive)` step と**同じ式**である
# （`src/` で始まる path だけ・再帰しない）。
#
# 【fail-open】ネットワーク不通・認証不足でも止めない。取れなければ従来どおり
# 「submodule 参照のプロジェクトが restore で落ちる」に戻るだけで、退行はしない。
if command -v git >/dev/null 2>&1 && [ -f .gitmodules ]; then
  git config --file .gitmodules --get-regexp '^submodule\..*\.path$' 2>/dev/null \
    | awk '$2 ~ /^src\// { print $2 }' \
    | while read -r sub_path; do
        log "git submodule update --init $sub_path を実行します"
        git submodule update --init "$sub_path" 2>&1 | sed 's/^/[setup] /' \
          || log "submodule $sub_path の取得でエラー（継続）"
      done
fi

# ソリューションを自動発見して復元する（ルート単一 .sln/.slnx でも、ユニット第一構成
# `src/<unit>/backend/backend.slnx` でも編集不要で動く）。
#
# 【落とし穴】自動発見は「編集不要」を謳う分、拾ってほしくないものまで拾う。
# ビルド不可の**雛形ソリューション**（スキャフォールド用に置いてあり、共通 props を
# 継承しないため単体では restore できないもの）を同梱するリポジトリでは、それも拾って
# 失敗する（実例: `templates/unit-template/backend/backend.slnx` が
# `error : 無効なフレームワーク識別子` で exit 1）。既定で `./templates/*` を除外してある。
# 雛形を別の場所に置く場合は、その除外を下の find に足すこと。
if command -v dotnet >/dev/null 2>&1; then
  restored=0
  for sln in $(find . -maxdepth 4 \( -name '*.slnx' -o -name '*.sln' \) -not -path '*/node_modules/*' -not -path './templates/*' | sort); do
    log "dotnet restore $sln を実行します"
    dotnet restore "$sln" || log "restore でエラー（継続）"
    restored=1
  done
  [ "$restored" -eq 1 ] || log ".sln/.slnx が無いため dotnet セットアップをスキップ"
fi

# --- Node.js / pnpm workspace（#1349） ---
# IADR-0121 決定 2: パッケージ管理は **pnpm workspace で、ルートは `src/`** である
# （リポジトリ直下に package.json は無い）。導入が無いと `pnpm run lint` / `typecheck` /
# `test:coverage` が一切走らない（planning#574 監査 B-7）。
#
# 🔴 **`--frozen-lockfile` を使う**（CI と同じ）—— ロックを更新すると
# **セットアップが差分を作る**という別の事故になる。
#
# 🔴 **pnpm 本体は入れない。** corepack が動かない環境が実測で在り、
# 「在れば使う → 無ければ入れる」（IADR-0180）は .NET SDK について採った判断である。
# **無ければスキップして継続する** —— Node 自体も、本スクリプトを起動する SessionStart hook が
# Node で書かれている以上、無ければそもそもここへ到達しない。
if command -v pnpm >/dev/null 2>&1 && [ -f src/pnpm-workspace.yaml ]; then
  log "pnpm install --frozen-lockfile（src/）を実行します"
  (cd src && pnpm install --frozen-lockfile 2>&1 | sed 's/^/[setup] /') \
    || log "pnpm セットアップでエラー（継続）"
elif [ -f src/pnpm-workspace.yaml ]; then
  log "pnpm が無いため Node 依存の導入をスキップ（継続。frontend の検査は走らない）"
fi

# --- Python（例。使う場合はコメント解除） ---
# if command -v python3 >/dev/null 2>&1 && { [ -f pyproject.toml ] || [ -f requirements.txt ]; }; then
#   log "Python 依存をインストールします"
#   python3 -m pip install -e '.[test]' 2>/dev/null || python3 -m pip install -r requirements.txt || log "pip セットアップでエラー（継続）"
# fi

log "セットアップ完了"
exit 0
