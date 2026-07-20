#!/usr/bin/env python3

"""Convert retained R0 logs into a small machine-readable evidence index."""

from __future__ import annotations

import csv
import json
import re
import sys
from pathlib import Path


def slug(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")


def number(value: str) -> float:
    return float(value.replace(",", "."))


def add_distribution(
    measurements: list[dict[str, object]],
    gate: str,
    label: str,
    name: str,
    p50: str,
    p95: str,
    maximum: str,
    unit: str,
) -> None:
    measurements.append(
        {
            "gate": gate,
            "metric": slug(f"{label}-{name}"),
            "unit": unit,
            "p50": number(p50),
            "p95": number(p95),
            "max": number(maximum),
        }
    )


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: summarize-r0-artifacts.py <artifact-directory>", file=sys.stderr)
        return 2

    root = Path(sys.argv[1]).resolve()
    metadata = json.loads((root / "metadata.json").read_text(encoding="utf-8"))
    with (root / "gates.tsv").open(newline="", encoding="utf-8") as stream:
        gates = list(csv.DictReader(stream, delimiter="\t"))

    measurements: list[dict[str, object]] = []
    correctness: list[dict[str, str]] = []
    numeric = r"[0-9]+(?:[.,][0-9]+)?"
    distribution = re.compile(
        rf"([a-z][a-z0-9 -]*)=p50=({numeric}) (ms|KB), "
        rf"p95=({numeric}) \3, max=({numeric}) \3(?:@[0-9]+)?",
        re.IGNORECASE,
    )
    css_pass = re.compile(
        rf"(Mutation \+ ensure|EnsureCurrent only): ({numeric}) ms/pass, "
        rf"({numeric}) KB/pass",
        re.IGNORECASE,
    )
    css_initial = re.compile(
        rf"Initial ensure: ({numeric}) ms, ({numeric}) KB",
        re.IGNORECASE,
    )
    pixel = re.compile(
        r"pixel parity: changed=([0-9]+)/([0-9]+)(?:, allowance=([0-9]+))?",
        re.IGNORECASE,
    )

    log_paths = sorted((root / "logs").glob("*.log"))
    for log_path in log_paths:
        gate = log_path.stem
        for raw_line in log_path.read_text(encoding="utf-8", errors="replace").splitlines():
            line = raw_line.strip()
            if not line:
                continue
            label = line.split(":", 1)[0]
            for match in distribution.finditer(line):
                add_distribution(
                    measurements,
                    gate,
                    label,
                    match.group(1),
                    match.group(2),
                    match.group(4),
                    match.group(5),
                    match.group(3),
                )

            match = css_pass.search(line)
            if match:
                measurements.extend(
                    [
                        {
                            "gate": gate,
                            "metric": slug(match.group(1)),
                            "unit": "ms/pass",
                            "value": number(match.group(2)),
                        },
                        {
                            "gate": gate,
                            "metric": slug(match.group(1) + " allocation"),
                            "unit": "KB/pass",
                            "value": number(match.group(3)),
                        },
                    ]
                )

            match = css_initial.search(line)
            if match:
                measurements.extend(
                    [
                        {
                            "gate": gate,
                            "metric": "css-initial-ensure",
                            "unit": "ms",
                            "value": number(match.group(1)),
                        },
                        {
                            "gate": gate,
                            "metric": "css-initial-ensure-allocation",
                            "unit": "KB",
                            "value": number(match.group(2)),
                        },
                    ]
                )

            match = pixel.search(line)
            if match:
                changed = int(match.group(1))
                total = int(match.group(2))
                allowance = int(match.group(3)) if match.group(3) else 0
                correctness.append(
                    {
                        "gate": gate,
                        "kind": "pixel-parity",
                        "result": "pass" if changed <= allowance else "fail",
                        "details": f"changed={changed};total={total};allowance={allowance}",
                    }
                )
            elif (
                "Correctness:" in line
                or "matches-initial=" in line
                or "disposal plateau:" in line
                or line.endswith(": pass")
            ):
                correctness.append(
                    {
                        "gate": gate,
                        "kind": "contract-signal",
                        "result": "recorded",
                        "details": line,
                    }
                )

    summary = {
        "schemaVersion": 1,
        "metadata": metadata,
        "gates": gates,
        "performanceMeasurements": measurements,
        "correctnessSignals": correctness,
        "artifacts": [str(path.relative_to(root)) for path in log_paths],
    }
    (root / "evidence-summary.json").write_text(
        json.dumps(summary, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    if not measurements:
        print("No performance measurements were found in R0 logs.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
