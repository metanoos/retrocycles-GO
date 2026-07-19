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
-- run_history — score breakdown per run; survives the geometry sweep (§23).
-- ─────────────────────────────────────────────────────────────────────────────
create table if not exists public.run_history (
    id              bigint generated always as identity primary key,
    player_id       uuid not null references public.players (id) on delete cascade,
    distance_m      double precision not null,
    duration_s      double precision not null,
    avg_speed       double precision not null,
    score_total     int not null,
    score_distance  int not null,
    score_speed     int not null,
    score_beauty    int not null,
    score_proximity int not null,
    beacon_form     int not null default 0,
    crashed         boolean not null default false,
    recorded_at     timestamptz not null default now()
);

create index if not exists run_history_leaderboard_idx on public.run_history (score_total desc, recorded_at desc);
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
-- Rejected runs return success to the client (don't teach the prober) but land in
-- rejected_runs for audit.
create or replace function public.record_run(
    distance_m double precision,
    duration_s double precision,
    avg_speed double precision,
    score_total int,
    score_distance int,
    score_speed int,
    score_beauty int,
    score_proximity int,
    beacon_form int,
    crashed boolean
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
    if avg_speed > 12.0 then v_reason := 'avg_speed';
    elsif distance_m > 100000.0 then v_reason := 'distance';
    elsif duration_s < 10.0 and distance_m > 100.0 then v_reason := 'teleport';
    elsif score_total > 100
        or score_distance > 40 or score_speed > 20
        or score_beauty > 30 or score_proximity > 10
        or score_total < 0 or score_distance < 0 or score_speed < 0
        or score_beauty < 0 or score_proximity < 0 then v_reason := 'score_bounds';
    end if;

    if v_reason is not null then
        insert into public.rejected_runs (player_id, payload, reason)
        values (v_uid, jsonb_build_object(
            'distance_m', distance_m, 'duration_s', duration_s, 'avg_speed', avg_speed,
            'score_total', score_total), v_reason);
        return; -- silent success (spec §22)
    end if;

    insert into public.run_history (
        player_id, distance_m, duration_s, avg_speed,
        score_total, score_distance, score_speed, score_beauty, score_proximity,
        beacon_form, crashed)
    values (
        v_uid, distance_m, duration_s, avg_speed,
        score_total, score_distance, score_speed, score_beauty, score_proximity,
        beacon_form, crashed);

    perform public.update_player_stats(v_uid, distance_m);
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

create or replace function public.get_global_leaderboard(max_rows int default 20)
returns table (player_id uuid, display_name text, best_score int, recorded_at timestamptz)
language sql stable security definer set search_path = public
as $$
    select r.player_id, pl.display_name, max(r.score_total) as best_score, max(r.recorded_at)
    from public.run_history r
    join public.players pl on pl.id = r.player_id
    group by r.player_id, pl.display_name
    order by best_score desc
    limit least(max_rows, 100);
$$;

create or replace function public.get_nearby_leaderboard(
    center_lat double precision,
    center_lon double precision,
    radius_m double precision,
    max_rows int default 20
)
returns table (player_id uuid, display_name text, best_score int)
language sql stable security definer set search_path = public
as $$
    select r.player_id, pl.display_name, max(r.score_total) as best_score
    from public.run_history r
    join public.players pl on pl.id = r.player_id
    join public.trails t on t.player_id = r.player_id
    where t.start_geo is not null
      and st_dwithin(
            t.start_geo,
            st_setsrid(st_makepoint(center_lon, center_lat), 4326)::geography,
            radius_m)
    group by r.player_id, pl.display_name
    order by best_score desc
    limit least(max_rows, 100);
$$;

create or replace function public.get_player_best(p_player_id uuid)
returns table (best_score int, best_distance double precision, total_runs int)
language sql stable security definer set search_path = public
as $$
    select
        coalesce(max(r.score_total), 0),
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
