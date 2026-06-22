-- PCB 양면 검사 시스템 DB 초기화 (최종)
CREATE DATABASE IF NOT EXISTS pcb_inspection DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE pcb_inspection;

CREATE TABLE IF NOT EXISTS users (
    id INT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(80) UNIQUE NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    role ENUM('super_admin','admin','operator') DEFAULT 'operator',
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS active_sessions (
    id INT AUTO_INCREMENT PRIMARY KEY,
    user_id INT NOT NULL,
    sid VARCHAR(255) UNIQUE NOT NULL,
    login_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS inspection_log (
    id INT AUTO_INCREMENT PRIMARY KEY,
    timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
    result ENUM('OK','NG') NOT NULL,
    defect_type VARCHAR(100),
    serial_number VARCHAR(100),
    front_image VARCHAR(255),
    back_image VARCHAR(255),
    front_conf DECIMAL(5,3) DEFAULT 0,
    back_conf DECIMAL(5,3) DEFAULT 0,
    INDEX idx_ts (timestamp), INDEX idx_result (result), INDEX idx_sn (serial_number)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS alarm_log (
    id INT AUTO_INCREMENT PRIMARY KEY,
    timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
    type VARCHAR(50) NOT NULL DEFAULT 'SYSTEM',
    detail TEXT,
    resolved TINYINT DEFAULT 0,
    resolved_at DATETIME,
    INDEX idx_ts (timestamp)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS sensor_log (
    id INT AUTO_INCREMENT PRIMARY KEY,
    timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
    temperature DECIMAL(5,2),
    humidity DECIMAL(5,2),
    conveyor_rpm DECIMAL(8,2),
    box_ng_level DECIMAL(5,2) DEFAULT 0,
    box_ok_level DECIMAL(5,2) DEFAULT 0,
    INDEX idx_ts (timestamp)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS system_settings (
    id INT AUTO_INCREMENT PRIMARY KEY,
    yolo_conf DECIMAL(5,3) DEFAULT 0.500,
    temp_min DECIMAL(5,2) DEFAULT 15.00,
    temp_max DECIMAL(5,2) DEFAULT 35.00,
    humid_min DECIMAL(5,2) DEFAULT 30.00,
    humid_max DECIMAL(5,2) DEFAULT 70.00,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB;

INSERT IGNORE INTO system_settings (id) VALUES (1);

DELIMITER //

-- 검사
CREATE PROCEDURE IF NOT EXISTS sp_create_inspection(IN p_result VARCHAR(10),IN p_defect_type VARCHAR(100),IN p_front_image VARCHAR(255),IN p_back_image VARCHAR(255),IN p_front_conf DECIMAL(5,3),IN p_back_conf DECIMAL(5,3))
BEGIN INSERT INTO inspection_log (result,defect_type,front_image,back_image,front_conf,back_conf) VALUES (p_result,p_defect_type,p_front_image,p_back_image,p_front_conf,p_back_conf); SELECT LAST_INSERT_ID() AS id; END //

CREATE PROCEDURE IF NOT EXISTS sp_get_inspection_history(IN p_result VARCHAR(10),IN p_date_from DATE,IN p_date_to DATE,IN p_limit INT,IN p_offset INT)
BEGIN SELECT SQL_CALC_FOUND_ROWS id,timestamp,result,defect_type,front_image,back_image,front_conf,back_conf FROM inspection_log WHERE (p_result IS NULL OR p_result='' OR result=p_result) AND (p_date_from IS NULL OR DATE(timestamp)>=p_date_from) AND (p_date_to IS NULL OR DATE(timestamp)<=p_date_to) ORDER BY timestamp DESC LIMIT p_limit OFFSET p_offset; SELECT FOUND_ROWS() AS total; END //

CREATE PROCEDURE IF NOT EXISTS sp_get_latest_inspection()
BEGIN SELECT * FROM inspection_log ORDER BY timestamp DESC LIMIT 1; END //

CREATE PROCEDURE IF NOT EXISTS sp_get_today_stats()
BEGIN SELECT COUNT(*) AS total, SUM(CASE WHEN result='OK' THEN 1 ELSE 0 END) AS ok_count, SUM(CASE WHEN result='NG' THEN 1 ELSE 0 END) AS ng_count, ROUND(SUM(CASE WHEN result='OK' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0)*100,1) AS yield_rate FROM inspection_log WHERE DATE(timestamp)=CURDATE(); END //

CREATE PROCEDURE IF NOT EXISTS sp_get_defect_stats(IN p_date_from DATE,IN p_date_to DATE)
BEGIN SELECT defect_type, COUNT(*) AS cnt FROM inspection_log WHERE result='NG' AND (p_date_from IS NULL OR DATE(timestamp)>=p_date_from) AND (p_date_to IS NULL OR DATE(timestamp)<=p_date_to) GROUP BY defect_type ORDER BY cnt DESC; END //

CREATE PROCEDURE IF NOT EXISTS sp_get_hourly_trend()
BEGIN SELECT HOUR(timestamp) AS hour, COUNT(*) AS total, SUM(CASE WHEN result='OK' THEN 1 ELSE 0 END) AS ok_count, SUM(CASE WHEN result='NG' THEN 1 ELSE 0 END) AS ng_count FROM inspection_log WHERE DATE(timestamp)=CURDATE() GROUP BY HOUR(timestamp) ORDER BY hour; END //

-- 센서
CREATE PROCEDURE IF NOT EXISTS sp_create_sensor(IN p_temp DECIMAL(5,2),IN p_humid DECIMAL(5,2),IN p_rpm DECIMAL(8,2),IN p_ng DECIMAL(5,2),IN p_ok DECIMAL(5,2))
BEGIN INSERT INTO sensor_log (temperature,humidity,conveyor_rpm,box_ng_level,box_ok_level) VALUES (p_temp,p_humid,p_rpm,p_ng,p_ok); END //

CREATE PROCEDURE IF NOT EXISTS sp_get_sensor_history(IN p_hours INT)
BEGIN SELECT timestamp,temperature,humidity,conveyor_rpm,box_ng_level,box_ok_level FROM sensor_log WHERE timestamp>=DATE_SUB(NOW(),INTERVAL p_hours HOUR) ORDER BY timestamp ASC; END //

CREATE PROCEDURE IF NOT EXISTS sp_get_latest_sensor()
BEGIN SELECT * FROM sensor_log ORDER BY timestamp DESC LIMIT 1; END //

-- 알람
CREATE PROCEDURE IF NOT EXISTS sp_create_alarm(IN p_type VARCHAR(50),IN p_detail TEXT)
BEGIN INSERT INTO alarm_log (type,detail) VALUES (p_type,p_detail); SELECT LAST_INSERT_ID() AS id; END //

CREATE PROCEDURE IF NOT EXISTS sp_get_unresolved_alarms()
BEGIN SELECT * FROM alarm_log WHERE resolved=0 ORDER BY timestamp DESC; END //

CREATE PROCEDURE IF NOT EXISTS sp_get_alarm_history()
BEGIN SELECT * FROM alarm_log ORDER BY timestamp DESC LIMIT 100; END //

CREATE PROCEDURE IF NOT EXISTS sp_resolve_alarm(IN p_id INT)
BEGIN UPDATE alarm_log SET resolved=1, resolved_at=NOW() WHERE id=p_id; END //

-- 설정
CREATE PROCEDURE IF NOT EXISTS sp_get_settings()
BEGIN SELECT * FROM system_settings WHERE id=1; END //

CREATE PROCEDURE IF NOT EXISTS sp_update_settings(IN p_yolo DECIMAL(5,3),IN p_tmin DECIMAL(5,2),IN p_tmax DECIMAL(5,2),IN p_hmin DECIMAL(5,2),IN p_hmax DECIMAL(5,2))
BEGIN UPDATE system_settings SET yolo_conf=p_yolo,temp_min=p_tmin,temp_max=p_tmax,humid_min=p_hmin,humid_max=p_hmax WHERE id=1; END //

-- 회원관리
CREATE PROCEDURE IF NOT EXISTS sp_get_all_users()
BEGIN SELECT id,username,role,created_at FROM users ORDER BY created_at DESC; END //

CREATE PROCEDURE IF NOT EXISTS sp_create_user(IN p_username VARCHAR(80),IN p_password_hash VARCHAR(255),IN p_role VARCHAR(20))
BEGIN INSERT INTO users (username,password_hash,role) VALUES (p_username,p_password_hash,p_role); SELECT LAST_INSERT_ID() AS id; END //

CREATE PROCEDURE IF NOT EXISTS sp_update_user_role(IN p_id INT,IN p_role VARCHAR(20))
BEGIN UPDATE users SET role=p_role WHERE id=p_id; END //

CREATE PROCEDURE IF NOT EXISTS sp_update_user_password(IN p_id INT,IN p_password_hash VARCHAR(255))
BEGIN UPDATE users SET password_hash=p_password_hash WHERE id=p_id; END //

CREATE PROCEDURE IF NOT EXISTS sp_update_username(IN p_id INT,IN p_username VARCHAR(80))
BEGIN UPDATE users SET username=p_username WHERE id=p_id; END //

CREATE PROCEDURE IF NOT EXISTS sp_delete_user(IN p_id INT)
BEGIN DELETE FROM active_sessions WHERE user_id=p_id; DELETE FROM users WHERE id=p_id; END //

-- 날짜별 통계
CREATE PROCEDURE IF NOT EXISTS sp_get_daily_stats(IN p_date_from DATE,IN p_date_to DATE)
BEGIN SELECT DATE(timestamp) AS date, COUNT(*) AS total, SUM(CASE WHEN result='OK' THEN 1 ELSE 0 END) AS ok_count, SUM(CASE WHEN result='NG' THEN 1 ELSE 0 END) AS ng_count FROM inspection_log WHERE (p_date_from IS NULL OR DATE(timestamp)>=p_date_from) AND (p_date_to IS NULL OR DATE(timestamp)<=p_date_to) GROUP BY DATE(timestamp) ORDER BY date; END //

-- 불량유형별 퍼센트
CREATE PROCEDURE IF NOT EXISTS sp_get_defect_percent(IN p_date_from DATE,IN p_date_to DATE)
BEGIN SELECT defect_type, COUNT(*) AS cnt, ROUND(COUNT(*)*100.0/(SELECT COUNT(*) FROM inspection_log WHERE result='NG' AND (p_date_from IS NULL OR DATE(timestamp)>=p_date_from) AND (p_date_to IS NULL OR DATE(timestamp)<=p_date_to)),1) AS pct FROM inspection_log WHERE result='NG' AND (p_date_from IS NULL OR DATE(timestamp)>=p_date_from) AND (p_date_to IS NULL OR DATE(timestamp)<=p_date_to) GROUP BY defect_type ORDER BY cnt DESC; END //

DELIMITER ;

-- ═══════════════════════════════════════════
-- 기존 DB 마이그레이션 (이미 테이블 있을 때)
-- ═══════════════════════════════════════════
-- serial_number 컬럼 추가 (없으면)
SET @col_exists = (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='inspection_log' AND COLUMN_NAME='serial_number');
SET @sql = IF(@col_exists=0, 'ALTER TABLE inspection_log ADD COLUMN serial_number VARCHAR(100) AFTER defect_type, ADD INDEX idx_sn (serial_number)', 'SELECT 1');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- alarm_log type 컬럼을 ENUM → VARCHAR로 변경 (새 알람 타입 지원)
ALTER TABLE alarm_log MODIFY COLUMN type VARCHAR(50) NOT NULL DEFAULT 'SYSTEM';
