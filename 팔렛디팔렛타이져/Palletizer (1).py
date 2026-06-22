from picamera2 import Picamera2
import cv2
import numpy as np
import serial
import time

# 아두이노 시리얼 연결 (리셋 방지)
arduino = serial.Serial('/dev/arduino', 9600, timeout=1, dsrdtr=False, rtscts=False)
arduino.setDTR(False)
time.sleep(2)

picam2 = Picamera2()
picam2.start()

detect_count = 0
last_detect_time = 0
COOLDOWN = 5

while True:
    time.sleep(0.1)

    frame = picam2.capture_array()
    frame = cv2.cvtColor(frame, cv2.COLOR_RGB2BGR)

    h, w = frame.shape[:2]
    roi_w, roi_h = 200, 200
    x1 = w//2 - roi_w//2
    y1 = h//2 - roi_h//2
    x2 = w//2 + roi_w//2
    y2 = h//2 + roi_h//2

    roi = frame[y1:y2, x1:x2]
    hsv = cv2.cvtColor(roi, cv2.COLOR_BGR2HSV)
    lower_green = np.array([35, 30, 30])
    upper_green = np.array([90, 255, 255])
    mask = cv2.inRange(hsv, lower_green, upper_green)
    green_ratio = cv2.countNonZero(mask) / (roi_w * roi_h)

    now = time.time()
    cooldown_remaining = COOLDOWN - (now - last_detect_time)
    in_cooldown = cooldown_remaining > 0

    if green_ratio > 0.5:
        detect_count += 1
    else:
        detect_count = 0

    if detect_count >= 5 and not in_cooldown:
        print("PCB 감지! 신호 전송")
        try:
            arduino.write(b'1')
            last_detect_time = time.time()
            detect_count = 0
        except:
            print("시리얼 오류 - 재연결 시도")
            try:
                arduino.close()
                time.sleep(1)
                arduino = serial.Serial('/dev/arduino', 9600, timeout=1, dsrdtr=False, rtscts=False)
                arduino.setDTR(False)
                time.sleep(2)
            except:
                pass

    print(f"Green: {green_ratio:.2f} | 쿨다운: {max(0, cooldown_remaining):.1f}s")

picam2.stop()
arduino.close()