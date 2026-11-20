#!/bin/bash
# Waits for SQL Server to be healthy (handled by depends_on condition: service_healthy),
# then applies schema and seed scripts in order.

set -e

SCRIPTS_DIR="/usr/src/scripts"
SA_PASSWORD="${MSSQL_SA_PASSWORD:-DevPassword123!}"
SERVER="sqlserver"

echo "[init] Applying SQL Server scripts..."

for script in "$SCRIPTS_DIR"/0*.sql; do
    # 01 creates the database; the others run in it.
    database=(); [[ $(basename "$script") == 01-* ]] || database=(-d dblens_demo)
    echo "[init] Running $script ..."
    /opt/mssql-tools18/bin/sqlcmd \
        -S "$SERVER" \
        -U sa \
        -P "$SA_PASSWORD" \
        "${database[@]}" \
        -i "$script" \
        -No \
        -C \
        -b
    echo "[init] $script completed."
done

echo "[init] SQL Server initialization complete."
