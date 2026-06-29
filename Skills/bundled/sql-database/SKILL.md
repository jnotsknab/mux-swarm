---
name: sql-database
description: Safe schema-aware SQL querying against CSV/Parquet/JSON files, SQLite databases, and live Postgres. Use when querying structured data, profiling a schema, or building data pipelines.
requires_bins: [uv, python]
---

## When to use
- Query CSV, Parquet, or JSON files with SQL (no database setup required)
- Inspect or query a SQLite `.db` file
- Read-only analytics against a live Postgres instance
- Profile an unknown schema before writing migration or ETL code

---

## DuckDB — zero-setup, recommended first choice

DuckDB runs in-process; no server, no config. Query files directly.

```python
# Install
import subprocess
subprocess.run(["uv", "pip", "install", "duckdb"])

import duckdb

# Query a CSV directly
result = duckdb.sql("SELECT * FROM 'data.csv' LIMIT 10").df()

# Query Parquet
result = duckdb.sql("SELECT col, COUNT(*) FROM 'events.parquet' GROUP BY col").df()

# Query JSON
result = duckdb.sql("SELECT * FROM 'records.json' WHERE status = 'active'").df()

# Multiple files as one table (glob)
result = duckdb.sql("SELECT * FROM '{{paths.sandbox}}/data/*.csv'").df()

# Profile: list columns + types
duckdb.sql("DESCRIBE SELECT * FROM 'data.csv'").show()

# Profile: row count + basic stats
duckdb.sql("SUMMARIZE SELECT * FROM 'data.csv'").show()
```

### DuckDB with a persistent file
```python
con = duckdb.connect("{{paths.sandbox}}/analysis.duckdb")
con.execute("CREATE TABLE IF NOT EXISTS events AS SELECT * FROM 'events.csv'")
con.execute("SELECT COUNT(*) FROM events").fetchall()
con.close()
```

---

## SQLite — local .db files

```python
import sqlite3

con = sqlite3.connect("app.db")
cur = con.cursor()

# Profile: list all tables
cur.execute("SELECT name FROM sqlite_master WHERE type='table'")
print(cur.fetchall())

# Describe a table
cur.execute("PRAGMA table_info(users)")
print(cur.fetchall())

# Row count
cur.execute("SELECT COUNT(*) FROM users")
print(cur.fetchone())

# Safe parameterized query — ALWAYS use ? placeholders
user_id = 42
cur.execute("SELECT * FROM users WHERE id = ?", (user_id,))
print(cur.fetchall())

con.close()
```

---

## Postgres — read-only analytics

```python
# Install
subprocess.run(["uv", "pip", "install", "psycopg2-binary"])

import psycopg2

# Use env vars for credentials — never hardcode
import os
con = psycopg2.connect(
    host=os.environ["PG_HOST"],
    dbname=os.environ["PG_DB"],
    user=os.environ["PG_USER"],
    password=os.environ["PG_PASSWORD"],
)
con.autocommit = False  # explicit transactions

cur = con.cursor()

# Profile: list tables
cur.execute("""
    SELECT table_name FROM information_schema.tables
    WHERE table_schema = 'public' ORDER BY table_name
""")
print(cur.fetchall())

# Describe a table
cur.execute("""
    SELECT column_name, data_type, is_nullable
    FROM information_schema.columns
    WHERE table_name = %s ORDER BY ordinal_position
""", ("orders",))
print(cur.fetchall())

# Safe query — ALWAYS parameterize
cur.execute(
    "SELECT id, total FROM orders WHERE status = %s LIMIT 100",
    ("pending",)
)
rows = cur.fetchall()

con.rollback()  # never commit in a read-only audit
con.close()
```

### Postgres safety rules
- **SELECT before any write** — inspect schema and row counts first
- **Parameterized queries only** — never `f"WHERE id = {user_input}"`
- **Always use LIMIT** — unknown table sizes can return millions of rows
- **Rollback, don't commit** — wrap exploratory writes in a transaction and roll back
- **Prefer a read-only DB role** — connect as a user with `CONNECT` + `SELECT` only

---

## Schema profiling — universal pattern

```python
# DuckDB (any file format)
duckdb.sql("SUMMARIZE SELECT * FROM 'file.csv'").show()

# SQLite
cur.execute("SELECT name FROM sqlite_master WHERE type='table'")
for (table,) in cur.fetchall():
    print(f"\n--- {table} ---")
    cur.execute(f"PRAGMA table_info({table})")  # table name is not user input here
    print(cur.fetchall())
    cur.execute(f"SELECT COUNT(*) FROM {table}")
    print("rows:", cur.fetchone()[0])
```
