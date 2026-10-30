"""Clean selected Tags into derived "...Clean" Tags.

Per configured Tag, in Batch order:
  - a missing value or Bad Quality is rejected (no cleaned value for that time);
  - a jump larger than maxStep from the previous accepted value is flagged and the
    previous value is held, so the cleaned series has no spike (its Quality becomes Uncertain);
  - otherwise the value is copied to the output Tag.

Stateless: the first accepted value of each Batch starts a fresh comparison.

parameters:
  tags   { "<Tag>": { "output": "<declared output Tag>", "maxStep": <number> } }
"""


def transform(batch, context):
    settings = context["parameters"]["tags"]
    previous = {}
    samples, dispositions = [], []
    for sample in batch["samples"]:
        tag = sample["tag"]
        if tag not in settings:
            continue
        setting = settings[tag]
        if sample["value"] is None or sample["quality"] == "bad":
            reason = "missing value" if sample["value"] is None else "bad quality (status or range)"
            dispositions.append({"index": sample["index"], "disposition": "rejected", "reason": reason})
            continue
        value = sample["value"]
        if tag in previous and abs(value - previous[tag]) > setting["maxStep"]:
            dispositions.append({
                "index": sample["index"],
                "disposition": "flagged",
                "reason": f"step {value - previous[tag]:+g} exceeds {setting['maxStep']:g}; held {previous[tag]:g}",
            })
            value = previous[tag]
        else:
            previous[tag] = value
        samples.append({
            "tag": setting["output"],
            "value": value,
            "sourceTimestamp": sample["sourceTimestamp"],
            "inputs": [sample["index"]],
        })
    return {"samples": samples, "dispositions": dispositions}
