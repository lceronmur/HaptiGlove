/*
 * HaptiGlove - Flexores + motores de vibracion (ESP32)
 * -----------------------------------------------------
 * Conexion de cada flexor (divisor de voltaje):
 *s
 *   3.3V ---[ FLEXOR ]---+---[ R ]--- GND     (R ~ raiz(R_estirado * R_doblado))
 *                        |
 *                     GPIO (ADC1)
 *
 * Conexion de cada motor de vibracion (transistor NPN 2N2222 o MOSFET logico):
 *
 *   V_MOTOR (bateria) ---+------------+
 *                        |            |
 *                     [MOTOR]   diodo 1N4148 (catodo/raya hacia V_MOTOR)
 *                        |            |
 *                        +------------+
 *                        |
 *                    Colector
 *   GPIO --[ 1k ]-- Base       2N2222
 *                    Emisor --- GND  (GND comun con el ESP32)
 *
 * MPU6050 (modulo GY-521), I2C:
 *   VCC -> 3.3V   GND -> GND   SCL -> GPIO 22   SDA -> GPIO 21   AD0 -> GND
 *   (XDA, XCL e INT sin conectar). Al encender, dejar el guante QUIETO ~1 s
 *   mientras se calibra el giroscopio.
 *
 * Se usan solo pines ADC1 (32-36) porque ADC2 no funciona con Bluetooth/WiFi activo.
 *
 * Comandos por el Monitor Serie (115200 baudios, "Nueva linea"):
 *   a  -> calibrar MANO ABIERTA (dedos estirados)
 *   c  -> calibrar MANO CERRADA (puño)
 *   r  -> reiniciar calibracion
 *   1  -> modo PLOTTER  (valores crudos filtrados, para el Serial Plotter)
 *   2  -> modo PORCENTAJE (0-100 % de flexion, tambien sirve en Plotter)
 *   3  -> modo TABLA (texto legible con barras y diagnostico)
 *   4  -> modo UNITY (CSV 0-4095 calibrado, formato para la app)
 *   5..9 -> prender/apagar un motor (5 pulgar, 6 indice, 7 medio, 8 anular, 9 menique)
 *   v  -> prender/apagar todos los motores
 *   x  -> apagar todos los motores
 *   t  -> probar motores (vibra cada dedo, uno por uno)
 *   g  -> recalibrar giroscopio del MPU6050 (guante quieto)
 *   i  -> estado del MPU6050
 *   h  -> mostrar ayuda
 *   V,p,i,m,a,n -> intensidad de vibracion por dedo (0..255). Ej: V,0,200,0,0,0
 *                  Si no llega otro V en 1 s, los motores se apagan solos.
 *
 * Los datos salen a la vez por USB y por Bluetooth clasico (nombre "HaptiGlove"),
 * y los comandos se aceptan por cualquiera de los dos.
 *
 * La calibracion se guarda en la memoria flash del ESP32: no se pierde al
 * desconectarlo. Si hay calibracion guardada, arranca directo en modo UNITY.
 */

#include <Preferences.h>
#include <Wire.h>
#include "BluetoothSerial.h"

#if !defined(CONFIG_BT_ENABLED) || !defined(CONFIG_BLUEDROID_ENABLED)
#error "Bluetooth clasico no disponible: use un ESP32 normal (no S2/S3/C3)"
#endif

Preferences prefs;
BluetoothSerial SerialBT;
const char* NOMBRE_BT = "HaptiGlove";

// Todo lo que se imprime con Salida va al USB y, si hay alguien conectado, al Bluetooth.
class SalidaDual : public Print {
public:
  size_t write(uint8_t c) override {
    Serial.write(c);
    if (SerialBT.hasClient()) SerialBT.write(c);
    return 1;
  }
  size_t write(const uint8_t* buf, size_t n) override {
    Serial.write(buf, n);
    if (SerialBT.hasClient()) SerialBT.write(buf, n);
    return n;
  }
};
SalidaDual Salida;

const int NUM_DEDOS = 5;
const int pines[NUM_DEDOS] = {32, 33, 34, 35, 36};
const char* nombres[NUM_DEDOS] = {"Pulgar", "Indice", "Medio", "Anular", "Menique"};

// --- Filtro ---
const int   MUESTRAS = 8;      // sobremuestreo por lectura (reduce ruido del ADC)
const float ALFA     = 0.25f;  // filtro exponencial: 0.0 (muy suave) .. 1.0 (sin filtro)

// --- Diagnostico ---
const int UMBRAL_BAJO = 30;    // ~0 V  -> flexor desconectado o resistencia en corto
const int UMBRAL_ALTO = 4065;  // ~3.3V -> resistencia a GND desconectada o flexor en corto
const int RANGO_MIN   = 150;   // rango minimo esperado entre abierto y cerrado

const unsigned long PERIODO_MS = 20;  // 50 Hz

// --- Motores de vibracion ---
const int pinesMotor[NUM_DEDOS] = {25, 26, 27, 14, 13};  // pulgar..menique
const int PWM_FREQ = 5000;  // Hz
const int PWM_BITS = 8;     // duty 0..255
const int PWM_MIN  = 90;    // duty minimo para que el motor arranque
const int PWM_MAX  = 255;   // bajar si la bateria supera el voltaje del motor (ej. 160 con 5 V y motor de 3 V)
const unsigned long TIMEOUT_MOTORES_MS = 1000;  // apaga si Unity deja de enviar

int intensidad[NUM_DEDOS] = {0};
unsigned long ultimoComandoMotor = 0;
bool motoresManual = false;  // encendidos con tecla: no se apagan solos

// --- MPU6050 ---
const int PIN_SDA = 21;
const int PIN_SCL = 22;
const uint8_t MPU_ADDR = 0x68;           // AD0 a GND (0x69 si AD0 a 3.3V)
const float ACC_LSB  = 16384.0f;         // +-2 g
const float GIRO_LSB = 65.5f;            // +-500 grados/s
const unsigned long PERIODO_IMU_US = 5000;  // 200 Hz
const float BETA_INICIAL = 2.0f;         // converge rapido al arrancar
const float BETA_NORMAL  = 0.08f;        // luego confia mas en el giroscopio

bool  imuOk = false;
float q0 = 1, q1 = 0, q2 = 0, q3 = 0;    // orientacion (w, x, y, z)
float giroBias[3] = {0, 0, 0};           // grados/s
float beta = BETA_INICIAL;

// Correccion automatica del giroscopio: cuando el guante esta quieto (lecturas estables)
// se re-estima el bias y no se integra giro, asi la orientacion no deriva sola.
const float QUIETO_GIRO = 0.8f;          // grados/s de variacion maxima para considerarlo quieto
const float QUIETO_ACC  = 0.03f;         // g de variacion maxima
const unsigned long QUIETO_MS = 800;     // tiempo quieto antes de corregir
const float BIAS_ALFA = 0.02f;           // velocidad de ajuste del bias (a 200 Hz, ~0.25 s)
float giroMedio[3], accMedio[3];
bool  hayMedio = false;
unsigned long quietoDesde = 0;
unsigned long ultimoIMU = 0, inicioIMU = 0;

enum Modo { PLOTTER = 1, PORCENTAJE = 2, TABLA = 3, UNITY = 4 };
Modo modo = PLOTTER;

float filtrado[NUM_DEDOS];
int   abierto[NUM_DEDOS];   // lectura con dedo estirado
int   cerrado[NUM_DEDOS];   // lectura con dedo doblado
bool  calAbierto = false, calCerrado = false;
int   minVisto[NUM_DEDOS], maxVisto[NUM_DEDOS];

unsigned long ultimo = 0;
unsigned long ultimaTabla = 0;

int leerPromedio(int pin) {
  long suma = 0;
  for (int i = 0; i < MUESTRAS; i++) suma += analogRead(pin);
  return suma / MUESTRAS;
}

void reiniciarCalibracion() {
  calAbierto = calCerrado = false;
  for (int i = 0; i < NUM_DEDOS; i++) {
    abierto[i] = 0;
    cerrado[i] = 4095;
    minVisto[i] = 4095;
    maxVisto[i] = 0;
  }
}

// Promedia ~1 s de lecturas para una calibracion estable
void capturar(int destino[], const char* etiqueta) {
  Salida.printf("\n>> Calibrando %s: mantenga la posicion...\n", etiqueta);
  long suma[NUM_DEDOS] = {0};
  const int N = 50;
  for (int n = 0; n < N; n++) {
    for (int i = 0; i < NUM_DEDOS; i++) suma[i] += leerPromedio(pines[i]);
    delay(20);
  }
  for (int i = 0; i < NUM_DEDOS; i++) {
    destino[i] = suma[i] / N;
    Salida.printf("   %-8s = %4d\n", nombres[i], destino[i]);
  }
  Salida.println(">> Listo.\n");
}

void guardarCalibracion() {
  prefs.begin("haptiglove", false);
  prefs.putBytes("abierto", abierto, sizeof(abierto));
  prefs.putBytes("cerrado", cerrado, sizeof(cerrado));
  prefs.end();
  Salida.println(">> Calibracion guardada en memoria.");
}

bool cargarCalibracion() {
  prefs.begin("haptiglove", true);
  bool ok = prefs.getBytesLength("abierto") == sizeof(abierto) &&
            prefs.getBytesLength("cerrado") == sizeof(cerrado);
  if (ok) {
    prefs.getBytes("abierto", abierto, sizeof(abierto));
    prefs.getBytes("cerrado", cerrado, sizeof(cerrado));
  }
  prefs.end();
  calAbierto = calCerrado = ok;
  return ok;
}

void borrarCalibracionGuardada() {
  prefs.begin("haptiglove", false);
  prefs.clear();
  prefs.end();
}

void validarCalibracion() {
  if (!(calAbierto && calCerrado)) return;
  guardarCalibracion();
  Salida.println(">> Rango por dedo (cerrado - abierto):");
  for (int i = 0; i < NUM_DEDOS; i++) {
    int rango = cerrado[i] - abierto[i];
    Salida.printf("   %-8s %5d  %s\n", nombres[i], rango,
                  abs(rango) < RANGO_MIN ? "<-- RANGO BAJO: revise flexor/resistencia" : "OK");
  }
  Salida.println();
}

// Flexion 0.0 (estirado) .. 1.0 (doblado). Soporta que el valor suba o baje al doblar.
float flexion(int i) {
  float lo, hi;
  if (calAbierto && calCerrado) {
    lo = abierto[i];
    hi = cerrado[i];
  } else {
    // Sin calibracion: usa el min/max observado desde el arranque
    lo = minVisto[i];
    hi = maxVisto[i];
    if (hi - lo < RANGO_MIN) return 0.0f;
  }
  if (fabsf(hi - lo) < 1) return 0.0f;
  return constrain((filtrado[i] - lo) / (hi - lo), 0.0f, 1.0f);
}

const char* estado(int v) {
  if (v < UMBRAL_BAJO) return "SIN SENAL?";
  if (v > UMBRAL_ALTO) return "SATURADO?";
  return "ok";
}

// ------------------------------------------------------------ Motores

void iniciarMotores() {
  for (int i = 0; i < NUM_DEDOS; i++) {
#if ESP_ARDUINO_VERSION_MAJOR >= 3
    ledcAttach(pinesMotor[i], PWM_FREQ, PWM_BITS);
#else
    ledcSetup(i, PWM_FREQ, PWM_BITS);
    ledcAttachPin(pinesMotor[i], i);
#endif
  }
}

void escribirMotor(int i, int valor) {
  // 0 = apagado; 1..255 se lleva a PWM_MIN..PWM_MAX para que siempre arranque
  valor = constrain(valor, 0, 255);
  int duty = valor == 0 ? 0 : map(valor, 1, 255, PWM_MIN, PWM_MAX);
  intensidad[i] = valor;
#if ESP_ARDUINO_VERSION_MAJOR >= 3
  ledcWrite(pinesMotor[i], duty);
#else
  ledcWrite(i, duty);
#endif
}

void apagarMotores() {
  for (int i = 0; i < NUM_DEDOS; i++) escribirMotor(i, 0);
}

void probarMotores() {
  Salida.println(">> Prueba de motores:");
  for (int i = 0; i < NUM_DEDOS; i++) {
    Salida.printf("   %s\n", nombres[i]);
    escribirMotor(i, 255);
    delay(400);
    escribirMotor(i, 0);
    delay(150);
  }
  Salida.println(">> Prueba terminada.");
}

// "V,p,i,m,a,n" -> intensidades 0..255
void comandoVibracion(const char* linea) {
  int v[NUM_DEDOS];
  int n = sscanf(linea, "%*c,%d,%d,%d,%d,%d", &v[0], &v[1], &v[2], &v[3], &v[4]);
  if (n != NUM_DEDOS) return;
  for (int i = 0; i < NUM_DEDOS; i++) escribirMotor(i, v[i]);
  ultimoComandoMotor = millis();
  motoresManual = false;
}

// Teclas 5..9: prende/apaga un motor (5 = pulgar ... 9 = menique)
void alternarMotor(int i) {
  escribirMotor(i, intensidad[i] > 0 ? 0 : 255);
  motoresManual = true;
  Salida.printf(">> Motor %s (GPIO %d): %s\n", nombres[i], pinesMotor[i],
                intensidad[i] > 0 ? "ENCENDIDO" : "apagado");
}

// Tecla v: si hay alguno encendido apaga todos; si no, prende todos
void alternarTodos() {
  bool alguno = false;
  for (int i = 0; i < NUM_DEDOS; i++) alguno |= intensidad[i] > 0;
  for (int i = 0; i < NUM_DEDOS; i++) escribirMotor(i, alguno ? 0 : 255);
  motoresManual = !alguno;
  Salida.println(alguno ? ">> Todos los motores apagados" : ">> Todos los motores ENCENDIDOS");
}

void revisarTimeoutMotores() {
  if (motoresManual) return;
  bool alguno = false;
  for (int i = 0; i < NUM_DEDOS; i++) alguno |= intensidad[i] > 0;
  if (alguno && millis() - ultimoComandoMotor > TIMEOUT_MOTORES_MS) apagarMotores();
}

// ------------------------------------------------------------ MPU6050

bool imuEscribir(uint8_t reg, uint8_t val) {
  Wire.beginTransmission(MPU_ADDR);
  Wire.write(reg);
  Wire.write(val);
  return Wire.endTransmission() == 0;
}

// Lee acelerometro (g) y giroscopio (grados/s, sin bias)
bool imuLeer(float a[3], float g[3]) {
  Wire.beginTransmission(MPU_ADDR);
  Wire.write(0x3B);  // ACCEL_XOUT_H
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom(MPU_ADDR, (uint8_t)14) != 14) return false;
  int16_t r[7];
  for (int i = 0; i < 7; i++) r[i] = (Wire.read() << 8) | Wire.read();
  for (int i = 0; i < 3; i++) {
    a[i] = r[i] / ACC_LSB;
    g[i] = r[i + 4] / GIRO_LSB;  // r[3] es temperatura
  }
  return true;
}

void calibrarGiro() {
  if (!imuOk) { Salida.println(">> MPU6050 no detectado."); return; }
  Salida.println(">> Calibrando giroscopio: no mueva el guante...");
  float suma[3] = {0, 0, 0}, a[3], g[3];
  int n = 0;
  for (int k = 0; k < 300; k++) {
    if (imuLeer(a, g)) { for (int i = 0; i < 3; i++) suma[i] += g[i]; n++; }
    delay(3);
  }
  if (n > 0) for (int i = 0; i < 3; i++) giroBias[i] = suma[i] / n;
  Salida.printf(">> Giroscopio calibrado. Bias: %.2f %.2f %.2f grados/s\n",
                giroBias[0], giroBias[1], giroBias[2]);
  q0 = 1; q1 = q2 = q3 = 0;
  beta = BETA_INICIAL;
  inicioIMU = millis();
}

void iniciarIMU() {
  Wire.begin(PIN_SDA, PIN_SCL);
  Wire.setClock(400000);

  Wire.beginTransmission(MPU_ADDR);
  imuOk = Wire.endTransmission() == 0;
  if (!imuOk) {
    Salida.println(">> MPU6050 NO detectado: revise SDA=21, SCL=22, VCC y GND.");
    return;
  }

  imuEscribir(0x6B, 0x80); delay(100);  // reset
  imuEscribir(0x6B, 0x01);              // despertar, reloj del giroscopio X
  imuEscribir(0x1A, 0x03);              // filtro pasa bajos ~44 Hz
  imuEscribir(0x19, 0x04);              // 200 Hz
  imuEscribir(0x1B, 0x08);              // giroscopio +-500 grados/s
  imuEscribir(0x1C, 0x00);              // acelerometro +-2 g
  delay(50);

  Salida.println(">> MPU6050 detectado.");
  calibrarGiro();
}

// Filtro de Madgwick (6 ejes): fusiona acelerometro y giroscopio en un cuaternion
void madgwick(float gx, float gy, float gz, float ax, float ay, float az, float dt) {
  float qd0 = 0.5f * (-q1 * gx - q2 * gy - q3 * gz);
  float qd1 = 0.5f * ( q0 * gx + q2 * gz - q3 * gy);
  float qd2 = 0.5f * ( q0 * gy - q1 * gz + q3 * gx);
  float qd3 = 0.5f * ( q0 * gz + q1 * gy - q2 * gx);

  float n = sqrtf(ax * ax + ay * ay + az * az);
  if (n > 0.0f) {
    ax /= n; ay /= n; az /= n;
    float _2q0 = 2 * q0, _2q1 = 2 * q1, _2q2 = 2 * q2, _2q3 = 2 * q3;
    float _4q0 = 4 * q0, _4q1 = 4 * q1, _4q2 = 4 * q2;
    float _8q1 = 8 * q1, _8q2 = 8 * q2;
    float q0q0 = q0 * q0, q1q1 = q1 * q1, q2q2 = q2 * q2, q3q3 = q3 * q3;

    float s0 = _4q0 * q2q2 + _2q2 * ax + _4q0 * q1q1 - _2q1 * ay;
    float s1 = _4q1 * q3q3 - _2q3 * ax + 4 * q0q0 * q1 - _2q0 * ay - _4q1 + _8q1 * q1q1 + _8q1 * q2q2 + _4q1 * az;
    float s2 = 4 * q0q0 * q2 + _2q0 * ax + _4q2 * q3q3 - _2q3 * ay - _4q2 + _8q2 * q1q1 + _8q2 * q2q2 + _4q2 * az;
    float s3 = 4 * q1q1 * q3 - _2q1 * ax + 4 * q2q2 * q3 - _2q2 * ay;
    float sn = sqrtf(s0 * s0 + s1 * s1 + s2 * s2 + s3 * s3);
    if (sn > 0.0f) {
      qd0 -= beta * s0 / sn; qd1 -= beta * s1 / sn;
      qd2 -= beta * s2 / sn; qd3 -= beta * s3 / sn;
    }
  }

  q0 += qd0 * dt; q1 += qd1 * dt; q2 += qd2 * dt; q3 += qd3 * dt;
  float qn = sqrtf(q0 * q0 + q1 * q1 + q2 * q2 + q3 * q3);
  q0 /= qn; q1 /= qn; q2 /= qn; q3 /= qn;
}

void actualizarIMU() {
  if (!imuOk) return;
  unsigned long ahora = micros();
  if (ahora - ultimoIMU < PERIODO_IMU_US) return;
  float dt = (ahora - ultimoIMU) * 1e-6f;
  ultimoIMU = ahora;
  if (dt > 0.1f) return;  // primera vez o pausa larga (calibracion, prueba de motores)

  if (beta != BETA_NORMAL && millis() - inicioIMU > 2000) beta = BETA_NORMAL;

  float a[3], g[3];
  if (!imuLeer(a, g)) return;

  // Quieto = las lecturas casi no cambian (no se exige giro ~0: asi corrige tambien un bias malo)
  bool quieto = hayMedio;
  for (int i = 0; i < 3; i++) {
    if (!hayMedio) { giroMedio[i] = g[i]; accMedio[i] = a[i]; }
    quieto = quieto && fabsf(g[i] - giroMedio[i]) < QUIETO_GIRO && fabsf(a[i] - accMedio[i]) < QUIETO_ACC;
    giroMedio[i] += 0.1f * (g[i] - giroMedio[i]);
    accMedio[i]  += 0.1f * (a[i] - accMedio[i]);
  }
  hayMedio = true;
  if (!quieto) quietoDesde = millis();
  bool fijo = quieto && millis() - quietoDesde > QUIETO_MS;
  if (fijo) for (int i = 0; i < 3; i++) giroBias[i] += BIAS_ALFA * (giroMedio[i] - giroBias[i]);

  const float D2R = 0.0174533f;
  float k = fijo ? 0.0f : D2R;  // quieto: no integrar giro (el acelerometro sigue corrigiendo roll/pitch)
  madgwick((g[0] - giroBias[0]) * k, (g[1] - giroBias[1]) * k, (g[2] - giroBias[2]) * k,
           a[0], a[1], a[2], dt);
}

// Angulos en grados (solo para mostrar en la tabla)
void angulosEuler(float& roll, float& pitch, float& yaw) {
  const float R2D = 57.29578f;
  roll  = atan2f(2 * (q0 * q1 + q2 * q3), 1 - 2 * (q1 * q1 + q2 * q2)) * R2D;
  pitch = asinf(constrain(2 * (q0 * q2 - q3 * q1), -1.0f, 1.0f)) * R2D;
  yaw   = atan2f(2 * (q0 * q3 + q1 * q2), 1 - 2 * (q2 * q2 + q3 * q3)) * R2D;
}

// ------------------------------------------------------------ Comandos

void ayuda() {
  Salida.println("\n=== HaptiGlove ===");
  Salida.println(" a: calibrar abierta | c: calibrar cerrada | r: reset cal");
  Salida.println(" 1: plotter | 2: porcentaje | 3: tabla | 4: unity");
  Salida.println(" 5-9: prender/apagar motor (5=pulgar..9=menique) | v: todos | x: apagar");
  Salida.println(" t: probar motores | V,p,i,m,a,n: vibrar (0-255)");
  Salida.println(" g: calibrar giroscopio | i: estado MPU6050 | h: ayuda\n");
}

// Cada fuente (USB y Bluetooth) arma su propia linea hasta recibir '\n'
struct Entrada {
  Stream* s;
  char buf[48];
  int len;
};
Entrada entradas[2] = {{&Serial, {0}, 0}, {&SerialBT, {0}, 0}};

void ejecutarLinea(const char* linea) {
  if (linea[0] == '\0') return;
  if ((linea[0] == 'V' || linea[0] == 'v') && linea[1] == ',') {
    comandoVibracion(linea);
    return;
  }
  switch (linea[0]) {
      case 'a': case 'A': capturar(abierto, "MANO ABIERTA"); calAbierto = true; validarCalibracion(); break;
      case 'c': case 'C': capturar(cerrado, "MANO CERRADA"); calCerrado = true; validarCalibracion(); break;
      case 'r': case 'R':
        reiniciarCalibracion();
        borrarCalibracionGuardada();
        Salida.println(">> Calibracion reiniciada y borrada de memoria");
        break;
      case '1': modo = PLOTTER;    break;
      case '2': modo = PORCENTAJE; break;
      case '3': modo = TABLA;      break;
      case '4': modo = UNITY;      break;
      case 't': case 'T': apagarMotores(); probarMotores(); break;
      case '5': case '6': case '7': case '8': case '9': alternarMotor(linea[0] - '5'); break;
      case 'v': case 'V': alternarTodos(); break;
      case 'x': case 'X': apagarMotores(); motoresManual = false; Salida.println(">> Motores apagados"); break;
      case 'g': case 'G': calibrarGiro(); break;
      case 'i': case 'I': {
        if (!imuOk) { Salida.println(">> MPU6050 no detectado."); break; }
        float r, p, y;
        angulosEuler(r, p, y);
        Salida.printf(">> MPU6050 OK. Roll %.1f  Pitch %.1f  Yaw %.1f\n", r, p, y);
        break;
      }
      case 'h': case 'H': ayuda(); break;
  }
}

void procesarComandos() {
  for (auto& e : entradas) {
    while (e.s->available()) {
      char ch = e.s->read();
      if (ch == '\r') continue;
      if (ch == '\n') {
        e.buf[e.len] = '\0';
        ejecutarLinea(e.buf);
        e.len = 0;
      } else if (e.len < (int)sizeof(e.buf) - 1) {
        e.buf[e.len++] = ch;
      }
    }
  }
}

void imprimir() {
  switch (modo) {
    case PLOTTER:
      // Formato "etiqueta:valor" -> el Serial Plotter muestra una linea por dedo
      for (int i = 0; i < NUM_DEDOS; i++) {
        Salida.printf("%s:%d", nombres[i], (int)filtrado[i]);
        Salida.print(i < NUM_DEDOS - 1 ? "," : "\n");
      }
      break;

    case PORCENTAJE:
      for (int i = 0; i < NUM_DEDOS; i++) {
        Salida.printf("%s:%d", nombres[i], (int)(flexion(i) * 100));
        Salida.print(i < NUM_DEDOS - 1 ? "," : "\n");
      }
      break;

    case UNITY: {
      // "p,i,m,a,n" y, si hay MPU6050, ",qw,qx,qy,qz"
      char linea[96];
      int n = 0;
      for (int i = 0; i < NUM_DEDOS; i++)
        n += snprintf(linea + n, sizeof(linea) - n, i ? ",%d" : "%d", (int)(flexion(i) * 4095));
      if (imuOk)
        n += snprintf(linea + n, sizeof(linea) - n, ",%.4f,%.4f,%.4f,%.4f", q0, q1, q2, q3);
      snprintf(linea + n, sizeof(linea) - n, "\n");
      Salida.print(linea);
      break;
    }

    case TABLA:
      if (millis() - ultimaTabla < 300) return;  // refresco legible
      ultimaTabla = millis();
      Salida.println("Dedo      Crudo   Volt  Flex  Barra                 Estado");
      for (int i = 0; i < NUM_DEDOS; i++) {
        int v = (int)filtrado[i];
        float pct = flexion(i);
        char barra[21];
        int llenos = (int)(pct * 20);
        for (int k = 0; k < 20; k++) barra[k] = k < llenos ? '#' : '.';
        barra[20] = '\0';
        Salida.printf("%-8s  %4d   %4.2f  %3d%%  [%s]  %s\n",
                      nombres[i], v, v * 3.3f / 4095.0f, (int)(pct * 100), barra, estado(v));
      }
      if (imuOk) {
        float r, p, y;
        angulosEuler(r, p, y);
        Salida.printf("Muneca:   Roll %6.1f   Pitch %6.1f   Yaw %6.1f\n", r, p, y);
      } else {
        Salida.println("Muneca:   MPU6050 no detectado");
      }
      Salida.printf("Calibracion: abierta=%s cerrada=%s\n\n",
                    calAbierto ? "si" : "no", calCerrado ? "si" : "no");
      break;
  }
}

void setup() {
  Serial.begin(115200);
  SerialBT.begin(NOMBRE_BT);  // aparece como "HaptiGlove" al buscar dispositivos
  analogReadResolution(12);           // 0..4095
  analogSetAttenuation(ADC_11db);     // rango ~0..3.3 V
  for (int i = 0; i < NUM_DEDOS; i++) pinMode(pines[i], INPUT);
  iniciarMotores();
  apagarMotores();
  iniciarIMU();

  reiniciarCalibracion();
  delay(500);
  for (int i = 0; i < NUM_DEDOS; i++) filtrado[i] = leerPromedio(pines[i]);
  ayuda();

  if (cargarCalibracion()) {
    Salida.println(">> Calibracion cargada de memoria. Modo UNITY.");
    modo = UNITY;
  } else {
    Salida.println(">> Sin calibracion guardada: use 'a' y 'c'.");
  }
}

void loop() {
  procesarComandos();
  revisarTimeoutMotores();
  actualizarIMU();

  if (millis() - ultimo < PERIODO_MS) return;
  ultimo = millis();

  for (int i = 0; i < NUM_DEDOS; i++) {
    int v = leerPromedio(pines[i]);
    filtrado[i] += ALFA * (v - filtrado[i]);
    int f = (int)filtrado[i];
    if (f < minVisto[i]) minVisto[i] = f;
    if (f > maxVisto[i]) maxVisto[i] = f;
  }

  imprimir();
}
