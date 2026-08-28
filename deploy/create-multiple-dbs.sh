#!/bin/bash
# PostgreSQL 16 init script — creates per-service schemas
# ADR-0002: DB per service (shared PostgreSQL instance for dev/staging)
set -e

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    CREATE DATABASE document_svc;
    CREATE DATABASE datasource_svc;
    CREATE DATABASE conversion_svc;
    CREATE DATABASE retrieval_svc;
    CREATE DATABASE aianalysis_svc;
    CREATE DATABASE authz_svc;
    CREATE DATABASE wiki_svc;
    CREATE DATABASE feedback_svc;
    CREATE DATABASE dashboard_svc;
    -- FR-17, UC-10, ADR-0033/ADR-0034 (#908/#957): 知識グラフの GraphService 専用 DB。compose の
    -- graph-service が Host=postgres;Database=graph_svc へ接続するため作成する（未作成だとクラッシュループ）。
    CREATE DATABASE graph_svc;
    -- FR-22, ADR-0045, IADR-0288 (#1025): 利用者通知の NotificationService 専用 DB。compose の
    -- notification-service が Host=postgres;Database=notification_svc へ接続し、起動時に
    -- MigrateAsync を走らせるため作成する（未作成だとクラッシュループ）。
    CREATE DATABASE notification_svc;
    -- Issue #283, IADR-0070: AST 設定画面の ConfigurationService 専用 DB。compose の configuration-service が
    -- Host=postgres;Database=configuration_svc へ接続するため作成する（未作成だと DB 不在でクラッシュループ）。
    CREATE DATABASE configuration_svc;
    -- Issue #287, IADR-0071: AST リスク設定/統制状態の RiskManagementService 専用 DB。compose の
    -- risk-management-service が Host=postgres;Database=risk_management_svc へ接続するため作成する。
    CREATE DATABASE risk_management_svc;
    -- Issue #288, IADR-0072: AST 監視銘柄（watchlist）の MarketMonitorService 専用 DB。compose の
    -- market-monitor-service が Host=postgres;Database=market_monitor_svc へ接続するため作成する。
    CREATE DATABASE market_monitor_svc;
    -- FR-13, UC-07, IADR-0020: Wiki.js 専用 DB。Wiki.js が自スキーマを作成するため所有権を kp に付与する。
    CREATE DATABASE wikijs;
    -- Issue #282, IADR-0079: Keycloak を共有 Postgres 外部 DB 化して runtime state を永続化する。compose の
    -- keycloak が KC_DB_URL_DATABASE=keycloak / kp で接続し、自スキーマ（約90テーブル）を作成する。
    CREATE DATABASE keycloak;

    CREATE USER kp WITH PASSWORD 'kp';
    -- PostgreSQL 15+ では GRANT ALL ON DATABASE だけではスキーマ public への CREATE 権限が
    -- 付与されず、各サービスの EF Core Migration が 42501（permission denied for schema public）で
    -- 失敗する（Issue #88 の稼働 PoC で実測）。所有権を kp へ移し（public スキーマは
    -- pg_database_owner 所有のため DB 所有者に追随する）、自スキーマ管理を可能にする。
    ALTER DATABASE document_svc OWNER TO kp;
    ALTER DATABASE datasource_svc OWNER TO kp;
    ALTER DATABASE conversion_svc OWNER TO kp;
    ALTER DATABASE retrieval_svc OWNER TO kp;
    ALTER DATABASE aianalysis_svc OWNER TO kp;
    ALTER DATABASE authz_svc OWNER TO kp;
    ALTER DATABASE wiki_svc OWNER TO kp;
    ALTER DATABASE feedback_svc OWNER TO kp;
    ALTER DATABASE dashboard_svc OWNER TO kp;
    ALTER DATABASE graph_svc OWNER TO kp;
    ALTER DATABASE notification_svc OWNER TO kp;
    ALTER DATABASE configuration_svc OWNER TO kp;
    ALTER DATABASE risk_management_svc OWNER TO kp;
    ALTER DATABASE market_monitor_svc OWNER TO kp;
    ALTER DATABASE wikijs OWNER TO kp;
    -- Issue #282, IADR-0079: keycloak DB 所有権を kp へ（Keycloak が public スキーマにテーブルを作成できるように）。
    ALTER DATABASE keycloak OWNER TO kp;
EOSQL
