// ===================== 컨베이어 벨트 + 적외선 센서 코드 =====================

// 적외선 센서 OUT 핀
#define IR_PIN 2

// 컨베이어 모터드라이버 핀
#define CONV_IN1 5
#define CONV_IN2 6

// 컨베이어 속도: 0~255
int CONVEYOR_SPEED = 180;

// 전원 켜진 뒤 시작 대기 시간: 2분 20초
const unsigned long START_DELAY_MS = 140000;

// 감지 후 정지 시간: 15초
const unsigned long STOP_TIME_MS = 15000;

// 대부분 적외선 센서는 물체 감지 시 LOW입니다.
// 만약 반대로 동작하면 LOW를 HIGH로 바꾸세요.
#define IR_DETECTED LOW

bool isStarted = false;
unsigned long powerOnTime = 0;

bool isStopped = false;

// 15초 정지 후 다시 출발했는데,
// 물체가 아직 센서 앞에 있으면 바로 또 멈추지 않게 하기 위한 플래그
bool ignoreUntilClear = false;

unsigned long stopStartTime = 0;

// ===================== 컨베이어 정방향 회전 =====================
void conveyorForward() {
  analogWrite(CONV_IN1, CONVEYOR_SPEED);
  analogWrite(CONV_IN2, 0);
}

// ===================== 컨베이어 정지 =====================
void conveyorStop() {
  analogWrite(CONV_IN1, 0);
  analogWrite(CONV_IN2, 0);
}

void setup() {
  Serial.begin(9600);

  pinMode(IR_PIN, INPUT);

  pinMode(CONV_IN1, OUTPUT);
  pinMode(CONV_IN2, OUTPUT);

  conveyorStop();
  delay(500);

  Serial.println("CONVEYOR READY");
  Serial.println("WAIT 2 MIN 20 SEC BEFORE START");

  powerOnTime = millis();

  // 전원 켜지면 바로 도는 게 아니라,
  // 2분 20초 동안 정지 상태 유지
}

void loop() {
  int irValue = digitalRead(IR_PIN);

  // ===================== 0. 시작 전 대기 =====================
  if (!isStarted) {
    conveyorStop();

    if (millis() - powerOnTime >= START_DELAY_MS) {
      Serial.println("2 MIN 20 SEC DONE -> CONVEYOR START");

      isStarted = true;
      conveyorForward();
    }

    delay(50);
    return;
  }

  Serial.print("IR=");
  Serial.print(irValue);
  Serial.print(" | stopped=");
  Serial.print(isStopped);
  Serial.print(" | ignore=");
  Serial.println(ignoreUntilClear);

  // ===================== 1. 정지 중일 때 =====================
  if (isStopped) {
    conveyorStop();

    // 감지되면 무조건 15초 정지
    // 중간에 물체가 사라져도 여기서는 다시 안 움직임
    if (millis() - stopStartTime >= STOP_TIME_MS) {
      Serial.println("15 SEC DONE -> FORCE RESTART");

      isStopped = false;

      // 15초 뒤에도 물체가 센서 앞에 남아 있으면
      // 같은 물체 때문에 바로 다시 멈추지 않도록 무시 모드 ON
      if (irValue == IR_DETECTED) {
        ignoreUntilClear = true;
      } else {
        ignoreUntilClear = false;
      }

      conveyorForward();
    }

    delay(50);
    return;
  }

  // ===================== 2. 강제 재시작 후 센서 해제 기다리는 중 =====================
  if (ignoreUntilClear) {
    // 센서 앞에 물체가 아직 있어도 컨베이어는 계속 돌아감
    conveyorForward();

    // 센서 앞 물체가 사라지면 다시 감지 가능 상태로 복귀
    if (irValue != IR_DETECTED) {
      Serial.println("IR CLEARED -> SENSOR ACTIVE AGAIN");
      ignoreUntilClear = false;
    }

    delay(50);
    return;
  }

  // ===================== 3. 정상 작동 중 =====================
  // 센서 앞에 아무것도 없으면 계속 정상 작동
  conveyorForward();

  // 물체가 하나라도 감지되면 무조건 15초 정지 시작
  if (irValue == IR_DETECTED) {
    Serial.println("OBJECT DETECTED -> STOP 15 SEC");

    conveyorStop();
    isStopped = true;
    stopStartTime = millis();
  }

  delay(50);
}
