using System.Collections.Generic;
using UnityEngine;

// Maquina de estados de agarre (Figura 7, seccion 11.4): Libre -> Tocando -> Sostenido -> Libre.
// Las yemas (YemaHaptica) avisan aqui, con NotificarContacto, cuando entran o salen del
// trigger de un objeto Grabbable. Cuando el pulgar y al menos otro dedo tocan el mismo
// objeto y la flexion promedio de esos dedos supera umbralAgarrar, el objeto pasa a
// cinematico y se hace hijo de palmAnchor (sigue a la mano). Se suelta cuando la flexion
// promedio baja de umbralSoltar o el pulgar deja de tocarlo (histeresis para evitar que el
// ruido de los sensores lo suelte y lo vuelva a agarrar).
public class GrabController : MonoBehaviour {
    [Tooltip("Si se deja vacio se busca el HandDemo de la escena")]
    public HandDemo mano;
    [Tooltip("Objeto vacio en el centro de la palma; el bloque se emparenta aqui al sostenerlo")]
    public Transform palmAnchor;

    [Range(0, 1)] public float umbralAgarrar = 0.45f;
    [Range(0, 1)] public float umbralSoltar = 0.30f;

    public enum Estado { Libre, Tocando, Sostenido }
    public Estado estado = Estado.Libre;

    // Para cada objeto agarrable, que dedos lo estan tocando ahora mismo (0 = pulgar ... 4 = menique)
    readonly Dictionary<Grabbable, HashSet<int>> contactos = new Dictionary<Grabbable, HashSet<int>>();
    // Cada dedo tiene varios sensores (uno por falange): se cuenta cuantos tocan cada objeto,
    // para que el dedo no "suelte" cuando sale una falange pero otra sigue tocando
    readonly Dictionary<Grabbable, int[]> sensores = new Dictionary<Grabbable, int[]>();
    Grabbable sostenido;

    [Header("Objeto de prueba (cilindro)")]
    [Tooltip("Si se deja vacio se usa el primer Grabbable de la escena")]
    public Grabbable objeto;
    [Tooltip("Al iniciar pone el objeto dentro de la mano: se toma solo cerrando los dedos")]
    public bool colocarEnLaMano = true;
    [Tooltip("Flexion (0..1) a la que los dedos empiezan a tocar el objeto")]
    [Range(0.1f, 0.9f)] public float flexionContacto = 0.4f;

    [Header("Mover la mano con teclado (solo mientras sostiene el objeto)")]
    [Tooltip("W/A/S/D = adelante/izquierda/atras/derecha, Q/E = bajar/subir")]
    public float velocidadMano = 2f;
    public KeyCode teclaRestablecer = KeyCode.R;

    Transform raizMano;
    Vector3 posInicialMano;

    System.Collections.IEnumerator Start() {
        if (mano == null) mano = FindFirstObjectByType<HandDemo>();
        if (mano == null) { Debug.LogError("GrabController: no hay HandDemo en la escena."); yield break; }
        if (palmAnchor == null) {
            // Sin PalmAnchor asignado: se crea uno en la base del dedo medio (centro de la palma)
            Transform baseMedio = mano.middle != null && mano.middle.Length > 0 ? mano.middle[0] : null;
            Transform padre = mano.muneca != null ? mano.muneca : (baseMedio != null ? baseMedio.parent : null);
            if (padre == null) { Debug.LogWarning("GrabController: asigna PalmAnchor en el Inspector."); yield break; }
            palmAnchor = new GameObject("PalmAnchor").transform;
            palmAnchor.SetParent(padre, false);
            if (baseMedio != null) palmAnchor.position = Vector3.Lerp(padre.position, baseMedio.position, 0.5f);
        }
        raizMano = palmAnchor.root;
        posInicialMano = raizMano.position;

        if (objeto == null) objeto = FindFirstObjectByType<Grabbable>();
        yield return null;  // esperar a que HandDemo guarde la pose de reposo de los dedos
        if (objeto != null && colocarEnLaMano) ColocarEnLaMano();
    }

    // Pone el objeto donde se cierran el pulgar, el indice y el medio, atravesado en la palma
    // (eje paralelo al eje en que se doblan los dedos) y del grosor justo para que lo toquen
    // al llegar a 'flexionContacto'.
    void ColocarEnLaMano() {
        if (mano.thumb.Length == 0 || mano.index.Length == 0 || mano.middle.Length == 0) {
            Debug.LogWarning("GrabController: faltan huesos de pulgar/indice/medio; no se coloca el objeto.");
            return;
        }
        Vector3 centro = Vector3.zero;
        for (int f = 0; f < 3; f++) centro += mano.PuntaDoblada(f, 1f);
        centro /= 3f;

        float radio = 0f;
        for (int f = 0; f < 3; f++) radio = Mathf.Max(radio, Vector3.Distance(centro, mano.PuntaDoblada(f, flexionContacto)));

        Transform menique = mano.pinky.Length > 0 ? mano.pinky[0] : mano.middle[0];
        float largo = Mathf.Max(Vector3.Distance(mano.index[0].position, menique.position) * 1.4f, radio * 2f);
        Vector3 eje = mano.middle[0].rotation * mano.flexAxis;

        var t = objeto.transform;
        t.SetParent(null, true);
        t.SetPositionAndRotation(centro, Quaternion.FromToRotation(Vector3.up, eje));
        t.localScale = new Vector3(radio * 2f, largo / 2f, radio * 2f);  // cilindro de Unity: alto 2, diametro 1
        objeto.FijarInicio();
    }

    // Devuelve el objeto (y la mano) a su posicion inicial
    public void Restablecer() {
        // Los contactos no se borran: los sensores de los dedos avisan solos si dejan de tocarlo
        sostenido = null;
        estado = Estado.Libre;
        if (raizMano != null) raizMano.position = posInicialMano;
        if (objeto != null) objeto.Restablecer();
    }

    void MoverMano() {
        if (raizMano == null) return;
        Vector3 d = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) d += Vector3.forward;
        if (Input.GetKey(KeyCode.S)) d += Vector3.back;
        if (Input.GetKey(KeyCode.A)) d += Vector3.left;
        if (Input.GetKey(KeyCode.D)) d += Vector3.right;
        if (Input.GetKey(KeyCode.E)) d += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) d += Vector3.down;
        if (d == Vector3.zero) return;

        // Relativo a la camara, para que "adelante" sea hacia donde mira
        var cam = Camera.main;
        if (cam != null) {
            Vector3 adelante = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
            Vector3 derecha = Vector3.ProjectOnPlane(cam.transform.right, Vector3.up).normalized;
            d = derecha * d.x + Vector3.up * d.y + adelante * d.z;
        }
        raizMano.position += d.normalized * velocidadMano * Time.deltaTime;
    }

    void OnGUI() {
        GUILayout.BeginArea(new Rect(Screen.width - 230, 10, 220, 110), GUI.skin.box);
        GUILayout.Label("Agarre: " + estado);
        GUILayout.Label(estado == Estado.Sostenido ? "Mover: W A S D, Q/E" : "Cierra los dedos para tomarlo");
        if (GUILayout.Button("Restablecer cilindro (" + teclaRestablecer + ")", GUILayout.Height(30))) Restablecer();
        GUILayout.EndArea();
    }

    // Llamado por YemaHaptica al entrar/salir del trigger de un objeto Grabbable
    public void NotificarContacto(int dedo, Grabbable objeto, bool entrando) {
        if (objeto == null || dedo < 0 || dedo >= 5) return;
        if (!sensores.TryGetValue(objeto, out var n)) sensores[objeto] = n = new int[5];
        n[dedo] = Mathf.Max(0, n[dedo] + (entrando ? 1 : -1));

        if (!contactos.TryGetValue(objeto, out var dedos)) {
            dedos = new HashSet<int>();
            contactos[objeto] = dedos;
        }
        if (n[dedo] > 0) dedos.Add(dedo);
        else dedos.Remove(dedo);
        if (dedos.Count == 0) { contactos.Remove(objeto); sensores.Remove(objeto); }
    }

    float FlexionPromedio(IEnumerable<int> dedos) {
        if (mano == null || mano.flexionActual == null) return 0f;
        float suma = 0f; int n = 0;
        foreach (var d in dedos) {
            if (d >= 0 && d < mano.flexionActual.Length) { suma += mano.flexionActual[d]; n++; }
        }
        return n > 0 ? suma / n : 0f;
    }

    void Update() {
        if (mano == null) return;
        if (Input.GetKeyDown(teclaRestablecer)) { Restablecer(); return; }

        if (estado == Estado.Sostenido) {
            MoverMano();
            bool sigueContacto = contactos.TryGetValue(sostenido, out var dedosAhora);
            bool pulgarTocando = sigueContacto && dedosAhora.Contains(0);
            float flexion = sigueContacto ? FlexionPromedio(dedosAhora) : 0f;

            if (!pulgarTocando || flexion < umbralSoltar) {
                sostenido.Soltar();
                sostenido = null;
                estado = contactos.Count > 0 ? Estado.Tocando : Estado.Libre;
            }
            return;
        }

        // Libre / Tocando -> Sostenido
        foreach (var par in contactos) {
            var candidato = par.Key;
            var dedos = par.Value;
            if (!dedos.Contains(0) || dedos.Count < 2) continue; // pulgar + al menos otro dedo
            if (FlexionPromedio(dedos) > umbralAgarrar) {
                candidato.Agarrar(palmAnchor);
                sostenido = candidato;
                estado = Estado.Sostenido;
                return;
            }
        }
        estado = contactos.Count > 0 ? Estado.Tocando : Estado.Libre;
    }
}
