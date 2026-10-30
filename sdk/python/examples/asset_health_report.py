"""Post-storage analysis of one Asset: Quality and null values per Tag, Rollups and Gaps.

    python asset_health_report.py --asset Pump01 --hours 24
    python asset_health_report.py --asset Tank01 --start 2026-10-01T00:00:00Z --end 2026-10-02T00:00:00Z

Connection settings come from TELEMETRIX_DATABASE_URL and the PG* variables (see README).
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone

from telemetrix_query import FeatureUnavailableError, Quality, connect


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--asset", default="Pump01")
    parser.add_argument("--start", type=datetime.fromisoformat, help="ISO-8601 with offset; default END - HOURS")
    parser.add_argument("--end", type=datetime.fromisoformat, help="ISO-8601 with offset; default now")
    parser.add_argument("--hours", type=float, default=24.0)
    args = parser.parse_args(argv)
    end = args.end or datetime.now(timezone.utc)
    start = args.start or end - timedelta(hours=args.hours)

    with connect() as client:
        tags = client.tags(asset_id=args.asset)
        if not tags:
            print(f"Asset {args.asset} has no Tags")
            return 1
        print(f"Asset {args.asset}, {start.isoformat()} <= time < {end.isoformat()}")

        qualities: defaultdict[str, Counter[Quality]] = defaultdict(Counter)
        nulls: Counter[str] = Counter()
        for measurement in client.iter_measurements(start, end, asset_id=args.asset):
            qualities[measurement.tag][measurement.quality] += 1
            if measurement.value is None:
                nulls[measurement.tag] += 1
        for tag in tags:
            counts = qualities[tag.name]
            total = sum(counts.values())
            if total == 0:
                print(f"  {tag.name:<20} no measurements")
                continue
            print(f"  {tag.name:<20} measurements={total} good={counts[Quality.GOOD]} "
                  f"uncertain={counts[Quality.UNCERTAIN]} bad={counts[Quality.BAD]} null={nulls[tag.name]} "
                  f"good_share={100 * counts[Quality.GOOD] / total:.1f}%")

        try:
            minutes: defaultdict[str, list] = defaultdict(list)
            for rollup in client.iter_rollups(start, end, asset_id=args.asset):
                minutes[rollup.tag].append(rollup)
            for tag in tags:
                rollups = minutes[tag.name]
                if rollups:
                    count = sum(r.sample_count for r in rollups)
                    average = sum(r.avg_value * r.sample_count for r in rollups) / count
                    print(f"  rollups {tag.name:<12} minutes={len(rollups)} min={min(r.min_value for r in rollups):g} "
                          f"max={max(r.max_value for r in rollups):g} avg={average:g} {tag.unit or ''}".rstrip())
        except FeatureUnavailableError as error:
            print(f"  rollups unavailable: {error}")

        try:
            gaps = list(client.iter_gaps(start, end, asset_id=args.asset))
            missing = sum(((min(g.end, end) - max(g.start, start)) for g in gaps), timedelta())
            print(f"  gaps={len(gaps)} without_samples={missing.total_seconds():g}s")
        except FeatureUnavailableError as error:
            print(f"  gaps unavailable: {error}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
