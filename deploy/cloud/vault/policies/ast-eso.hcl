# NFR-18, IADR-0689 決定 6: 取引ユニットの名前空間（ai-stock-trading の SecretStore openbao。取引ユニットの側が置く）が読めるもの。
#   取引ユニットだけの値（cloud/ast/*）と、基盤と共有する名指しの値（cloud/shared の下。ワイルドカードにしない）。基盤だけの値（cloud/platform/*）は読めない。
path "cloud/data/ast/*"                     { capabilities = ["read"] }
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
path "cloud/data/shared/platform-infra-ca"  { capabilities = ["read"] }
# ［2026-11-08 / AST の IADR-0646］取引ユニットのサービス間の TLS の受け側の証明書の発行（名指しの 11 個。ワイルドカードにしない）。
#   ESO の VaultDynamicSecret（取引ユニットの chart）が POST する。role は vault/lib.sh の OB_PKI_ROLES の名前空間 ai-stock-trading の 11 個と同じ。
path "pki_cloud/issue/audit-service"                  { capabilities = ["create", "update"] }
path "pki_cloud/issue/backtest-service"               { capabilities = ["create", "update"] }
path "pki_cloud/issue/configuration-service"          { capabilities = ["create", "update"] }
path "pki_cloud/issue/cost-control-service"           { capabilities = ["create", "update"] }
path "pki_cloud/issue/information-collection-service" { capabilities = ["create", "update"] }
path "pki_cloud/issue/market-monitor-service"         { capabilities = ["create", "update"] }
path "pki_cloud/issue/notification-service"           { capabilities = ["create", "update"] }
path "pki_cloud/issue/order-execution-service"        { capabilities = ["create", "update"] }
path "pki_cloud/issue/report-service"                 { capabilities = ["create", "update"] }
path "pki_cloud/issue/risk-management-service"        { capabilities = ["create", "update"] }
path "pki_cloud/issue/trade-decision-service"         { capabilities = ["create", "update"] }
