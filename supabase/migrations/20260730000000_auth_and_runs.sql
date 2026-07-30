-- Runs, their files, and their chat history. All strictly per-user.
create table public.runs (
  id                uuid primary key default gen_random_uuid(),
  user_id           uuid not null references auth.users(id) on delete cascade,
  status            text not null default 'ready'
                      check (status in ('ready','running','done','error','interrupted')),
  model_id          text,
  set_labels        jsonb not null default '[]'::jsonb,
  log               jsonb not null default '[]'::jsonb,
  result            jsonb,
  analysis_payload  jsonb,
  error             text,
  created_at        timestamptz not null default now(),
  started_at        timestamptz,
  finished_at       timestamptz,
  inputs_purged_at  timestamptz
);

create index runs_user_created_idx on public.runs (user_id, created_at desc);

create table public.run_files (
  id             uuid primary key default gen_random_uuid(),
  run_id         uuid not null references public.runs(id) on delete cascade,
  user_id        uuid not null references auth.users(id) on delete cascade,
  kind           text not null check (kind in ('input','output')),
  set_key        text,
  relative_path  text not null,
  storage_path   text not null,
  size_bytes     bigint not null,
  created_at     timestamptz not null default now()
);

create index run_files_run_idx on public.run_files (run_id);

create table public.chat_messages (
  id            uuid primary key default gen_random_uuid(),
  run_id        uuid not null references public.runs(id) on delete cascade,
  user_id       uuid not null references auth.users(id) on delete cascade,
  role          text not null check (role in ('user','assistant')),
  content       text not null,
  content_html  text,
  created_at    timestamptz not null default now()
);

create index chat_messages_run_created_idx on public.chat_messages (run_id, created_at);

-- Defense in depth only. The server writes as service_role, which bypasses RLS.
-- These policies exist so a leaked anon key reads nothing, not as the primary
-- enforcement mechanism -- that is the server filtering on the token's sub claim.
alter table public.runs enable row level security;
alter table public.run_files enable row level security;
alter table public.chat_messages enable row level security;

create policy "own runs readable" on public.runs
  for select to authenticated using (auth.uid() = user_id);

create policy "own run files readable" on public.run_files
  for select to authenticated using (auth.uid() = user_id);

create policy "own chat readable" on public.chat_messages
  for select to authenticated using (auth.uid() = user_id);
