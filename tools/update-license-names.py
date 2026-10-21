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


# Plans whose "friendly" name in Microsoft's table is still a code.
PLAN_OVERRIDES = {
    "YAMMER_ENTERPRISE": "Viva Engage (Yammer)",
    "OFFICEMOBILE_SUBSCRIPTION": "Office mobile apps",
}

# Words kept in capitals when tidying a shouted name.
KEEP_UPPER = {"II", "III", "AAD", "ATP", "MDM", "MCAS", "SQL", "AI", "BI", "VPN", "PSTN", "UK", "US", "EU", "M365", "O365"}


def tidy_plan_name(name: str) -> str:
    """'EXCHANGE ONLINE (PLAN 2)' -> 'Exchange Online (Plan 2)'; codes from PLAN_OVERRIDES; others unchanged."""
    if name in PLAN_OVERRIDES:
        return PLAN_OVERRIDES[name]
    letters = [c for c in name if c.isalpha()]
    if not letters or not all(c.isupper() for c in letters) or " " not in name:
        return name

    def word(w: str) -> str:
        core = w.strip("()[],")
        if core in KEEP_UPPER or any(ch.isdigit() for ch in core):
            return w  # 50GB, E1, 365, PLAN2-style tokens stay as written
        return w.lower().capitalize() if not w.startswith("(") else "(" + w[1:].lower().capitalize()

    return " ".join(word(w) for w in name.split(" "))


def main() -> int:
    source = sys.argv[1] if len(sys.argv) > 1 else None
    if source:
        text = pathlib.Path(source).read_text(encoding="utf-8-sig")
    else:
        with urllib.request.urlopen(URL, timeout=60) as response:
            text = response.read().decode("utf-8-sig")

    by_id, by_code, plans = {}, {}, {}
    for row in csv.DictReader(io.StringIO(text)):
        name = (row.get("Product_Display_Name") or "").strip()
        sku_id = (row.get("GUID") or "").strip().lower()
        code = (row.get("String_Id") or "").strip()
        # Each row is one service plan (app) inside a license: keep its friendly name too, so the
        # app can list "Microsoft Teams" rather than "TEAMS1".
        plan_id = (row.get("Service_Plan_Id") or "").strip().lower()
        plan_name = tidy_plan_name((row.get("Service_Plans_Included_Friendly_Names") or "").strip())
        if plan_id and plan_name:
            plans.setdefault(plan_id, plan_name)
        if not name:
            continue
        if sku_id:
            by_id.setdefault(sku_id, name)
        if code:
            by_code.setdefault(code.upper(), name)

    if len(by_id) < 200 or len(plans) < 500:
        raise SystemExit(f"Only {len(by_id)} licenses / {len(plans)} plans parsed; the CSV format may have changed.")

    OUT.parent.mkdir(parents=True, exist_ok=True)
    data = {"source": URL, "byId": dict(sorted(by_id.items())), "byCode": dict(sorted(by_code.items())),
            "plans": dict(sorted(plans.items()))}
    OUT.write_text(json.dumps(data, indent=0, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(by_id)} licenses by ID, {len(by_code)} by code, {len(plans)} service plans -> {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
