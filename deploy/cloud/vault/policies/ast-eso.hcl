# NFR-18, IADR-0689 決定 6: 取引ユニットの名前空間（ai-stock-trading の SecretStore openbao。取引ユニットの側が置く）が読めるもの。
#   取引ユニットだけの値（cloud/ast/*）と、基盤と共有する名指しの 4 つ。基盤だけの値（cloud/platform/*）は読めない。
path "cloud/data/ast/*"                     { capabilities = ["read"] }
path "cloud/data/shared/ast-db"             { capabilities = ["read"] }
path "cloud/data/shared/ast-rabbitmq-users" { capabilities = ["read"] }
path "cloud/data/shared/ast-client-secrets" { capabilities = ["read"] }
path "cloud/data/shared/platform-infra-ca"  { capabilities = ["read"] }
