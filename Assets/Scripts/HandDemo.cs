using System.IO.Ports;
using System.Threading;
using UnityEngine;
using System.Globalization;

public class HandDemo : MonoBehaviour
{
    [Header("Serial")]
    public string portName = "/dev/cu.HaptiGlove";
    public int baudRate = 115200;

    [Header("Dedos")]
    public float maxAngle = 90f;
    public Vector3 flexAxis = Vector3.forward;
    public Transform[] thumb, index, middle, ring, pinky;
    static readonly float[] W = { 1.0f, 1.0f, 0.7f };

    [Header("Muñeca (MPU)")]
    public Transform wrist;              // Transform del pulso/muñeca a rotar
    public Vector3 mpuAxesInvert = Vector3.one; // usa -1 en algún eje si la rotación sale invertida
    public float rotSmooth = 15f;
    public float flexSmooth = 15f;

    SerialPort port;
    Thread thread;
    volatile bool running = true;

    // Datos crudos compartidos entre threads (lock simple, suficiente a esta frecuencia)
    readonly object dataLock = new object();
    int rawFlex = 0;
    float rawPitch = 0f, rawRoll = 0f, rawYaw = 0f;

    Transform[][] fingers;
    Quaternion[][] rest;
    Quaternion wristRest;

    float angle;                 // ángulo de flexión suavizado
    Quaternion wristTarget;      // rotación objetivo de muñeca

    void Start()
    {
        fingers = new[] { thumb, index, middle, ring, pinky };
        rest = new Quaternion[5][];
        for (int f = 0; f < 5; f++)
        {
            rest[f] = new Quaternion[fingers[f].Length];
            for (int j = 0; j < fingers[f].Length; j++)
                rest[f][j] = fingers[f][j].localRotation;
        }

        if (wrist != null) wristRest = wrist.localRotation;

        try
        {
            port = new SerialPort(portName, baudRate);
            port.ReadTimeout = 500;
            port.NewLine = "\n";
            port.Open();
            Debug.Log("Puerto abierto OK: " + portName);
        }
        catch (System.Exception e)
        {
            Debug.LogError("No se pudo abrir el puerto: " + e.Message);
            return;
        }

        thread = new Thread(ReadLoop) { IsBackground = true };
        thread.Start();
    }

    void ReadLoop()
    {
        while (running)
        {
            string line;
            try
            {
                line = port.ReadLine();
            }
            catch (System.TimeoutException)
            {
                continue; // normal si no llegan datos en 500ms
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("Excepcion leyendo: " + ex.GetType().Name + " - " + ex.Message);
                continue;
            }
            Debug.Log("Recibido: [" + line + "]");
            // Formato esperado: "flex,pitch,roll,yaw"  ej: "512,12.3,-4.1,88.7"
            string[] parts = line.Trim().Split(',');
            if (parts.Length < 4)
            {
                Debug.LogWarning("Linea con formato inesperado: [" + line + "]");
                continue;
            }

            if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) &&
               float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float p) &&
               float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
               float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            {
                lock (dataLock)
                {
                    rawFlex = f;
                    rawPitch = p;
                    rawRoll = r;
                    rawYaw = y;
                }
                Debug.Log($"Parseado -> flex:{f} pitch:{p} roll:{r} yaw:{y}");
            }
            else
            {
                Debug.LogWarning("No se pudo parsear: [" + line + "]");
            }
        }
    }

    void Update()
    {
        int flex; float pitch, roll, yaw;
        lock (dataLock)
        {
            flex = rawFlex; pitch = rawPitch; roll = rawRoll; yaw = rawYaw;
        }

        // --- Flexión de dedos ---
        float target = Mathf.Clamp01(flex / 4095f) * maxAngle;
        angle = Mathf.Lerp(angle, target, Time.deltaTime * flexSmooth);

        for (int fi = 0; fi < 5; fi++)
            for (int j = 0; j < fingers[fi].Length; j++)
                fingers[fi][j].localRotation =
                    rest[fi][j] * Quaternion.AngleAxis(angle * W[Mathf.Min(j, 2)], flexAxis);

        // --- Rotación de muñeca con el MPU ---
        if (wrist != null)
        {
            Vector3 euler = new Vector3(
                pitch * mpuAxesInvert.x,
                yaw * mpuAxesInvert.y,
                roll * mpuAxesInvert.z
            );
            wristTarget = wristRest * Quaternion.Euler(euler);
            wrist.localRotation = Quaternion.Slerp(wrist.localRotation, wristTarget, Time.deltaTime * rotSmooth);
        }
    }

    void OnDestroy()
    {
        running = false;
        try { if (port != null && port.IsOpen) port.Close(); } catch { }
        thread?.Join(200);
    }
}