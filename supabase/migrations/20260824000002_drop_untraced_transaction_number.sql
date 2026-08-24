-- Drops the transaction_number column added two migrations ago.
--
-- It was added on the understanding that a trade receivables default is identified
-- by an account plus a transaction. It is not: the identifier is the customer
-- number alone, which the existing account column already holds. The column could
-- therefore only ever be blank, and an unexplained not-null column in a table an
-- auditor reads is worse than none.
--
-- save_run_completion is restated in full, copied back from
-- 20260731000000_normalize_run_results.sql, because a function body cannot be
-- altered one statement at a time. The signature is unchanged, so this replaces it
-- in place and keeps the existing grant - a new parameter would create an overload
-- instead, and PostgREST would then answer /rpc/save_run_completion with 300
-- Multiple Choices. The grant is restated anyway so this file also works on a
-- database that never had the function.
--
-- Written to be re-runnable, for the reason 20260803000000_column_mappings.sql
-- sets out: this schema has been applied by hand through the SQL editor as well as
-- by the CLI.

alter table public.run_set_untraced_rows drop column if exists transaction_number;

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
