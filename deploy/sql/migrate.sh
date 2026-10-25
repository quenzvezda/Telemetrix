#!/bin/sh
set -eu

set -- /migrations/*.sql
if [ ! -f "$1" ]; then
    echo "No SQL migrations found in /migrations" >&2
    exit 1
fi

for migration in "$@"; do
    echo "Applying $(basename "$migration")"
    psql -v ON_ERROR_STOP=1 --file "$migration"
done
