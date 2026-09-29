import argparse
import datetime as dt
import difflib
import json
import sqlite3
import urllib.request
import uuid
from pathlib import Path


def api(base, path, token=None, body=None):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(f"{base}{path}", data=data, headers=headers, method="POST" if data else "GET")
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def enum_value(value, values):
    return values.index(value)


parser = argparse.ArgumentParser(description="Import KIVRA label history from another KIVRA API into local SQLite.")
parser.add_argument("--source", required=True)
parser.add_argument("--pin", required=True)
parser.add_argument("--database", required=True)
args = parser.parse_args()

database = Path(args.database).resolve()
token = api(args.source.rstrip("/"), "/api/auth/pin", body={"pin": args.pin})["token"]
bootstrap = api(args.source.rstrip("/"), "/api/bootstrap", token=token)
jobs = api(args.source.rstrip("/"), "/api/print-jobs", token=token)

connection = sqlite3.connect(database, timeout=30)
connection.execute("PRAGMA foreign_keys=ON")
backup_dir = database.parent / "backups"
backup_dir.mkdir(exist_ok=True)
stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
backup_path = backup_dir / f"kivra-{stamp}-before-remote-import.db"
backup = sqlite3.connect(backup_path)
connection.backup(backup)
integrity = backup.execute("PRAGMA integrity_check").fetchone()[0]
backup.close()
if integrity.lower() != "ok":
    raise RuntimeError(f"Pre-import backup failed integrity check: {integrity}")

items = {row[1].strip().lower(): row for row in connection.execute(
    'SELECT "Id", "Name", "Classification", "DateTerminology", "ShelfLifeValue", "ShelfLifeUnit" FROM "Items"'
)}
locations = {row[1].strip().lower(): row[0] for row in connection.execute('SELECT "Id", "Name" FROM "StorageLocations"')}
printers = {row[1].strip().lower(): row[0] for row in connection.execute('SELECT "Id", "Name" FROM "Printers"')}
user_id = connection.execute('SELECT "Id" FROM "Users" WHERE "Active"=1 ORDER BY "Role" LIMIT 1').fetchone()[0]
existing_codes = {row[0] for row in connection.execute('SELECT "LabelCode" FROM "Labels"')}
existing_job_ids = {row[0] for row in connection.execute('SELECT "Id" FROM "PrintJobs"')}
existing_keys = {row[0] for row in connection.execute('SELECT "IdempotencyKey" FROM "PrintJobs" WHERE "IdempotencyKey" IS NOT NULL')}

remote_items = {item["name"].strip().lower(): item for item in bootstrap["items"]}
remote_printers = {printer["id"]: printer for printer in bootstrap.get("allPrinters", [])}
historical_aliases = {
    "chopped chilly": "chopped chilli",
    "mutton boneless": "boiled mutton (boneless)",
    "boneless boiled chicken": "boiled chicken (boneless)",
    "crispy chicken": "chicken crispy",
    "onion / capsicum cut": "onion and capsicum",
}
jobs_by_label = {}
for job in jobs:
    jobs_by_label.setdefault(job["labelId"], []).append(job)

classification_values = ["NotApplicable", "Veg", "NonVeg", "Egg"]
shelf_values = ["Minutes", "Hours", "Days", "Weeks", "Months"]
label_status_values = ["Active", "Consumed", "Discarded", "Expired", "Cancelled"]
job_status_values = ["Queued", "Sending", "Printed", "Failed", "Cancelled"]
inserted_labels = 0
inserted_jobs = 0
mapped_names = []
skipped_test_labels = []

with connection:
    for label in bootstrap["labels"]:
        if label["labelCode"] in existing_codes:
            continue
        if label["itemName"].strip().upper() == "KIVRA TEST LABEL":
            skipped_test_labels.append(label["labelCode"])
            continue
        label_name = label["itemName"].strip().lower()
        item_key = historical_aliases.get(label_name, label_name)
        if item_key != label_name:
            mapped_names.append({"label": label["itemName"], "item": remote_items[item_key]["name"]})
        if item_key not in remote_items or item_key not in items:
            candidates = sorted(set(remote_items).intersection(items))
            matches = difflib.get_close_matches(item_key, candidates, n=1, cutoff=0.65)
            if matches:
                item_key = matches[0]
                mapped_names.append({"label": label["itemName"], "item": remote_items[item_key]["name"]})
        item = remote_items.get(item_key)
        local_item = items.get(item_key)
        if not item or not local_item:
            raise RuntimeError(f"Cannot map label item: {label['itemName']}")
        label_jobs = sorted(jobs_by_label.get(label["id"], []), key=lambda value: value["requestedAt"])
        remote_printer = remote_printers.get(label_jobs[0]["printerId"]) if label_jobs else None
        printer_id = printers.get((remote_printer or {}).get("name", "").strip().lower())
        if not printer_id:
            printer_id = printers.get("kitchen printer") or next(iter(printers.values()))
        storage_name = label.get("storageLocation") or "Not assigned"
        storage_id = locations.get(storage_name.strip().lower())
        created_at = label_jobs[0]["createdAt"] if label_jobs else label["operationalDateTime"]
        updated_at = label_jobs[-1]["updatedAt"] if label_jobs else created_at
        printed_jobs = [job for job in label_jobs if job["status"] == "Printed"]
        last_print = printed_jobs[-1]["completedAt"] if printed_jobs else None
        connection.execute(
            'INSERT INTO "Labels" ("Id","LabelCode","ItemId","ItemNameSnapshot","CategorySnapshot","ClassificationSnapshot","DateTerminologySnapshot","OperationalDateTime","ExpiryDateTime","ShelfLifeRuleSnapshot","StorageLocationId","StorageLocationSnapshot","Quantity","Unit","CreatedByUserId","CurrentStatus","PrinterId","LastSuccessfulPrintAt","SuccessfulPrintCount","CreatedAt","UpdatedAt") VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)',
            (label["id"], label["labelCode"], local_item[0], label["itemName"], item.get("category") or "Uncategorized",
             enum_value(item["classification"], classification_values), item["dateTerminology"], label["operationalDateTime"],
             label["expiryDateTime"], f'{item["shelfLifeValue"]} {item["shelfLifeUnit"]}', storage_id, storage_name,
             None, None, user_id, enum_value(label["status"], label_status_values), printer_id, last_print,
             label["successfulPrintCount"], created_at, updated_at),
        )
        inserted_labels += 1
        existing_codes.add(label["labelCode"])

        for job in label_jobs:
            if job["id"] in existing_job_ids:
                continue
            remote_job_printer = remote_printers.get(job["printerId"])
            job_printer_id = printers.get((remote_job_printer or {}).get("name", "").strip().lower()) or printer_id
            key = job.get("idempotencyKey")
            if key in existing_keys:
                key = f"import-{uuid.uuid4()}"
            connection.execute(
                'INSERT INTO "PrintJobs" ("Id","LabelId","PrinterId","RequestedByUserId","RequestedAt","StartedAt","CompletedAt","Status","FailureReason","RetryCount","IsReprint","Payload","IdempotencyKey","CreatedAt","UpdatedAt","BridgeDeviceId","LeaseExpiresAt") VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)',
                (job["id"], label["id"], job_printer_id, user_id, job["requestedAt"], job.get("startedAt"),
                 job.get("completedAt"), enum_value(job["status"], job_status_values), job.get("failureReason"),
                 job["retryCount"], 1 if job["isReprint"] else 0, job.get("payload"), key,
                 job["createdAt"], job["updatedAt"], None, job.get("leaseExpiresAt")),
            )
            inserted_jobs += 1
            existing_job_ids.add(job["id"])
            if key:
                existing_keys.add(key)

    for historical_name, item_key in historical_aliases.items():
        item = remote_items[item_key]
        local_item = items[item_key]
        connection.execute(
            'UPDATE "Labels" SET "ItemId"=?, "CategorySnapshot"=?, "ClassificationSnapshot"=?, "DateTerminologySnapshot"=?, "ShelfLifeRuleSnapshot"=? WHERE lower("ItemNameSnapshot")=?',
            (local_item[0], item.get("category") or "Uncategorized", enum_value(item["classification"], classification_values),
             item["dateTerminology"], f'{item["shelfLifeValue"]} {item["shelfLifeUnit"]}', historical_name),
        )

result = connection.execute("PRAGMA integrity_check").fetchone()[0]
label_count = connection.execute('SELECT COUNT(*) FROM "Labels"').fetchone()[0]
job_count = connection.execute('SELECT COUNT(*) FROM "PrintJobs"').fetchone()[0]
connection.close()
print(json.dumps({
    "backup": str(backup_path),
    "integrity": result,
    "insertedLabels": inserted_labels,
    "insertedPrintJobs": inserted_jobs,
    "totalLabels": label_count,
    "totalPrintJobs": job_count,
    "mappedHistoricalNames": mapped_names,
    "skippedTestLabels": skipped_test_labels,
}))
