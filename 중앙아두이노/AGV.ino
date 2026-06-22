# pcb-automation-system
AI 비전 기반 PCB 불량 검출 및 자동 분류·운반 시스템
// ===================== Arduino UNO + L298N 최종 코드 =====================
// 전원 ON -> 2분 15초 정지 -> 부저 3번 -> 0.7초 소프트 후진 -> 왼쪽으로 반바퀴보다 살짝 덜 회전 -> 계속 직진
// ENA, ENB 점퍼캡 빼고 사용해야 속도 조절됩니다.

// ===================== 부저 핀 =====================
#define BUZZER_PIN 4

// ===================== L298N Enable 핀 =====================
#define ENA 5
#define ENB 6

// ===================== L298N 입력 핀 =====================
#define IN1 7
#define IN2 8
#define IN3 9
#define IN4 10

// ===================== 속도 설정 =====================
const int MOTOR_SPEED = 140;       // 전진/회전 속도
const int BACKWARD_SPEED = 120;    // 후진만 조금 더 소프트하게

// ===================== 시간 설정 =====================
const unsigned long WAIT_BEFORE_START_MS = 135000;  // 2분 15초
const unsigned long BACKWARD_MS = 400;              // 후진 0.7초
const unsigned long SPIN_180_MS = 300;              // 회전 시간 그대로

// ===================== 부저 3번 =====================
void beepStart() {
  for (int i = 0; i < 3; i++) {
    tone(BUZZER_PIN, 1000);
    delay(250);
    noTone(BUZZER_PIN);
    delay(250);
  }
}

// ===================== 모터 속도 적용 =====================
void setSpeed(int speedValue) {
  analogWrite(ENA, speedValue);
  analogWrite(ENB, speedValue);
}

// ===================== 모터 정지 =====================
void stopMotor() {
  analogWrite(ENA, 0);
  analogWrite(ENB, 0);

  digitalWrite(IN1, LOW);
  digitalWrite(IN2, LOW);
  digitalWrite(IN3, LOW);
  digitalWrite(IN4, LOW);
}

// ===================== 두 바퀴 후진 =====================
// 형님 기준: 이 조합이 실제 후진
void backwardBoth() {
  setSpeed(BACKWARD_SPEED);

  // 왼쪽 바퀴 후진
  digitalWrite(IN1, HIGH);
  digitalWrite(IN2, LOW);

  // 오른쪽 바퀴 후진
  digitalWrite(IN3, HIGH);
  digitalWrite(IN4, LOW);
}

// ===================== 두 바퀴 전진 =====================
// 형님 기준: 이 조합이 실제 전진
void forwardBoth() {
  setSpeed(MOTOR_SPEED);

  // 왼쪽 바퀴 전진
  digitalWrite(IN1, LOW);
  digitalWrite(IN2, HIGH);

  // 오른쪽 바퀴 전진
  digitalWrite(IN3, LOW);
  digitalWrite(IN4, HIGH);
}

// ===================== 왼쪽으로 제자리 회전 =====================
// 왼쪽 후진 + 오른쪽 전진
void spinInPlace180() {
  setSpeed(MOTOR_SPEED);

  // 왼쪽 바퀴 후진
  digitalWrite(IN1, HIGH);
  digitalWrite(IN2, LOW);

  // 오른쪽 바퀴 전진
  digitalWrite(IN3, LOW);
  digitalWrite(IN4, HIGH);
}

// ===================== 오른쪽으로 제자리 회전 =====================
void spinInPlace180Reverse() {
  setSpeed(MOTOR_SPEED);

  // 왼쪽 바퀴 전진
  digitalWrite(IN1, LOW);
  digitalWrite(IN2, HIGH);

  // 오른쪽 바퀴 후진
  digitalWrite(IN3, HIGH);
  digitalWrite(IN4, LOW);
}

// ===================== setup =====================
void setup() {
  pinMode(BUZZER_PIN, OUTPUT);

  pinMode(ENA, OUTPUT);
  pinMode(ENB, OUTPUT);

  pinMode(IN1, OUTPUT);
  pinMode(IN2, OUTPUT);
  pinMode(IN3, OUTPUT);
  pinMode(IN4, OUTPUT);

  stopMotor();

  // 전원 들어오면 2분 15초 동안 완전 정지
  unsigned long waitStart = millis();

  while (millis() - waitStart < WAIT_BEFORE_START_MS) {
    stopMotor();
    noTone(BUZZER_PIN);
    delay(20);
  }

  // 2분 15초 정지 후 부저 3번
  beepStart();

  delay(500);

  // 1. 두 바퀴 0.7초 소프트 후진
  backwardBoth();
  delay(BACKWARD_MS);

  stopMotor();
  delay(300);

  // 2. 왼쪽으로 제자리 회전
  spinInPlace180();
  delay(SPIN_180_MS);

  stopMotor();
  delay(300);

  // 3. 이후 계속 직진
  forwardBoth();
}

// ===================== loop =====================
void loop() {
  // 아예 계속 직진
  forwardBoth();
}
