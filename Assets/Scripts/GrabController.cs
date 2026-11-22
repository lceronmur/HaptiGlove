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

    void Start() {
        if (mano == null) mano = FindFirstObjectByType<HandDemo>();
        if (mano == null) { Debug.LogError("GrabController: no hay HandDemo en la escena."); return; }
        if (palmAnchor == null) {
            // Sin PalmAnchor asignado: se crea uno en la base del dedo medio (centro de la palma)
            Transform baseMedio = mano.middle != null && mano.middle.Length > 0 ? mano.middle[0] : null;
            Transform padre = mano.muneca != null ? mano.muneca : (baseMedio != null ? baseMedio.parent : null);
            if (padre == null) { Debug.LogWarning("GrabController: asigna PalmAnchor en el Inspector."); return; }
            palmAnchor = new GameObject("PalmAnchor").transform;
            palmAnchor.SetParent(padre, false);
            if (baseMedio != null) palmAnchor.position = Vector3.Lerp(padre.position, baseMedio.position, 0.5f);
        }
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

        if (estado == Estado.Sostenido) {
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
            var objeto = par.Key;
            var dedos = par.Value;
            if (!dedos.Contains(0) || dedos.Count < 2) continue; // pulgar + al menos otro dedo
            if (FlexionPromedio(dedos) > umbralAgarrar) {
                objeto.Agarrar(palmAnchor);
                sostenido = objeto;
                estado = Estado.Sostenido;
                return;
            }
        }
        estado = contactos.Count > 0 ? Estado.Tocando : Estado.Libre;
    }
}
