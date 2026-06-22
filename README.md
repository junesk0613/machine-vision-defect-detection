# 머신비전 활용 불량 검출 시스템

> 스텝모터 컨베이어와 AI 비전을 결합해 **PCB 양면을 자동으로 촬영·검사하고 양품/불량을 분류**하는 통합 자동화 검사 시스템

![C#](https://img.shields.io/badge/C%23-.NET%208-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF-0078D6)
![Python](https://img.shields.io/badge/Python-Flask-3776AB)
![OpenCV](https://img.shields.io/badge/Vision-OpenCV-5C3EE8)
![YOLOv8](https://img.shields.io/badge/AI-YOLOv8%20%7C%20ONNX-00FFFF)
![MySQL](https://img.shields.io/badge/DB-MySQL-4479A1)
![Arduino](https://img.shields.io/badge/HW-Arduino-00979D)

---

## 📋 프로젝트 개요

PCB 검사 현장의 육안 검사는 작업자 숙련도에 따라 품질 편차가 발생합니다. 본 프로젝트는 **AI 비전 기반 자동 검사로 균일한 품질 기준**을 적용하고, 촬영 → 판정 → 분류 → 데이터 관리까지 전 과정을 통합한 검사 시스템입니다.

- **수행 기간**: 2026.05.12 ~ 2026.06.19
- **팀 구성**: 총 5명 (팀장)
- **검사 대상**: PCB 앞면(부품 실장) / 뒷면(납땜 상태)

## ✨ 주요 기능

- **AI 불량 검출**: YOLOv8s(ONNX Runtime)로 PCB 앞면 부품 누락 등 13개 클래스 판정
- **뒷면 검사**: OpenCV HSV 분석으로 납땜 그을림(burn) 면적 기반 판정
- **시리얼 인식**: OCR로 PCB 시리얼 번호 자동 인식 및 이력 추적
- **자동 분류**: 양품/불량 판정에 따라 컨베이어·실린더·로봇으로 분류 및 적재
- **실시간 모니터링**: 웹 대시보드에서 검사 현황·센서·영상 실시간 확인
- **생산 통계**: 수율·불량률·불량 유형·Cycle/Takt Time 시각화
- **권한 관리**: 전체관리자 / 관리자 / 현장작업자 3단계 역할 분리

## 🏗️ 시스템 아키텍처

```
[카메라] → [WPF 현장 운영 프로그램] ──RS-232──→ [중앙 아두이노] ──TCP/IP──→ [현장 장비]
              │  (공정 제어 허브)                                          (팔레트/AGV/로봇)
              │  · YOLO(ONNX) 앞면 판정
              │  · OpenCV 뒷면 검사
              │  · OCR 시리얼 인식
              │
       Socket.IO(실시간) / HTTP(저장)
              │
              ▼
        [Flask 백엔드] ──→ [MySQL DB] / [웹 대시보드]
         (데이터 저장·중계)
```

- **WPF**가 카메라·AI 판정·장비 제어를 담당하는 **단일 공정 제어 허브**
- **Flask**는 데이터 저장·조회·실시간 중계 전담 (제어에는 관여하지 않음)
- 실시간 데이터는 **Socket.IO**, 단발성 저장 요청은 **HTTP** 로 분리

## 🛠️ 기술 스택

| 구분 | 기술 |
|------|------|
| 현장 운영 (HMI) | C# (.NET 8), WPF, OpenCvSharp |
| AI 비전 | YOLOv8s, ONNX Runtime, OpenCV, Tesseract OCR |
| 백엔드 | Python, Flask, Flask-SocketIO |
| 데이터베이스 | MySQL, JSON |
| 웹 | HTML, CSS, JavaScript |
| 펌웨어 / 통신 | Arduino, RS-232, TCP/IP |

## 📁 폴더 구조

```
.
├── WPF/                    # 현장 운영 프로그램 (C# HMI) — 공정 제어 허브
│   ├── CameraService.cs    #   카메라 영상 캡처
│   ├── YoloService.cs      #   YOLO(ONNX) 추론
│   ├── OcrService.cs       #   시리얼 번호 OCR
│   └── MainWindow.xaml.cs  #   메인 UI / 제어 로직
│
├── flask_app/              # 백엔드 서버 + 웹 대시보드
│   └── flask_app/
│       ├── app.py          #   Flask + Socket.IO 서버
│       ├── db_manager.py   #   MySQL 관리
│       ├── store.py        #   데이터 처리
│       ├── templates/      #   대시보드·검사이력·생산통계 등 화면
│       └── static/         #   CSS / JS
│
├── 중앙아두이노/            # 중앙 제어 펌웨어
│   ├── Conveyor.ino        #   컨베이어(스텝모터)
│   ├── Cylinder.ino        #   불량 배출 실린더
│   └── AGV.ino             #   AGV 운반
│
└── 팔렛디팔렛타이져/        # 디팔레타이저 / 팔레타이저 로봇 제어
    ├── Depalletizer.ino / .py
    └── Palletizer.ino / .py
```



---

*본 프로젝트는 졸업 작품으로 진행되었으며, 데이터베이스 접속 정보 등 민감 정보는 제거되어 있습니다.*
