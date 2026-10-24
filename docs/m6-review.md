# M6 PlantSim review

Change: `add-plant-simulator`.
Branch: `m6-add-plant-simulator`.
The reviewer authorized autonomous implementation of all groups on 2026-10-24.

## Groups 1–2: value model and OPC UA server

Seven unit cases passed: seeded 600-step normal ranges, seed repeatability,
correlated pump stop/start, and timed Bad quality/single-update spikes.
Three integration cases passed: anonymous SecurityMode None connection,
namespace 2 with seven typed tags and engineering units, and increasing UTC
source timestamps for all tags over four observations.

The reviewer connected UaExpert to `opc.tcp://localhost:4840` and supplied a
screenshot showing five Pump01 tags and two Tank01 tags, all Good, with Boolean
Running and Double numeric values. Numeric values changed as expected.
UaExpert's default StatusValue data-change filter does not notify a timestamp-only
change while Running stays true. Read the attribute directly or use a
StatusValueTimestamp filter to inspect each source timestamp.

Further group verification and repeatable manual checks are recorded below as
implementation proceeds.
