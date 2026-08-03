-- Saved + per-run column mappings for uploaded write-off/exposure (IFRS9)
-- files. See docs/superpowers/specs/2026-08-03-upload-column-mapping-design.md.

create table public.saved_column_mappings (
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

create index saved_column_mappings_lookup_idx
  on public.saved_column_mappings (user_id, file_kind, column_signature);

create table public.run_set_column_mappings (
  id             bigint generated always as identity primary key,
  run_id         uuid not null references public.runs(id) on delete cascade,
  set_key        text not null,
  file_kind      text not null check (file_kind in ('writeoff', 'exposure')),
  field_name     text not null,
  source_column  text not null,
  unique (run_id, set_key, file_kind, field_name)
);

alter table public.saved_column_mappings enable row level security;
alter table public.run_set_column_mappings enable row level security;

create policy "own saved column mappings readable" on public.saved_column_mappings
  for select to authenticated using (auth.uid() = user_id);

create policy "own run set column mappings readable" on public.run_set_column_mappings
  for select to authenticated using (
    exists (select 1 from public.runs r where r.id = run_set_column_mappings.run_id and r.user_id = auth.uid())
  );

grant select, insert, update, delete on public.saved_column_mappings to service_role;
grant select, insert, update, delete on public.run_set_column_mappings to service_role;
grant select on public.saved_column_mappings to authenticated;
grant select on public.run_set_column_mappings to authenticated;
