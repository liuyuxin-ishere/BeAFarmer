"""Read business snapshots / expire tokens only in this task's isolated DBs.
Never selects password fields or token hashes. The real DB is never copied or
rewritten while a server is running; sqlite transactions perform expiration.
"""
import argparse
import hashlib
import json
import sqlite3
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("action", choices=["snapshot", "expire"])
parser.add_argument("--database", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()
owned_root = Path(__file__).resolve().parent
database = Path(args.database).resolve()
output = Path(args.output).resolve()
if not database.is_relative_to(owned_root) or not output.is_relative_to(owned_root):
    raise RuntimeError("fixture paths must be inside Root-Finalization")
mode = "ro" if args.action == "snapshot" else "rw"
with sqlite3.connect(database.as_uri() + "?mode=" + mode, uri=True, timeout=10) as connection:
    if args.action == "expire":
        cursor = connection.execute("UPDATE auth_token SET expires_at_ms=0 WHERE revoked=0")
        connection.commit()
        result = {"expiredCurrentRecords": cursor.rowcount, "expiresAtMs": 0}
    else:
        connection.row_factory = sqlite3.Row
        players = [dict(row) for row in connection.execute("SELECT * FROM player ORDER BY id")]
        saved = []
        for row in connection.execute("SELECT player_id,saved_revision,body FROM player_snapshot ORDER BY player_id"):
            body = bytes(row["body"])
            saved.append({"playerId": row["player_id"], "savedRevision": row["saved_revision"],
                          "bodyBytes": len(body), "bodySha256": hashlib.sha256(body).hexdigest()})
        result = {"players": players, "savedSnapshots": saved}
with output.open("x", encoding="utf-8") as handle:
    json.dump(result, handle, ensure_ascii=False, indent=2)
print(json.dumps({"action": args.action, "output": str(output)}, ensure_ascii=False))
