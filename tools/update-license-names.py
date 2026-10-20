"""Refresh src/Tenantry.Core/Licensing/license-names.json from Microsoft's published table.

Graph's subscribedSkus gives only a license's ID (skuId) and code (skuPartNumber, e.g. "SPB").
The names admins know ("Microsoft 365 Business Premium") come from Microsoft's "Product names
and service plan identifiers for licensing" CSV. This keeps a compact skuId -> name map, plus a
code -> name map as a fallback, in the app. Run occasionally (python -I tools/update-license-names.py)
to pick up new licenses; the app shows the code for anything it doesn't know.
"""
import csv
import io
import json
import pathlib
import sys
import urllib.request

URL = ("https://download.microsoft.com/download/e/3/e/e3e9faf2-f28b-490a-9ada-c6089a1fc5b0/"
       "Product%20names%20and%20service%20plan%20identifiers%20for%20licensing.csv")
OUT = pathlib.Path(__file__).resolve().parent.parent / "src" / "Tenantry.Core" / "Licensing" / "license-names.json"


def main() -> int:
    source = sys.argv[1] if len(sys.argv) > 1 else None
    if source:
        text = pathlib.Path(source).read_text(encoding="utf-8-sig")
    else:
        with urllib.request.urlopen(URL, timeout=60) as response:
            text = response.read().decode("utf-8-sig")

    by_id, by_code = {}, {}
    for row in csv.DictReader(io.StringIO(text)):
        name = (row.get("Product_Display_Name") or "").strip()
        sku_id = (row.get("GUID") or "").strip().lower()
        code = (row.get("String_Id") or "").strip()
        if not name:
            continue
        if sku_id:
            by_id.setdefault(sku_id, name)
        if code:
            by_code.setdefault(code.upper(), name)

    if len(by_id) < 200:
        raise SystemExit(f"Only {len(by_id)} licenses parsed; the CSV format may have changed.")

    OUT.parent.mkdir(parents=True, exist_ok=True)
    data = {"source": URL, "byId": dict(sorted(by_id.items())), "byCode": dict(sorted(by_code.items()))}
    OUT.write_text(json.dumps(data, indent=0, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(by_id)} licenses by ID, {len(by_code)} by code -> {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
