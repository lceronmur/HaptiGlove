using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using UnityEngine;

// Lee del ESP32 lineas "p,i,m,a,n" (0..4095, ya calibradas, modo 4 UNITY) y,
// si hay MPU6050, ",qw,qx,qy,qz" con la orientacion de la muneca.
// Funciona por cable USB o por Bluetooth (en Windows el Bluetooth del ESP32
// aparece como un puerto COM virtual al emparejarlo).
// Incluye un panel en pantalla para calibrar el guante y probar los motores,
// y control de los vibromotores con el teclado.
public class HandDemo : MonoBehaviour {
    public enum Conexion { Auto, USB, Bluetooth }

    [Header("Conexion")]
    [Tooltip("Auto: intenta USB y, si no hay datos, Bluetooth. Se reconecta solo si se pierde la senal.")]
    public Conexion conexion = Conexion.Auto;
    [Tooltip("COM del cable USB (Administrador de dispositivos > Puertos)")]
    public string puertoUSB = "COM3";
    [Tooltip("COM 'saliente' del Bluetooth HaptiGlove (Configuracion Bluetooth > Mas opciones > Puertos COM)")]
    public string puertoBluetooth = "COM5";
    public int baudRate = 115200;
    [Tooltip("Activar si la placa usa USB nativo y no llegan datos por cable")]
    public bool dtr = false;
    [Tooltip("Segundos sin datos para considerar la conexion perdida")]
    public float segundosSinDatos = 3f;

    [Header("Movimiento")]
    public float maxAngle = 90f;
    public Vector3 flexAxis = Vector3.forward;
    public float suavizado = 15f;
    [Tooltip("Marque un dedo si se mueve al reves")]
    public bool[] invertir = new bool[5];

    [Header("Huesos de cada dedo (base -> punta)")]
    public Transform[] thumb, index, middle, ring, pinky;

    [Header("Muneca (MPU6050)")]
    [Tooltip("Hueso de la muneca / raiz de la mano que gira con el MPU6050")]
    public Transform muneca;
    public bool usarIMU = true;
    public float suavizadoRotacion = 20f;
    public bool imuDetectado = false;

    [Header("Vibracion")]
    public bool hapticaActiva = true;
    [Tooltip("Cada cuanto se envian cambios de vibracion al guante (s)")]
    public float intervaloEnvio = 0.05f;
    [Tooltip("Reenvio periodico mientras vibra; el ESP32 apaga los motores si no recibe nada en 1 s")]
    public float intervaloKeepAlive = 0.3f;
    [Range(0, 1)] public float[] vibracionActual = new float[5];

    [Header("Vibracion con teclado (mantener presionada)")]
    public bool tecladoActivo = true;
    [Tooltip("Tecla de cada motor: pulgar, indice, medio, anular, menique")]
    public KeyCode[] teclasMotor = { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5 };
    [Tooltip("Tecla que enciende los 5 motores a la vez")]
    public KeyCode teclaTodos = KeyCode.Space;
    [Range(0, 1)] public float intensidadTeclado = 1f;

    [Header("Vibracion por colision (dedo toca un objeto)")]
    [Tooltip("Crea al iniciar sensores en la punta de cada dedo; al tocar un objeto con Collider vibra ese dedo")]
    public bool vibrarAlColisionar = true;
    [Tooltip("Cuantos huesos de la punta de cada dedo detectan colision (1 = solo la ultima falange)")]
    [Range(1, 4)] public int huesosConSensor = 2;
    [Tooltip("Radio del sensor como fraccion del largo de la falange")]
    [Range(0.1f, 1.5f)] public float radioSensor = 0.5f;
    [Tooltip("Vacio = cualquier objeto. Si pone un Tag (ej. 'Tocable'), solo vibra con objetos que lo tengan")]
    public string soloConTag = "";
    [Range(0, 1)] public float intensidadContacto = 0.7f;  // mientras sigue tocando
    [Range(0, 1)] public float intensidadImpacto = 1f;     // golpe al empezar a tocar
    public float duracionImpacto = 0.12f;
    [Tooltip("Solo lectura: que dedos estan tocando algo")]
    public bool[] dedoTocando = new bool[5];

    [Header("Calibracion")]
    public bool mostrarPanel = true;
    [Tooltip("Segundos para acomodar la mano antes de cada captura")]
    public int cuentaRegresiva = 3;

    [Header("Debug (solo lectura)")]
    public bool logLineas = false;
    public string estado = "Desconectado";
    [Range(0, 1)] public float[] flexionActual = new float[5];

    static readonly float[] W = { 1.0f, 1.0f, 0.7f };  // peso por falange
    static readonly string[] NOMBRES = { "Pulgar", "Indice", "Medio", "Anular", "Menique" };

    SerialPort port;
    Thread thread;
    readonly int[] raw = new int[5];
    readonly float[] imuQ = { 1, 0, 0, 0 };  // w, x, y, z (marco del sensor)
    volatile bool hayIMU = false;
    Quaternion qReferencia, rotInicialMuneca;
    bool referenciaTomada = false;
    volatile bool running = true;
    volatile bool conectado = false;
    Transform[][] fingers;
    Quaternion[][] rest;
    readonly float[] angle = new float[5];

    // Mensajes de texto del ESP32 (calibracion, ayuda...) para mostrar en el panel
    readonly ConcurrentQueue<string> colaMensajes = new ConcurrentQueue<string>();
    readonly List<string> mensajes = new List<string>();
    const int MAX_MENSAJES = 12;

    // Estado del asistente de calibracion
    bool calibrando = false;
    string instruccion = "";
    volatile bool listoRecibido = false;
    Vector2 scrollMensajes;

    // Estado de la vibracion
    readonly float[] contacto = new float[5];        // vibracion sostenida (mientras toca)
    readonly float[] pulsoIntensidad = new float[5]; // vibracion corta (impacto)
    readonly float[] pulsoHasta = new float[5];
    readonly int[] ultimoEnviado = new int[5];
    float proximoEnvio, ultimoEnvioMotores;

    void Start() {
        fingers = new[] { thumb, index, middle, ring, pinky };
        rest = new Quaternion[5][];
        for (int f = 0; f < 5; f++) {
            if (fingers[f] == null) fingers[f] = new Transform[0];
            rest[f] = new Quaternion[fingers[f].Length];
            for (int j = 0; j < fingers[f].Length; j++)
                rest[f][j] = fingers[f][j].localRotation;
        }
        if (flexionActual == null || flexionActual.Length != 5) flexionActual = new float[5];
        if (vibracionActual == null || vibracionActual.Length != 5) vibracionActual = new float[5];
        if (muneca != null) rotInicialMuneca = muneca.rotation;
        if (dedoTocando == null || dedoTocando.Length != 5) dedoTocando = new bool[5];
        // El agarre (GrabController) se agrega solo si la escena no tiene uno
        if (FindFirstObjectByType<GrabController>() == null) gameObject.AddComponent<GrabController>().mano = this;
        if (vibrarAlColisionar) CrearSensores();

        thread = new Thread(BucleConexion) { IsBackground = true };
        thread.Start();
    }

    // ------------------------------------------------------------------ Conexion

    List<(string nombre, string puerto, bool bt)> Candidatos() {
        var l = new List<(string, string, bool)>();
        if (conexion != Conexion.Bluetooth && !string.IsNullOrEmpty(puertoUSB))
            l.Add(("USB", puertoUSB, false));
        if (conexion != Conexion.USB && !string.IsNullOrEmpty(puertoBluetooth))
            l.Add(("Bluetooth", puertoBluetooth, true));
        return l;
    }

    void BucleConexion() {
        while (running) {
            foreach (var c in Candidatos()) {
                if (!running) return;
                if (Abrir(c.nombre, c.puerto, c.bt)) {
                    Leer(c.nombre);   // vuelve cuando se pierden los datos
                    Cerrar();
                }
            }
            if (running) {
                estado = "Sin conexion, reintentando...";
                Thread.Sleep(1000);
            }
        }
    }

    bool Abrir(string nombre, string puerto, bool bt) {
        estado = "Conectando " + nombre + " (" + puerto + ")...";
        try {
            // COM10 en adelante necesita el prefijo \\.\ en Windows
            string p = puerto.StartsWith("COM") && puerto.Length > 4 ? @"\\.\" + puerto : puerto;
            var sp = new SerialPort(p, baudRate) {
                ReadTimeout = 500,
                WriteTimeout = 1000,
                NewLine = "\n",
                DtrEnable = !bt && dtr,
                RtsEnable = false
            };
            sp.Open();  // por Bluetooth puede tardar unos segundos en enlazar
            sp.DiscardInBuffer();
            sp.Write("4\n");  // fuerza modo UNITY en el ESP32
            port = sp;
            Debug.Log("Puerto " + nombre + " abierto: " + puerto);
            return true;
        } catch (System.Exception e) {
            Debug.LogWarning("No se pudo abrir " + nombre + " (" + puerto + "): " + e.Message);
            Cerrar();
            return false;
        }
    }

    void Leer(string nombre) {
        bool recibio = false;
        var sinDatos = System.Diagnostics.Stopwatch.StartNew();

        while (running && sinDatos.Elapsed.TotalSeconds < segundosSinDatos) {
            try {
                string line = port.ReadLine().Trim();
                if (line.Length == 0) continue;
                if (logLineas) Debug.Log("[" + nombre + "] " + line);
                sinDatos.Restart();

                // "p,i,m,a,n" o "p,i,m,a,n,qw,qx,qy,qz" (con MPU6050)
                string[] partes = line.Split(',');
                int[] v = new int[5];
                bool ok = partes.Length == 5 || partes.Length == 9;
                for (int i = 0; i < 5 && ok; i++) ok = int.TryParse(partes[i], out v[i]);

                float[] q = null;
                if (ok && partes.Length == 9) {
                    q = new float[4];
                    for (int i = 0; i < 4 && ok; i++)
                        ok = float.TryParse(partes[5 + i], System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out q[i]);
                }

                if (!ok) {
                    // Texto del ESP32: se muestra en el panel
                    if (line.Contains("Listo")) listoRecibido = true;
                    colaMensajes.Enqueue(line);
                    continue;
                }

                if (!recibio) {
                    recibio = true;
                    conectado = true;
                    estado = "Conectado por " + nombre;
                    Debug.Log("Recibiendo datos por " + nombre + ": [" + line + "]");
                }
                lock (raw) {
                    System.Array.Copy(v, raw, 5);
                    if (q != null) System.Array.Copy(q, imuQ, 4);
                }
                hayIMU = q != null;
            } catch (System.TimeoutException) {
                // se controla con el cronometro sinDatos
            } catch (System.Exception ex) {
                if (running) Debug.LogWarning("[" + nombre + "] Error leyendo: " + ex.Message);
                break;
            }
        }

        conectado = false;
        if (running)
            Debug.LogWarning(recibio
                ? "Se perdio la conexion por " + nombre + ". Reintentando..."
                : "Sin datos por " + nombre + ". Probando siguiente opcion...");
    }

    void Cerrar() {
        var sp = port;
        port = null;
        try { if (sp != null && sp.IsOpen) sp.Close(); } catch { }
    }

    // Envia un comando al ESP32 (a, c, r, t, g, 1..4, h, V,...)
    public bool Enviar(string comando, bool silencioso = false) {
        var sp = port;
        if (sp == null || !sp.IsOpen) {
            if (!silencioso) AgregarMensaje("!! No hay conexion con el guante");
            return false;
        }
        try {
            sp.Write(comando + "\n");
            return true;
        } catch (System.Exception e) {
            if (!silencioso) AgregarMensaje("!! Error enviando '" + comando + "': " + e.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ Vibracion (API publica)

    // Vibracion sostenida de un dedo (0..1). La usa YemaHaptica mientras hay contacto.
    public void SetContacto(int dedo, float intensidad) {
        if (dedo >= 0 && dedo < 5) contacto[dedo] = Mathf.Clamp01(intensidad);
    }

    // Vibracion corta de un dedo (0..1) durante 'duracion' segundos.
    public void Pulso(int dedo, float intensidad, float duracion) {
        if (dedo < 0 || dedo >= 5) return;
        pulsoIntensidad[dedo] = Mathf.Clamp01(intensidad);
        pulsoHasta[dedo] = Time.time + duracion;
    }

    // ------------------------------------------------------------------ Colisiones de los dedos

    readonly int[] toques = new int[5];  // sensores del dedo que estan tocando algo

    // Pone una esfera trigger en los ultimos huesos de cada dedo (si no tienen ya una YemaHaptica)
    void CrearSensores() {
        int creados = 0;
        for (int f = 0; f < 5; f++) {
            var huesos = fingers[f];
            if (huesos.Length == 0) continue;
            int desde = Mathf.Max(0, huesos.Length - huesosConSensor);
            for (int j = desde; j < huesos.Length; j++) {
                Transform h = huesos[j];
                if (h == null || h.GetComponentInChildren<YemaHaptica>() != null) continue;

                // Largo aproximado de la falange (distancia al hueso anterior o siguiente)
                Transform vecino = j > 0 ? huesos[j - 1] : (huesos.Length > 1 ? huesos[1] : null);
                float largo = vecino != null ? Vector3.Distance(h.position, vecino.position) : 0.02f;
                if (largo < 1e-4f) largo = 0.02f;

                var go = new GameObject("SensorColision_" + NOMBRES[f] + "_" + j);
                go.transform.SetParent(h, false);
                go.layer = h.gameObject.layer;
                var esfera = go.AddComponent<SphereCollider>();
                float escala = Mathf.Max(h.lossyScale.x, h.lossyScale.y, h.lossyScale.z);
                esfera.radius = largo * radioSensor / Mathf.Max(escala, 1e-6f);
                go.AddComponent<YemaHaptica>().Configurar(this, f, soloConTag);
                creados++;
            }
        }
        AgregarMensaje(">> Sensores de colision creados: " + creados);
    }

    // Llamado por YemaHaptica cuando un sensor empieza (true) o deja (false) de tocar un objeto
    public void Tocar(int dedo, bool tocando) {
        if (dedo < 0 || dedo >= 5) return;
        toques[dedo] = Mathf.Max(0, toques[dedo] + (tocando ? 1 : -1));
        bool antes = dedoTocando[dedo];
        dedoTocando[dedo] = toques[dedo] > 0;
        if (dedoTocando[dedo] && !antes) Pulso(dedo, intensidadImpacto, duracionImpacto);
        SetContacto(dedo, dedoTocando[dedo] ? intensidadContacto : 0f);
    }

    // Raiz de la mano 3D, para que los sensores ignoren los colliders de la propia mano
    public bool EsParteDeLaMano(Transform t) {
        if (fingers == null) return false;
        foreach (var dedo in fingers)
            if (dedo.Length > 0 && dedo[0] != null && t.IsChildOf(dedo[0].root)) return true;
        return muneca != null && t.IsChildOf(muneca.root);
    }

    [ContextMenu("Probar motores (uno por uno)")]
    public void ProbarMotores() { if (Application.isPlaying) Enviar("t"); }

    // Intensidad pedida por teclado para un dedo (0 si no hay tecla presionada)
    float Teclado(int dedo) {
        if (!tecladoActivo) return 0f;
        bool presionada = Input.GetKey(teclaTodos) ||
                          (teclasMotor != null && dedo < teclasMotor.Length && Input.GetKey(teclasMotor[dedo]));
        return presionada ? intensidadTeclado : 0f;
    }

    void ActualizarVibracion() {
        bool cambio = false, alguno = false;
        int[] v = new int[5];
        for (int f = 0; f < 5; f++) {
            float p = Time.time < pulsoHasta[f] ? pulsoIntensidad[f] : 0f;
            float total = hapticaActiva ? Mathf.Max(contacto[f], p, Teclado(f)) : 0f;
            vibracionActual[f] = total;
            v[f] = Mathf.RoundToInt(total * 255);
            if (v[f] != ultimoEnviado[f]) cambio = true;
            if (v[f] > 0) alguno = true;
        }

        if (Time.time < proximoEnvio) return;
        bool keepAlive = alguno && Time.time - ultimoEnvioMotores > intervaloKeepAlive;
        if (!cambio && !keepAlive) return;

        if (Enviar("V," + v[0] + "," + v[1] + "," + v[2] + "," + v[3] + "," + v[4], true)) {
            System.Array.Copy(v, ultimoEnviado, 5);
            ultimoEnvioMotores = Time.time;
        }
        proximoEnvio = Time.time + intervaloEnvio;
    }

    // ------------------------------------------------------------------ Calibracion

    [ContextMenu("Calibrar guante (asistente)")]
    public void IniciarCalibracion() {
        if (!Application.isPlaying || calibrando) return;
        StartCoroutine(AsistenteCalibracion());
    }

    [ContextMenu("Calibrar solo mano ABIERTA")]
    public void CalibrarAbierta() {
        if (Application.isPlaying && !calibrando) StartCoroutine(Capturar("a", "Estira todos los dedos", "MANO ABIERTA"));
    }

    [ContextMenu("Calibrar solo mano CERRADA")]
    public void CalibrarCerrada() {
        if (Application.isPlaying && !calibrando) StartCoroutine(Capturar("c", "Cierra el puno", "MANO CERRADA"));
    }

    [ContextMenu("Borrar calibracion")]
    public void BorrarCalibracion() { if (Application.isPlaying) Enviar("r"); }

    IEnumerator AsistenteCalibracion() {
        calibrando = true;
        yield return Capturar("a", "Estira todos los dedos", "MANO ABIERTA", false);
        if (calibrando) yield return Capturar("c", "Cierra el puno", "MANO CERRADA", false);
        if (calibrando) instruccion = "Calibracion completa. Revisa el rango de cada dedo abajo.";
        calibrando = false;
    }

    IEnumerator Capturar(string comando, string accion, string etiqueta, bool controlarFlag = true) {
        if (controlarFlag) calibrando = true;

        if (!conectado) {
            instruccion = "No hay conexion con el guante.";
            calibrando = false;
            yield break;
        }

        for (int s = cuentaRegresiva; s > 0; s--) {
            instruccion = accion + "...  " + s;
            yield return new WaitForSeconds(1f);
        }

        instruccion = "Capturando " + etiqueta + ": no muevas la mano";
        listoRecibido = false;
        if (!Enviar(comando)) { calibrando = false; yield break; }

        // El ESP32 tarda ~1 s; se espera su ">> Listo." (maximo 5 s)
        float limite = Time.time + 5f;
        while (!listoRecibido && Time.time < limite) yield return null;

        if (!listoRecibido) {
            instruccion = "El guante no respondio. Revisa la conexion.";
            calibrando = false;
            yield break;
        }

        instruccion = etiqueta + " guardada.";
        if (controlarFlag) calibrando = false;
    }

    // ------------------------------------------------------------------ Muneca (MPU6050)

    // Toma la posicion actual de la mano como "neutral" (mano al frente, palma abajo)
    [ContextMenu("Recentrar muneca")]
    public void RecentrarMuneca() {
        referenciaTomada = false;
        AgregarMensaje(">> Muneca recentrada");
    }

    [ContextMenu("Calibrar giroscopio")]
    public void CalibrarGiroscopio() {
        if (Application.isPlaying && !calibrando) StartCoroutine(RutinaGiroscopio());
    }

    IEnumerator RutinaGiroscopio() {
        calibrando = true;
        instruccion = "Deja el guante QUIETO sobre la mesa...";
        if (!Enviar("g")) { calibrando = false; yield break; }
        yield return new WaitForSeconds(3.5f);  // 1 s de calibracion + 2 s de convergencia
        calibrando = false;
        instruccion = "Giroscopio calibrado. Pon la mano en posicion neutral y pulsa Recentrar.";
        RecentrarMuneca();
    }

    void ActualizarMuneca() {
        imuDetectado = hayIMU;
        if (muneca == null || !usarIMU || !hayIMU) return;

        float w, x, y, z;
        lock (raw) { w = imuQ[0]; x = imuQ[1]; y = imuQ[2]; z = imuQ[3]; }

        // Sensor (mano derecha, Z arriba) -> Unity (mano izquierda, Y arriba)
        Quaternion qSensor = new Quaternion(x, z, y, -w);

        if (!referenciaTomada) {
            qReferencia = qSensor;
            referenciaTomada = true;
        }

        // Giro relativo a la referencia, aplicado sobre la pose inicial del modelo
        Quaternion objetivo = qSensor * Quaternion.Inverse(qReferencia) * rotInicialMuneca;
        muneca.rotation = Quaternion.Slerp(muneca.rotation, objetivo, Time.deltaTime * suavizadoRotacion);
    }

    void AgregarMensaje(string m) {
        mensajes.Add(m);
        while (mensajes.Count > MAX_MENSAJES) mensajes.RemoveAt(0);
        scrollMensajes.y = float.MaxValue;
    }

    // ------------------------------------------------------------------ Animacion

    void Update() {
        while (colaMensajes.TryDequeue(out string m)) AgregarMensaje(m);
        if (!calibrando) ActualizarVibracion();
        ActualizarMuneca();

        int[] v = new int[5];
        lock (raw) { System.Array.Copy(raw, v, 5); }

        for (int f = 0; f < 5; f++) {
            float t = Mathf.Clamp01(v[f] / 4095f);
            if (invertir != null && f < invertir.Length && invertir[f]) t = 1f - t;
            flexionActual[f] = t;

            angle[f] = Mathf.Lerp(angle[f], t * maxAngle, Time.deltaTime * suavizado);

            for (int j = 0; j < fingers[f].Length; j++)
                fingers[f][j].localRotation =
                    rest[f][j] * Quaternion.AngleAxis(angle[f] * W[Mathf.Min(j, 2)], flexAxis);
        }
    }

    // Donde quedaria la punta del dedo f doblado 't' (0..1). Dobla el dedo un instante y lo deja como estaba.
    public Vector3 PuntaDoblada(int f, float t) {
        var h = fingers[f];
        if (h.Length == 0) return transform.position;
        var antes = new Quaternion[h.Length];
        for (int j = 0; j < h.Length; j++) {
            antes[j] = h[j].localRotation;
            h[j].localRotation = rest[f][j] * Quaternion.AngleAxis(t * maxAngle * W[Mathf.Min(j, 2)], flexAxis);
        }
        Vector3 p = h[h.Length - 1].position;
        for (int j = 0; j < h.Length; j++) h[j].localRotation = antes[j];
        return p;
    }

    // ------------------------------------------------------------------ Panel en pantalla

    GUIStyle estiloTitulo, estiloInstruccion;

    void OnGUI() {
        if (!mostrarPanel) return;

        if (estiloTitulo == null) {
            estiloTitulo = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            estiloInstruccion = new GUIStyle(GUI.skin.label) {
                fontSize = 18, fontStyle = FontStyle.Bold, wordWrap = true,
                normal = { textColor = Color.yellow }
            };
        }

        GUILayout.BeginArea(new Rect(10, 10, 360, Screen.height - 20), GUI.skin.box);
        GUILayout.Label("HaptiGlove", estiloTitulo);
        GUILayout.Label("Estado: " + estado);
        GUILayout.Space(6);

        // Barras de flexion por dedo
        for (int f = 0; f < 5; f++) {
            GUILayout.BeginHorizontal();
            GUILayout.Label(NOMBRES[f], GUILayout.Width(65));
            Rect r = GUILayoutUtility.GetRect(200, 16);
            GUI.Box(r, GUIContent.none);
            GUI.DrawTexture(new Rect(r.x + 1, r.y + 1, (r.width - 2) * flexionActual[f], r.height - 2),
                            Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0,
                            Color.Lerp(Color.green, Color.red, flexionActual[f]), 0, 0);
            GUILayout.Label(Mathf.RoundToInt(flexionActual[f] * 100) + "%", GUILayout.Width(40));
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(8);
        GUI.enabled = conectado && !calibrando;
        if (GUILayout.Button("Calibrar guante (abierta + cerrada)", GUILayout.Height(32))) IniciarCalibracion();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Solo abierta")) CalibrarAbierta();
        if (GUILayout.Button("Solo cerrada")) CalibrarCerrada();
        if (GUILayout.Button("Borrar")) BorrarCalibracion();
        GUILayout.EndHorizontal();
        GUILayout.Space(6);
        GUILayout.Label("Vibracion (teclas 1-5, Espacio = todos):");
        GUILayout.BeginHorizontal();
        for (int f = 0; f < 5; f++)
            if (GUILayout.Button(NOMBRES[f].Substring(0, 3) + (vibracionActual[f] > 0 ? " *" : "")))
                Pulso(f, 1f, 0.3f);
        GUILayout.EndHorizontal();
        if (GUILayout.Button("Probar todos los motores")) ProbarMotores();

        GUILayout.Space(6);
        GUILayout.Label("Muneca (MPU6050): " + (hayIMU ? "OK" : "no detectado"));
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Recentrar muneca")) RecentrarMuneca();
        if (GUILayout.Button("Calibrar giroscopio")) CalibrarGiroscopio();
        GUILayout.EndHorizontal();
        GUI.enabled = true;
        hapticaActiva = GUILayout.Toggle(hapticaActiva, " Haptica activa");
        tecladoActivo = GUILayout.Toggle(tecladoActivo, " Vibrar con teclado");

        if (calibrando && GUILayout.Button("Cancelar")) {
            StopAllCoroutines();
            calibrando = false;
            instruccion = "Calibracion cancelada.";
        }

        if (!string.IsNullOrEmpty(instruccion)) {
            GUILayout.Space(6);
            GUILayout.Label(instruccion, estiloInstruccion);
        }

        GUILayout.Space(6);
        GUILayout.Label("Mensajes del guante:");
        scrollMensajes = GUILayout.BeginScrollView(scrollMensajes, GUILayout.Height(200));
        foreach (var m in mensajes) GUILayout.Label(m);
        GUILayout.EndScrollView();

        GUILayout.EndArea();
    }

    void OnDestroy() {
        running = false;
        thread?.Join(1000);
        Cerrar();
    }
}
