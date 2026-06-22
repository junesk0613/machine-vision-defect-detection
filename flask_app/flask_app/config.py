import os
class Config:
    SECRET_KEY = os.environ.get('SECRET_KEY', 'pcb-inspection-secret')
    MYSQL_HOST = os.environ.get('MYSQL_HOST', 'localhost')
    MYSQL_PORT = int(os.environ.get('MYSQL_PORT', 3306))
    MYSQL_USER = os.environ.get('MYSQL_USER', 'root')
    MYSQL_PASSWORD = os.environ.get('MYSQL_PASSWORD', 'YOUR_DB_PASSWORD')  # ← 비밀번호 입력
    MYSQL_DB = os.environ.get('MYSQL_DB', 'pcb_inspection')
    IMAGE_DIR = os.environ.get('IMAGE_DIR', 'inspection_images')

DB_CONFIG = {
    'host': os.environ.get('MYSQL_HOST', 'localhost'),
    'port': int(os.environ.get('MYSQL_PORT', 3306)),
    'user': os.environ.get('MYSQL_USER', 'root'),
    'password': os.environ.get('MYSQL_PASSWORD', 'j41005010!'),
    'database': os.environ.get('MYSQL_DB', 'pcb_inspection'),
    'charset': 'utf8mb4',
}
