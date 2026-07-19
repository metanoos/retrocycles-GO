-- Light Runners — Supabase schema (spec §12.5, §22, §23).
-- PostgreSQL + PostGIS. Apply with: psql or the Supabase SQL editor.
-- Requires "anonymous sign-ins" enabled on the project (spec §12.2).

create extension if not exists postgis;

-- ─────────────────────────────────────────────────────────────────────────────
-- players — one row per anonymous account, auto-created on sign-up.
-- ─────────────────────────────────────────────────────────────────────────────
create table if not exists public.players (
    id              uuid primary key references auth.users (id) on delete cascade,
    display_name    text not null default 'Runner_anon',
    beacon_form     int  not null default 0,
    total_distance  double precision not null default 0,   -- metres, lifetime
    total_runs      int  not null default 0,
    longest_run     double precision not null default 0,   -- metres
    level           int  not null default 0,
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now()
);

-- Auto-create the row on anonymous sign-in (spec §12.5).
create or replace function public.handle_new_user()
returns trigger
language plpgsql
security definer set search_path = public
as $$
begin
    insert into public.players (id, display_name)
    values (new.id, 'Runner_' || substr(replace(new.id::text, '-', ''), 1, 6))
    on conflict (id) do nothing;
    return new;
end;
$$;

drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created
    after insert on auth.users
    for each row execute function public.handle_new_user();

-- ─────────────────────────────────────────────────────────────────────────────
-- trails — one per run.
-- ─────────────────────────────────────────────────────────────────────────────
create table if not exists public.trails (
    id              uuid primary key default gen_random_uuid(),
    player_id       uuid not null references public.players (id) on delete cascade,
    room_id         text,
    beacon_form     int not null default 0,
    color_rgb       text,                                   -- hex RRGGBB
    total_distance  double precision not null default 0,
    point_count     int not null default 0,
    crashed         boolean not null default false,
    crash_cause     text,                                   -- causing player id (spec: crash_cause_id; TEXT because offline ids aren't UUIDs)
    start_geo       geography(pointz, 4326),
    end_geo         geography(pointz, 4326),
    started_at      timestamptz not null default now(),
    ended_at        timestamptz
);

create index if not exists trails_start_geo_gist on public.trails using gist (start_geo);
create index if not exists trails_player_idx     on public.trails (player_id);
create index if not exists trails_started_idx    on public.trails (started_at); -- retention sweep (§23)

-- ─────────────────────────────────────────────────────────────────────────────
-- trail_points — the geometry. Generated stored geography column (spec §12.5).
-- ─────────────────────────────────────────────────────────────────────────────
create table if not exists public.trail_points (
    id              bigint generated always as identity primary key,
    trail_id        uuid not null references public.trails (id) on delete cascade,
    lat             double precision not null,
    lon             double precision not null,
    alt             double precision not null default 0,
    sequence_index  int not null,
    geo             geography(pointz, 4326)
                    generated always as (st_setsrid(st_makepoint(lon, lat, alt), 4326)::geography) stored,
    recorded_at     timestamptz not null default now()
);

create index if not exists trail_points_geo_gist on public.trail_points using gist (geo);
create index if not exists trail_points_seq_idx  on public.trail_points (trail_id, sequence_index);

-- ─────────────────────────────────────────────────────────────────────────────
-- run_history — Lumen tally per run; survives the geometry sweep (§23).
-- NOTE: this is the post-migration shape (Track E / decision E). The deprecated
-- score_* columns are gone; lumens INT replaces them. The lumen-scoreboard
-- migration section near the end of this file performs the same column changes
-- idempotently for already-deployed DBs (DROP COLUMN IF EXISTS / ADD COLUMN).
-- ─────────────────────────────────────────────────────────────────────────────
create table if not exists public.run_history (
    id              bigint generated always as identity primary key,
    player_id       uuid not null references public.players (id) on delete cascade,
    distance_m      double precision not null,
    duration_s      double precision not null,
    avg_speed       double precision not null,
    lumens          int not null default 0,           -- decision E: player's Lumen tally for the run
    beacon_form     int not null default 0,
    crashed         boolean not null default false,
    recorded_at     timestamptz not null default now()
);

create index if not exists run_history_leaderboard_idx on public.run_history (lumens desc, recorded_at desc);
create index if not exists run_history_player_idx      on public.run_history (player_id);

-- rejected_runs — implausible submissions land here, not in run_history (§22).
create table if not exists public.rejected_runs (
    id          bigint generated always as identity primary key,
    player_id   uuid,
    payload     jsonb not null,
    reason      text not null,
    recorded_at timestamptz not null default now()
);

-- ─────────────────────────────────────────────────────────────────────────────
-- lobby_rooms — one per active friend match (spec §8.5 / §12.5).
-- ─────────────────────────────────────────────────────────────────────────────
create table if not exists public.lobby_rooms (
    code        text primary key,
    host_id     uuid not null references public.players (id) on delete cascade,
    room_name   text not null,
    region      text,
    members     jsonb not null default '[]'::jsonb,   -- [{user_id, display_name}]
    status      text not null default 'open',         -- open | racing | closed
    started_at  timestamptz,                          -- set by start_lobby_race (§8.5)
    created_at  timestamptz not null default now(),
    expires_at  timestamptz not null default now() + interval '30 minutes'
);

create index if not exists lobby_rooms_expires_idx on public.lobby_rooms (expires_at);
create index if not exists lobby_rooms_host_idx    on public.lobby_rooms (host_id);

-- lobby_join_attempts — rate-limit ledger for join_lobby (pitfall #15, §12.5).
create table if not exists public.lobby_join_attempts (
    id           bigint generated always as identity primary key,
    user_id      uuid not null,
    code         text,
    success      boolean not null default false,
    attempted_at timestamptz not null default now()
);

create index if not exists lobby_join_attempts_user_idx on public.lobby_join_attempts (user_id, attempted_at);

-- ─────────────────────────────────────────────────────────────────────────────
-- RLS (spec §12.5): reads public, writes owner-only; lobby writes go through the
-- SECURITY DEFINER RPCs so the row policies stay simple.
-- ─────────────────────────────────────────────────────────────────────────────
alter table public.players             enable row level security;
alter table public.trails              enable row level security;
alter table public.trail_points        enable row level security;
alter table public.run_history         enable row level security;
alter table public.rejected_runs       enable row level security;
alter table public.lobby_rooms         enable row level security;
alter table public.lobby_join_attempts enable row level security;

drop policy if exists players_read  on public.players;
drop policy if exists players_write on public.players;
create policy players_read  on public.players for select using (true);
create policy players_write on public.players for all
    using (auth.uid() = id) with check (auth.uid() = id);

drop policy if exists trails_read   on public.trails;
drop policy if exists trails_insert on public.trails;
drop policy if exists trails_update on public.trails;
create policy trails_read   on public.trails for select using (true);
create policy trails_insert on public.trails for insert with check (auth.uid() = player_id);
create policy trails_update on public.trails for update
    using (auth.uid() = player_id) with check (auth.uid() = player_id);

drop policy if exists trail_points_read   on public.trail_points;
drop policy if exists trail_points_insert on public.trail_points;
create policy trail_points_read   on public.trail_points for select using (true);
create policy trail_points_insert on public.trail_points for insert with check (
    exists (select 1 from public.trails t where t.id = trail_id and t.player_id = auth.uid())
);

drop policy if exists run_history_read on public.run_history;
create policy run_history_read on public.run_history for select using (true);
-- inserts only via the record_run RPC (security definer)

-- rejected_runs: no client policies at all — RPC-only.

drop policy if exists lobby_rooms_read on public.lobby_rooms;
create policy lobby_rooms_read on public.lobby_rooms for select using (true);
-- writes only via the lobby RPCs (security definer)

-- lobby_join_attempts: no client policies — RPC-only.

-- Realtime publication includes trails (spec §12.5).
do $$
begin
    if exists (select 1 from pg_publication where pubname = 'supabase_realtime') then
        begin
            alter publication supabase_realtime add table public.trails;
        exception when duplicate_object then null;
        end;
    end if;
end $$;

-- ─────────────────────────────────────────────────────────────────────────────
-- Player stats / scoring RPCs
-- ─────────────────────────────────────────────────────────────────────────────

-- Level formula (spec §12.5, pitfall #21): level = floor(sqrt(km)).
create or replace function public.compute_level(total_distance_m double precision)
returns int
language sql immutable
as $$
    select floor(sqrt(greatest(total_distance_m, 0) / 1000.0))::int;
$$;

create or replace function public.update_player_stats(
    p_player_id uuid,
    p_distance_m double precision
)
returns void
language plpgsql security definer set search_path = public
as $$
begin
    update public.players
    set total_distance = total_distance + greatest(p_distance_m, 0),
        total_runs     = total_runs + 1,
        longest_run    = greatest(longest_run, p_distance_m),
        level          = public.compute_level(total_distance + greatest(p_distance_m, 0)),
        updated_at     = now()
    where id = p_player_id;
end;
$$;

-- record_run — validates plausibility (spec §22) then inserts history + updates stats.
-- Post-migration signature (Track E / decision E): takes p_lumens INT instead of
-- the deprecated score_* params. Rejected runs return success to the client
-- (don't teach the prober) but land in rejected_runs for audit.
--
-- Idempotency note: CREATE OR REPLACE can't change a function's parameter list,
-- so we DROP the OLD signature first (no-op if it doesn't exist, e.g. on a
-- fresh DB or an already-migrated DB). The new signature is then (re)created.
drop function if exists public.record_run(
    double precision, double precision, double precision,
    int, int, int, int, int, int, boolean);
create or replace function public.record_run(
    p_distance_m double precision,
    p_duration_s double precision,
    p_avg_speed double precision,
    p_lumens int,
    p_beacon_form int,
    p_crashed boolean
)
returns void
language plpgsql security definer set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
    v_reason text := null;
begin
    if v_uid is null then
        raise exception 'not_authenticated';
    end if;

    -- Physical-plausibility floor (spec §22).
    if p_avg_speed > 12.0 then v_reason := 'avg_speed';
    elsif p_distance_m > 100000.0 then v_reason := 'distance';
    elsif p_duration_s < 10.0 and p_distance_m > 100.0 then v_reason := 'teleport';
    elsif p_lumens < 0 then v_reason := 'lumens_negative';
    end if;

    if v_reason is not null then
        insert into public.rejected_runs (player_id, payload, reason)
        values (v_uid, jsonb_build_object(
            'distance_m', p_distance_m, 'duration_s', p_duration_s,
            'avg_speed', p_avg_speed, 'lumens', p_lumens), v_reason);
        return; -- silent success (spec §22)
    end if;

    insert into public.run_history (
        player_id, distance_m, duration_s, avg_speed,
        lumens, beacon_form, crashed)
    values (
        v_uid, p_distance_m, p_duration_s, p_avg_speed,
        p_lumens, p_beacon_form, p_crashed);

    perform public.update_player_stats(v_uid, p_distance_m);
end;
$$;

-- get_nearby_trails — flat rows (client groups by trail_id). Serves only the last
-- 24 h (spec §23) and caps result size.
create or replace function public.get_nearby_trails(
    center_lat double precision,
    center_lon double precision,
    radius_m double precision,
    max_trails int default 50
)
returns table (
    trail_id uuid,
    color_rgb text,
    lat double precision,
    lon double precision,
    alt double precision,
    sequence_index int
)
language sql stable security definer set search_path = public
as $$
    with nearby as (
        select t.id, t.color_rgb
        from public.trails t
        where t.started_at > now() - interval '24 hours'
          and t.start_geo is not null
          and st_dwithin(
                t.start_geo,
                st_setsrid(st_makepoint(center_lon, center_lat), 4326)::geography,
                radius_m)
        order by t.started_at desc
        limit least(max_trails, 50)
    )
    select n.id, n.color_rgb, p.lat, p.lon, p.alt, p.sequence_index
    from nearby n
    join public.trail_points p on p.trail_id = n.id
    order by n.id, p.sequence_index;
$$;

-- Leaderboard RPCs (Track E): return best_lumens instead of best_score. The
-- return-column rename needs a DROP first (Postgres rejects CREATE OR REPLACE
-- that changes a RETURNS TABLE shape), so each is guarded by DROP IF EXISTS —
-- no-op on a fresh or already-migrated DB.
drop function if exists public.get_global_leaderboard(int);
create or replace function public.get_global_leaderboard(max_rows int default 20)
returns table (player_id uuid, display_name text, best_lumens int, recorded_at timestamptz)
language sql stable security definer set search_path = public
as $$
    select r.player_id, pl.display_name, max(r.lumens) as best_lumens, max(r.recorded_at)
    from public.run_history r
    join public.players pl on pl.id = r.player_id
    group by r.player_id, pl.display_name
    order by best_lumens desc
    limit least(max_rows, 100);
$$;

drop function if exists public.get_nearby_leaderboard(double precision, double precision, double precision, int);
create or replace function public.get_nearby_leaderboard(
    center_lat double precision,
    center_lon double precision,
    radius_m double precision,
    max_rows int default 20
)
returns table (player_id uuid, display_name text, best_lumens int)
language sql stable security definer set search_path = public
as $$
    select r.player_id, pl.display_name, max(r.lumens) as best_lumens
    from public.run_history r
    join public.players pl on pl.id = r.player_id
    join public.trails t on t.player_id = r.player_id
    where t.start_geo is not null
      and st_dwithin(
            t.start_geo,
            st_setsrid(st_makepoint(center_lon, center_lat), 4326)::geography,
            radius_m)
    group by r.player_id, pl.display_name
    order by best_lumens desc
    limit least(max_rows, 100);
$$;

drop function if exists public.get_player_best(uuid);
create or replace function public.get_player_best(p_player_id uuid)
returns table (best_lumens int, best_distance double precision, total_runs int)
language sql stable security definer set search_path = public
as $$
    select
        coalesce(max(r.lumens), 0),
        coalesce(max(r.distance_m), 0),
        count(*)::int
    from public.run_history r
    where r.player_id = p_player_id;
$$;

-- ─────────────────────────────────────────────────────────────────────────────
-- Friend-match RPCs (spec §8.5 / §12.5), all SECURITY DEFINER.
-- ─────────────────────────────────────────────────────────────────────────────

create or replace function public.sweep_expired_lobbies()
returns void
language sql security definer set search_path = public
as $$
    delete from public.lobby_rooms where expires_at < now();
    delete from public.lobby_join_attempts where attempted_at < now() - interval '1 hour';
$$;

-- create_lobby — mints a unique code, inserts the row with the host as sole member.
-- Lazy-sweeps expired rows as a side effect (spec §12.5).
create or replace function public.create_lobby(region text default null)
returns table (
    code text, room_name text, host_id uuid, status text,
    started_at timestamptz, expires_at timestamptz, members jsonb
)
language plpgsql security definer set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
    v_name text;
    v_code text;
    v_alphabet text := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    v_tries int := 0;
begin
    if v_uid is null then raise exception 'not_authenticated'; end if;

    perform public.sweep_expired_lobbies();

    select display_name into v_name from public.players where id = v_uid;
    if v_name is null then raise exception 'not_found'; end if;

    -- A player hosts at most one lobby; re-hosting closes the previous one.
    delete from public.lobby_rooms l where l.host_id = v_uid;

    loop
        v_tries := v_tries + 1;
        v_code := '';
        for i in 1..6 loop
            v_code := v_code || substr(v_alphabet, 1 + floor(random() * length(v_alphabet))::int, 1);
        end loop;
        exit when not exists (select 1 from public.lobby_rooms l where l.code = v_code);
        if v_tries > 20 then raise exception 'code_mint_failed'; end if;
    end loop;

    return query
    insert into public.lobby_rooms (code, host_id, room_name, region, members, status, expires_at)
    values (
        v_code, v_uid, 'party_' || v_code, region,
        jsonb_build_array(jsonb_build_object('user_id', v_uid, 'display_name', v_name)),
        'open', now() + interval '30 minutes')
    returning lobby_rooms.code, lobby_rooms.room_name, lobby_rooms.host_id,
              lobby_rooms.status, lobby_rooms.started_at, lobby_rooms.expires_at,
              lobby_rooms.members;
end;
$$;

-- join_lobby — rate-limited (10/min per uid, pitfall #15); appends the caller.
create or replace function public.join_lobby(lobby_code text)
returns table (
    code text, room_name text, host_id uuid, status text,
    started_at timestamptz, expires_at timestamptz, members jsonb
)
language plpgsql security definer set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
    v_name text;
    v_row public.lobby_rooms%rowtype;
    v_count int;
    v_max_members int := 8;
begin
    if v_uid is null then raise exception 'not_authenticated'; end if;

    select count(*) into v_count
    from public.lobby_join_attempts a
    where a.user_id = v_uid and a.attempted_at > now() - interval '1 minute';
    if v_count >= 10 then raise exception 'rate_limited'; end if;

    insert into public.lobby_join_attempts (user_id, code, success)
    values (v_uid, upper(lobby_code), false);

    select * into v_row from public.lobby_rooms l where l.code = upper(lobby_code);
    if v_row.code is null then raise exception 'not_found'; end if;
    if v_row.expires_at < now() then raise exception 'lobby_expired'; end if;
    if v_row.status = 'closed' then raise exception 'lobby_closed'; end if;
    if jsonb_array_length(v_row.members) >= v_max_members
        and not (v_row.members @> jsonb_build_array(jsonb_build_object('user_id', v_uid)))
    then raise exception 'lobby_full'; end if;

    select display_name into v_name from public.players where id = v_uid;

    -- Append if not already a member (idempotent join).
    if not exists (
        select 1 from jsonb_array_elements(v_row.members) m
        where m->>'user_id' = v_uid::text)
    then
        update public.lobby_rooms l
        set members = l.members || jsonb_build_array(
                jsonb_build_object('user_id', v_uid, 'display_name', coalesce(v_name, 'Runner')))
        where l.code = v_row.code;
    end if;

    update public.lobby_join_attempts a set success = true
    where a.id = (select max(b.id) from public.lobby_join_attempts b where b.user_id = v_uid);

    return query
    select l.code, l.room_name, l.host_id, l.status, l.started_at, l.expires_at, l.members
    from public.lobby_rooms l where l.code = upper(lobby_code);
end;
$$;

-- leave_lobby — removes the caller; host leaving promotes the next member or closes
-- the lobby, atomically (pitfall #12).
create or replace function public.leave_lobby()
returns void
language plpgsql security definer set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
    v_row public.lobby_rooms%rowtype;
    v_new_members jsonb;
    v_next_host uuid;
begin
    if v_uid is null then raise exception 'not_authenticated'; end if;

    for v_row in
        select * from public.lobby_rooms l
        where l.members @> jsonb_build_array(jsonb_build_object('user_id', v_uid))
           or exists (select 1 from jsonb_array_elements(l.members) m where m->>'user_id' = v_uid::text)
        for update
    loop
        select coalesce(jsonb_agg(m), '[]'::jsonb) into v_new_members
        from jsonb_array_elements(v_row.members) m
        where m->>'user_id' <> v_uid::text;

        if jsonb_array_length(v_new_members) = 0 then
            delete from public.lobby_rooms l where l.code = v_row.code;
        elsif v_row.host_id = v_uid then
            v_next_host := ((v_new_members->0)->>'user_id')::uuid;
            update public.lobby_rooms l
            set members = v_new_members, host_id = v_next_host
            where l.code = v_row.code;
        else
            update public.lobby_rooms l
            set members = v_new_members
            where l.code = v_row.code;
        end if;
    end loop;
end;
$$;

-- start_lobby_race — host-only; the joiners' start signal (spec §8.5).
create or replace function public.start_lobby_race()
returns void
language plpgsql security definer set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
    v_code text;
begin
    if v_uid is null then raise exception 'not_authenticated'; end if;
    select l.code into v_code from public.lobby_rooms l
    where l.host_id = v_uid and l.status = 'open';
    if v_code is null then raise exception 'not_host'; end if;

    update public.lobby_rooms l
    set status = 'racing', started_at = now()
    where l.code = v_code;
end;
$$;

-- get_lobby — public read: join lookup + the §8.5 start-signal poll.
create or replace function public.get_lobby(lobby_code text)
returns table (
    code text, room_name text, host_id uuid, status text,
    started_at timestamptz, expires_at timestamptz, members jsonb
)
language sql stable security definer set search_path = public
as $$
    select l.code, l.room_name, l.host_id, l.status, l.started_at, l.expires_at, l.members
    from public.lobby_rooms l
    where l.code = upper(lobby_code) and l.expires_at > now();
$$;

-- ─────────────────────────────────────────────────────────────────────────────
-- Retention (spec §23): geometry sweeps after 30 days; run_history stays.
-- Schedule daily via pg_cron if available; otherwise run manually / via edge fn.
-- ─────────────────────────────────────────────────────────────────────────────
create or replace function public.sweep_old_trails()
returns void
language sql security definer set search_path = public
as $$
    delete from public.trails where started_at < now() - interval '30 days';
$$;

do $$
begin
    if exists (select 1 from pg_extension where extname = 'pg_cron') then
        perform cron.schedule('sweep_old_trails', '17 3 * * *', 'select public.sweep_old_trails()');
        perform cron.schedule('sweep_expired_lobbies', '*/15 * * * *', 'select public.sweep_expired_lobbies()');
    end if;
exception when others then
    raise notice 'pg_cron scheduling skipped: %', sqlerrm;
end $$;


-- ════════════════════════════════════════════════════════════════════════════
-- Migration: lumen-scoreboard (2026-07-18) — Track E.
--
-- Replaces the deprecated 4-axis RunScorer float score (distance/speed/beauty/
-- proximity /100) with the integer Lumen tally from ILumenScoreboard (Core).
-- Adds match metadata (matches) and per-player match results (match_players)
-- so the "most Lumens wins" timed match (decision O) survives end-of-match.
--
-- This section is IDEMPOTENT: every statement uses IF EXISTS / IF NOT EXISTS /
-- CREATE OR REPLACE, so re-applying schema.sql on an already-migrated DB is a
-- no-op. Run it in the Supabase SQL editor or via psql; a live-DB run is a
-- human checkpoint (this file is not verified on this machine — see README).
-- ════════════════════════════════════════════════════════════════════════════

-- ─────────────────────────────────────────────────────────────────────────────
-- Migration: lumen-scoreboard — run_history column changes.
-- Drop the score_* columns (RunScorer is gone); add lumens INT NOT NULL DEFAULT 0
-- (the player's Lumen tally for that run; decision E).
-- ─────────────────────────────────────────────────────────────────────────────
alter table public.run_history
    drop column if exists score_total,
    drop column if exists score_distance,
    drop column if exists score_speed,
    drop column if exists score_beauty,
    drop column if exists score_proximity;

alter table public.run_history
    add column if not exists lumens int not null default 0;

-- The old leaderboard index keyed on score_total desc; replace it with one on
-- lumens desc so get_global_leaderboard / get_nearby_leaderboard (updated below)
-- can use it. DROP IF EXISTS + CREATE IF NOT EXISTS keep this idempotent.
drop index if exists public.run_history_leaderboard_idx;
create index if not exists run_history_leaderboard_idx on public.run_history (lumens desc, recorded_at desc);

-- ─────────────────────────────────────────────────────────────────────────────
-- Migration: lumen-scoreboard — matches table.
-- One row per Lightfield match (decision O: timed match, most Lumens wins).
-- Host authority (decision Q) is resolved via a match_players row with
-- role = 'host' (see below), NOT a denormalized column on matches — so host
-- handoff (if added later) is a single-row update.
-- ─────────────────────────────────────────────────────────────────────────────
create table if not exists public.matches (
    id                uuid primary key default gen_random_uuid(),
    room_id           text,                                       -- Fusion room name (ILobbyService.ActiveRoomName)
    started_at        timestamptz not null default now(),
    ended_at          timestamptz,
    duration_seconds  int,
    winner_player_id  text,                                       -- player_id from match_players (TEXT — offline ids aren't UUIDs)
    created_at        timestamptz not null default now()
);

create index if not exists matches_started_idx  on public.matches (started_at);  -- retention sweep
create index if not exists matches_room_idx     on public.matches (room_id);

-- ─────────────────────────────────────────────────────────────────────────────
-- Migration: lumen-scoreboard — match_players table.
-- One row per (match, player). role matches the PlayerRole enum (decision Q/R):
-- 'runner' | 'host' | 'referee'. finish_rank is 1-based; lumens is the player's
-- final Lumen tally for this match (decision E).
-- ─────────────────────────────────────────────────────────────────────────────
create table if not exists public.match_players (
    match_id      uuid not null references public.matches (id) on delete cascade,
    player_id     text not null,
    lumens        int  not null default 0,
    finish_rank   int,
    role          text not null check (role in ('runner','host','referee')),
    created_at    timestamptz not null default now(),
    primary key (match_id, player_id)
);

create index if not exists match_players_match_idx on public.match_players (match_id);
create index if not exists match_players_player_idx on public.match_players (player_id);

-- ─────────────────────────────────────────────────────────────────────────────
-- Migration: lumen-scoreboard — RLS for matches + match_players.
--
-- matches: SELECT for participants (a match_players row exists for them);
--          INSERT/UPDATE restricted to the match's host (resolved via a
--          match_players row with role = 'host'). Direct client writes to
--          ended_at / winner_player_id / duration_seconds are blocked — those
--          go through the finalize_match SECURITY DEFINER RPC (host-only check
--          inside). We still allow the host row-existence policy as a defense-
--          in-depth guard for create_match's initial insert.
--
-- match_players: SELECT for the row owner OR any host of the same match;
--                client INSERT/UPDATE blocked — all writes go through the
--                SECURITY DEFINER RPCs (create_match / record_match_result),
--                which enforce host-authority inside.
-- ─────────────────────────────────────────────────────────────────────────────
alter table public.matches        enable row level security;
alter table public.match_players  enable row level security;

-- matches SELECT: participants only.
drop policy if exists matches_read on public.matches;
create policy matches_read on public.matches for select using (
    exists (select 1 from public.match_players mp
            where mp.match_id = matches.id and mp.player_id = auth.uid()::text)
);

-- matches INSERT: only if the caller will be the host (defense-in-depth; the
-- real authority check is in create_match). A player may insert a match row
-- only when no host exists yet for that id (first-writer) — enforced by the
-- RPC. Here we permit the insert shape; the RPC controls who can call it.
drop policy if exists matches_insert on public.matches;
create policy matches_insert on public.matches for insert with check (
    exists (select 1 from public.match_players mp
            where mp.match_id = matches.id
              and mp.player_id = auth.uid()::text
              and mp.role = 'host')
    or not exists (select 1 from public.match_players mp where mp.match_id = matches.id)
);

-- matches UPDATE: only the host of that match (so ended_at / winner can move).
drop policy if exists matches_update on public.matches;
create policy matches_update on public.matches for update using (
    exists (select 1 from public.match_players mp
            where mp.match_id = matches.id
              and mp.player_id = auth.uid()::text
              and mp.role = 'host')
);

-- match_players SELECT: own row, or you're the host of the match.
drop policy if exists match_players_read on public.match_players;
create policy match_players_read on public.match_players for select using (
    match_players.player_id = auth.uid()::text
    or exists (select 1 from public.match_players h
               where h.match_id = match_players.match_id
                 and h.player_id = auth.uid()::text
                 and h.role = 'host')
);
-- match_players writes: client-direct writes are NOT allowed; all writes go
-- through the SECURITY DEFINER RPCs below. (No insert/update/delete policy =
-- denied at the RLS layer for non-SUPERUSER roles, which is what we want.)


-- ─────────────────────────────────────────────────────────────────────────────
-- Migration: lumen-scoreboard — record_run RPC replacement.
-- Drops the score_* params; takes p_lumens INT instead. BACKWARDS-COMPAT NOTE:
-- any client still posting the OLD signature (score_total, score_distance, ...)
-- will get a PostgREST 400 ("function parameter does not exist"). That is the
-- intended hard cutover signal — Track D's RunSummaryUI.cs is the only caller
-- and is updated in lockstep. Queued offline ops (PendingOpsQueue) from before
-- the migration must be drained or cleared; a mismatched payload stays queued
-- and retries forever — see PendingOpsQueue.ClearForFn (Track E adds nothing
-- here, just flagging for ops).
--
-- NOTE: the canonical post-migration record_run definition lives above (in the
-- Player stats / scoring RPCs section), preceded by a `drop function if exists`
-- guard for the OLD signature so CREATE OR REPLACE can re-create it cleanly.
-- It is not repeated here — single source of truth.
-- ─────────────────────────────────────────────────────────────────────────────

-- ─────────────────────────────────────────────────────────────────────────────
-- Migration: lumen-scoreboard — leaderboard RPCs now read lumens, not score_total.
-- NOTE: the canonical post-migration definitions live above (near record_run),
-- each preceded by a `drop function if exists` guard so an already-deployed DB
-- whose leaderboard functions return best_score can be re-created cleanly. They
-- are not repeated here — repeating CREATE OR REPLACE with the same shape is a
-- harmless no-op, but keeping a single source of truth is easier to review.
-- ─────────────────────────────────────────────────────────────────────────────

-- ─────────────────────────────────────────────────────────────────────────────
-- Migration: lumen-scoreboard — match lifecycle RPCs (all SECURITY DEFINER).
-- Mirror the lobby RPC pattern (raise exception '<token>'; client surfaces the
-- token verbatim via LobbyServices.ErrorToken). Tokens: not_authenticated,
-- not_host, not_found, bad_role.
-- ─────────────────────────────────────────────────────────────────────────────

-- create_match — host calls this when the match begins (IMatchSession.BeginMatch).
-- Inserts a matches row + a host match_players row; returns the match id.
create or replace function public.create_match(
    p_room_id text,
    p_host_player_id text
)
returns uuid
language plpgsql security definer set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
    v_match_id uuid;
begin
    if v_uid is null then raise exception 'not_authenticated'; end if;

    insert into public.matches (room_id)
    values (p_room_id)
    returning id into v_match_id;

    insert into public.match_players (match_id, player_id, lumens, finish_rank, role)
    values (v_match_id, p_host_player_id, 0, null, 'host');

    return v_match_id;
end;
$$;

-- record_match_result — upsert a match_players row. Host-only for OTHER players;
-- a player may record their OWN result (the host calls this on their behalf in
-- the normal flow, but allowing self-write is harmless and simplifies offline
-- reconcile). Lumens/rank come from the authoritative ILumenScoreboard.
create or replace function public.record_match_result(
    p_match_id uuid,
    p_player_id text,
    p_lumens int,
    p_finish_rank int,
    p_role text
)
returns void
language plpgsql security definer set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
    v_caller_uid text;
begin
    if v_uid is null then raise exception 'not_authenticated'; end if;
    v_caller_uid := v_uid::text;

    if p_role is null or p_role not in ('runner','host','referee') then
        raise exception 'bad_role';
    end if;

    if not exists (select 1 from public.matches m where m.id = p_match_id) then
        raise exception 'not_found';
    end if;

    -- Determine whether the caller is already an established host of THIS match.
    -- (Round-1 review fix R1-F5: the prior check gated the host-privilege path only when
    -- p_player_id <> v_caller_uid, which let a runner self-write role='host' and then issue
    -- further calls as host — a privilege-escalation chain. The role-lock below is independent
    -- of self-vs-other and closes the hole: only an EXISTING host may write host/referee roles.)
    declare
        v_caller_is_host boolean;
    begin
        select exists (
            select 1 from public.match_players h
            where h.match_id = p_match_id
              and h.player_id = v_caller_uid
              and h.role = 'host'
        ) into v_caller_is_host;
    end;

    -- A non-host may never assign host or referee roles (to anyone, including themselves).
    -- They may only record a 'runner' row.
    if p_role in ('host','referee') and not coalesce(v_caller_is_host, false) then
        raise exception 'not_host';
    end if;

    -- Host can write any player's row; a player may write their own (runner) row.
    if p_player_id <> v_caller_uid and not coalesce(v_caller_is_host, false) then
        raise exception 'not_host';
    end if;

    -- Defense-in-depth: if the row already exists with role='host' for a different player,
    -- refuse to overwrite it (prevents a host from demoting a co-host to steal host status, and
    -- prevents a late-arriving self-escalation from replacing the real host's row).
    if p_role = 'host' and exists (
        select 1 from public.match_players existing
        where existing.match_id = p_match_id
          and existing.player_id <> p_player_id
          and existing.role = 'host')
    then
        raise exception 'host_already_exists';
    end if;

    insert into public.match_players (match_id, player_id, lumens, finish_rank, role)
    values (p_match_id, p_player_id, p_lumens, p_finish_rank, p_role)
    on conflict (match_id, player_id) do update
       set lumens      = excluded.lumens,
           finish_rank = excluded.finish_rank,
           role        = excluded.role;
end;
$$;

-- finalize_match — host-only; sets ended_at / winner / duration. Called by the
-- host when IMatchSession.EndMatch fires (decision O).
create or replace function public.finalize_match(
    p_match_id uuid,
    p_winner_player_id text,
    p_duration_seconds int
)
returns void
language plpgsql security definer set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
    v_caller_uid text;
begin
    if v_uid is null then raise exception 'not_authenticated'; end if;
    v_caller_uid := v_uid::text;

    if not exists (
        select 1 from public.match_players h
        where h.match_id = p_match_id
          and h.player_id = v_caller_uid
          and h.role = 'host')
    then
        raise exception 'not_host';
    end if;

    update public.matches
    set ended_at         = now(),
        winner_player_id = p_winner_player_id,
        duration_seconds = p_duration_seconds
    where id = p_match_id;
end;
$$;

-- ─────────────────────────────────────────────────────────────────────────────
-- Migration: lumen-scoreboard — retention sweep for matches.
-- Same 30-day window as trails (spec §23). match_players cascade-deletes with
-- matches (ON DELETE CASCADE). run_history (now lumens) still survives — it's
-- the long-term player-stats ledger, not match-scoped.
-- ─────────────────────────────────────────────────────────────────────────────
create or replace function public.sweep_old_matches()
returns void
language sql security definer set search_path = public
as $$
    delete from public.matches where started_at < now() - interval '30 days';
$$;

do $$
begin
    if exists (select 1 from pg_extension where extname = 'pg_cron') then
        perform cron.schedule('sweep_old_matches', '19 3 * * *', 'select public.sweep_old_matches()');
    end if;
exception when others then
    raise notice 'pg_cron scheduling skipped: %', sqlerrm;
end $$;
