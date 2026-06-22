#include <Servo.h>
#include <SoftwareSerial.h>

SoftwareSerial convSerial(A0, A1); // RX, TX

Servo motor1, motor2, motor3, motor4, motor5, motor6;
int pcb_count = 0;

void setup() {
  Serial.begin(9600);
  convSerial.begin(9600);
  motor1.attach(2);
  motor2.attach(3);
  motor3.attach(9);
  motor4.attach(8);
  motor5.attach(4);
  motor6.attach(5);

  motor1.write(135);
  motor2.write(110);
  motor3.write(20);
  motor4.write(0);
  motor5.write(90);
  motor6.write(50);
}

void loop() {
  if (Serial.available()) {
    char cmd = Serial.read();

    if (cmd == '1') {
      pcb_count++;

      // PCB 집기
      delay(500);
      for (int i = 0; i <= 50; i++) {
        motor2.write(map(i, 0, 50, 110, 65));
        delay(20);
      }
      delay(1000);

      // 그리퍼 닫기
      for (int i = 0; i <= 50; i++) {
        motor6.write(map(i, 0, 50, 50, 155));
        delay(20);
      }
      delay(1000);

      // 들어올리기
      for (int i = 0; i <= 50; i++) {
        motor2.write(map(i, 0, 50, 65, 110));
        delay(20);
      }
      delay(1000);

      if (pcb_count == 1) {
        for (int i = 0; i <= 50; i++) {
          motor1.write(map(i, 0, 50, 135, 67));
          delay(20);
        }
        delay(2000);

        for (int i = 0; i <= 50; i++) {
          motor2.write(map(i, 0, 50, 110, 80));
          motor3.write(map(i, 0, 50, 20, 40));
          motor4.write(map(i,0,50,0,20));
          delay(20);
        }
        delay(1000);

        for (int i = 0; i <= 50; i++) {
          motor6.write(map(i, 0, 50, 155, 50));
          delay(20);
        }
        delay(2000);

        for (int i = 0; i <= 50; i++) {
          motor2.write(map(i, 0, 50, 80, 110));
          delay(20);
        }
        delay(500);
        for (int i = 0; i <= 50; i++) {
          motor3.write(map(i, 0, 50, 40, 20));
          motor4.write(map(i,0,50,20,0));
          delay(20);
        }
        delay(500);
        for (int i = 0; i <= 50; i++) {
          motor1.write(map(i, 0, 50, 67, 135));
          delay(20);
        }

      } else if (pcb_count == 2) {
        for (int i = 0; i <= 50; i++) {
          motor1.write(map(i, 0, 50, 135, 52));
          delay(20);
        }
        delay(2000);

        for (int i = 0; i <= 50; i++) {
          motor2.write(map(i, 0, 50, 110, 80));
          motor3.write(map(i, 0, 50, 20, 40));
          motor4.write(map(i,0,50,0,20));
          delay(20);
        }
        delay(1000);

        for (int i = 0; i <= 50; i++) {
          motor6.write(map(i, 0, 50, 155, 50));
          delay(20);
        }
        delay(2000);

        for (int i = 0; i <= 50; i++) {
          motor2.write(map(i, 0, 50, 80, 110));
          delay(20);
        }
        delay(500);
        for (int i = 0; i <= 50; i++) {
          motor3.write(map(i, 0, 50, 40, 20));
          motor4.write(map(i,0,50,20,0));
          delay(20);
        }
        delay(500);
        for (int i = 0; i <= 50; i++) {
          motor1.write(map(i, 0, 50, 52, 135));
          delay(20);
        }

        pcb_count = 0;

        // 초기값 고정 후 컨베이어에 신호 전송
        motor1.write(135);
        motor2.write(110);
        motor3.write(20);
        motor4.write(0);
        motor5.write(90);
        motor6.write(50);
        delay(500);
        convSerial.write('1');
        return;
      }

      motor1.write(135);
      motor2.write(110);
      motor3.write(20);
      motor4.write(0);
      motor5.write(90);
      motor6.write(50);
    }
  }
}