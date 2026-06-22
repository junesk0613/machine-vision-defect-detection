import pymysql
from config import Config

def get_db():
    return pymysql.connect(host=Config.MYSQL_HOST, port=Config.MYSQL_PORT, user=Config.MYSQL_USER,
        password=Config.MYSQL_PASSWORD, database=Config.MYSQL_DB, charset='utf8mb4',
        cursorclass=pymysql.cursors.DictCursor, autocommit=True)

def call_proc(name, args=(), fetch='all'):
    conn = get_db()
    try:
        with conn.cursor() as cur:
            cur.callproc(name, args)
            if fetch == 'one': return cur.fetchone()
            elif fetch == 'all': return cur.fetchall()
            return None
    finally: conn.close()

def call_proc_multi(name, args=()):
    conn = get_db()
    try:
        with conn.cursor() as cur:
            cur.callproc(name, args)
            results = [cur.fetchall()]
            while cur.nextset(): results.append(cur.fetchall())
            return results
    finally: conn.close()

def execute(sql, args=(), fetch=None):
    conn = get_db()
    try:
        with conn.cursor() as cur:
            cur.execute(sql, args)
            if fetch == 'one': return cur.fetchone()
            elif fetch == 'all': return cur.fetchall()
            return cur.lastrowid
    finally: conn.close()

# 사용자
def get_user(username): return execute("SELECT * FROM users WHERE username=%s", (username,), fetch='one')
def get_user_by_id(uid): return execute("SELECT * FROM users WHERE id=%s", (uid,), fetch='one')
def create_user(u, h, r='operator'): return execute("INSERT INTO users (username,password_hash,role) VALUES (%s,%s,%s)", (u, h, r))
def get_all_users():
    try: return call_proc('sp_get_all_users')
    except: return execute("SELECT id,username,role,created_at FROM users ORDER BY created_at DESC", fetch='all')
def update_user_role(uid, role):
    try: call_proc('sp_update_user_role', (uid, role), fetch=None)
    except: execute("UPDATE users SET role=%s WHERE id=%s", (role, uid))
def update_user_password(uid, h):
    try: call_proc('sp_update_user_password', (uid, h), fetch=None)
    except: execute("UPDATE users SET password_hash=%s WHERE id=%s", (h, uid))
def update_username(uid, name):
    try: call_proc('sp_update_username', (uid, name), fetch=None)
    except: execute("UPDATE users SET username=%s WHERE id=%s", (name, uid))
def delete_user(uid):
    try: call_proc('sp_delete_user', (uid,), fetch=None)
    except: execute("DELETE FROM active_sessions WHERE user_id=%s", (uid,)); execute("DELETE FROM users WHERE id=%s", (uid,))

# 세션
def get_active_sessions(uid): return execute("SELECT * FROM active_sessions WHERE user_id=%s", (uid,), fetch='all')
def add_session(uid, sid): execute("INSERT INTO active_sessions (user_id,sid) VALUES (%s,%s)", (uid, sid))
def remove_session(sid): execute("DELETE FROM active_sessions WHERE sid=%s", (sid,))
def remove_user_sessions(uid): execute("DELETE FROM active_sessions WHERE user_id=%s", (uid,))

# 검사
def create_inspection(r, d, fi, bi, fc, bc, sn=None):
    """검사 결과 저장. serial_number 컬럼이 있으면 포함, 없으면 기존 방식 폴백"""
    try:
        # serial_number 컬럼 포함 직접 SQL
        import pymysql
        from config import DB_CONFIG
        conn = pymysql.connect(**DB_CONFIG, cursorclass=pymysql.cursors.DictCursor)
        with conn.cursor() as cur:
            cur.execute(
                "INSERT INTO inspection_log (result, defect_type, front_image, back_image, front_conf, back_conf, serial_number) VALUES (%s,%s,%s,%s,%s,%s,%s)",
                (r, d, fi, bi, fc, bc, sn))
            conn.commit()
            cur.execute("SELECT LAST_INSERT_ID() AS id")
            row = cur.fetchone()
        conn.close()
        return row
    except Exception:
        # serial_number 컬럼 없는 경우 → 기존 프로시저로 폴백
        try:
            return call_proc('sp_create_inspection', (r, d, fi, bi, fc, bc), fetch='one')
        except:
            return {'id': 0}
def get_inspection_history(rf='', df=None, dt=None, lim=20, off=0, sn=None):
    """검사 이력 조회. sn이 있으면 시리얼 번호 검색"""
    if sn:
        # 시리얼 검색 시 직접 SQL (프로시저에 없는 기능)
        where = ["serial_number LIKE %s"]
        params = [f'%{sn}%']
        if rf: where.append("result=%s"); params.append(rf)
        if df: where.append("DATE(timestamp)>=%s"); params.append(df)
        if dt: where.append("DATE(timestamp)<=%s"); params.append(dt)
        w = ' AND '.join(where)
        rows = execute(f"SELECT * FROM inspection_log WHERE {w} ORDER BY timestamp DESC LIMIT %s OFFSET %s", params + [lim, off], fetch='all')
        total_row = execute(f"SELECT COUNT(*) AS total FROM inspection_log WHERE {w}", params, fetch='one')
        total = total_row['total'] if total_row else 0
        return (rows or [], total)
    # 직접 SQL (serial_number 포함)
    where = []
    params = []
    if rf: where.append("result=%s"); params.append(rf)
    if df: where.append("DATE(timestamp)>=%s"); params.append(df)
    if dt: where.append("DATE(timestamp)<=%s"); params.append(dt)
    w = (' WHERE ' + ' AND '.join(where)) if where else ''
    rows = execute(f"SELECT * FROM inspection_log{w} ORDER BY timestamp DESC LIMIT %s OFFSET %s", params + [lim, off], fetch='all')
    total_row = execute(f"SELECT COUNT(*) AS total FROM inspection_log{w}", params, fetch='one')
    total = total_row['total'] if total_row else 0
    return (rows or [], total)
def get_latest_inspection(): return call_proc('sp_get_latest_inspection', fetch='one')
def get_today_stats(): return call_proc('sp_get_today_stats', fetch='one')
def get_defect_stats(df=None, dt=None): return call_proc('sp_get_defect_stats', (df, dt))
def get_defect_percent(df=None, dt=None): return call_proc('sp_get_defect_percent', (df, dt))
def get_hourly_trend(): return call_proc('sp_get_hourly_trend')
def get_daily_stats(df=None, dt=None): return call_proc('sp_get_daily_stats', (df, dt))

def get_stats_by_date(df=None, dt=None):
    if not df and not dt: return get_today_stats()
    return execute("SELECT COUNT(*) AS total, SUM(CASE WHEN result='OK' THEN 1 ELSE 0 END) AS ok_count, SUM(CASE WHEN result='NG' THEN 1 ELSE 0 END) AS ng_count, ROUND(SUM(CASE WHEN result='OK' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0)*100,1) AS yield_rate FROM inspection_log WHERE (%s IS NULL OR DATE(timestamp)>=%s) AND (%s IS NULL OR DATE(timestamp)<=%s)", (df, df, dt, dt), fetch='one')

def get_hourly_trend_by_date(df=None, dt=None):
    if not df and not dt: return get_hourly_trend()
    return execute("SELECT HOUR(timestamp) AS hour, COUNT(*) AS total, SUM(CASE WHEN result='OK' THEN 1 ELSE 0 END) AS ok_count, SUM(CASE WHEN result='NG' THEN 1 ELSE 0 END) AS ng_count FROM inspection_log WHERE (%s IS NULL OR DATE(timestamp)>=%s) AND (%s IS NULL OR DATE(timestamp)<=%s) GROUP BY HOUR(timestamp) ORDER BY hour", (df, df, dt, dt), fetch='all')

# 센서
def create_sensor(t, h, r, ng, ok): call_proc('sp_create_sensor', (t, h, r, ng, ok), fetch=None)
def get_latest_sensor(): return call_proc('sp_get_latest_sensor', fetch='one')
def get_sensor_history(hours=1): return call_proc('sp_get_sensor_history', (hours,))

# 알람
def create_alarm(t, d=''): return call_proc('sp_create_alarm', (t, d), fetch='one')

def get_hourly_ng_count():
    """최근 1시간 NG 건수"""
    try:
        import pymysql
        from config import DB_CONFIG
        conn = pymysql.connect(**DB_CONFIG, cursorclass=pymysql.cursors.DictCursor)
        with conn.cursor() as cur:
            cur.execute("SELECT COUNT(*) AS cnt FROM inspection_log WHERE result='NG' AND timestamp >= NOW() - INTERVAL 1 HOUR")
            row = cur.fetchone()
        conn.close()
        return row['cnt'] if row else 0
    except Exception as e:
        print(f'[DB] get_hourly_ng_count error: {e}')
        return 0

def get_recent_alarm_count(alarm_type, minutes=30):
    """최근 N분 이내 같은 타입 알람이 있는지 (중복 방지)"""
    try:
        import pymysql
        from config import DB_CONFIG
        conn = pymysql.connect(**DB_CONFIG, cursorclass=pymysql.cursors.DictCursor)
        with conn.cursor() as cur:
            cur.execute("SELECT COUNT(*) AS cnt FROM alarm_log WHERE type=%s AND timestamp >= NOW() - INTERVAL %s MINUTE", (alarm_type, minutes))
            row = cur.fetchone()
        conn.close()
        return row['cnt'] if row else 0
    except:
        return 0
def get_unresolved_alarms(): return call_proc('sp_get_unresolved_alarms')
def get_alarm_history(): return call_proc('sp_get_alarm_history')
def resolve_alarm(aid): call_proc('sp_resolve_alarm', (aid,), fetch=None)

# Cycle Time
def get_cycle_time(n=30, df=None, dt=None):
    """최근 N건 검사 timestamp 간격 평균 (초). 5분 초과 간격은 라인 정지로 보고 제외."""
    where = []
    params = []
    if df: where.append("DATE(timestamp)>=%s"); params.append(df)
    if dt: where.append("DATE(timestamp)<=%s"); params.append(dt)
    w = ('WHERE ' + ' AND '.join(where) + ' ') if where else ''
    rows = execute(f"SELECT timestamp FROM inspection_log {w}ORDER BY timestamp DESC LIMIT %s", params + [n], fetch='all')
    if not rows or len(rows) < 2:
        return None
    diffs = []
    for i in range(len(rows) - 1):
        diff = (rows[i]['timestamp'] - rows[i+1]['timestamp']).total_seconds()
        if 0 < diff <= 300:
            diffs.append(diff)
    return round(sum(diffs) / len(diffs), 1) if diffs else None

# 설정
def get_settings(): return call_proc('sp_get_settings', fetch='one')
def update_settings(yc, tmin, tmax, hmin, hmax): call_proc('sp_update_settings', (yc, tmin, tmax, hmin, hmax), fetch=None)
