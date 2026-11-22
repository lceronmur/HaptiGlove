using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

/// <summary>
/// Mueve la POSICIÓN de este objeto (la mano) siguiendo tu mano real en la webcam con MediaPipe.
/// No toca la rotación ni los dedos.
///
/// Al dar Play arranca solo hand_tracking/hand_tracker.py (descarga el modelo si falta) y lo cierra al parar.
/// La mano se mueve en pantalla igual que tu mano en la imagen de la cámara, y se acerca/aleja con la profundidad.
///
/// Teclas (ventana Game enfocada): C = recentrar (tu mano actual = posición inicial del modelo).
/// </summary>
public class HandTrackerUDP : MonoBehaviour {
    const string ModelUrl = "https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/latest/hand_landmarker.task";

    [Header("Rastreador MediaPipe (Python)")]
    [Tooltip("Arrancar hand_tracker.py automáticamente al dar Play.")]
    public bool autoStartTracker = true;
    [Tooltip("Ruta a python3. Vacío = buscarlo automáticamente.")]
    public string pythonPath = "";
    public int cameraIndex = 0;
    [Tooltip("Además, abrir la ventana aparte de OpenCV (normalmente no hace falta).")]
    public bool showSeparateWindow = false;
    public int port = 5065;

    [Header("Imagen de la cámara en la escena")]
    [Tooltip("Mostrar la webcam (con la mano dibujada) en una esquina de abajo de la pantalla del juego.")]
    public bool showCameraPreview = true;
    public enum Corner { AbajoDerecha, AbajoIzquierda }
    public Corner previewCorner = Corner.AbajoDerecha;
    [Tooltip("Alto de la imagen como fracción de la altura de la pantalla.")]
    [Range(0.1f, 0.5f)] public float previewHeight = 0.25f;
    [Tooltip("Separación desde el borde inferior (fracción de la pantalla).")]
    [Range(0f, 0.1f)] public float previewMargin = 0.02f;

    [Header("Movimiento")]
    [Tooltip("Cámara de la escena desde la que se ve la mano. Vacío = Main Camera.")]
    public Camera viewCamera;
    [Tooltip("1 = el modelo recorre la pantalla igual que tu mano recorre la imagen de la webcam.")]
    [Range(0.2f, 3f)] public float gain = 1f;
    public bool useDepth = true;
    [Tooltip("Márcalo si al acercar la mano a la webcam el modelo se mueve al revés de lo esperado.")]
    public bool invertDepth = false;
    [Tooltip("Sensibilidad HACIA EL FONDO (alejándose de ti, hacia la escena).")]
    [Range(0f, 6f)] public float depthForwardGain = 2.5f;
    [Tooltip("Cuánto puede ir hacia el fondo, como múltiplo de la distancia inicial a la cámara (1 = el doble de lejos).")]
    [Range(0f, 5f)] public float depthForwardLimit = 1.5f;
    [Tooltip("Sensibilidad HACIA TI (acercándose a la cámara de la escena).")]
    [Range(0f, 6f)] public float depthBackGain = 0.6f;
    [Tooltip("Cuánto puede acercarse hacia ti, como fracción de la distancia inicial (0.3 = 30% más cerca).")]
    [Range(0f, 0.9f)] public float depthBackLimit = 0.3f;

    [Header("Piso")]
    [Tooltip("No dejar que la mano atraviese el piso.")]
    public bool stayAboveFloor = true;
    [Tooltip("Objeto del piso. Vacío = busca un objeto llamado \"Plane\".")]
    public Transform floor;
    [Tooltip("Separación mínima entre la mano y el piso.")]
    public float floorMargin = 0.02f;

    [Header("Suavizado (natural, sin temblor)")]
    [Tooltip("Más bajo = más quieto cuando no mueves la mano.")]
    [Range(0.05f, 5f)] public float stability = 1.2f;
    [Tooltip("Más alto = sigue más rápido los movimientos bruscos.")]
    [Range(0f, 40f)] public float responsiveness = 12f;
    [Tooltip("Estabilidad de la profundidad (es la señal más ruidosa).")]
    [Range(0.05f, 5f)] public float depthStability = 0.6f;
    [Tooltip("Interpolación entre cuadros de la cámara para que se vea fluido.")]
    [Range(5f, 60f)] public float followSpeed = 25f;

    [Header("Si se pierde la mano")]
    [Tooltip("Segundos sin ver la mano antes de volver suavemente a la posición inicial (0 = quedarse quieta).")]
    public float returnToRestAfter = 2f;

    [Header("Debug")]
    public bool showStatus = true;

    // red
    readonly object sync = new object();
    UdpClient client;
    Thread thread;
    volatile bool running;
    bool rxNew, rxFound;
    float rxX, rxY, rxSize;
    volatile int packetCount;
    int seenPackets;
    float lastPacketTime = -999f;

    // video
    UdpClient videoClient;
    Thread videoThread;
    byte[] videoFrame;
    bool videoNew;
    Texture2D videoTex;

    // proceso de Python
    Process proc;
    string trackerStatus = "iniciando...";

    // estado
    Vector3 restPosition, targetPosition;
    bool calibrated, handVisible;
    float lastHandTime = -999f, lastSampleTime = -1f;
    Vector2 centerN, filtN;
    float refLogSize, filtLogSize;
    Vector3 anchorVP;
    readonly OneEuro fx = new OneEuro(), fy = new OneEuro(), fs = new OneEuro();

    Renderer[] handRenderers;
    Collider floorCollider;

    string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    string TrackerDir => Path.Combine(ProjectRoot, "hand_tracking");

    void Start() {
        Application.runInBackground = true;   // seguir moviendo la mano aunque Unity no esté al frente
        if (viewCamera == null) viewCamera = Camera.main;
        restPosition = targetPosition = transform.position;
        handRenderers = GetComponentsInChildren<Renderer>();
        if (floor == null) { var go = GameObject.Find("Plane"); if (go != null) floor = go.transform; }
        if (floor != null) floorCollider = floor.GetComponent<Collider>();

        try {
            client = new UdpClient(port);
        } catch (System.Exception e) {
            Debug.LogWarning("[HandTracker] No se pudo abrir el puerto UDP " + port + ": " + e.Message);
            enabled = false;
            return;
        }
        running = true;
        thread = new Thread(ReceiveLoop) { IsBackground = true };
        thread.Start();

        if (showCameraPreview) {
            try {
                videoClient = new UdpClient(port + 1);
                videoClient.Client.ReceiveBufferSize = 1 << 20;
                videoThread = new Thread(VideoLoop) { IsBackground = true };
                videoThread.Start();
                videoTex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            } catch (System.Exception e) {
                Debug.LogWarning("[HandTracker] No se pudo abrir el puerto de video " + (port + 1) + ": " + e.Message);
            }
        }

        if (autoStartTracker) StartCoroutine(BootTracker());
        else trackerStatus = "esperando hand_tracker.py (arranque manual)";
    }

    // ---------- Rastreador ----------

    IEnumerator BootTracker() {
        string script = Path.Combine(TrackerDir, "hand_tracker.py");
        if (!File.Exists(script)) { Fail("No encuentro " + script); yield break; }

        string model = Path.Combine(TrackerDir, "hand_landmarker.task");
        if (!File.Exists(model)) {
            trackerStatus = "descargando modelo de MediaPipe...";
            Debug.Log("[HandTracker] Descargando modelo de MediaPipe (solo la primera vez)...");
            string tmp = model + ".part";
            using (var req = new UnityWebRequest(ModelUrl, UnityWebRequest.kHttpVerbGET)) {
                req.downloadHandler = new DownloadHandlerFile(tmp) { removeFileOnAbort = true };
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) {
                    Fail("No se pudo descargar el modelo: " + req.error);
                    yield break;
                }
            }
            if (File.Exists(model)) File.Delete(model);
            File.Move(tmp, model);
            Debug.Log("[HandTracker] Modelo descargado.");
        }

        // En Windows se usa la version empaquetada (no necesita Python instalado); si no esta, Python
        string exe = Path.Combine(TrackerDir, "hand_tracker_win", "hand_tracker_win.exe");
        bool usarExe = Application.platform == RuntimePlatform.WindowsEditor && File.Exists(exe);
        string py = usarExe ? exe : ResolvePython();
        if (py == null) { Fail("No encontré python3. Escribe su ruta en el campo Python Path."); yield break; }

        string argsLine = (usarExe ? "" : "-u \"" + script + "\"") + " --camera " + cameraIndex + " --port " + port +
                          (showSeparateWindow ? "" : " --no-window") +
                          (videoClient != null ? " --video-port " + (port + 1) : "");
        var psi = new ProcessStartInfo(py, argsLine) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = TrackerDir
        };
        try {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) OnTrackerLine(e.Data, false); };
            proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) OnTrackerLine(e.Data, true); };
            proc.Exited += (s, e) => {
                int code = -1;
                try { code = proc.ExitCode; } catch { }
                if (running && code != 0) Debug.LogWarning("[HandTracker] El rastreador se cerró (código " + code + "). Revisa los mensajes [Tracker] de arriba.");
                trackerStatus = "rastreador cerrado";
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            trackerStatus = "arrancando MediaPipe...";
            Debug.Log("[HandTracker] Rastreador iniciado con " + py);
        } catch (System.Exception e) {
            Fail("No se pudo iniciar Python (" + py + "): " + e.Message);
        }
    }

    void OnTrackerLine(string line, bool stderr) {
        if (line.StartsWith("ERROR")) { Debug.LogWarning("[Tracker] " + line); trackerStatus = line; return; }
        // MediaPipe escribe mucha información técnica en stderr; solo mostramos lo útil.
        if (stderr && !(line.Contains("Error") || line.Contains("Traceback") || line.StartsWith("  File") ||
                        line.Contains("No module") || line.Contains("Exception"))) return;
        if (stderr) Debug.LogWarning("[Tracker] " + line);
        else { Debug.Log("[Tracker] " + line); if (line.StartsWith("Rastreador listo")) trackerStatus = "rastreador listo"; }
    }

    void Fail(string msg) {
        trackerStatus = msg;
        Debug.LogWarning("[HandTracker] " + msg);
    }

    string ResolvePython() {
        // Una ruta de otro computador (que no existe aqui) se ignora y se busca Python solo
        if (!string.IsNullOrWhiteSpace(pythonPath) && File.Exists(pythonPath.Trim())) return pythonPath.Trim();
        string[] candidates = {
            "/usr/local/bin/python3",
            "/Library/Frameworks/Python.framework/Versions/Current/bin/python3",
            "/opt/homebrew/bin/python3",
            "/usr/bin/python3",
            "C:\\Windows\\py.exe"
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    void StopTracker() {
        try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
        proc = null;
    }

    // ---------- Red ----------

    void ReceiveLoop() {
        var any = new IPEndPoint(IPAddress.Any, 0);
        var inv = CultureInfo.InvariantCulture;
        while (running) {
            try {
                string[] p = System.Text.Encoding.ASCII.GetString(client.Receive(ref any)).Split(',');
                if (p.Length < 4) continue;
                bool f = p[0].Trim() == "1";
                float.TryParse(p[1], NumberStyles.Float, inv, out float x);
                float.TryParse(p[2], NumberStyles.Float, inv, out float y);
                float.TryParse(p[3], NumberStyles.Float, inv, out float s);
                lock (sync) { rxFound = f; rxX = x; rxY = y; rxSize = s; rxNew = true; }
                packetCount++;
            } catch (SocketException) {
            } catch (System.ObjectDisposedException) { break; }
        }
    }

    void VideoLoop() {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (running) {
            try {
                byte[] data = videoClient.Receive(ref any);
                lock (sync) { videoFrame = data; videoNew = true; }
            } catch (SocketException) {
            } catch (System.ObjectDisposedException) { break; }
        }
    }

    // ---------- Movimiento ----------

    void Update() {
        var kb = Keyboard.current;
        if (kb != null && kb.cKey.wasPressedThisFrame) Recenter();

        if (packetCount != seenPackets) { seenPackets = packetCount; lastPacketTime = Time.time; }

        byte[] frame = null;
        lock (sync) { if (videoNew) { frame = videoFrame; videoNew = false; } }
        if (frame != null && videoTex != null) videoTex.LoadImage(frame, false);

        bool isNew, f; float x, y, s;
        lock (sync) { isNew = rxNew; f = rxFound; x = rxX; y = rxY; s = rxSize; rxNew = false; }
        if (isNew) {
            if (f && s > 0f) OnHand(x, y, s);
            else OnNoHand();
        } else if (Time.time - lastPacketTime > 0.5f) OnNoHand();

        if (!handVisible && returnToRestAfter > 0f && Time.time - lastHandTime > returnToRestAfter)
            targetPosition = restPosition;

        float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
        Vector3 pos = Vector3.Lerp(transform.position, targetPosition, t);
        if (stayAboveFloor && floor != null) pos.y = Mathf.Max(pos.y, MinHeightAboveFloor());
        transform.position = pos;   // solo posición
    }

    /// Altura mínima del pivote para que el punto más bajo de la mano quede sobre el piso.
    float MinHeightAboveFloor() {
        float floorY = floorCollider != null ? floorCollider.bounds.max.y : floor.position.y;
        if (handRenderers == null || handRenderers.Length == 0) return floorY + floorMargin;
        float minY = float.MaxValue;
        foreach (var r in handRenderers) if (r != null && r.enabled) minY = Mathf.Min(minY, r.bounds.min.y);
        if (minY == float.MaxValue) return floorY + floorMargin;
        float bottomOffset = transform.position.y - minY;   // del pivote al punto más bajo
        return floorY + bottomOffset + floorMargin;
    }

    void OnHand(float x, float y, float size) {
        float now = Time.time;
        float dt = lastSampleTime < 0f ? 1f / 30f : Mathf.Clamp(now - lastSampleTime, 1e-3f, 0.2f);
        lastSampleTime = now;
        handVisible = true;
        lastHandTime = now;

        filtN.x = fx.Filter(x, dt, stability, responsiveness);
        filtN.y = fy.Filter(y, dt, stability, responsiveness);
        filtLogSize = fs.Filter(Mathf.Log(size), dt, depthStability, 1f);

        if (!calibrated) Recenter();
        if (viewCamera == null) return;

        // Posición en pantalla: el modelo se desplaza igual que tu mano en la imagen.
        Vector2 vp = new Vector2(anchorVP.x, anchorVP.y) + (filtN - centerN) * gain;

        // Profundidad: mano más grande en la imagen = más cerca de la webcam.
        float depth = anchorVP.z;
        if (useDepth) {
            float ratio = Mathf.Exp(filtLogSize - refLogSize);   // >1 = más cerca de la webcam
            float delta = (ratio - 1f) * (invertDepth ? -1f : 1f);
            // Hacia el fondo y hacia ti tienen sensibilidad y límite propios.
            delta = delta >= 0f
                ? Mathf.Min(delta * depthForwardGain, depthForwardLimit)
                : Mathf.Max(delta * depthBackGain, -depthBackLimit);
            // Visto desde atrás de la mano: acercarla a la webcam la aleja de la cámara de la escena.
            depth = anchorVP.z * (1f + delta);
        }
        targetPosition = viewCamera.ViewportToWorldPoint(new Vector3(vp.x, vp.y, depth));
    }


    void OnNoHand() {
        if (!handVisible) return;
        handVisible = false;
        lastSampleTime = -1f;
        fx.Reset(); fy.Reset(); fs.Reset();
    }

    /// Tu mano en su posición actual = el modelo en su posición inicial.
    public void Recenter() {
        if (!handVisible) { Debug.Log("[HandTracker] Pon la mano frente a la cámara para recentrar."); return; }
        if (viewCamera == null) viewCamera = Camera.main;
        centerN = filtN;
        refLogSize = filtLogSize;
        anchorVP = viewCamera.WorldToViewportPoint(restPosition);
        if (anchorVP.z < 0.1f) anchorVP.z = Mathf.Max(0.1f, Vector3.Distance(viewCamera.transform.position, restPosition));
        calibrated = true;
        Debug.Log("[HandTracker] Centro fijado.");
    }

    void OnGUI() {
        if (!showStatus) return;
        bool connected = Time.time - lastPacketTime <= 1f;
        string txt = !connected ? "MediaPipe: " + trackerStatus
                   : handVisible ? "MediaPipe: mano detectada   (C = recentrar)"
                   : "MediaPipe: conectado, muestra la mano a la cámara";
        GUI.color = Color.black; GUI.Label(new Rect(11, 11, 700, 22), txt);
        GUI.color = Color.white; GUI.Label(new Rect(10, 10, 700, 22), txt);

        if (showCameraPreview && videoTex != null && videoTex.width > 2) {
            float h = Screen.height * previewHeight;
            float w = h * videoTex.width / videoTex.height;
            float m = Screen.height * previewMargin;
            float x = previewCorner == Corner.AbajoDerecha ? Screen.width - w - m : m;
            Rect r = new Rect(x, Screen.height - h - m, w, h);
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(r, videoTex, ScaleMode.StretchToFill);
        }
    }

    void OnDestroy() {
        running = false;
        StopTracker();
        client?.Close();
        videoClient?.Close();
        thread?.Join(200);
        videoThread?.Join(200);
        if (videoTex != null) Destroy(videoTex);
    }

    void OnApplicationQuit() => StopTracker();

    /// Filtro One Euro: suave cuando la mano va lenta, rápido cuando va rápida.
    class OneEuro {
        float x, dx; bool init;
        public void Reset() { init = false; }
        public float Filter(float v, float dt, float minCutoff, float beta, float dCutoff = 1f) {
            if (!init) { x = v; dx = 0f; init = true; return v; }
            float d = (v - x) / dt;
            dx = Mathf.Lerp(dx, d, Alpha(dt, dCutoff));
            float cutoff = minCutoff + beta * Mathf.Abs(dx);
            x = Mathf.Lerp(x, v, Alpha(dt, cutoff));
            return x;
        }
        static float Alpha(float dt, float cutoff) {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }
}
