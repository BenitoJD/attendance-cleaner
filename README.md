# attendance-cleaner

Takes raw attendance Excel files, removes unwanted columns/rows, and writes a clean Excel file.

## How it works

1. Drop input `.xlsx` files into `input/`.
2. Edit `config.json` to describe the cleanup rules (columns to drop, rows to skip, sheets to keep, output columns).
3. Run the cleaner — cleaned files land in `output/`.

```bash
pip install -r requirements.txt
python clean_attendance.py
```

## Configuration

`config.json` controls everything, so new file layouts don't require code changes:

| Key | What it does |
| --- | --- |
| `drop_columns` | Column names/letters to remove |
| `drop_rows_containing` | Remove any row where a cell contains one of these values |
| `skip_top_rows` | Number of header/junk rows to skip at the top |
| `keep_sheets` | Only process these sheet names (empty = all sheets) |
| `output_columns` | Exact column order for the output file (empty = keep as-is) |

## Layout

```
input/                  # raw attendance Excel files go here
output/                 # cleaned files are written here
config.json             # cleanup rules
clean_attendance.py     # the cleaner
```
