"""Flag outliers per Tag with a robust (median / MAD) z-score inside one Batch.

Stateless: only the Samples of the current Batch are compared. Raw Measurements are kept;
flagged Samples are recorded in sample_disposition. No output Tags.

parameters:
  tags        Tag names to check
  threshold   modified z-score above which a Sample is flagged (3.5 is the usual choice)
  minSamples  fewer usable Samples of a Tag than this are not scored
"""

from statistics import median


def transform(batch, context):
    parameters = context["parameters"]
    tags = set(parameters["tags"])
    threshold = float(parameters.get("threshold", 3.5))
    min_samples = int(parameters.get("minSamples", 5))

    by_tag = {}
    for sample in batch["samples"]:
        if sample["tag"] in tags and sample["value"] is not None and sample["quality"] != "bad":
            by_tag.setdefault(sample["tag"], []).append(sample)

    dispositions = []
    for tag, samples in by_tag.items():
        if len(samples) < min_samples:
            continue
        values = [sample["value"] for sample in samples]
        center = median(values)
        mad = median(abs(value - center) for value in values)
        for sample in samples:
            deviation = sample["value"] - center
            if deviation == 0:
                continue
            score = float("inf") if mad == 0 else 0.6745 * deviation / mad
            if abs(score) > threshold:
                dispositions.append({
                    "index": sample["index"],
                    "disposition": "flagged",
                    "reason": f"{tag} modified z-score {score:.2f} exceeds {threshold} (batch median {center:g})",
                })
    return {"dispositions": dispositions}
