# NFR-18, IADR-0689 決定 6: MSP の名前空間（microservices-platform の SecretStore openbao）が読めるもの。
path "pki_cloud/issue/llmgateway-service" { capabilities = ["update"] }
# ［2026-11-07 / IADR-0689 決定 11］LLM の提供元の鍵（外から持ち込む値。オーナーが put-external-secret.sh で書く）。
path "cloud/data/msp/llm-provider-credentials" { capabilities = ["read"] }
