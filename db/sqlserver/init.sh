#!/bin/bash
# Waits for SQL Server to be healthy (handled by depends_on condition: service_healthy),
# then applies schema and seed scripts in order.

set -e

SCRIPTS_DIR="/usr/src/scripts"
SA_PASSWORD="${MSSQL_SA_PASSWORD:-DevPassword123!}"
SERVER="sqlserver"

echo "[init] Applying SQL Server scripts..."

for script in "$SCRIPTS_DIR"/0*.sql; do
    echo "[init] Running $script ..."
    /opt/mssql-tools18/bin/sqlcmd \
        -S "$SERVER" \
        -U sa \
        -P "$SA_PASSWORD" \
        -i "$script" \
        -No \
        -C \
        -b
    echo "[init] $script completed."
done

echo "[init] SQL Server initialization complete."
