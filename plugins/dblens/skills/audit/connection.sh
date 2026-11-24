#!/usr/bin/env bash
# Saves a dblens named connection (<folder>/<name>.conn: the dialect, then the connection string),
# so the audit passes only the name. It never prints the password.
#
#   connection.sh container <container> <name> [database]   a local PostgreSQL or SQL Server container
#   connection.sh demo <PostgreSql|SqlServer> <name>         the dblens demo, as its read-only login
#
# The folder is DbLens__ConnectionsPath, else the server's default: dblens/connections under the
# local application-data folder.
set -euo pipefail

folder="${DbLens__ConnectionsPath:-}"
if [[ -z $folder ]]; then
  case "$(uname -s)" in
    Darwin) base="$HOME/Library/Application Support" ;;
    MINGW*|MSYS*|CYGWIN*) base="${LOCALAPPDATA:?LOCALAPPDATA is not set}" ;;
    *) base="${XDG_DATA_HOME:-$HOME/.local/share}" ;;
  esac
  folder="$base/dblens/connections"
fi

quote() { local v=${1//\'/\'\'}; printf "'%s'" "$v"; }

save() { # name dialect connection-string summary
  [[ $1 =~ ^[A-Za-z0-9_.-]+$ ]] || { echo "Not a connection name: $1" >&2; exit 2; }
  mkdir -p "$folder"
  (umask 077; printf '%s\n%s\n' "$2" "$3" > "$folder/$1.conn")
  echo "saved $1: $4 (in $folder)"
}

case "${1:-}" in
  container)
    container="${2:?container name}"; name="${3:?connection name}"; database="${4:-}"
    image="$(docker inspect -f '{{.Config.Image}}' "$container")"
    env_of() { docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$container" | sed -n "s/^$1=//p" | head -1; }
    port_of() { docker port "$container" "$1/tcp" | head -1 | sed 's/.*://'; }
    case "$image" in
      *postgres*|*postgis*|*timescale*)
        user="$(env_of POSTGRES_USER)"; user="${user:-postgres}"
        database="${database:-$(env_of POSTGRES_DB)}"; database="${database:-$user}"
        port="$(port_of 5432)"; [[ -n $port ]] || { echo "$container publishes no port for 5432" >&2; exit 3; }
        save "$name" PostgreSql \
          "Host=localhost;Port=$port;Database=$(quote "$database");Username=$(quote "$user");Password=$(quote "$(env_of POSTGRES_PASSWORD)")" \
          "PostgreSql localhost:$port/$database as $user" ;;
      *mssql*|*sqlserver*|*azure-sql-edge*)
        [[ -n $database ]] || { echo "Name the SQL Server database: connection.sh container $container $name <database>" >&2; exit 2; }
        password="$(env_of MSSQL_SA_PASSWORD)"; password="${password:-$(env_of SA_PASSWORD)}"
        port="$(port_of 1433)"; [[ -n $port ]] || { echo "$container publishes no port for 1433" >&2; exit 3; }
        save "$name" SqlServer \
          "Server=localhost,$port;Database=$(quote "$database");User Id=sa;Password=$(quote "$password");TrustServerCertificate=true" \
          "SqlServer localhost,$port/$database as sa" ;;
      *) echo "$container runs $image, neither PostgreSQL nor SQL Server" >&2; exit 3 ;;
    esac ;;
  demo)
    case "${2:-}" in
      PostgreSql) save "${3:?connection name}" PostgreSql \
        "Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword" \
        "PostgreSql localhost:5433/dblens_demo as dblens_reader (the dblens demo)" ;;
      SqlServer) save "${3:?connection name}" SqlServer \
        "Server=localhost,1434;Database=dblens_demo;User Id=dblens_reader;Password=ReaderPassword123!;TrustServerCertificate=true" \
        "SqlServer localhost,1434/dblens_demo as dblens_reader (the dblens demo)" ;;
      *) echo "demo needs PostgreSql or SqlServer" >&2; exit 2 ;;
    esac ;;
  *) sed -n '2,10p' "$0" >&2; exit 2 ;;
esac
