#!/usr/bin/env bash
# A second PostgreSQL server that is not the demo: its own container (dblens-eval-target, port 5435),
# database target_db, holding a copy of the demo's clean demo_good schema. Idempotent; the container is
# left running (docker rm -f dblens-eval-target removes it). Needs the demo container, as the copy's source.
set -euo pipefail
name=dblens-eval-target
docker rm -f "$name" > /dev/null 2>&1 || true
docker run -d --name "$name" -e POSTGRES_PASSWORD=evaltarget -e POSTGRES_DB=target_db -p 5435:5432 postgres:17-alpine > /dev/null
for _ in $(seq 1 30); do
  docker exec "$name" pg_isready -U postgres -d target_db > /dev/null 2>&1 && break
  sleep 1
done
# The first start restarts once, after initdb: wait for it to settle.
sleep 2
docker exec dblens-postgres pg_dump -U postgres -d dblens_demo -n demo_good --no-owner --no-privileges \
  | docker exec -i "$name" psql -q -v ON_ERROR_STOP=1 -U postgres -d target_db > /dev/null
