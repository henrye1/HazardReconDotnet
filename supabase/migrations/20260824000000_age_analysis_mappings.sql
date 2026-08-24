-- Column mappings gain a second dimension: a field may now map to SEVERAL
-- columns, in a chosen order. Only one field does today - the aging buckets of a
-- trade receivables age analysis, which are summed to give the exposure - but the
-- ordering matters for display, so it is stored rather than derived.
--
-- Written to be re-runnable, for the reason 20260803000000_column_mappings.sql
-- sets out: this schema has been applied by hand through the SQL editor as well
-- as by the CLI, so whether a given database has already seen this file is not
-- knowable from the migration history alone.

alter table public.saved_column_mappings add column if not exists ordinal int not null default 0;
alter table public.run_set_column_mappings add column if not exists ordinal int not null default 0;

-- The uniques have to widen to include the ordinal, or a second column for the
-- same field is a conflict rather than a sibling.
--
-- The old constraints' names were auto-generated and truncated to 63 characters,
-- so they are found by their column list rather than guessed at. Dropping by
-- shape also makes this block naturally repeatable: the second run finds the new
-- constraint already in place and the lookup returns nothing.
do $$
declare
  v_name text;
begin
  select con.conname into v_name
    from pg_constraint con
    join pg_class rel on rel.oid = con.conrelid
    join pg_namespace nsp on nsp.oid = rel.relnamespace
   where nsp.nspname = 'public'
     and rel.relname = 'saved_column_mappings'
     and con.contype = 'u'
     and (select array_agg(att.attname::text order by att.attname)
            from unnest(con.conkey) k
            join pg_attribute att on att.attrelid = con.conrelid and att.attnum = k)
         = array['column_signature','field_name','file_kind','user_id'];

  if v_name is not null then
    execute format('alter table public.saved_column_mappings drop constraint %I', v_name);
  end if;

  if not exists (
    select 1 from pg_constraint where conname = 'saved_column_mappings_field_ordinal_key'
  ) then
    alter table public.saved_column_mappings
      add constraint saved_column_mappings_field_ordinal_key
      unique (user_id, file_kind, column_signature, field_name, ordinal);
  end if;
end $$;

do $$
declare
  v_name text;
begin
  select con.conname into v_name
    from pg_constraint con
    join pg_class rel on rel.oid = con.conrelid
    join pg_namespace nsp on nsp.oid = rel.relnamespace
   where nsp.nspname = 'public'
     and rel.relname = 'run_set_column_mappings'
     and con.contype = 'u'
     and (select array_agg(att.attname::text order by att.attname)
            from unnest(con.conkey) k
            join pg_attribute att on att.attrelid = con.conrelid and att.attnum = k)
         = array['field_name','file_kind','run_id','set_key'];

  if v_name is not null then
    execute format('alter table public.run_set_column_mappings drop constraint %I', v_name);
  end if;

  if not exists (
    select 1 from pg_constraint where conname = 'run_set_column_mappings_field_ordinal_key'
  ) then
    alter table public.run_set_column_mappings
      add constraint run_set_column_mappings_field_ordinal_key
      unique (run_id, set_key, file_kind, field_name, ordinal);
  end if;
end $$;

-- An age analysis is its own file kind, not an exposure file with different
-- fields. Its field set has no AmountOutstanding, so serving it a saved exposure
-- mapping would offer a column for a field that does not exist - and the audit
-- row should record which field set the run was actually mapped against.
--
-- Unlike the uniques above, a column CHECK's name is predictable, so this pair
-- stays repeatable by dropping and re-adding under the same name.
alter table public.saved_column_mappings drop constraint if exists saved_column_mappings_file_kind_check;
alter table public.saved_column_mappings
  add constraint saved_column_mappings_file_kind_check
  check (file_kind in ('writeoff', 'exposure', 'age_analysis'));

alter table public.run_set_column_mappings drop constraint if exists run_set_column_mappings_file_kind_check;
alter table public.run_set_column_mappings
  add constraint run_set_column_mappings_file_kind_check
  check (file_kind in ('writeoff', 'exposure', 'age_analysis'));
