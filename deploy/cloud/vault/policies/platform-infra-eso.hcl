# NFR-18, IADR-0689 決定 6: 基盤の側（platform-infra の SecretStore openbao）が読めるもの。読むだけ・名指しだけ。
#   取引ユニットだけの値（cloud/ast/*）は読めない。
path "cloud/data/platform/postgres"       { capabilities = ["read"] }
# ［2026-11-07 / IADR-0702］バックアップの利用者（超利用者でない）。
path "cloud/data/platform/postgres-backup" { capabilities = ["read"] }
path "cloud/data/platform/keycloak-db"    { capabilities = ["read"] }
path "cloud/data/platform/keycloak-admin" { capabilities = ["read"] }
path "cloud/data/platform/rabbitmq"       { capabilities = ["read"] }
path "cloud/data/shared/ast-db"             { capabilities = ["read"] }
# ［2026-11-07 / IADR-0695］サービスごとの DB の利用者（名指しの 7 つ。共有の ast-db は移行の後に外す）。
path "cloud/data/shared/ast-db-audit"       { capabilities = ["read"] }
path "cloud/data/shared/ast-db-configuration" { capabilities = ["read"] }
path "cloud/data/shared/ast-db-cost-control" { capabilities = ["read"] }
path "cloud/data/shared/ast-db-market-monitor" { capabilities = ["read"] }
path "cloud/data/shared/ast-db-order-execution" { capabilities = ["read"] }
path "cloud/data/shared/ast-db-report"      { capabilities = ["read"] }
path "cloud/data/shared/ast-db-risk-management" { capabilities = ["read"] }
path "cloud/data/shared/ast-rabbitmq-users" { capabilities = ["read"] }
path "cloud/data/shared/ast-client-secrets" { capabilities = ["read"] }
# ［2026-11-07 / IADR-0689 決定 11］外から持ち込む値（オーナーが put-external-secret.sh で書く）。
path "cloud/data/platform/platform-backup-s3"     { capabilities = ["read"] }
path "cloud/data/platform/alertmanager-discord"   { capabilities = ["read"] }
path "cloud/data/platform/alertmanager-heartbeat" { capabilities = ["read"] }
# TLS の鍵（IADR-0689 決定 4）。発行だけ（role の外の名前は PKI の role が拒む）。
path "pki_cloud/issue/postgres" { capabilities = ["update"] }
path "pki_cloud/issue/rabbitmq" { capabilities = ["update"] }
# ［2026-11-07 / IADR-0693］認可サーバー（Keycloak）のサーバ証明書。稼働中の構成へは root-token.sh apply-pki-roles と apply-policies で足す。
path "pki_cloud/issue/keycloak" { capabilities = ["update"] }
