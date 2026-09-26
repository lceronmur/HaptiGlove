using System.IO.Ports;
using System.Threading;
using UnityEngine;

public class HandDemo : MonoBehaviour {
    public string portName = "/dev/cu.HaptiGlove";
    public int baudRate = 115200;
    public float maxAngle = 90f;
    public Vector3 flexAxis = Vector3.forward;

    public Transform[] thumb, index, middle, ring, pinky;
    static readonly float[] W = { 1.0f, 1.0f, 0.7f };

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

        for (int f = 0; f < 5; f++)
            for (int j = 0; j < fingers[f].Length; j++)
                fingers[f][j].localRotation =
                    rest[f][j] * Quaternion.AngleAxis(angle * W[Mathf.Min(j, 2)], flexAxis);
    }

    void OnDestroy() {
        running = false;
        thread?.Join(200);
        if (port != null && port.IsOpen) port.Close();
    }
}
