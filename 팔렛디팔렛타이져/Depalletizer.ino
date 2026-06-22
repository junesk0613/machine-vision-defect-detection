#include <Servo.h>

Servo motor1, motor2, motor3, motor4, motor5, motor6;

void setup() {
  Serial.begin(9600);
  motor1.attach(2);
  motor2.attach(3);
  motor3.attach(9);
  motor4.attach(8);
  motor5.attach(4);
  motor6.attach(5);

  // 초기값
  motor1.write(130);
  motor2.write(180);
  motor3.write(100);
  motor4.write(180);
  motor5.write(0);
  motor6.write(93);
}

void loop() {
  if (Serial.available()) {
    char cmd = Serial.read();
    
    if (cmd == '1') {  // 라파에서 '1' 신호 오면 동작
      // PCB 집기
      for (int i = 0; i <= 50; i++) {
        motor2.write(map(i, 0, 50, 180, 170));
        motor3.write(map(i, 0, 50, 100, 150));
        motor6.write(map(i, 0, 50, 93, 180));
        delay(20);
      }
      delay(2000);

      // 들어올리기
      for (int i = 0; i <= 50; i++) {
        motor2.write(map(i, 0, 50, 170, 180));
        motor3.write(map(i, 0, 50, 150, 180));
        delay(20);
      }
      delay(2000);

      // 옆으로 회전
      for (int i = 0; i <= 50; i++) {
        motor1.write(map(i, 0, 50, 130, 180));
        delay(20);
      }
      delay(2000);

      // 내려놓기
      for (int i = 0; i <= 50; i++) {
        motor2.write(map(i, 0, 50, 180, 170));
        motor3.write(map(i, 0, 50, 180, 150));
        delay(20);
      }
      delay(1000);

      // 그리퍼 열기
      for (int i = 0; i <= 50; i++) {
        motor6.write(map(i, 0, 50, 180, 93));
        delay(20);
      }
      delay(2000);

      // 복귀
      for (int i = 0; i <= 50; i++) {
        motor3.write(map(i, 0, 50, 150, 100));
        delay(20);
      }
      delay(500);
      for (int i = 0; i <= 50; i++) {
        motor1.write(map(i, 0, 50, 180, 130));
        delay(20);
      }
      delay(500);
      for (int i = 0; i <= 50; i++) {
        motor2.write(map(i, 0, 50, 170, 180));
        delay(20);
      }
    }
  }
}