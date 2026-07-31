-- Replaces the four opaque jsonb columns on public.runs (status, log, result,
-- analysis_payload) with real tables. No production data exists yet, so this
-- drops rather than migrates.
--
-- result.dashboard_sets[] and analysis_payload.sets[] were two more
-- serializations of the same per-set aggregate as result.sets[]
-- (ReconciliationSummary) - they collapse into one run_set_results table plus
-- shared child tables rather than three parallel table families.

-- ---------------------------------------------------------------------------
-- Lookup tables
-- ---------------------------------------------------------------------------

create table public.run_status (
  id           smallint primary key,
  code         text not null unique,
  description  text not null
);

insert into public.run_status (id, code, description) values
  (1, 'ready',       'Discovered, not yet started'),
  (2, 'running',     'Engine is processing the run'),
  (3, 'done',        'Finished successfully'),
  (4, 'error',       'Finished with an error'),
  (5, 'interrupted', 'Killed by a server restart before it finished');

create table public.log_types (
  id           smallint primary key,
  code         text not null unique,
  description  text not null
);

insert into public.log_types (id, code, description) values
  (1, 'ok',   'Step completed as expected'),
  (2, 'warn', 'Step completed, but something was missing, failed, or did not reconcile'),
  (3, 'info', 'Informational note, no outcome implied'),
  (4, 'head', 'Section heading in the log, not a step outcome'),
  (5, 'tool', 'A tool invocation note (console-mode only)');

-- ---------------------------------------------------------------------------
-- runs: replace status text+check with a lookup FK; drop the jsonb blobs
-- ---------------------------------------------------------------------------

alter table public.runs add column status_id smallint references public.run_status(id) default 1;
update public.runs set status_id = (select id from public.run_status where code = public.runs.status);
alter table public.runs alter column status_id set not null;
alter table public.runs drop column status;
alter table public.runs drop column log;
alter table public.runs drop column result;
alter table public.runs drop column analysis_payload;

-- ---------------------------------------------------------------------------
-- run_results: 1:1 completion-only extension of runs (filenames + the LLM's
-- markdown). elapsed_seconds is not stored - it is finished_at - started_at.
-- ---------------------------------------------------------------------------

create table public.run_results (
  run_id              uuid primary key references public.runs(id) on delete cascade,
  workbook_filename   text,
  dashboard_filename  text,
  memo_filename       text,
  analysis_markdown   text
);

-- ---------------------------------------------------------------------------
-- logs
-- ---------------------------------------------------------------------------

create table public.logs (
  id           bigint generated always as identity primary key,
  run_id       uuid not null references public.runs(id) on delete cascade,
  user_id      uuid not null references auth.users(id) on delete cascade,
  seq          int not null,
  occurred_at  timestamptz not null,
  type_id      smallint not null references public.log_types(id),
  message      text not null,
  unique (run_id, seq)
);

create index logs_run_seq_idx on public.logs (run_id, seq);

-- ---------------------------------------------------------------------------
-- run_set_results: one row per run x set. Merges ReconciliationSummary
-- (result.sets[]) with the scalar fields DashboardSet and analysis_payload
-- duplicated under different names. Presentation-only "_fmt" strings are
-- dropped - the client recomputes them from the numeric columns.
-- ---------------------------------------------------------------------------

create table public.run_set_results (
  id                                bigint generated always as identity primary key,
  run_id                            uuid not null references public.runs(id) on delete cascade,
  user_id                           uuid not null references auth.users(id) on delete cascade,
  set_key                           text not null,
  label                             text not null,
  "window"                          text not null,
  total_defaults                    int not null,
  total_exposure                    numeric not null,
  traced_writeoff                   int not null,
  traced_ifrs9                      int not null,
  traced_total                      int not null,
  untraced_total                    int not null,
  traced_exposure                   numeric not null,
  untraced_exposure                 numeric not null,
  trace_rate                        double precision not null,
  ifrs9_key_overlap                 int not null,
  ifrs9_rows                        int not null,
  ifrs9_file                        text not null,
  wo_not_default_total              int not null,
  wo_not_default_amount             numeric not null,
  wo_in_window                      int not null,
  wo_in_window_amount               numeric not null,
  wo_pre_window                     int not null,
  wo_post_window                    int not null,
  scored_in_writeoff                int not null,
  scored_in_ifrs9                   int,
  wo_in_window_bucket4              int not null,
  wo_in_window_bucket4_amount       numeric not null,
  wo_in_window_bucket4_pct          double precision not null,
  mig_validation                    text not null,
  mig_validation_max_diff           int,
  scored_distinct                   int not null,
  writeoff_distinct                 int not null,
  ifrs9_distinct                    int not null,
  defaults_distinct                 int not null,
  default_pct_of_scored             double precision,
  pd_rows                           int not null,
  untraced_fully_recovered          int not null,
  untraced_fully_recovered_amount   numeric not null,
  unique (run_id, set_key)
);

create index run_set_results_run_idx on public.run_set_results (run_id);

create table public.run_set_migration_cells (
  id                  bigint generated always as identity primary key,
  run_set_result_id   bigint not null references public.run_set_results(id) on delete cascade,
  month_label         text not null,
  from_bucket         smallint not null,
  to_bucket           smallint not null,
  count               int not null,
  unique (run_set_result_id, month_label, from_bucket, to_bucket)
);

create table public.run_set_monthly_totals (
  id                  bigint generated always as identity primary key,
  run_set_result_id   bigint not null references public.run_set_results(id) on delete cascade,
  month_label         text not null,
  total               int not null,
  position            int not null,
  unique (run_set_result_id, position)
);

create table public.run_set_hazard_matrix (
  id                  bigint generated always as identity primary key,
  run_set_result_id   bigint not null references public.run_set_results(id) on delete cascade,
  row_idx             smallint not null,
  col_idx             smallint not null,
  value               double precision not null,
  unique (run_set_result_id, row_idx, col_idx)
);

create table public.run_set_cohort_matrix (
  id                  bigint generated always as identity primary key,
  run_set_result_id   bigint not null references public.run_set_results(id) on delete cascade,
  row_idx             smallint not null,
  col_idx             smallint not null,
  value               double precision not null,
  unique (run_set_result_id, row_idx, col_idx)
);

-- Sparse (event_name, term_days, value) facts rather than the padded/aligned
-- array the UI renders - that alignment is a rendering concern, not a fact.
create table public.run_set_lgd_points (
  id                  bigint generated always as identity primary key,
  run_set_result_id   bigint not null references public.run_set_results(id) on delete cascade,
  event_name          text not null,
  term_days           int not null,
  value               double precision,
  unique (run_set_result_id, event_name, term_days)
);

-- Serves both DashboardSet.LastBuckets and analysis_payload's
-- in_window_last_bucket_hist; "share" is derived client-side.
create table public.run_set_last_bucket_rows (
  id                  bigint generated always as identity primary key,
  run_set_result_id   bigint not null references public.run_set_results(id) on delete cascade,
  bucket              text not null,
  accounts            int not null,
  amount              numeric not null,
  position            int not null,
  unique (run_set_result_id, position)
);

create table public.run_set_untraced_rows (
  id                  bigint generated always as identity primary key,
  run_set_result_id   bigint not null references public.run_set_results(id) on delete cascade,
  account             text not null,
  cohort_date         text not null,
  rating              text not null,
  amount              numeric not null,
  position            int not null,
  unique (run_set_result_id, position)
);

create table public.run_set_wo_exception_rows (
  id                  bigint generated always as identity primary key,
  run_set_result_id   bigint not null references public.run_set_results(id) on delete cascade,
  account             text not null,
  amount              numeric not null,
  wo_date             date,
  "window"            text not null,
  last_bucket         text not null,
  position            int not null,
  unique (run_set_result_id, position)
);

-- EngineScenario.Params is genuinely open-ended per scenario type - the one
-- place a narrow jsonb column stays, since the data is dynamic engine
-- metadata rather than a structured business object.
create table public.run_set_engine_params (
  id                  bigint generated always as identity primary key,
  run_set_result_id   bigint not null references public.run_set_results(id) on delete cascade,
  param_key           text not null,
  param_value         jsonb not null,
  unique (run_set_result_id, param_key)
);

-- ---------------------------------------------------------------------------
-- Run-level arrays
-- ---------------------------------------------------------------------------

-- Covers memo/workbook/dashboard (run_set_result_id null) and each set's own
-- output CSVs (run_set_result_id set) in one list, in OutputFiles.Describe's
-- original order.
create table public.run_output_files (
  id                  bigint generated always as identity primary key,
  run_id              uuid not null references public.runs(id) on delete cascade,
  user_id             uuid not null references auth.users(id) on delete cascade,
  run_set_result_id   bigint references public.run_set_results(id) on delete cascade,
  name                text not null,
  bytes               bigint not null,
  position            int not null,
  unique (run_id, position)
);

create table public.run_commentary_lines (
  id        bigint generated always as identity primary key,
  run_id    uuid not null references public.runs(id) on delete cascade,
  user_id   uuid not null references auth.users(id) on delete cascade,
  line      text not null,
  position  int not null,
  unique (run_id, position)
);

-- ---------------------------------------------------------------------------
-- RLS - defense in depth only, as for the tables in the first migration. The
-- server writes as service_role, which bypasses all of this; a leaked anon
-- key should still read nothing.
-- ---------------------------------------------------------------------------

alter table public.run_results enable row level security;
alter table public.logs enable row level security;
alter table public.run_set_results enable row level security;
alter table public.run_set_migration_cells enable row level security;
alter table public.run_set_monthly_totals enable row level security;
alter table public.run_set_hazard_matrix enable row level security;
alter table public.run_set_cohort_matrix enable row level security;
alter table public.run_set_lgd_points enable row level security;
alter table public.run_set_last_bucket_rows enable row level security;
alter table public.run_set_untraced_rows enable row level security;
alter table public.run_set_wo_exception_rows enable row level security;
alter table public.run_set_engine_params enable row level security;
alter table public.run_output_files enable row level security;
alter table public.run_commentary_lines enable row level security;

-- Tables with a direct user_id column.
create policy "own logs readable" on public.logs
  for select to authenticated using (auth.uid() = user_id);

create policy "own run set results readable" on public.run_set_results
  for select to authenticated using (auth.uid() = user_id);

create policy "own output files readable" on public.run_output_files
  for select to authenticated using (auth.uid() = user_id);

create policy "own commentary readable" on public.run_commentary_lines
  for select to authenticated using (auth.uid() = user_id);

-- run_results has no user_id of its own - it is 1:1 with runs.
create policy "own run results readable" on public.run_results
  for select to authenticated using (
    exists (select 1 from public.runs r where r.id = run_results.run_id and r.user_id = auth.uid())
  );

-- Second-level children of run_set_results carry no user_id of their own, to
-- avoid denormalizing it onto every leaf table - join through the parent.
create policy "own migration cells readable" on public.run_set_migration_cells
  for select to authenticated using (
    exists (select 1 from public.run_set_results r where r.id = run_set_migration_cells.run_set_result_id and r.user_id = auth.uid())
  );

create policy "own monthly totals readable" on public.run_set_monthly_totals
  for select to authenticated using (
    exists (select 1 from public.run_set_results r where r.id = run_set_monthly_totals.run_set_result_id and r.user_id = auth.uid())
  );

create policy "own hazard matrix readable" on public.run_set_hazard_matrix
  for select to authenticated using (
    exists (select 1 from public.run_set_results r where r.id = run_set_hazard_matrix.run_set_result_id and r.user_id = auth.uid())
  );

create policy "own cohort matrix readable" on public.run_set_cohort_matrix
  for select to authenticated using (
    exists (select 1 from public.run_set_results r where r.id = run_set_cohort_matrix.run_set_result_id and r.user_id = auth.uid())
  );

create policy "own lgd points readable" on public.run_set_lgd_points
  for select to authenticated using (
    exists (select 1 from public.run_set_results r where r.id = run_set_lgd_points.run_set_result_id and r.user_id = auth.uid())
  );

create policy "own last bucket rows readable" on public.run_set_last_bucket_rows
  for select to authenticated using (
    exists (select 1 from public.run_set_results r where r.id = run_set_last_bucket_rows.run_set_result_id and r.user_id = auth.uid())
  );

create policy "own untraced rows readable" on public.run_set_untraced_rows
  for select to authenticated using (
    exists (select 1 from public.run_set_results r where r.id = run_set_untraced_rows.run_set_result_id and r.user_id = auth.uid())
  );

create policy "own wo exception rows readable" on public.run_set_wo_exception_rows
  for select to authenticated using (
    exists (select 1 from public.run_set_results r where r.id = run_set_wo_exception_rows.run_set_result_id and r.user_id = auth.uid())
  );

create policy "own engine params readable" on public.run_set_engine_params
  for select to authenticated using (
    exists (select 1 from public.run_set_results r where r.id = run_set_engine_params.run_set_result_id and r.user_id = auth.uid())
  );

-- ---------------------------------------------------------------------------
-- Data API grants. Confirmed against a fresh local instance (supabase start)
-- that new tables are NOT auto-exposed to the Data API roles by default -
-- RLS alone is not enough, the roles need the underlying table privilege too.
-- This was missing for the *original* migration's tables as well
-- (runs/run_files/chat_messages carried no explicit grants), so this section
-- covers those too rather than leaving them dependent on a per-project
-- dashboard/legacy default that may not hold everywhere this schema is applied.
-- service_role bypasses RLS and is what the server writes/reads as; authenticated
-- only ever gets SELECT, matching the "own X readable" policies above - it is
-- the defense-in-depth path for a leaked anon-signed token, not a write path.
-- ---------------------------------------------------------------------------

grant select, insert, update, delete on
  public.runs, public.run_files, public.chat_messages,
  public.run_results, public.logs, public.run_set_results,
  public.run_set_migration_cells, public.run_set_monthly_totals,
  public.run_set_hazard_matrix, public.run_set_cohort_matrix,
  public.run_set_lgd_points, public.run_set_last_bucket_rows,
  public.run_set_untraced_rows, public.run_set_wo_exception_rows,
  public.run_set_engine_params, public.run_output_files, public.run_commentary_lines
to service_role;

grant select on
  public.runs, public.run_files, public.chat_messages,
  public.run_results, public.logs, public.run_set_results,
  public.run_set_migration_cells, public.run_set_monthly_totals,
  public.run_set_hazard_matrix, public.run_set_cohort_matrix,
  public.run_set_lgd_points, public.run_set_last_bucket_rows,
  public.run_set_untraced_rows, public.run_set_wo_exception_rows,
  public.run_set_engine_params, public.run_output_files, public.run_commentary_lines
to authenticated;

-- lookup tables: read-only reference data, no RLS needed (nothing user-scoped)
grant select on public.run_status, public.log_types to service_role, authenticated, anon;

-- ---------------------------------------------------------------------------
-- save_run_completion: replaces the single-row PATCH that used to write
-- status/log/result/analysis_payload atomically. Exploding those into ~11
-- tables means a sequence of PostgREST calls would no longer be atomic, and a
-- run's id can be reused on re-run - so this replaces a run's completion data
-- wholesale, inside one transaction, in one HTTP call (POST
-- /rest/v1/rpc/save_run_completion).
-- ---------------------------------------------------------------------------

create or replace function public.save_run_completion(p_run_id uuid, p_user_id uuid, p_payload jsonb)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
  v_set jsonb;
  v_new_id bigint;
begin
  update public.runs
     set status_id = (p_payload->>'status_id')::smallint,
         error = p_payload->>'error',
         finished_at = now()
   where id = p_run_id;

  delete from public.run_results where run_id = p_run_id;
  insert into public.run_results (run_id, workbook_filename, dashboard_filename, memo_filename, analysis_markdown)
  values (
    p_run_id,
    p_payload->'run_results'->>'workbook_filename',
    p_payload->'run_results'->>'dashboard_filename',
    p_payload->'run_results'->>'memo_filename',
    p_payload->'run_results'->>'analysis_markdown'
  );

  delete from public.logs where run_id = p_run_id;
  insert into public.logs (run_id, user_id, seq, occurred_at, type_id, message)
  select p_run_id, p_user_id,
         (x->>'seq')::int, (x->>'occurred_at')::timestamptz, (x->>'type_id')::smallint, x->>'message'
  from jsonb_array_elements(coalesce(p_payload->'logs', '[]'::jsonb)) as x;

  delete from public.run_commentary_lines where run_id = p_run_id;
  insert into public.run_commentary_lines (run_id, user_id, line, position)
  select p_run_id, p_user_id, x->>'line', (x->>'position')::int
  from jsonb_array_elements(coalesce(p_payload->'run_commentary_lines', '[]'::jsonb)) as x;

  -- cascades every run_set_results child table
  delete from public.run_set_results where run_id = p_run_id;

  for v_set in select * from jsonb_array_elements(coalesce(p_payload->'run_set_results', '[]'::jsonb))
  loop
    insert into public.run_set_results (
      run_id, user_id, set_key, label, "window", total_defaults, total_exposure,
      traced_writeoff, traced_ifrs9, traced_total, untraced_total, traced_exposure, untraced_exposure,
      trace_rate, ifrs9_key_overlap, ifrs9_rows, ifrs9_file,
      wo_not_default_total, wo_not_default_amount, wo_in_window, wo_in_window_amount, wo_pre_window, wo_post_window,
      scored_in_writeoff, scored_in_ifrs9, wo_in_window_bucket4, wo_in_window_bucket4_amount, wo_in_window_bucket4_pct,
      mig_validation, mig_validation_max_diff, scored_distinct, writeoff_distinct, ifrs9_distinct, defaults_distinct,
      default_pct_of_scored, pd_rows, untraced_fully_recovered, untraced_fully_recovered_amount
    ) values (
      p_run_id, p_user_id, v_set->>'set_key', v_set->>'label', v_set->>'window',
      (v_set->>'total_defaults')::int, (v_set->>'total_exposure')::numeric,
      (v_set->>'traced_writeoff')::int, (v_set->>'traced_ifrs9')::int, (v_set->>'traced_total')::int,
      (v_set->>'untraced_total')::int, (v_set->>'traced_exposure')::numeric, (v_set->>'untraced_exposure')::numeric,
      (v_set->>'trace_rate')::double precision, (v_set->>'ifrs9_key_overlap')::int, (v_set->>'ifrs9_rows')::int,
      v_set->>'ifrs9_file',
      (v_set->>'wo_not_default_total')::int, (v_set->>'wo_not_default_amount')::numeric, (v_set->>'wo_in_window')::int,
      (v_set->>'wo_in_window_amount')::numeric, (v_set->>'wo_pre_window')::int, (v_set->>'wo_post_window')::int,
      (v_set->>'scored_in_writeoff')::int, (v_set->>'scored_in_ifrs9')::int, (v_set->>'wo_in_window_bucket4')::int,
      (v_set->>'wo_in_window_bucket4_amount')::numeric, (v_set->>'wo_in_window_bucket4_pct')::double precision,
      v_set->>'mig_validation', (v_set->>'mig_validation_max_diff')::int, (v_set->>'scored_distinct')::int,
      (v_set->>'writeoff_distinct')::int, (v_set->>'ifrs9_distinct')::int, (v_set->>'defaults_distinct')::int,
      (v_set->>'default_pct_of_scored')::double precision, (v_set->>'pd_rows')::int,
      (v_set->>'untraced_fully_recovered')::int, (v_set->>'untraced_fully_recovered_amount')::numeric
    )
    returning id into v_new_id;

    insert into public.run_set_migration_cells (run_set_result_id, month_label, from_bucket, to_bucket, count)
    select v_new_id, c->>'month_label', (c->>'from_bucket')::smallint, (c->>'to_bucket')::smallint, (c->>'count')::int
    from jsonb_array_elements(coalesce(v_set->'run_set_migration_cells', '[]'::jsonb)) as c;

    insert into public.run_set_monthly_totals (run_set_result_id, month_label, total, position)
    select v_new_id, c->>'month_label', (c->>'total')::int, (c->>'position')::int
    from jsonb_array_elements(coalesce(v_set->'run_set_monthly_totals', '[]'::jsonb)) as c;

    insert into public.run_set_hazard_matrix (run_set_result_id, row_idx, col_idx, value)
    select v_new_id, (c->>'row_idx')::smallint, (c->>'col_idx')::smallint, (c->>'value')::double precision
    from jsonb_array_elements(coalesce(v_set->'run_set_hazard_matrix', '[]'::jsonb)) as c;

    insert into public.run_set_cohort_matrix (run_set_result_id, row_idx, col_idx, value)
    select v_new_id, (c->>'row_idx')::smallint, (c->>'col_idx')::smallint, (c->>'value')::double precision
    from jsonb_array_elements(coalesce(v_set->'run_set_cohort_matrix', '[]'::jsonb)) as c;

    insert into public.run_set_lgd_points (run_set_result_id, event_name, term_days, value)
    select v_new_id, c->>'event_name', (c->>'term_days')::int, (c->>'value')::double precision
    from jsonb_array_elements(coalesce(v_set->'run_set_lgd_points', '[]'::jsonb)) as c;

    insert into public.run_set_last_bucket_rows (run_set_result_id, bucket, accounts, amount, position)
    select v_new_id, c->>'bucket', (c->>'accounts')::int, (c->>'amount')::numeric, (c->>'position')::int
    from jsonb_array_elements(coalesce(v_set->'run_set_last_bucket_rows', '[]'::jsonb)) as c;

    insert into public.run_set_untraced_rows (run_set_result_id, account, cohort_date, rating, amount, position)
    select v_new_id, c->>'account', c->>'cohort_date', c->>'rating', (c->>'amount')::numeric, (c->>'position')::int
    from jsonb_array_elements(coalesce(v_set->'run_set_untraced_rows', '[]'::jsonb)) as c;

    insert into public.run_set_wo_exception_rows (run_set_result_id, account, amount, wo_date, "window", last_bucket, position)
    select v_new_id, c->>'account', (c->>'amount')::numeric, (c->>'wo_date')::date, c->>'window', c->>'last_bucket',
           (c->>'position')::int
    from jsonb_array_elements(coalesce(v_set->'run_set_wo_exception_rows', '[]'::jsonb)) as c;

    insert into public.run_set_engine_params (run_set_result_id, param_key, param_value)
    select v_new_id, c->>'param_key', c->'param_value'
    from jsonb_array_elements(coalesce(v_set->'run_set_engine_params', '[]'::jsonb)) as c;
  end loop;

  -- output_files entries carry an optional set_key, resolved against the rows
  -- just (re)inserted above; null for run-level files (memo/workbook/dashboard).
  delete from public.run_output_files where run_id = p_run_id;
  insert into public.run_output_files (run_id, user_id, run_set_result_id, name, bytes, position)
  select p_run_id, p_user_id,
         (select r.id from public.run_set_results r where r.run_id = p_run_id and r.set_key = f->>'set_key'),
         f->>'name', (f->>'bytes')::bigint, (f->>'position')::int
  from jsonb_array_elements(coalesce(p_payload->'run_output_files', '[]'::jsonb)) as f;
end;
$$;

grant execute on function public.save_run_completion(uuid, uuid, jsonb) to service_role;
