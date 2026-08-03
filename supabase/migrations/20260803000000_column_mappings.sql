-- Saved + per-run column mappings for uploaded write-off/exposure (IFRS9)
-- files. See docs/superpowers/specs/2026-08-03-upload-column-mapping-design.md.
--
-- Written to be re-runnable, because this project's schema has been applied by
-- hand through the SQL editor as well as by the CLI (see docs/deployment.md),
-- so whether a given database has already seen this file is not knowable from
-- the migration history alone. Every statement below either guards itself or is
-- naturally repeatable.
--
-- The one thing "if not exists" cannot do is reshape a table that is already
-- there under an older definition - it skips silently instead. That is safe for
-- these two, which are new in this migration and have no earlier shape.

create table if not exists public.saved_column_mappings (
  id                bigint generated always as identity primary key,
  user_id           uuid not null references auth.users(id) on delete cascade,
  file_kind         text not null check (file_kind in ('writeoff', 'exposure')),
  column_signature  text not null,
  field_name        text not null,
  source_column     text not null,
  created_at        timestamptz not null default now(),
  last_used_at      timestamptz not null default now(),
  unique (user_id, file_kind, column_signature, field_name)
);

create index if not exists saved_column_mappings_lookup_idx
  on public.saved_column_mappings (user_id, file_kind, column_signature);

create table if not exists public.run_set_column_mappings (
  id             bigint generated always as identity primary key,
  run_id         uuid not null references public.runs(id) on delete cascade,
  set_key        text not null,
  file_kind      text not null check (file_kind in ('writeoff', 'exposure')),
  field_name     text not null,
  source_column  text not null,
  unique (run_id, set_key, file_kind, field_name)
);

-- enabling row level security twice is a no-op, so these need no guard
alter table public.saved_column_mappings enable row level security;
alter table public.run_set_column_mappings enable row level security;

-- Postgres has no "create policy if not exists", so each is dropped first. That
-- also makes the definition here authoritative: a policy left over from an
-- earlier version of this file is replaced rather than kept.
drop policy if exists "own saved column mappings readable" on public.saved_column_mappings;
create policy "own saved column mappings readable" on public.saved_column_mappings
  for select to authenticated using (auth.uid() = user_id);

drop policy if exists "own run set column mappings readable" on public.run_set_column_mappings;
create policy "own run set column mappings readable" on public.run_set_column_mappings
  for select to authenticated using (
    exists (select 1 from public.runs r where r.id = run_set_column_mappings.run_id and r.user_id = auth.uid())
  );

-- grants are idempotent: re-granting an existing privilege changes nothing
grant select, insert, update, delete on public.saved_column_mappings to service_role;
grant select, insert, update, delete on public.run_set_column_mappings to service_role;
grant select on public.saved_column_mappings to authenticated;
grant select on public.run_set_column_mappings to authenticated;
