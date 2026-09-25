void setup() {
  Serial.begin(115200);
}

void loop() {
  int valor = (int)(2047 + 2047 * sin(millis() / 600.0));
  Serial.println(valor);
  delay(50);
}