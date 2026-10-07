"""Clean attendance Excel files according to config.json.

Reads every .xlsx in input/, applies the cleanup rules from config.json,
and writes the result to output/ with the same filename.
"""

import json
from pathlib import Path

import pandas as pd

ROOT = Path(__file__).parent
INPUT_DIR = ROOT / "input"
OUTPUT_DIR = ROOT / "output"


def load_config() -> dict:
    with open(ROOT / "config.json", encoding="utf-8") as f:
        return json.load(f)


def clean_sheet(df: pd.DataFrame, cfg: dict) -> pd.DataFrame:
    if cfg.get("skip_top_rows"):
        df = df.iloc[cfg["skip_top_rows"]:]
        df.columns = df.iloc[0]
        df = df.iloc[1:].reset_index(drop=True)

    drop_cols = [c for c in cfg.get("drop_columns", []) if c in df.columns]
    if drop_cols:
        df = df.drop(columns=drop_cols)

    for value in cfg.get("drop_rows_containing", []):
        df = df[~df.astype(str).apply(lambda row: row.str.contains(str(value), na=False).any(), axis=1)]

    output_cols = [c for c in cfg.get("output_columns", []) if c in df.columns]
    if output_cols:
        df = df[output_cols]

    return df.reset_index(drop=True)


def clean_file(path: Path, cfg: dict) -> None:
    all_sheets = pd.read_excel(path, sheet_name=None, dtype=str)
    keep = set(cfg.get("keep_sheets") or all_sheets.keys())

    cleaned = {
        name: clean_sheet(df, cfg)
        for name, df in all_sheets.items()
        if name in keep
    }

    out_path = OUTPUT_DIR / path.name
    with pd.ExcelWriter(out_path, engine="openpyxl") as writer:
        for name, df in cleaned.items():
            df.to_excel(writer, sheet_name=name, index=False)

    print(f"{path.name}: wrote {out_path.name} ({len(cleaned)} sheet(s))")


def main() -> None:
    cfg = load_config()
    OUTPUT_DIR.mkdir(exist_ok=True)

    files = sorted(INPUT_DIR.glob("*.xlsx"))
    if not files:
        print("No .xlsx files found in input/. Add files and run again.")
        return

    for path in files:
        clean_file(path, cfg)


if __name__ == "__main__":
    main()
