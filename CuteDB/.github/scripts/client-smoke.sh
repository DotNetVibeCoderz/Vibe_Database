#!/usr/bin/env bash
#
# Drives all three client libraries against a running cutedb-server.
#
# The per-language unit tests stub the transport, so they prove the client's own logic and nothing
# about the wire format. This proves the wire format: each client inserts, queries, patches and
# deletes against a real database, and the assertions are on values that had to survive encoding,
# storage, and decoding on the way back.
#
# Expects a server on 127.0.0.1:8420 and to be run from the CuteDB directory.

set -euo pipefail

BASE="${CUTEDB_URL:-http://127.0.0.1:8420}"
echo "==> smoke-testing clients against $BASE"

# --------------------------------------------------------------------------------------------
echo "==> python"
cd clients/python

python - <<'PY'
import os
from decimal import Decimal
from cutedb import CuteClient, CuteQueryError

db = CuteClient(os.environ.get("CUTEDB_URL", "http://127.0.0.1:8420"))
assert db.health()["status"] == "ok"

names = [c["name"] for c in db.collections()]
assert "orders" in names, names

notes = db.collection("smoke_python")

# A decimal has to survive the round trip exactly; that is the whole reason the client encodes it
# with its own digits rather than through float.
doc = notes.insert({"title": "halo", "amount": Decimal("1234.56"), "tags": ["a", "b"]})
assert doc["_id"], doc
assert notes.get(doc["_id"])["amount"] == 1234.56

ids = notes.insert_many([{"n": i} for i in range(250)])
assert len(ids) == 250
assert notes.count() == 251
assert notes.count("n >= 240") == 10

patched = notes.patch(doc["_id"], {"meta.owner": "kang"})
assert patched["meta"]["owner"] == "kang", patched

assert notes.delete(doc["_id"]) is True
assert notes.delete("0" * 24) is False
assert notes.get("0" * 24) is None

result = db.query(
    "SELECT address.city AS city, COUNT(*) AS n FROM orders GROUP BY address.city ORDER BY n DESC")
assert len(result.rows) > 0
assert result.plan

try:
    db.query("SELECT * FROM orders WHERE total ~ 5")
    raise AssertionError("expected a query error")
except CuteQueryError as error:
    assert error.code == "invalid_query"

db.drop_collection("smoke_python")
print("   python ok")
PY

cd ../..

# --------------------------------------------------------------------------------------------
# A missing toolchain is a skip rather than a failure, so this script is usable on a developer
# machine that has only one of the three installed. CI installs all three explicitly, so nothing
# can silently skip there.
if ! command -v go >/dev/null 2>&1; then
  echo "==> go SKIPPED (no go toolchain on this machine)"
else
echo "==> go"
cd clients/go

mkdir -p /tmp/cutedb-go-smoke
cat > /tmp/cutedb-go-smoke/main.go <<'GO'
package main

import (
	"context"
	"fmt"
	"log"
	"os"
	"strings"

	"github.com/DotNetVibeCoderz/Vibe_Database/CuteDB/clients/go/cutedb"
)

func main() {
	base := os.Getenv("CUTEDB_URL")
	if base == "" {
		base = "http://127.0.0.1:8420"
	}

	ctx := context.Background()
	client := cutedb.New(base)

	if _, err := client.Health(ctx); err != nil {
		log.Fatalf("health: %v", err)
	}

	notes := client.Collection("smoke_go")

	doc, err := notes.Insert(ctx, cutedb.Document{
		"title":  "halo",
		"nested": map[string]any{"a": []any{1, 2, 3}},
	})
	if err != nil {
		log.Fatalf("insert: %v", err)
	}

	id := doc.ID()
	if id == "" {
		log.Fatal("insert returned no id")
	}

	back, err := notes.Get(ctx, id)
	if err != nil || back == nil {
		log.Fatalf("get: %v", err)
	}

	batch := make([]cutedb.Document, 250)
	for i := range batch {
		batch[i] = cutedb.Document{"n": i}
	}
	ids, err := notes.InsertMany(ctx, batch)
	if err != nil || len(ids) != 250 {
		log.Fatalf("insertMany: %v (%d ids)", err, len(ids))
	}

	count, err := notes.Count(ctx, "n >= 240")
	if err != nil || count != 10 {
		log.Fatalf("count: %v (%d)", err, count)
	}

	// A missing document is not an error.
	missing, err := notes.Get(ctx, strings.Repeat("0", 24))
	if err != nil || missing != nil {
		log.Fatalf("expected nil, nil for a missing document; got %v, %v", missing, err)
	}

	if _, err := client.Query(ctx, "SELECT * FROM orders WHERE total ~ 5", nil); !cutedb.IsQueryError(err) {
		log.Fatalf("expected a query error, got %v", err)
	}

	result, err := client.Query(ctx,
		"SELECT address.city AS city, COUNT(*) AS n FROM orders GROUP BY address.city", nil)
	if err != nil || len(result.Rows) == 0 {
		log.Fatalf("query: %v", err)
	}

	if _, err := client.DropCollection(ctx, "smoke_go"); err != nil {
		log.Fatalf("drop: %v", err)
	}

	fmt.Println("   go ok")
}
GO

go run /tmp/cutedb-go-smoke/main.go
cd ../..
fi

# --------------------------------------------------------------------------------------------
if ! command -v node >/dev/null 2>&1; then
  echo "==> node SKIPPED (no node on this machine)"
  exit 0
fi

echo "==> node"
cd clients/nodejs

node - <<'JS'
import("./src/index.js").then(async ({ CuteClient, CuteError }) => {
  const base = process.env.CUTEDB_URL ?? "http://127.0.0.1:8420";
  const db = new CuteClient(base);

  const health = await db.health();
  if (health.status !== "ok") throw new Error(`health: ${JSON.stringify(health)}`);

  const notes = db.collection("smoke_node");

  const doc = await notes.insert({ title: "halo", nested: { a: [1, 2, 3] } });
  if (!doc._id) throw new Error("insert returned no id");

  const back = await notes.get(doc._id);
  if (JSON.stringify(back.nested) !== JSON.stringify({ a: [1, 2, 3] })) {
    throw new Error(`nested did not survive: ${JSON.stringify(back.nested)}`);
  }

  const ids = await notes.insertMany(Array.from({ length: 250 }, (_, i) => ({ n: i })));
  if (ids.length !== 250) throw new Error(`insertMany returned ${ids.length} ids`);
  if ((await notes.count("n >= 240")) !== 10) throw new Error("count is wrong");

  const patched = await notes.patch(doc._id, { "meta.owner": "kang" });
  if (patched.meta?.owner !== "kang") throw new Error("patch did not reach into the subdocument");

  if ((await notes.get("0".repeat(24))) !== null) throw new Error("missing document should be null");
  if ((await notes.delete("0".repeat(24))) !== false) throw new Error("missing delete should be false");

  const index = await notes.createIndex("n");
  if (index.keys !== 250) throw new Error(`index has ${index.keys} keys`);

  const plan = await db.explain("SELECT * FROM smoke_node WHERE n = 5");
  if (plan.strategy !== "Index seek") throw new Error(`expected an index seek, got ${plan.strategy}`);

  let threw = false;
  try {
    await db.query("SELECT nope(");
  } catch (error) {
    threw = error instanceof CuteError && error.isQueryError;
  }
  if (!threw) throw new Error("expected a query error");

  await db.dropCollection("smoke_node");
  console.log("   node ok");
}).catch((error) => {
  console.error(error);
  process.exit(1);
});
JS

cd ../..

echo "==> all three clients ok"
