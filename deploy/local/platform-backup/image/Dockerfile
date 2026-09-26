# NFR-21, NFR-05, ADR-0002, ADR-0008, IADR-0471 決定 3 (#1564): platform-infra の日次バックアップ（CronJob 2 本）のイメージ。
# pg_dump（Postgres 16）と age を同梱し、**実行時にネットワークから何も入れない**。
#
# - ベースは image index の digest で固定する（amd64・arm64 の両方で同じ参照が効く）。タグは読み手のための注記で、
#   効くのは digest である。PG のメジャー版は本体（deploy/local/infra/postgres.yaml）と揃える（試験が固定する）。
# - age は Alpine の community パッケージを**版・チェックサム・署名**の 3 つで固定する:
#   版は `age=<AGE_VERSION>` で指定し、取ってきたファイルの sha256 をアーキテクチャごとに照合し、`apk add <ファイル>` が
#   パッケージの署名を /etc/apk/keys で検証する（--allow-untrusted は使わない）。
#   🔴 Alpine の安定版ブランチは最新のリリースしか置かないため、上流が -rN を上げると取得が失敗してビルドが止まる。
#   **止まるのが正しい**（黙って別の版を入れない）。上げ方は docs/operations/platform-infra-backup-runbook.md。
# - 版を上げたら scripts/k8s-local-images.sh の LOCAL_ONLY_IMAGES のタグと 2 つの CronJob のイメージを同じ値へ上げる
#   （scripts/platform-backup.test.js が 3 か所の一致を見る）。
#
# 🔴 deploy/local 専用（ローカル開発・PoC 環境）。本番のバックアップ設計ではない。
FROM docker.io/library/postgres:16.15-alpine3.24@sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea

ARG AGE_VERSION=1.3.1-r6
ARG AGE_APK_SHA256_X86_64=9e360d891d504924cc0996aa3fa7eabb7d93fc5d80c813139a4d2b3f7ad88b5b
ARG AGE_APK_SHA256_AARCH64=38e2cd2bc60d5fbfcceab415a438b4ef3a24d083a029b7e469af04edb6e7e5a6

RUN set -eu; \
    arch="$(apk --print-arch)"; \
    case "$arch" in \
      x86_64) want="$AGE_APK_SHA256_X86_64" ;; \
      aarch64) want="$AGE_APK_SHA256_AARCH64" ;; \
      *) echo "platform-backup: age のチェックサムを持たないアーキテクチャです: $arch" >&2; exit 1 ;; \
    esac; \
    tmp="$(mktemp -d)"; \
    apk fetch --no-cache --output "$tmp" "age=$AGE_VERSION"; \
    echo "$want  $tmp/age-$AGE_VERSION.apk" | sha256sum -c -; \
    apk add --no-cache "$tmp/age-$AGE_VERSION.apk"; \
    rm -f "$tmp/age-$AGE_VERSION.apk"; \
    rmdir "$tmp"; \
    age --version | grep -Fq "${AGE_VERSION%-r*}"; \
    command -v age-keygen >/dev/null; \
    pg_dump --version | grep -Eq '^pg_dump \(PostgreSQL\) 16\.'
