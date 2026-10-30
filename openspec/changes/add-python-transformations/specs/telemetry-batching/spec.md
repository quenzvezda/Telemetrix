## ADDED Requirements

### Requirement: Transformation label per Asset
The bridge SHALL assign the transformation label configured for an Asset (`Transformations:Assets:<AssetId>`) to every Batch of that Asset, emitting `schemaVersion` 2 with a `transformation` field. Batches of Assets without a label SHALL be emitted as `schemaVersion` 1 without the field. An invalid label SHALL fail bridge startup (FR-26, ADR-0017).

#### Scenario: Two Assets, two labels
- **WHEN** Pump01 is assigned `pump-cleaning` and Tank01 `celsius-to-fahrenheit`
- **THEN** Pump01 Batches carry `"schemaVersion": 2, "transformation": "pump-cleaning"` and Tank01 Batches carry `celsius-to-fahrenheit`

#### Scenario: Unlabeled Asset
- **WHEN** an Asset has no label or an empty label
- **THEN** its Batches are `schemaVersion` 1 and contain no `transformation` field

#### Scenario: Invalid label
- **WHEN** an Asset is assigned `../evil.py`
- **THEN** the bridge fails to start with an error naming `Transformations:Assets`
