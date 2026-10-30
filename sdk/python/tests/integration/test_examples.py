import contextlib
import io
import json
import os
import subprocess
import sys
from datetime import timedelta
from pathlib import Path

import pytest

from .conftest import BOUNDARY

pytestmark = pytest.mark.integration
EXAMPLES = Path(__file__).resolve().parents[2] / "examples"


@pytest.fixture
def reader_environment(database, monkeypatch):
    monkeypatch.setenv("TELEMETRIX_DATABASE_URL", f"postgresql://sdk_reader@{database.host}:{database.port}/telemetry")
    monkeypatch.setenv("PGPASSWORD", database.reader_password)
    return dict(os.environ)


def test_examples_asset_health_report(reader_environment):
    result = subprocess.run(
        [sys.executable, str(EXAMPLES / "asset_health_report.py"), "--asset", "Pump01",
         "--start", BOUNDARY.isoformat(), "--end", (BOUNDARY + timedelta(minutes=1)).isoformat()],
        env=reader_environment, capture_output=True, text=True, timeout=60)

    assert result.returncode == 0, result.stderr
    assert "MotorTemperature     measurements=4 good=1 uncertain=1 bad=2 null=1 good_share=25.0%" in result.stdout
    assert "FlowRate             no measurements" in result.stdout
    assert "rollups MotorTemperature minutes=1 min=50 max=50 avg=50 degC" in result.stdout
    assert "gaps=0 without_samples=0s" in result.stdout


def test_examples_query_tour_notebook(reader_environment):
    notebook = json.loads((EXAMPLES / "query_tour.ipynb").read_text(encoding="utf-8"))
    namespace = {"ASSET": "Pump01", "START": BOUNDARY, "END": BOUNDARY + timedelta(minutes=1)}
    output = io.StringIO()

    with contextlib.redirect_stdout(output):
        for cell in notebook["cells"]:
            # The parameters cell is replaced by the seeded interval, as papermill would.
            if cell["cell_type"] == "code" and "parameters" not in cell["metadata"].get("tags", []):
                exec(compile("".join(cell["source"]), "query_tour.ipynb", "exec"), namespace)

    assert len(namespace["tags"]) == 5
    assert [(m.tag, m.quality.name) for m in namespace["page"]][:2] == [("MotorTemperature", "GOOD"),
                                                                        ("MotorTemperature", "BAD")]
    assert "2 Bad Measurements, 1 of them without a value" in output.getvalue()
    assert len(namespace["rollups"]) == 1
    assert namespace["client"].closed
