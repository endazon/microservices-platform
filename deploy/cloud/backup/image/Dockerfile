# NFR-21, NFR-05, ADR-0002, ADR-0008, IADR-0674 (#2402): クラウドの最小構成の日次バックアップ（CronJob）のイメージ。
# ローカルのイメージ（deploy/local/platform-backup/image/Dockerfile）と同じベース・同じ age に、rclone（S3 互換の
# オブジェクトストレージへ送る）を足したもの。**実行時にネットワークから何も入れない。**
#
# - ベース・age の固定（digest・版・sha256・署名）はローカルと**同じ値**にする（scripts/cloud-ops.test.js T-2402-33 が突き合わせる）。
#   ローカルを上げたら、ここも同じ値へ上げる。
# - rclone も Alpine の community パッケージを**版・チェックサム・署名**の 3 つで固定する（age と同じ取り方）。
#   🔴 Alpine の安定版ブランチは最新のリリースしか置かないため、上流が -rN を上げると取得が失敗してビルドが止まる。
#   **止まるのが正しい**（黙って別の版を入れない）。
# - タグは中身の版から作る（pg<PG>-age<AGE_VERSION>-rclone<RCLONE_VERSION>。CronJob の image と試験が突き合わせる）。
#   イメージはレジストリへ置かない。手元でビルドしてノードへ取り込む（手順は docs/operations/cloud-backup-alerts-heartbeat-runbook.md）。
ARG BASE_REGISTRY=mirror.gcr.io/library
FROM ${BASE_REGISTRY}/postgres:16.15-alpine3.24@sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea

ARG AGE_VERSION=1.3.2-r1
ARG AGE_APK_SHA256_X86_64=d0ae31a65ecc6d3acb704bf3c7eacce3955b48c806bd15a505163c8c2ec09f8c
ARG AGE_APK_SHA256_AARCH64=fb36e1050864e1936c6c87807d5bcca08f5388921b9c2ce154bbc780feae9936
ARG RCLONE_VERSION=1.75.2-r0
ARG RCLONE_APK_SHA256_X86_64=d2ae4f2055a5281dafc259272600695e2a55bf174c473eb2b6a96fe04fbaee34
ARG RCLONE_APK_SHA256_AARCH64=79ebe2368179e3784d6b469ded2565d1fff185b6d66c22d0da0ac88453515836
ARG ALPINE_BRANCH=v3.24
ARG ALPINE_MIRROR=https://dl-cdn.alpinelinux.org/alpine

RUN set -eu; \
    case "$(cat /etc/alpine-release)" in \
      "${ALPINE_BRANCH#v}".*) ;; \
      *) echo "platform-backup-cloud: ベースの Alpine（$(cat /etc/alpine-release)）が ALPINE_BRANCH=$ALPINE_BRANCH と違います" >&2; exit 1 ;; \
    esac; \
    arch="$(apk --print-arch)"; \
    case "$arch" in \
      x86_64) age_want="$AGE_APK_SHA256_X86_64"; rclone_want="$RCLONE_APK_SHA256_X86_64" ;; \
      aarch64) age_want="$AGE_APK_SHA256_AARCH64"; rclone_want="$RCLONE_APK_SHA256_AARCH64" ;; \
      *) echo "platform-backup-cloud: チェックサムを持たないアーキテクチャです: $arch" >&2; exit 1 ;; \
    esac; \
    tmp="$(mktemp -d)"; \
    wget -q -O "$tmp/age-$AGE_VERSION.apk" "$ALPINE_MIRROR/$ALPINE_BRANCH/community/$arch/age-$AGE_VERSION.apk"; \
    wget -q -O "$tmp/rclone-$RCLONE_VERSION.apk" "$ALPINE_MIRROR/$ALPINE_BRANCH/community/$arch/rclone-$RCLONE_VERSION.apk"; \
    echo "$age_want  $tmp/age-$AGE_VERSION.apk" | sha256sum -c -; \
    echo "$rclone_want  $tmp/rclone-$RCLONE_VERSION.apk" | sha256sum -c -; \
    apk add --no-cache "$tmp/age-$AGE_VERSION.apk" "$tmp/rclone-$RCLONE_VERSION.apk"; \
    rm -f "$tmp/age-$AGE_VERSION.apk" "$tmp/rclone-$RCLONE_VERSION.apk"; \
    rmdir "$tmp"; \
    age --version | grep -Fq "${AGE_VERSION%-r*}"; \
    rclone version | grep -Fq "v${RCLONE_VERSION%-r*}"; \
    pg_dump --version | grep -Eq '^pg_dump \(PostgreSQL\) 16\.'
