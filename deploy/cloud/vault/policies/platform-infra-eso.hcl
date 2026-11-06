# NFR-18, IADR-0689 決定 6: 基盤の側（platform-infra の SecretStore openbao）が読めるもの。読むだけ・名指しだけ。
#   取引ユニットだけの値（cloud/ast/*）は読めない。
path "cloud/data/platform/postgres"       { capabilities = ["read"] }
path "cloud/data/platform/keycloak-db"    { capabilities = ["read"] }
path "cloud/data/platform/keycloak-admin" { capabilities = ["read"] }
path "cloud/data/platform/rabbitmq"       { capabilities = ["read"] }
path "cloud/data/shared/ast-db"             { capabilities = ["read"] }
path "cloud/data/shared/ast-rabbitmq-users" { capabilities = ["read"] }
path "cloud/data/shared/ast-client-secrets" { capabilities = ["read"] }
# TLS の鍵（IADR-0689 決定 4）。発行だけ（role の外の名前は PKI の role が拒む）。
path "pki_cloud/issue/postgres" { capabilities = ["update"] }
path "pki_cloud/issue/rabbitmq" { capabilities = ["update"] }
