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
