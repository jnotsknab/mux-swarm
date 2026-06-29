---
name: data-analysis
description: "Profile, clean, and analyze tabular data (CSV/JSON/Parquet) with pandas/polars. Use when exploring datasets, computing aggregations, joining tables, resampling time series, or producing analysis charts."
requires_bins: [uv, python]
---

## When to use

Use this skill whenever the task involves loading structured data, understanding its shape and quality, computing statistics or aggregations, or producing charts saved to disk. Pairs naturally with DataAnalysisAgent, which has a REPL for in-session computation.

---

## Environment setup

```python
# One-off: install deps into a local venv (never on NAS)
# uv venv .venv && uv pip install pandas polars pyarrow matplotlib seaborn
import pandas as pd
import polars as pl
import matplotlib.pyplot as plt
import seaborn as sns
```

---

## Load data

```python
# CSV
df = pd.read_csv("data.csv")

# JSON (records or lines)
df = pd.read_json("data.json")           # records
df = pd.read_json("data.ndjson", lines=True)  # newline-delimited

# Parquet (prefer polars for large files)
df = pd.read_parquet("data.parquet")
lf = pl.scan_parquet("data.parquet")    # lazy polars – no memory blow-up
```

---

## Profile: shape, dtypes, nulls, distributions

```python
print(df.shape)          # (rows, cols)
print(df.dtypes)
print(df.isnull().sum())                   # null counts per column
print(df.describe(include="all"))          # numeric + categorical stats
print(df["col"].value_counts(dropna=False))  # top categories + null freq
print(df.duplicated().sum())               # duplicate rows
```

---

## Clean

```python
df = df.drop_duplicates()
df["col"] = df["col"].fillna(df["col"].median())   # impute numeric
df["cat"] = df["cat"].fillna("unknown")             # impute categorical
df["date"] = pd.to_datetime(df["date"], utc=True)  # parse dates
df = df[df["value"] > 0]                            # filter outliers
df.columns = df.columns.str.strip().str.lower().str.replace(" ", "_")
```

---

## Joins, groupby, aggregations

```python
# Join
merged = df_a.merge(df_b, on="id", how="left")

# Groupby + multi-agg
summary = (
    df.groupby(["region", "category"])
      .agg(
          total=("revenue", "sum"),
          avg_order=("revenue", "mean"),
          n=("order_id", "count"),
      )
      .reset_index()
      .sort_values("total", ascending=False)
)

# Pivot
pivot = df.pivot_table(index="month", columns="category", values="revenue", aggfunc="sum")
```

---

## Time series

```python
df = df.set_index("date").sort_index()

# Resample to weekly sums
weekly = df["revenue"].resample("W").sum()

# Rolling average
df["rolling_7d"] = df["revenue"].rolling(7, min_periods=1).mean()

# Year-over-year comparison
df["year"] = df.index.year
yoy = df.groupby("year")["revenue"].sum()
```

---

## Polars for large data

```python
# Lazy evaluation — executes only at .collect()
result = (
    pl.scan_parquet("large.parquet")
      .filter(pl.col("status") == "active")
      .group_by(["region"])
      .agg(pl.col("amount").sum().alias("total"))
      .sort("total", descending=True)
      .collect()
)
```

---

## Charts — always save to {{paths.sandbox}}

```python
out = "{{paths.sandbox}}/charts"
import os; os.makedirs(out, exist_ok=True)

# Distribution
fig, ax = plt.subplots()
sns.histplot(df["value"], kde=True, ax=ax)
ax.set_title("Value distribution")
fig.savefig(f"{out}/distribution.png", dpi=150, bbox_inches="tight")
plt.close(fig)

# Time series
fig, ax = plt.subplots(figsize=(10, 4))
weekly.plot(ax=ax, title="Weekly revenue")
fig.savefig(f"{out}/weekly_revenue.png", dpi=150, bbox_inches="tight")
plt.close(fig)

# Heatmap (correlation)
fig, ax = plt.subplots(figsize=(8, 6))
sns.heatmap(df.select_dtypes("number").corr(), annot=True, fmt=".2f", ax=ax)
fig.savefig(f"{out}/correlation.png", dpi=150, bbox_inches="tight")
plt.close(fig)
```

Always close figures (`plt.close(fig)`) to avoid memory leaks in long sessions.

---

## Output convention

- Return a **3-line summary** (rows × cols, key findings, chart paths) to the lead.
- Write full tables or large DataFrames to `{{paths.sandbox}}/analysis_<name>.csv`, not inline.
- If the dataset exceeds ~100k rows, prefer polars lazy scan; do NOT load the whole file into pandas.
