-- A user-supplied name and a portfolio type for every run, captured on a new
-- first wizard step before any file is picked.
--
-- The type is metadata only: the engine, the two checks and the exporters
-- behave identically for both values. The column exists so that the day
-- behaviour does need to branch on it, the data is already there.
--
-- Written to be re-runnable, for the reason 20260803000000_column_mappings.sql
-- sets out: this schema has been applied by hand through the SQL editor as well
-- as by the CLI, so whether a given database has already seen this file is not
-- knowable from the migration history alone.
--
-- The one thing "if not exists" cannot do is reshape a column that is already
-- there under an older definition - it skips silently instead. On a database
-- where someone hand-added a bare run_type_id, neither the default nor the
-- foreign key below would be applied; check for that before trusting this file
-- on such a database.

create table if not exists public.run_types (
  id           smallint primary key,
  code         text not null unique,
  description  text not null
);

-- "on conflict do update" rather than a plain insert, so this file stays
-- authoritative on a database that has already seen an earlier version of it
insert into public.run_types (id, code, description) values
  (1, 'lending',           'Lending'),
  (2, 'trade_receivables', 'Trade Receivables')
on conflict (id) do update
  set code = excluded.code, description = excluded.description;

-- Nullable, and existing runs are deliberately not backfilled: a name for a run
-- nobody named would be invented data. The UI falls back to what it shows today.
alter table public.runs add column if not exists name text;

-- Unlike status_id, this needs no backfill pass. That column derived its value
-- per row from the old status column; here every existing run is lending, which
-- is exactly what the default says, and Postgres applies it without a rewrite.
alter table public.runs
  add column if not exists run_type_id smallint not null default 1
    references public.run_types(id);

-- No index on run_type_id, matching status_id: nothing filters or orders by it,
-- it holds two distinct values, and runs_user_created_idx serves the list query.

-- lookup tables: read-only reference data, no RLS needed (nothing user-scoped)
grant select on public.run_types to service_role, authenticated, anon;
