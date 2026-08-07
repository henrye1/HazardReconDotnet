-- Records the confirmed "first row is a header" reading alongside each run's
-- column mapping, so reopening a run (see
-- docs/superpowers/specs/2026-08-05-run-again-edit-inputs-design.md, addendum
-- 2026-08-07) can recompute the same column signature instead of falling back
-- to a fresh sniff that may disagree with what the user actually confirmed.
--
-- Re-runnable, like every migration here: this schema has been applied by hand
-- through the SQL editor as well as by the CLI (see docs/deployment.md), so
-- whether a database has already seen this file is not knowable from the
-- migration history alone.
--
-- Nullable on purpose: rows written before this migration, and rows for a file
-- where the client never overrode the sniffer's guess, both mean "use the
-- sniffer's own reading" rather than asserting one.

alter table public.run_set_column_mappings add column if not exists has_headers boolean;
