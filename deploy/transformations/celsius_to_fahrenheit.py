"""Convert Celsius Tags to derived Fahrenheit Tags (<Tag><suffix>, e.g. TemperatureF).

Only Samples whose Tag unit is sourceUnit and whose output Tag is declared are converted.
The derived Tag has its own unit (degF) and range, so a converted value is never
range-checked against, or stored under, the Celsius Tag.

parameters:
  sourceUnit  unit to convert from (degC)
  suffix      appended to the Tag name to form the output Tag (F)
"""


def transform(batch, context):
    source_unit = context["parameters"].get("sourceUnit", "degC")
    suffix = context["parameters"].get("suffix", "F")
    outputs = context["outputs"]
    samples = []
    for sample in batch["samples"]:
        output = sample["tag"] + suffix
        if sample["unit"] != source_unit or output not in outputs or sample["value"] is None:
            continue
        samples.append({
            "tag": output,
            "value": sample["value"] * 9 / 5 + 32,
            "sourceTimestamp": sample["sourceTimestamp"],
            "inputs": [sample["index"]],
        })
    return {"samples": samples}
