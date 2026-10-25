#!/bin/sh
set -eu

for migration in /migrations/*.sql; do
    [ -f "$migration" ] || continue
    echo "Applying $(basename "$migration")"
    psql -v ON_ERROR_STOP=1 --file "$migration"
done
