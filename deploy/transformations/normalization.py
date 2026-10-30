"""Min-max normalise selected Tags to derived 0..1 Tags.

The scale comes from static parameters, not from the Batch, so the same raw value always
normalises to the same result. Values outside min..max fall outside 0..1 and are stored
with Quality Bad by the derived Tag's range check.

parameters:
  tags   { "<Tag>": { "output": "<declared output Tag>", "min": <number>, "max": <number> } }
"""


def transform(batch, context):
    settings = context["parameters"]["tags"]
    samples = []
    for sample in batch["samples"]:
        setting = settings.get(sample["tag"])
        if setting is None or sample["value"] is None:
            continue
        low, high = float(setting["min"]), float(setting["max"])
        samples.append({
            "tag": setting["output"],
            "value": (sample["value"] - low) / (high - low),
            "sourceTimestamp": sample["sourceTimestamp"],
            "inputs": [sample["index"]],
        })
    return {"samples": samples}
