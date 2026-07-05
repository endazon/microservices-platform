#!/bin/bash
# PostgreSQL 16 init script — creates per-service schemas
# ADR-0002: DB per service (shared PostgreSQL instance for dev/staging)
set -e

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    CREATE DATABASE document_svc;
    CREATE DATABASE datasource_svc;
    CREATE DATABASE retrieval_svc;
    CREATE DATABASE aianalysis_svc;
    CREATE DATABASE authz_svc;
    CREATE DATABASE wiki_svc;
    CREATE DATABASE feedback_svc;
    CREATE DATABASE dashboard_svc;
    -- FR-13, UC-07, IADR-0020: Wiki.js 専用 DB。Wiki.js が自スキーマを作成するため所有権を kp に付与する。
    CREATE DATABASE wikijs;

    CREATE USER kp WITH PASSWORD 'kp';
    ALTER DATABASE wikijs OWNER TO kp;
    GRANT ALL PRIVILEGES ON DATABASE document_svc TO kp;
    GRANT ALL PRIVILEGES ON DATABASE datasource_svc TO kp;
    GRANT ALL PRIVILEGES ON DATABASE retrieval_svc TO kp;
    GRANT ALL PRIVILEGES ON DATABASE aianalysis_svc TO kp;
    GRANT ALL PRIVILEGES ON DATABASE authz_svc TO kp;
    GRANT ALL PRIVILEGES ON DATABASE wiki_svc TO kp;
    GRANT ALL PRIVILEGES ON DATABASE feedback_svc TO kp;
    GRANT ALL PRIVILEGES ON DATABASE dashboard_svc TO kp;
    GRANT ALL PRIVILEGES ON DATABASE wikijs TO kp;
EOSQL
