"""
설정/공지/감사로그/이미지 라벨 합성을 위한 통합 헬퍼
DB 마이그레이션 없이 JSON 파일로 동작
"""
import os, json, threading
from datetime import datetime, timedelta

DATA_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'data')
os.makedirs(DATA_DIR, exist_ok=True)

_lock = threading.Lock()

# ══════════════════════════════════════
# 기본 설정값
# ══════════════════════════════════════
DEFAULT_POLICY = {
    'factory': {
        'company': '○○전자',
        'factory': '○○공장',
        'line': 'LINE 01',
        'model': 'RELAY-2CH-5V'
    },
    'alarm_threshold': {
        'hourly_ng_max': 10,
        'daily_defect_pct_max': 5.0
    },
    'email_recipients': [],
    'retention': {
        'images_days': 30,
        'alarms_days': 90,
        'audit_days': 60
    },
    'takt_time': 30
}

# ══════════════════════════════════════
# 파일 I/O
# ══════════════════════════════════════
def _path(name):
    return os.path.join(DATA_DIR, name)

def _load(name, default):
    p = _path(name)
    if not os.path.exists(p):
        return default
    try:
        with open(p, 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return default

def _save(name, data):
    p = _path(name)
    with _lock:
        with open(p, 'w', encoding='utf-8') as f:
            json.dump(data, f, ensure_ascii=False, indent=2)

# ══════════════════════════════════════
# 정책 설정 (공장정보 + 임계값 + 이메일 + 보존기간)
# ══════════════════════════════════════
def get_policy():
    p = _load('app_settings.json', None)
    if p is None:
        _save('app_settings.json', DEFAULT_POLICY)
        return DEFAULT_POLICY
    # 누락된 키 보완
    for k, v in DEFAULT_POLICY.items():
        if k not in p:
            p[k] = v
        elif isinstance(v, dict):
            for k2, v2 in v.items():
                if k2 not in p[k]:
                    p[k][k2] = v2
    return p

def save_policy(p):
    _save('app_settings.json', p)

# ══════════════════════════════════════
# 공지사항
# ══════════════════════════════════════
def list_notices(active_only=False):
    data = _load('notices.json', [])
    if active_only:
        return [n for n in data if n.get('active', True)]
    return data

def add_notice(title, content, created_by):
    data = _load('notices.json', [])
    new_id = (max([n.get('id', 0) for n in data]) + 1) if data else 1
    notice = {
        'id': new_id,
        'title': title,
        'content': content,
        'active': True,
        'created_at': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
        'created_by': created_by
    }
    data.insert(0, notice)
    _save('notices.json', data)
    return notice

def update_notice(nid, title=None, content=None, active=None):
    data = _load('notices.json', [])
    for n in data:
        if n['id'] == nid:
            if title is not None: n['title'] = title
            if content is not None: n['content'] = content
            if active is not None: n['active'] = active
            _save('notices.json', data)
            return n
    return None

def delete_notice(nid):
    data = _load('notices.json', [])
    data = [n for n in data if n['id'] != nid]
    _save('notices.json', data)

# ══════════════════════════════════════
# 감사 로그 (Audit Log)
# ══════════════════════════════════════
def add_audit(user, role, action, detail=''):
    data = _load('audit.json', [])
    entry = {
        'time': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
        'user': user or 'system',
        'role': role or '-',
        'action': action,
        'detail': detail or ''
    }
    data.insert(0, entry)
    # 보존 기간 초과한 것 삭제
    try:
        days = get_policy()['retention']['audit_days']
        cutoff = datetime.now() - timedelta(days=days)
        data = [d for d in data if datetime.strptime(d['time'], '%Y-%m-%d %H:%M:%S') >= cutoff]
    except Exception:
        # 안전 장치 — 5000건으로 제한
        data = data[:5000]
    _save('audit.json', data)

def list_audit(limit=200, user=None, action=None, page=1):
    data = _load('audit.json', [])
    if user:
        data = [d for d in data if d['user'] == user]
    if action:
        data = [d for d in data if d['action'] == action]
    total = len(data)
    start = (page - 1) * limit
    end = start + limit
    return {
        'data': data[start:end],
        'total': total,
        'page': page,
        'pages': max(1, (total + limit - 1) // limit)
    }

# ══════════════════════════════════════
# 이미지 라벨 합성 (Pillow 사용)
# ══════════════════════════════════════
def make_labeled_image(image_path, result, defect_type=None, front_conf=None, back_conf=None):
    """원본 이미지에 OK/NG 라벨, 신뢰도를 워터마크처럼 그려서 PIL Image 반환"""
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        return None

    if not os.path.exists(image_path):
        return None

    try:
        img = Image.open(image_path).convert('RGB')
        draw = ImageDraw.Draw(img, 'RGBA')
        W, H = img.size

        # 폰트 로드 (시스템 폰트 시도)
        font_paths = [
            '/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc',
            '/usr/share/fonts/truetype/noto/NotoSansCJK-Bold.ttc',
            'C:/Windows/Fonts/malgunbd.ttf',
            'C:/Windows/Fonts/malgun.ttf',
            '/System/Library/Fonts/AppleSDGothicNeo.ttc',
        ]
        font_big = None
        font_small = None
        for fp in font_paths:
            if os.path.exists(fp):
                try:
                    font_big = ImageFont.truetype(fp, size=max(48, W // 18))
                    font_small = ImageFont.truetype(fp, size=max(22, W // 40))
                    break
                except Exception:
                    continue
        if font_big is None:
            font_big = ImageFont.load_default()
            font_small = ImageFont.load_default()

        # 결과에 따라 색상
        is_ok = (result == 'OK')
        color = (34, 197, 94) if is_ok else (239, 68, 68)
        label = 'OK' if is_ok else 'NG'

        # 상단 라벨 배너
        banner_h = max(60, H // 10)
        draw.rectangle([(0, 0), (W, banner_h)], fill=(0, 0, 0, 200))
        draw.rectangle([(0, 0), (8, banner_h)], fill=color)
        draw.text((24, banner_h // 2 - max(48, W // 18) // 2), label, fill=color, font=font_big)

        # 하단 상세 정보 배너
        details = []
        if defect_type and not is_ok:
            details.append(f'유형: {defect_type}')
        if front_conf is not None:
            details.append(f'F: {float(front_conf)*100:.1f}%')
        if back_conf is not None:
            details.append(f'B: {float(back_conf)*100:.1f}%')

        if details:
            detail_h = max(50, H // 14)
            draw.rectangle([(0, H - detail_h), (W, H)], fill=(0, 0, 0, 200))
            txt = '  ·  '.join(details)
            draw.text((20, H - detail_h + (detail_h - max(22, W // 40)) // 2),
                      txt, fill=(255, 255, 255), font=font_small)

        # 우상단 타임스탬프
        ts = datetime.now().strftime('%Y-%m-%d %H:%M:%S')
        draw.text((W - 280, banner_h + 8), ts, fill=(200, 200, 200), font=font_small)

        return img
    except Exception as e:
        print(f'라벨 이미지 생성 실패: {e}')
        return None

# ══════════════════════════════════════
# 채팅 메시지 (Web ↔ WPF)
# ══════════════════════════════════════
CHAT_FILE_DIR = os.path.join(DATA_DIR, 'chat_files')
os.makedirs(CHAT_FILE_DIR, exist_ok=True)

def list_lines():
    """채팅 가능한 라인 목록 — 어떤 경우에도 최소 1개 라인 보장"""
    # 1) 커스텀 라인 파일 우선
    try:
        custom = _load('lines.json', None)
        if custom and isinstance(custom, list) and len(custom) > 0:
            return custom
    except Exception:
        pass

    # 2) 정책의 factory.line 기반 단일 라인
    try:
        pol = get_policy()
        f = pol.get('factory', {}) if isinstance(pol, dict) else {}
        return [{
            'id': 'line-01',
            'name': f.get('line') or 'LINE 01',
            'factory': f.get('factory') or '○○공장',
            'model': f.get('model') or '-'
        }]
    except Exception as e:
        print(f'[store] list_lines 폴백: {e}')

    # 3) 최후의 폴백
    return [{'id': 'line-01', 'name': 'LINE 01', 'factory': '○○공장', 'model': '-'}]

def save_lines(lines):
    """라인 목록 저장. lines는 [{id, name, factory, model}, ...] 형식"""
    if not isinstance(lines, list):
        return False
    # 정규화
    normalized = []
    seen_ids = set()
    for i, l in enumerate(lines):
        if not isinstance(l, dict): continue
        lid = (l.get('id') or '').strip()
        if not lid or lid in seen_ids:
            lid = f'line-{i+1:02d}'
            while lid in seen_ids:
                i += 1
                lid = f'line-{i+1:02d}'
        seen_ids.add(lid)
        normalized.append({
            'id': lid,
            'name': (l.get('name') or '').strip() or lid.upper(),
            'factory': (l.get('factory') or '').strip(),
            'model': (l.get('model') or '').strip()
        })
    _save('lines.json', normalized)
    return True

def list_chat_messages(line_id, limit=100):
    data = _load(f'chat_{line_id}.json', [])
    return data[-limit:] if len(data) > limit else data

def add_chat_message(line_id, sender, sender_role, direction, content, file_name=None, file_size=None):
    """direction: 'web_to_wpf' or 'wpf_to_web'"""
    data = _load(f'chat_{line_id}.json', [])
    new_id = (max([m.get('id', 0) for m in data]) + 1) if data else 1
    msg = {
        'id': new_id,
        'line_id': line_id,
        'sender': sender,
        'sender_role': sender_role,
        'direction': direction,
        'content': content or '',
        'file_name': file_name,
        'file_size': file_size,
        'timestamp': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
        'read': False
    }
    data.append(msg)
    # 보관 한도: 라인당 1000개
    if len(data) > 1000:
        data = data[-1000:]
    _save(f'chat_{line_id}.json', data)
    return msg

def mark_chat_read(line_id, direction_to_mark):
    """direction_to_mark: 읽음 처리할 메시지 방향 (예: WPF가 web_to_wpf를 읽음 처리)"""
    data = _load(f'chat_{line_id}.json', [])
    changed = 0
    for m in data:
        if m['direction'] == direction_to_mark and not m.get('read'):
            m['read'] = True
            changed += 1
    if changed:
        _save(f'chat_{line_id}.json', data)
    return changed

def delete_chat_message(line_id, msg_id):
    """메시지 1개 삭제. 첨부 파일도 같이 삭제"""
    data = _load(f'chat_{line_id}.json', [])
    target = None
    for m in data:
        if m.get('id') == msg_id:
            target = m
            break
    if not target:
        return False
    # 첨부 파일 삭제
    if target.get('file_name'):
        fp = os.path.join(CHAT_FILE_DIR, target['file_name'])
        if os.path.exists(fp):
            try: os.remove(fp)
            except: pass
    data = [m for m in data if m.get('id') != msg_id]
    _save(f'chat_{line_id}.json', data)
    return True

def clear_chat_messages(line_id):
    """라인의 모든 메시지 삭제"""
    data = _load(f'chat_{line_id}.json', [])
    # 첨부 파일 모두 삭제
    for m in data:
        if m.get('file_name'):
            fp = os.path.join(CHAT_FILE_DIR, m['file_name'])
            if os.path.exists(fp):
                try: os.remove(fp)
                except: pass
    _save(f'chat_{line_id}.json', [])
    return len(data)

def unread_count(line_id, direction):
    data = _load(f'chat_{line_id}.json', [])
    return sum(1 for m in data if m['direction'] == direction and not m.get('read'))

def save_chat_file(line_id, fileobj, original_name):
    """파일을 chat_files/ 에 저장하고 저장된 파일명 반환"""
    import re, uuid
    # 안전한 파일명
    safe = re.sub(r'[^A-Za-z0-9가-힣._-]', '_', original_name)
    if len(safe) > 80:
        ext = safe.rsplit('.', 1)[-1] if '.' in safe else ''
        safe = safe[:60] + ('.' + ext if ext else '')
    unique = f"{line_id}_{datetime.now().strftime('%Y%m%d_%H%M%S')}_{uuid.uuid4().hex[:6]}_{safe}"
    fp = os.path.join(CHAT_FILE_DIR, unique)
    fileobj.save(fp)
    return unique, os.path.getsize(fp)

def get_chat_file_path(filename):
    fp = os.path.join(CHAT_FILE_DIR, filename)
    return fp if os.path.exists(fp) else None
