using System.IO.Ports;
using System.Threading;
using UnityEngine;

public class HandDemo : MonoBehaviour {
    public string portName = "/dev/cu.HaptiGlove";
    public int baudRate = 115200;
    public float maxAngle = 90f;
    public Vector3 flexAxis = Vector3.forward;

    public Transform[] thumb, index, middle, ring, pinky;
    // Pesos relativos por articulacion (base -> punta). Se normalizan solos
    // para que la SUMA de flexion de un dedo nunca pase de maxAngle.
    static readonly float[] W = { 0.45f, 0.35f, 0.20f, 0.10f };

    SerialPort port;
    Thread thread;
    volatile int raw = 0;
    volatile bool running = true;
    Transform[][] fingers;
    Quaternion[][] rest;
    float angle;

    void Start() {
        fingers = new[] { thumb, index, middle, ring, pinky };
        rest = new Quaternion[5][];
        for (int f = 0; f < 5; f++) {
            rest[f] = new Quaternion[fingers[f].Length];
            for (int j = 0; j < fingers[f].Length; j++)
                rest[f][j] = fingers[f][j].localRotation;
        }

        try {
            port = new SerialPort(portName, baudRate);
            port.ReadTimeout = 500;
            port.Open();
            Debug.Log("Puerto abierto OK: " + portName);
        } catch (System.Exception e) {
            Debug.LogError("No se pudo abrir el puerto: " + e.Message);
            return;
        }

        thread = new Thread(() => {
            while (running) {
                try {
                    string line = port.ReadLine();
                    Debug.Log("Linea recibida: [" + line + "]");
                    if (int.TryParse(line.Trim(), out int v)) raw = v;
                    else Debug.LogWarning("No se pudo convertir a numero: [" + line + "]");
                } catch (System.Exception ex) {
                    Debug.LogWarning("Excepcion leyendo: " + ex.GetType().Name + " - " + ex.Message);
                }
            }
        }) { IsBackground = true };
        thread.Start();
    }

    void Update() {
        float target = Mathf.Clamp01(raw / 4095f) * maxAngle;
        angle = Mathf.Lerp(angle, target, Time.deltaTime * 15f);

        for (int f = 0; f < 5; f++) {
            int n = fingers[f].Length;
            float wSum = 0f;
            for (int j = 0; j < n; j++) wSum += W[Mathf.Min(j, W.Length - 1)];

            for (int j = 0; j < n; j++) {
                float share = W[Mathf.Min(j, W.Length - 1)] / wSum; // normalizado: suma = 1
                fingers[f][j].localRotation =
                    rest[f][j] * Quaternion.AngleAxis(angle * share, flexAxis);
            }
        }
    }

    void OnDestroy() {
        running = false;
        thread?.Join(200);
        if (port != null && port.IsOpen) port.Close();
    }
}