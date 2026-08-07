-- Records which slot each uploaded input file was picked for, and the name it
-- was picked under, so a past run's inputs can be listed back onto the Files
-- step. See docs/superpowers/specs/2026-08-05-run-again-edit-inputs-design.md.
--
-- Re-runnable, like every migration here: this schema has been applied by hand
-- through the SQL editor as well as by the CLI (see docs/deployment.md), so
-- whether a database has already seen this file is not knowable from the
-- migration history alone.
--
-- Both columns are nullable on purpose. Rows written before this migration
-- cannot have them, and readers fall back to the canonical file name and a
-- role derived from it, so existing history keeps working.

alter table public.run_files add column if not exists role text;
alter table public.run_files add column if not exists original_name text;

-- input rows only; outputs have no slot
alter table public.run_files drop constraint if exists run_files_role_check;
alter table public.run_files add constraint run_files_role_check
  check (role is null or role in ('exposure', 'writeoff', 'debug', 'scenario'));

-- the inputs endpoint reads one run's input rows and nothing else
create index if not exists run_files_run_kind_idx on public.run_files (run_id, kind);
