import os, time, uuid, logging, threading
from datetime import datetime, timedelta
from flask import Flask, render_template, request, jsonify, session, redirect, url_for, send_from_directory, send_file
from flask_socketio import SocketIO, emit
from werkzeug.security import generate_password_hash, check_password_hash
from config import Config
import db_manager as db
import store

app = Flask(__name__)
app.config['SECRET_KEY'] = Config.SECRET_KEY
socketio = SocketIO(app, cors_allowed_origins="*", async_mode='threading')
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)
os.makedirs(Config.IMAGE_DIR, exist_ok=True)

# 최근 카메라 프레임 (WPF에서 수신)
latest_frames = {'front': None, 'back': None}

# 최신 센서값 (WPF에서 수신 즉시 저장)
latest_sensor = {}

# 라인 시스템 상태 (stopped / running / estop)
system_status = {'status': 'stopped'}

# ── 인증 ──
@app.route('/login', methods=['GET','POST'])
def login():
    if request.method == 'GET': return render_template('login.html')
    username = request.form.get('username','').strip()
    password = request.form.get('password','')
    if not username or not password: return render_template('login.html', error='아이디와 비밀번호를 입력하세요.')

    fail_key, lock_key = f'fail_{username}', f'lock_{username}'
    if session.get(lock_key) and time.time() < session[lock_key]:
        return render_template('login.html', error=f'잠금 중 ({int(session[lock_key]-time.time())}초)')

    user = db.get_user(username)
    if not user or not check_password_hash(user['password_hash'], password):
        count = session.get(fail_key, 0) + 1; session[fail_key] = count
        if count >= 5: session[lock_key] = time.time() + 60; session[fail_key] = 0; return render_template('login.html', error='5회 실패 — 60초 잠금')
        return render_template('login.html', error=f'로그인 실패 ({count}/5)')

    if user['role'] == 'operator': return render_template('login.html', error='현장 작업자는 관리자 Web에 로그인 할 수 없습니다.')

    existing = db.get_active_sessions(user['id'])
    force = request.form.get('force_login') == '1'
    if existing and not force:
        return render_template('login.html', error='이미 접속 중인 세션이 있습니다.', show_force=True, username=username, password=password)
    if force: db.remove_user_sessions(user['id'])

    sid = str(uuid.uuid4()); db.add_session(user['id'], sid)
    session.update({'user_id': user['id'], 'username': user['username'], 'role': user['role'], 'sid': sid})
    session[fail_key] = 0
    store.add_audit(user['username'], user['role'], 'LOGIN', '로그인 성공')
    return redirect(url_for('dashboard'))

@app.route('/logout')
def logout():
    sid = session.get('sid')
    if sid: db.remove_session(sid)
    if session.get('username'):
        store.add_audit(session.get('username'), session.get('role'), 'LOGOUT', '로그아웃')
    session.clear(); return redirect(url_for('login'))

@app.route('/api/auth', methods=['POST'])
def api_auth():
    data = request.get_json()
    username, password = data.get('username','').strip(), data.get('password','')
    if not username or not password: return jsonify({'success': False, 'error': '아이디와 비밀번호를 입력하세요.'}), 400
    user = db.get_user(username)
    if not user or not check_password_hash(user['password_hash'], password):
        return jsonify({'success': False, 'error': '아이디 또는 비밀번호가 일치하지 않습니다.'}), 401
    return jsonify({'success': True, 'username': user['username'], 'role': user['role']})

def login_required(f):
    from functools import wraps
    @wraps(f)
    def d(*a, **k):
        if 'user_id' not in session: return redirect(url_for('login'))
        return f(*a, **k)
    return d

def super_admin_required(f):
    from functools import wraps
    @wraps(f)
    def d(*a, **k):
        if 'user_id' not in session: return redirect(url_for('login'))
        if session.get('role') != 'super_admin': return jsonify({'error': '권한 없음'}), 403
        return f(*a, **k)
    return d

# ── 페이지 ──
@app.route('/')
@login_required
def dashboard(): return render_template('dashboard.html')

@app.route('/monitoring')
@login_required
def monitoring(): return render_template('monitoring.html')

@app.route('/history')
@login_required
def history(): return render_template('history.html')

@app.route('/statistics')
@login_required
def statistics(): return render_template('statistics.html')

@app.route('/alarm')
@login_required
def alarm(): return render_template('alarm.html')

@app.route('/notices')
@login_required
def notices_page(): return render_template('notices.html')

@app.route('/messages')
@login_required
def messages_page(): return render_template('messages.html')

@app.route('/settings')
@super_admin_required
def settings_page(): return render_template('settings.html')

@app.route('/users')
@super_admin_required
def users_page(): return render_template('users.html')

# ── 검사 API ──
@app.route('/api/inspect', methods=['POST'])
def api_inspect():
    data = request.get_json() or {}
    result = data.get('result', 'OK'); defect = data.get('defect_type')
    fi, bi = data.get('front_image',''), data.get('back_image','')
    fc, bc = data.get('front_conf', 0), data.get('back_conf', 0)
    sn = data.get('serial_number') or None
    row = db.create_inspection(result, defect, fi, bi, fc, bc, sn)
    payload = {'id': row['id'] if row else 0, 'result': result, 'defect_type': defect,
               'front_conf': fc, 'back_conf': bc, 'timestamp': datetime.now().isoformat(),
               'front_image': fi, 'back_image': bi, 'serial_number': sn}
    socketio.emit('inspection_result', payload)

    # ── 알람 임계값 자동 체크 ──
    if result == 'NG':
        check_alarm_thresholds()

    return jsonify(payload)

def check_alarm_thresholds():
    """검사 결과 저장 후 임계값 초과 여부 체크 → 알람 자동 생성"""
    try:
        policy = store.get_policy()
        thresholds = policy.get('alarm_threshold', {})
        hourly_max = thresholds.get('hourly_ng_max', 10)
        daily_pct_max = thresholds.get('daily_defect_pct_max', 5.0)

        # 1) 시간당 NG 한계 체크
        hourly_ng = db.get_hourly_ng_count()
        if hourly_ng >= hourly_max:
            # 30분 내 같은 알람 없을 때만 생성 (중복 방지)
            if db.get_recent_alarm_count('HOURLY_NG_LIMIT', 30) == 0:
                detail = f'시간당 NG 한계 초과 ({hourly_ng}건 ≥ {hourly_max}건)'
                alarm = db.create_alarm('HOURLY_NG_LIMIT', detail)
                aid = alarm['id'] if alarm else 0
                socketio.emit('alarm_created', {
                    'id': aid, 'type': 'HOURLY_NG_LIMIT', 'detail': detail,
                    'timestamp': datetime.now().isoformat()
                })
                # 생산통계 페이지에도 이벤트 알림
                socketio.emit('threshold_alert', {
                    'type': 'HOURLY_NG_LIMIT', 'detail': detail,
                    'value': hourly_ng, 'limit': hourly_max
                })

        # 2) 일일 불량률 한계 체크
        today = db.get_today_stats()
        if today and today.get('total', 0) >= 10:  # 최소 10건 이상일 때만 의미 있음
            total = today['total']
            ng = today.get('ng_count', 0)
            pct = round(ng / total * 100, 1) if total > 0 else 0
            if pct >= daily_pct_max:
                if db.get_recent_alarm_count('DAILY_DEFECT_LIMIT', 30) == 0:
                    detail = f'일일 불량률 한계 초과 ({pct}% ≥ {daily_pct_max}%)'
                    alarm = db.create_alarm('DAILY_DEFECT_LIMIT', detail)
                    aid = alarm['id'] if alarm else 0
                    socketio.emit('alarm_created', {
                        'id': aid, 'type': 'DAILY_DEFECT_LIMIT', 'detail': detail,
                        'timestamp': datetime.now().isoformat()
                    })
                    socketio.emit('threshold_alert', {
                        'type': 'DAILY_DEFECT_LIMIT', 'detail': detail,
                        'value': pct, 'limit': daily_pct_max
                    })
    except Exception as e:
        print(f'[ALARM] threshold check error: {e}')

@app.route('/api/latest-inspection')
@login_required
def api_latest(): r = db.get_latest_inspection(); return jsonify(dict(r) if r else {})

@app.route('/api/history')
@login_required
def api_history():
    rf = request.args.get('result',''); df = request.args.get('date_from') or None; dt = request.args.get('date_to') or None
    sn = request.args.get('serial','').strip() or None
    page = max(1, request.args.get('page',1,type=int)); limit = 20; offset = (page-1)*limit
    rows, total = db.get_inspection_history(rf, df, dt, limit, offset, sn)
    data = [dict(r) for r in rows]
    for d in data:
        if d.get('timestamp'): d['timestamp'] = str(d['timestamp'])
    return jsonify({'data': data, 'total': total, 'page': page, 'pages': (total+limit-1)//limit})

@app.route('/api/stats')
@login_required
def api_stats():
    df = request.args.get('date_from') or None; dt = request.args.get('date_to') or None
    # 날짜 범위 없으면 오늘로 고정 (today 통계와 defect_pct 기준 일치)
    today_str = datetime.now().strftime('%Y-%m-%d')
    df_q = df or today_str; dt_q = dt or today_str
    stats = db.get_stats_by_date(df, dt); hourly = db.get_hourly_trend_by_date(df, dt)
    defects = db.get_defect_stats(df_q, dt_q); defect_pct = db.get_defect_percent(df_q, dt_q)
    daily = db.get_daily_stats(df, dt)
    return jsonify({
        'today': dict(stats) if stats else {'total':0,'ok_count':0,'ng_count':0,'yield_rate':0},
        'hourly': [dict(h) for h in hourly] if hourly else [],
        'defects': [dict(d) for d in defects] if defects else [],
        'defect_pct': [dict(d) for d in defect_pct] if defect_pct else [],
        'daily': [{'date':str(d['date']),'total':d['total'],'ok_count':d['ok_count'],'ng_count':d['ng_count']} for d in daily] if daily else []
    })

# ── 센서 API ──
@app.route('/api/sensor', methods=['GET','POST'])
def api_sensor():
    if request.method == 'POST':
        data = request.get_json()
        latest_sensor.update(data)
        latest_sensor['_received_at'] = time.time()
        db.create_sensor(data.get('temperature',0), data.get('humidity',0), data.get('conveyor_rpm',0), data.get('box_ng_level',0), data.get('box_ok_level',0))
        socketio.emit('sensor_update', data)
        return jsonify({'ok': True})
    return jsonify(latest_sensor if latest_sensor else dict(db.get_latest_sensor() or {}))

@app.route('/api/sensor/history')
@login_required
def api_sensor_history():
    hours = request.args.get('hours', 1, type=int)
    rows = db.get_sensor_history(hours); data = [dict(r) for r in rows] if rows else []
    for d in data:
        if d.get('timestamp'): d['timestamp'] = str(d['timestamp'])
    return jsonify(data)

# ── 알람 API ──
@app.route('/api/alarm', methods=['GET','POST'])
def api_alarm():
    if request.method == 'POST':
        data = request.get_json()
        row = db.create_alarm(data.get('type','SYSTEM'), data.get('detail',''))
        payload = {'id': row['id'] if row else 0, 'type': data.get('type'), 'detail': data.get('detail'), 'timestamp': datetime.now().isoformat()}
        socketio.emit('alarm_created', payload)
        return jsonify(payload)
    show_all = request.args.get('all','0') == '1'
    rows = db.get_alarm_history() if show_all else db.get_unresolved_alarms()
    data = [dict(r) for r in rows] if rows else []
    for d in data:
        if d.get('timestamp'): d['timestamp'] = str(d['timestamp'])
        if d.get('resolved_at'): d['resolved_at'] = str(d['resolved_at'])
    return jsonify(data)

@app.route('/api/alarm/<int:aid>/resolve', methods=['POST'])
@login_required
def api_resolve(aid):
    db.resolve_alarm(aid); socketio.emit('alarm_resolved', {'id': aid})
    store.add_audit(session.get('username'), session.get('role'), 'ALARM_RESOLVED', f'알람 #{aid} 해결')
    return jsonify({'ok': True})

@app.route('/api/alarm/resolve-all', methods=['POST'])
@login_required
def api_resolve_all():
    rows = db.get_unresolved_alarms() or []
    cnt = 0
    for r in rows:
        db.resolve_alarm(r['id'])
        socketio.emit('alarm_resolved', {'id': r['id']})
        cnt += 1
    if cnt:
        store.add_audit(session.get('username'), session.get('role'), 'ALARM_RESOLVED', f'전체 {cnt}건 일괄 해결')
    return jsonify({'ok': True, 'resolved': cnt})

# ── 설정 API (WPF에서 사용 - YOLO 신뢰도, 온습도 범위) ──
@app.route('/api/settings', methods=['GET','POST'])
def api_settings():
    if request.method == 'POST':
        data = request.get_json()
        db.update_settings(data.get('yolo_conf',0.5), data.get('temp_min',15), data.get('temp_max',35), data.get('humid_min',30), data.get('humid_max',70))
        socketio.emit('settings_changed', 'settings_updated')
        who = session.get('username') or data.get('username', 'WPF')
        role = session.get('role') or data.get('role', 'operator')
        store.add_audit(who, role, 'SETTING_CHANGED',
                        f"YOLO={data.get('yolo_conf')}, T={data.get('temp_min')}~{data.get('temp_max')}, H={data.get('humid_min')}~{data.get('humid_max')}")
        return jsonify({'ok': True})
    r = db.get_settings(); return jsonify(dict(r) if r else {})

# ══════════════════════════════════════════════════════════════
# 운영 정책 API (super_admin 웹 설정)
# ══════════════════════════════════════════════════════════════

# ── 정책 (공장정보 + 알람 임계값 + 이메일 + 보존 기간) ──
@app.route('/api/policy', methods=['GET','POST'])
@login_required
def api_policy():
    if request.method == 'POST':
        if session.get('role') != 'super_admin':
            return jsonify({'error': '권한 없음'}), 403
        data = request.get_json() or {}
        cur = store.get_policy()
        # 부분 업데이트 지원
        for k in ['factory', 'alarm_threshold', 'retention']:
            if k in data and isinstance(data[k], dict):
                cur[k].update(data[k])
        if 'email_recipients' in data:
            cur['email_recipients'] = data['email_recipients']
        if 'takt_time' in data:
            cur['takt_time'] = float(data['takt_time'])
        store.save_policy(cur)
        store.add_audit(session.get('username'), session.get('role'), 'POLICY_CHANGED', f'운영 정책 변경: {list(data.keys())}')
        return jsonify({'ok': True, 'policy': cur})
    return jsonify(store.get_policy())

# ── 공지사항 ──
@app.route('/api/notices', methods=['GET','POST'])
@login_required
def api_notices():
    if request.method == 'POST':
        if session.get('role') != 'super_admin':
            return jsonify({'error': '권한 없음'}), 403
        data = request.get_json() or {}
        title = (data.get('title','') or '').strip()
        content = (data.get('content','') or '').strip()
        if not title: return jsonify({'error': '제목을 입력하세요'}), 400
        n = store.add_notice(title, content, session.get('username',''))
        store.add_audit(session.get('username'), session.get('role'), 'NOTICE_CREATED', f"공지 #{n['id']}: {title}")
        return jsonify({'ok': True, 'notice': n})
    # GET: active_only 파라미터 지원 (대시보드/로그인에서 활성만 가져갈 때)
    active_only = request.args.get('active','') == '1'
    return jsonify(store.list_notices(active_only=active_only))

@app.route('/api/notices/<int:nid>', methods=['PUT','DELETE'])
@login_required
def api_notice_one(nid):
    if session.get('role') != 'super_admin':
        return jsonify({'error': '권한 없음'}), 403
    if request.method == 'DELETE':
        store.delete_notice(nid)
        store.add_audit(session.get('username'), session.get('role'), 'NOTICE_DELETED', f"공지 #{nid} 삭제")
        return jsonify({'ok': True})
    data = request.get_json() or {}
    updated = store.update_notice(nid, data.get('title'), data.get('content'), data.get('active'))
    store.add_audit(session.get('username'), session.get('role'), 'NOTICE_UPDATED', f"공지 #{nid} 수정")
    return jsonify({'ok': True, 'notice': updated})

# ── 사용자 활동 로그 ──
@app.route('/api/audit')
@login_required
def api_audit():
    if session.get('role') != 'super_admin':
        return jsonify({'error': '권한 없음'}), 403
    user = request.args.get('user') or None
    action = request.args.get('action') or None
    page = int(request.args.get('page', 1))
    limit = int(request.args.get('limit', 50))
    return jsonify(store.list_audit(limit=limit, user=user, action=action, page=page))

# ── 백업 & 이미지 ──
def _json_safe(obj):
    import datetime as _dt
    if isinstance(obj, dict): return {k: _json_safe(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)): return [_json_safe(v) for v in obj]
    if isinstance(obj, (_dt.datetime, _dt.date)): return obj.isoformat()
    if isinstance(obj, bytes): return obj.decode('utf-8', errors='replace')
    if isinstance(obj, (int, float, str, bool)) or obj is None: return obj
    return str(obj)

@app.route('/api/backup/data')
@login_required
def api_backup_data():
    """DB 데이터 + 정책 + 공지 + 감사로그를 JSON 한 파일로 다운로드"""
    import io, json, traceback
    try:
        backup = {
            'created_at': datetime.now().isoformat(),
            'policy': store.get_policy(),
            'notices': store.list_notices(),
            'audit': store.list_audit(limit=10000).get('data', []),
        }
        try:
            backup['users'] = [dict(u) for u in (db.get_all_users() or [])]
            for u in backup['users']:
                u.pop('password_hash', None)
        except Exception:
            backup['users'] = []
        buf = io.BytesIO()
        buf.write(json.dumps(_json_safe(backup), ensure_ascii=False, indent=2).encode('utf-8'))
        buf.seek(0)
        try:
            store.add_audit(session.get('username'), session.get('role'), 'BACKUP_DATA', 'JSON 백업')
        except Exception:
            pass
        return send_file(buf, mimetype='application/json', as_attachment=True,
                         download_name=f'pcb_backup_{datetime.now().strftime("%Y%m%d_%H%M%S")}.json')
    except Exception as e:
        app.logger.error('api_backup_data: %s', traceback.format_exc())
        return jsonify({'error': str(e)}), 500

@app.route('/api/backup/images')
@login_required
def api_backup_images():
    import io, zipfile, traceback, tempfile, time as _time
    from flask import after_this_request
    labeled = request.args.get('labeled','') == '1'
    days = min(int(request.args.get('days', 7)), 365)
    max_files = 500
    print(f'[BACKUP] images called user={session.get("username")} labeled={labeled} days={days}', flush=True)
    try:
        img_dir = Config.IMAGE_DIR
        # 최근 N일, 최대 max_files개만 포함
        cutoff = _time.time() - days * 86400
        candidates = []
        if os.path.exists(img_dir):
            for fn in os.listdir(img_dir):
                fp = os.path.join(img_dir, fn)
                if os.path.isfile(fp):
                    try:
                        mtime = os.path.getmtime(fp)
                        if mtime >= cutoff:
                            candidates.append((mtime, fn, fp))
                    except Exception:
                        pass
        candidates.sort(reverse=True)
        candidates = candidates[:max_files]
        print(f'[BACKUP] {len(candidates)} files to zip (days={days})', flush=True)

        tmp = tempfile.NamedTemporaryFile(delete=False, suffix='.zip')
        tmp_path = tmp.name
        tmp.close()

        cnt = 0
        with zipfile.ZipFile(tmp_path, 'w', zipfile.ZIP_STORED) as zf:
            if labeled:
                try:
                    result = db.get_inspection_history(lim=500)
                    records = (result[0] if isinstance(result, tuple) else result) or []
                except Exception:
                    records = []
                fn_set = {fn for _, fn, _ in candidates}
                for r in records:
                    rd = dict(r)
                    for side in ['front_image', 'back_image']:
                        fn = rd.get(side)
                        if not fn or fn not in fn_set: continue
                        fp = os.path.join(img_dir, fn)
                        if not os.path.exists(fp): continue
                        try:
                            img = store.make_labeled_image(fp, rd.get('result','OK'),
                                rd.get('defect_type'), rd.get('front_conf'), rd.get('back_conf'))
                        except Exception:
                            img = None
                        if img is None:
                            zf.write(fp, arcname=f'labeled/{fn}')
                        else:
                            sub = io.BytesIO()
                            img.save(sub, format='JPEG', quality=88)
                            zf.writestr(f'labeled/{fn}', sub.getvalue())
                        cnt += 1
            else:
                for _, fn, fp in candidates:
                    zf.write(fp, arcname=fn)
                    cnt += 1

        print(f'[BACKUP] done cnt={cnt}', flush=True)
        try:
            store.add_audit(session.get('username'), session.get('role'), 'BACKUP_IMAGES',
                            f"{'라벨합성' if labeled else '원본'} {cnt}개 (최근{days}일)")
        except Exception:
            pass

        @after_this_request
        def _cleanup(response):
            try: os.unlink(tmp_path)
            except: pass
            return response

        name = f'pcb_images_{"labeled_" if labeled else ""}{datetime.now().strftime("%Y%m%d_%H%M%S")}.zip'
        return send_file(tmp_path, mimetype='application/zip', as_attachment=True, download_name=name)
    except Exception as e:
        print(f'[BACKUP] ERROR: {traceback.format_exc()}', flush=True)
        return jsonify({'error': str(e)}), 500

@app.route('/api/labeled-image/<filename>')
@login_required
def api_labeled_image(filename):
    """단일 이미지 라벨 합성본 다운로드 — DB에서 해당 이미지 검사 결과 찾아서 합성"""
    fp = os.path.join(Config.IMAGE_DIR, filename)
    if not os.path.exists(fp):
        return jsonify({'error': '파일 없음'}), 404
    # DB에서 이 이미지의 검사 정보 조회
    try:
        result = db.get_inspection_history(lim=2000)
        records = result[0] if isinstance(result, tuple) else (result or [])
    except Exception:
        records = []
    info = None
    for r in records:
        rd = dict(r)
        if rd.get('front_image') == filename or rd.get('back_image') == filename:
            info = rd; break
    if info is None:
        info = {'result': 'OK'}
    is_front = info.get('front_image') == filename
    img = store.make_labeled_image(
        fp, info.get('result','OK'), info.get('defect_type'),
        info.get('front_conf') if is_front else None,
        info.get('back_conf') if not is_front else None
    )
    if img is None:
        return send_from_directory(Config.IMAGE_DIR, filename)
    import io as io2
    buf = io2.BytesIO(); img.save(buf, format='JPEG', quality=90); buf.seek(0)
    return send_file(buf, mimetype='image/jpeg', as_attachment=False, download_name=f'labeled_{filename}')

# ── 데이터 정리 (보존 기간 초과 데이터 삭제) ──
@app.route('/api/restore/data', methods=['POST'])
@login_required
def api_restore_data():
    if session.get('role') != 'super_admin':
        return jsonify({'error': '권한 없음'}), 403
    if 'file' not in request.files:
        return jsonify({'error': '파일이 없습니다'}), 400
    try:
        import json as _json
        raw = request.files['file'].read()
        data = _json.loads(raw.decode('utf-8'))
    except Exception:
        return jsonify({'error': 'JSON 파싱 실패 — 올바른 백업 파일인지 확인하세요'}), 400
    if not isinstance(data, dict):
        return jsonify({'error': '올바르지 않은 백업 파일 형식'}), 400

    result = {'policy': False, 'notices': 0, 'audit': 0}

    # 1) 정책 복원 (덮어쓰기)
    if 'policy' in data and isinstance(data['policy'], dict):
        try:
            cur = store.get_policy()
            bp = data['policy']
            for k in ['factory', 'alarm_threshold', 'retention']:
                if k in bp and isinstance(bp[k], dict):
                    cur[k].update(bp[k])
            if 'email_recipients' in bp:
                cur['email_recipients'] = bp['email_recipients']
            store.save_policy(cur)
            result['policy'] = True
        except Exception as e:
            logger.error(f'restore policy: {e}')

    # 2) 공지 복원 (제목+작성일 기준 중복 제외 후 추가)
    if 'notices' in data and isinstance(data['notices'], list):
        try:
            existing = store.list_notices()
            existing_keys = {(n.get('title',''), n.get('created_at','')) for n in existing}
            for n in data['notices']:
                if not isinstance(n, dict): continue
                title = (n.get('title') or '').strip()
                if not title: continue
                key = (title, n.get('created_at',''))
                if key in existing_keys: continue
                store.add_notice(title, n.get('content','') or '', n.get('created_by','[복원]'))
                existing_keys.add(key)
                result['notices'] += 1
        except Exception as e:
            logger.error(f'restore notices: {e}')

    # 3) 감사 로그 병합 (시간+사용자+액션 기준 중복 제외)
    if 'audit' in data and isinstance(data['audit'], list):
        try:
            all_audit = store._load('audit.json', [])
            existing_keys = {(a.get('time'), a.get('user'), a.get('action')) for a in all_audit}
            new_entries = []
            for a in data['audit']:
                if not isinstance(a, dict): continue
                key = (a.get('time'), a.get('user'), a.get('action'))
                if key in existing_keys: continue
                new_entries.append(a)
                existing_keys.add(key)
            if new_entries:
                all_audit.extend(new_entries)
                all_audit.sort(key=lambda x: x.get('time',''), reverse=True)
                store._save('audit.json', all_audit)
                result['audit'] = len(new_entries)
        except Exception as e:
            logger.error(f'restore audit: {e}')

    parts = []
    if result['policy']: parts.append('정책')
    if result['notices']: parts.append(f"공지 {result['notices']}건")
    if result['audit']: parts.append(f"활동로그 {result['audit']}건")
    summary = ', '.join(parts) if parts else '변경 없음'
    store.add_audit(session.get('username'), session.get('role'), 'RESTORE_DATA', f'복원: {summary}')
    return jsonify({'ok': True, 'summary': summary, 'detail': result})

@app.route('/api/cleanup', methods=['POST'])
@login_required
def api_cleanup():
    if session.get('role') != 'super_admin':
        return jsonify({'error': '권한 없음'}), 403
    policy = store.get_policy()
    img_days = policy['retention']['images_days']
    cutoff = datetime.now().timestamp() - img_days * 86400

    # 오래된 이미지 삭제
    deleted_imgs = 0
    deleted_fns = []
    if os.path.exists(Config.IMAGE_DIR):
        for fn in os.listdir(Config.IMAGE_DIR):
            fp = os.path.join(Config.IMAGE_DIR, fn)
            try:
                if os.path.isfile(fp) and os.path.getmtime(fp) < cutoff:
                    os.remove(fp)
                    deleted_imgs += 1
                    deleted_fns.append(fn)
            except Exception:
                pass

    # 삭제된 이미지 DB 참조 제거
    if deleted_fns:
        for fn in deleted_fns:
            db.execute("UPDATE inspection_log SET front_image='' WHERE front_image=%s", (fn,))
            db.execute("UPDATE inspection_log SET back_image='' WHERE back_image=%s", (fn,))

    store.add_audit(session.get('username'), session.get('role'), 'CLEANUP_RUN',
                    f"이미지 {deleted_imgs}개 삭제 (보존 {img_days}일)")
    return jsonify({'ok': True, 'deleted_images': deleted_imgs})

# ══════════════════════════════════════════════════════════════
# 채팅 메시지 API (Web ↔ WPF)
# ══════════════════════════════════════════════════════════════

@app.route('/api/chat/lines', methods=['GET', 'POST'])
def api_chat_lines():
    """채팅 가능한 라인 목록 (GET: 누구나, POST: super_admin)"""
    if request.method == 'POST':
        if session.get('role') != 'super_admin':
            return jsonify({'error': '권한 없음'}), 403
        data = request.get_json() or {}
        lines = data.get('lines', [])
        if not isinstance(lines, list):
            return jsonify({'error': 'lines는 배열이어야 합니다'}), 400
        store.save_lines(lines)
        store.add_audit(session.get('username'), session.get('role'), 'LINES_CHANGED',
                        f'라인 {len(lines)}개 저장')
        return jsonify({'ok': True, 'lines': store.list_lines()})
    return jsonify(store.list_lines())

@app.route('/api/chat/<line_id>/messages')
def api_chat_messages(line_id):
    """메시지 히스토리"""
    limit = int(request.args.get('limit', 100))
    msgs = store.list_chat_messages(line_id, limit=limit)
    return jsonify(msgs)

@app.route('/api/chat/<line_id>/unread')
def api_chat_unread(line_id):
    """미읽음 메시지 수
       direction='web_to_wpf' → WPF가 받을 미읽음
       direction='wpf_to_web' → 웹이 받을 미읽음"""
    direction = request.args.get('direction', 'wpf_to_web')
    return jsonify({'unread': store.unread_count(line_id, direction)})

@app.route('/api/chat/<line_id>/send', methods=['POST'])
def api_chat_send(line_id):
    """메시지 전송. text 필드 또는 파일 첨부 가능.
       source='web' → web_to_wpf
       source='wpf' → wpf_to_web"""
    source = request.form.get('source') or (request.get_json(silent=True) or {}).get('source', 'web')

    # 웹에서 보낼 때는 로그인 필요
    if source == 'web' and 'user_id' not in session:
        return jsonify({'error': '로그인 필요'}), 401

    sender = request.form.get('sender') or session.get('username') or 'WPF Operator'
    sender_role = session.get('role') or ('operator' if source == 'wpf' else '-')

    content = ''
    file_name = None
    file_size = None

    if 'file' in request.files and request.files['file'].filename:
        f = request.files['file']
        try:
            file_name, file_size = store.save_chat_file(line_id, f, f.filename)
        except Exception as e:
            return jsonify({'error': f'파일 업로드 실패: {e}'}), 400
        content = request.form.get('content', '')
    else:
        # JSON 또는 form
        if request.is_json:
            data = request.get_json() or {}
            content = data.get('content', '')
        else:
            content = request.form.get('content', '')

    if not content and not file_name:
        return jsonify({'error': '메시지 또는 파일이 필요합니다'}), 400

    direction = 'web_to_wpf' if source == 'web' else 'wpf_to_web'
    msg = store.add_chat_message(line_id, sender, sender_role, direction, content, file_name, file_size)

    # Socket.IO로 양쪽에 broadcast
    socketio.emit('chat_message', msg)

    return jsonify({'ok': True, 'message': msg})

@app.route('/api/chat/<line_id>/read', methods=['POST'])
def api_chat_read(line_id):
    """읽음 처리. body: {'direction': 'web_to_wpf' or 'wpf_to_web'}"""
    data = request.get_json(silent=True) or {}
    direction = data.get('direction', 'web_to_wpf')
    cnt = store.mark_chat_read(line_id, direction)
    if cnt:
        socketio.emit('chat_read', {'line_id': line_id, 'direction': direction, 'count': cnt})
    return jsonify({'ok': True, 'marked': cnt})

@app.route('/api/chat/<line_id>/delete', methods=['POST'])
def api_chat_delete(line_id):
    """메시지 삭제. body: {'msg_id': 123} 또는 {'all': true}"""
    data = request.get_json(silent=True) or {}
    if data.get('all'):
        cnt = store.clear_chat_messages(line_id)
        store.add_audit(session.get('username'), session.get('role'), 'CHAT_CLEAR',
                        f'{line_id} 메시지 {cnt}개 전체 삭제')
        socketio.emit('chat_cleared', {'line_id': line_id})
        return jsonify({'ok': True, 'deleted': cnt})
    msg_id = data.get('msg_id')
    if not msg_id:
        return jsonify({'error': 'msg_id 또는 all 필요'}), 400
    ok = store.delete_chat_message(line_id, msg_id)
    if ok:
        socketio.emit('chat_deleted', {'line_id': line_id, 'msg_id': msg_id})
    return jsonify({'ok': ok})

@app.route('/api/chat/file/<filename>')
def api_chat_file_download(filename):
    """채팅 첨부 파일 다운로드 (WPF/웹 모두 접근)"""
    fp = store.get_chat_file_path(filename)
    if not fp:
        return jsonify({'error': '파일 없음'}), 404
    # 원본 이름 추출 (line-01_20260517_211230_abc123_원본.pdf → 원본.pdf)
    parts = filename.split('_', 4)
    download_name = parts[4] if len(parts) >= 5 else filename
    return send_file(fp, as_attachment=True, download_name=download_name)


@app.route('/api/cycle-time')
@login_required
def api_cycle_time():
    df = request.args.get('date_from') or None
    dt = request.args.get('date_to') or None
    ct = db.get_cycle_time(df=df, dt=dt)
    takt = store.get_policy().get('takt_time', 30)
    return jsonify({'cycle_time': ct, 'takt_time': takt})

@app.route('/api/system-status')
@login_required
def api_system_status():
    return jsonify(system_status)

@app.route('/api/system', methods=['POST'])
def api_system():
    data = request.get_json(); action = data.get('action',''); source = data.get('source','wpf')
    if action == 'start': system_status['status'] = 'running'
    elif action == 'estop': system_status['status'] = 'estop'
    elif action in ('stop', 'estop_clear'): system_status['status'] = 'stopped'
    socketio.emit('system_action', {'action': action})
    if source == 'web':
        socketio.emit('remote_command', {'action': action})

    # 비상정지 해제 시 미해결 ESTOP 알람 자동 처리
    if action == 'estop_clear':
        try:
            unresolved = db.get_unresolved_alarms() or []
            for a in unresolved:
                if dict(a).get('type') == 'ESTOP':
                    db.resolve_alarm(a['id'])
                    socketio.emit('alarm_resolved', {'id': a['id']})
        except Exception as e:
            logger.error(f"ESTOP 자동 해제 실패: {e}")

    # 감사 로그 (의미 있는 액션만)
    if action in ('start', 'stop', 'estop', 'estop_clear'):
        who = session.get('username') or data.get('username') or source
        role = session.get('role') or data.get('role', '-')
        store.add_audit(who, role, f'LINE_{action.upper()}', f'source={source}')

    return jsonify({'ok': True})

# ── 카메라 프레임 수신 (WPF → Flask) ──
@app.route('/api/camera-frame', methods=['POST'])
def api_camera_frame():
    if 'front' in request.files: latest_frames['front'] = request.files['front'].read()
    if 'back' in request.files: latest_frames['back'] = request.files['back'].read()
    import base64
    payload = {}
    if latest_frames['front']: payload['front'] = base64.b64encode(latest_frames['front']).decode()
    if latest_frames['back']: payload['back'] = base64.b64encode(latest_frames['back']).decode()
    if payload: socketio.emit('camera_frame', payload)
    return jsonify({'ok': True})

@app.route('/api/upload-image', methods=['POST'])
def api_upload_image():
    if 'file' not in request.files: return jsonify({'error': 'no file'}), 400
    f = request.files['file']; f.save(os.path.join(Config.IMAGE_DIR, f.filename))
    return jsonify({'ok': True})

@app.route('/images/<path:filename>')
@login_required
def serve_image(filename): return send_from_directory(Config.IMAGE_DIR, filename)

# ── 회원관리 API (전체관리자) ──
@app.route('/api/users', methods=['GET'])
@super_admin_required
def api_get_users():
    try:
        rows = db.get_all_users(); data = [dict(r) for r in rows] if rows else []
        for d in data:
            if d.get('created_at'): d['created_at'] = str(d['created_at'])
        return jsonify(data)
    except Exception as e:
        logger.error(f"회원목록 조회 실패: {e}")
        return jsonify([]), 200

@app.route('/api/users', methods=['POST'])
@super_admin_required
def api_create_user():
    data = request.get_json(); u = data.get('username','').strip(); p = data.get('password',''); r = data.get('role','operator')
    if not u or not p: return jsonify({'error': '아이디와 비밀번호를 입력하세요.'}), 400
    if r not in ('super_admin','admin','operator'): return jsonify({'error': '올바르지 않은 역할'}), 400
    if db.get_user(u): return jsonify({'error': '이미 존재하는 아이디'}), 400
    db.create_user(u, generate_password_hash(p), r)
    store.add_audit(session.get('username'), session.get('role'), 'USER_CREATED', f'아이디={u}, 역할={r}')
    return jsonify({'ok': True})

@app.route('/api/users/<int:uid>/role', methods=['PUT'])
@super_admin_required
def api_update_role(uid):
    r = request.get_json().get('role','')
    if r not in ('super_admin','admin','operator'): return jsonify({'error': '올바르지 않은 역할'}), 400
    db.update_user_role(uid, r)
    store.add_audit(session.get('username'), session.get('role'), 'USER_ROLE_CHANGED', f'#{uid} → {r}')
    return jsonify({'ok': True})

@app.route('/api/users/<int:uid>/password', methods=['PUT'])
@super_admin_required
def api_update_pw(uid):
    p = request.get_json().get('password','')
    if not p: return jsonify({'error': '비밀번호를 입력하세요.'}), 400
    db.update_user_password(uid, generate_password_hash(p))
    store.add_audit(session.get('username'), session.get('role'), 'USER_PW_CHANGED', f'#{uid}')
    return jsonify({'ok': True})

@app.route('/api/users/<int:uid>/username', methods=['PUT'])
@super_admin_required
def api_update_uname(uid):
    u = request.get_json().get('username','').strip()
    if not u: return jsonify({'error': '아이디를 입력하세요.'}), 400
    if db.get_user(u): return jsonify({'error': '이미 존재하는 아이디'}), 400
    db.update_username(uid, u)
    store.add_audit(session.get('username'), session.get('role'), 'USER_NAME_CHANGED', f'#{uid} → {u}')
    return jsonify({'ok': True})

@app.route('/api/users/<int:uid>', methods=['DELETE'])
@super_admin_required
def api_delete_user(uid):
    user = db.get_user_by_id(uid)
    if not user: return jsonify({'error': '존재하지 않는 사용자'}), 404
    if user['id'] == session.get('user_id'): return jsonify({'error': '자기 자신은 삭제 불가'}), 400
    db.delete_user(uid)
    store.add_audit(session.get('username'), session.get('role'), 'USER_DELETED', f"#{uid} ({user['username']})")
    return jsonify({'ok': True})

# ── Socket.IO ──
@socketio.on('connect')
def handle_connect(): emit('connected', {'status': 'ok'})

@socketio.on('camera_frame_upload')
def handle_camera_frame(data):
    """WPF에서 Socket.IO로 직접 받은 카메라 프레임 → 브라우저에 전달
       WPF는 camera_frame 이벤트를 수신하지 않으므로 전체 emit 안전"""
    payload = {}
    if data.get('front'): payload['front'] = data['front']
    if data.get('back'): payload['back'] = data['back']
    if payload:
        socketio.emit('camera_frame', payload)


def init_app():
    try:
        if not db.get_user('admin'):
            db.create_user('admin', generate_password_hash('admin'), 'super_admin')
            db.create_user('operator', generate_password_hash('1234'), 'operator')
            logger.info("기본 계정 생성: admin/admin(전체관리자), operator/1234(현장작업자)")
    except Exception as e: logger.error(f"초기화 실패: {e}")

if __name__ == '__main__':
    init_app()

    # ── 자동 정리 스케줄러 (매일 00:05에 실행) ──
    def auto_cleanup():
        while True:
            try:
                now = datetime.now()
                # 다음 00:05까지 대기 시간 계산
                tomorrow = now.replace(hour=0, minute=5, second=0, microsecond=0)
                if tomorrow <= now:
                    tomorrow += timedelta(days=1)
                wait_sec = (tomorrow - now).total_seconds()
                import time as _time
                _time.sleep(wait_sec)

                # 정리 실행
                policy = store.get_policy()
                ret = policy.get('retention', {})
                img_days = ret.get('images_days', 30)
                alarm_days = ret.get('alarms_days', 90)
                audit_days = ret.get('audit_days', 60)

                deleted_imgs = 0
                deleted_fns = []
                # 1) 이미지 정리
                cutoff = datetime.now().timestamp() - img_days * 86400
                if os.path.exists(Config.IMAGE_DIR):
                    for fn in os.listdir(Config.IMAGE_DIR):
                        fp = os.path.join(Config.IMAGE_DIR, fn)
                        try:
                            if os.path.isfile(fp) and os.path.getmtime(fp) < cutoff:
                                os.remove(fp); deleted_imgs += 1; deleted_fns.append(fn)
                        except: pass

                # 삭제된 이미지 DB 참조 제거
                if deleted_fns:
                    for fn in deleted_fns:
                        try:
                            db.execute("UPDATE inspection_log SET front_image='' WHERE front_image=%s", (fn,))
                            db.execute("UPDATE inspection_log SET back_image='' WHERE back_image=%s", (fn,))
                        except: pass

                # 2) 오래된 알람 정리 (DB)
                deleted_alarms = 0
                try:
                    import pymysql
                    from config import DB_CONFIG
                    conn = pymysql.connect(**DB_CONFIG)
                    with conn.cursor() as cur:
                        cur.execute("DELETE FROM alarm_log WHERE timestamp < NOW() - INTERVAL %s DAY", (alarm_days,))
                        deleted_alarms = cur.rowcount
                    conn.commit(); conn.close()
                except: pass

                # 3) 감사 로그 정리 (JSON)
                deleted_audit = 0
                try:
                    audit = store._load('audit.json', [])
                    cutoff_dt = datetime.now() - timedelta(days=audit_days)
                    cutoff_str = cutoff_dt.strftime('%Y-%m-%d %H:%M:%S')
                    before = len(audit)
                    audit = [a for a in audit if a.get('time', '') >= cutoff_str]
                    deleted_audit = before - len(audit)
                    if deleted_audit > 0:
                        store._save('audit.json', audit)
                except: pass

                # 4) 오래된 채팅 파일 정리 (30일 초과)
                deleted_chat = 0
                chat_dir = store.CHAT_FILE_DIR
                chat_cutoff = datetime.now().timestamp() - 30 * 86400
                if os.path.exists(chat_dir):
                    for fn in os.listdir(chat_dir):
                        fp = os.path.join(chat_dir, fn)
                        try:
                            if os.path.isfile(fp) and os.path.getmtime(fp) < chat_cutoff:
                                os.remove(fp); deleted_chat += 1
                        except: pass

                summary = f"[자동 정리] 이미지 {deleted_imgs}개, 알람 {deleted_alarms}개, 감사로그 {deleted_audit}개, 채팅파일 {deleted_chat}개 삭제"
                store.add_audit('SYSTEM', 'system', 'AUTO_CLEANUP', summary)
                print(summary)

            except Exception as e:
                print(f'[AUTO_CLEANUP] error: {e}')
                import time as _time
                _time.sleep(3600)  # 에러 시 1시간 후 재시도

    threading.Thread(target=auto_cleanup, daemon=True).start()
    logger.info("자동 정리 스케줄러 시작 (매일 00:05)")

    socketio.run(app, debug=True, host='0.0.0.0', port=5000, allow_unsafe_werkzeug=True)
