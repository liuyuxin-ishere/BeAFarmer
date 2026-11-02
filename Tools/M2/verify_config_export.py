"""Verify the config export failure boundary in an isolated project copy.

The real project's source and published data are read only. A successful control
export runs first; each invalid workbook must then fail without changing any of
the five published targets. Keep all reports and fixtures for diagnosis.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
from datetime import datetime, timezone

import openpyxl


TARGETS = (
    "Assets/Config/Generated",
    "Shared/config/Output/json",
    "Server/ConfigLuban",
    "Assets/StreamingAssets/ConfigLuban",
    "Assets/ConfigLuban",
)


def fingerprint(project: Path) -> dict[str, str]:
    """Hash every published file, including metadata; exclude temporary staging."""
    return {
        p.relative_to(project).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
        for target in TARGETS
        for p in (project / target).rglob("*")
        if p.is_file()
    }


def copy_source(source: Path, destination: Path) -> None:
    """Copy only the Luban schema/data inputs and configuration file."""
    destination.mkdir(parents=True)
    for name in ("Defines", "Datas"):
        shutil.copytree(source / name, destination / name)
    shutil.copyfile(source / "luban.conf", destination / "luban.conf")


def alter(source: Path, case: str) -> None:
    """Introduce exactly one known-invalid input into a copied workbook."""
    workbook = "shop" if case in ("missing_item", "price_overflow") else (
        "tool" if case in ("zero_quantity", "empty_material_segment") else "mapinstance"
    )
    path = source / "Datas" / (workbook + ".xlsx")
    book = openpyxl.load_workbook(path)
    sheet = book.active
    columns = {cell.value: cell.column for cell in sheet[1] if cell.value}
    if case == "missing_item":
        sheet.cell(4, columns["itemId"], 99999999)
    elif case == "price_overflow":
        sheet.cell(4, columns["price"], 4294967297)
    elif case in ("zero_quantity", "empty_material_segment"):
        row = next(r for r in range(4, sheet.max_row + 1)
                   if sheet.cell(r, columns["materials"]).value)
        sheet.cell(row, columns["materials"],
                   "3009:0" if case == "zero_quantity" else "3009:1;;3010:1")
    elif case == "duplicate_instance":
        sheet.append([cell.value for cell in sheet[4]])
    else:
        kind = "Node" if case == "missing_node" else "Facility"
        row = next(r for r in range(4, sheet.max_row + 1)
                   if sheet.cell(r, columns["kind"]).value == kind)
        sheet.delete_rows(row)
    book.save(path)
    book.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[2],
                        help="Full Unity project root, used only as an input.")
    parser.add_argument("--output", type=Path,
                        help="New directory inside Docs/Acceptance/M2; existing reports are never overwritten.")
    args = parser.parse_args()
    root = args.repo_root.resolve()
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    output = (args.output or root / "Docs/Acceptance/M2" / ("Config Review " + stamp)).resolve()
    if not output.is_relative_to(root / "Docs/Acceptance/M2"):
        parser.error("--output must stay inside this project's Docs/Acceptance/M2")
    output.mkdir(parents=True, exist_ok=False)
    fixture = output / "Fixture Project"
    fixture.mkdir()
    script = fixture / "Tools/gen_config.ps1"
    script.parent.mkdir()
    shutil.copyfile(root / "Tools/gen_config.ps1", script)
    for target in TARGETS:
        destination = fixture / target
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(root / target, destination)
    source = fixture / "Shared/config/Source"
    copy_source(root / "Shared/config", source)
    powershell = shutil.which("powershell.exe")
    if not powershell:
        parser.error("Windows PowerShell 5.1 is required by this export adapter")
    exporter = root / "Tools/Luban/Luban/Luban.dll"

    def run(name: str, config_source: Path) -> int:
        """Invoke the real adapter with argument-array quoting, save its actual exit."""
        with (output / (name + ".stdout.txt")).open("wb") as stream:
            result = subprocess.run(
                [powershell, "-NoProfile", "-File", str(script), "-RepoRoot", str(fixture),
                 "-SourceDir", str(config_source), "-ExporterPath", str(exporter),
                 "-ConfigVersion", "200"],
                cwd=root, stdout=stream, stderr=subprocess.STDOUT,
            )
        (output / (name + ".exit.txt")).write_text(str(result.returncode), encoding="ascii")
        return result.returncode

    control_exit = run("control", source)
    results: list[dict[str, object]] = []
    if control_exit == 0:
        baseline = fingerprint(fixture)
        for case in ("missing_item", "duplicate_instance", "zero_quantity", "missing_node",
                     "missing_facility", "empty_material_segment", "price_overflow"):
            bad_source = output / "InvalidSources" / case
            copy_source(root / "Shared/config", bad_source)
            alter(bad_source, case)
            exit_code = run(case, bad_source)
            results.append({"case": case, "exit": exit_code,
                            "publishedFilesUnchanged": fingerprint(fixture) == baseline})
    passed = control_exit == 0 and len(results) == 7 and all(
        r["exit"] != 0 and r["publishedFilesUnchanged"] for r in results
    )
    report = {"realProjectUnmodified": True, "controlExit": control_exit,
              "negativeCases": results, "passed": passed}
    (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "result.json"), **report}))
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
