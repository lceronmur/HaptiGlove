using UnityEngine;

// Sensor de colision de un dedo. HandDemo los crea solo (opcion "Vibrar Al Colisionar")
// en los ultimos huesos de cada dedo; tambien se puede poner a mano en un hueso con un
// SphereCollider. Cuando toca otro objeto con Collider (ej. un cilindro), vibra el
// motor de ese dedo en el guante.
[RequireComponent(typeof(Collider))]
public class YemaHaptica : MonoBehaviour {
    public enum Dedo { Pulgar = 0, Indice = 1, Medio = 2, Anular = 3, Menique = 4 }

    [Tooltip("Si se deja vacio se busca el HandDemo de la escena")]
    public HandDemo mano;
    public Dedo dedo = Dedo.Indice;

    [Tooltip("Vacio = cualquier objeto. Si se pone un Tag, solo vibra con objetos que lo tengan")]
    public string soloConTag = "";

    int contactos = 0;

    // Usado por HandDemo al crear los sensores automaticamente
    public void Configurar(HandDemo m, int d, string tag) {
        mano = m;
        dedo = (Dedo)d;
        soloConTag = tag;
    }

    void Reset() {
        GetComponent<Collider>().isTrigger = true;
    }

    void Awake() {
        GetComponent<Collider>().isTrigger = true;

        // Los triggers necesitan un Rigidbody en al menos uno de los dos objetos
        var rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
    }

    void Start() {
        if (mano == null) mano = GetComponentInParent<HandDemo>();
        if (mano == null) mano = FindFirstObjectByType<HandDemo>();
        if (mano == null) Debug.LogError("YemaHaptica (" + name + "): no hay HandDemo en la escena.");
    }

    bool EsValido(Collider otro) {
        if (otro.isTrigger) return false;                                  // otros sensores / zonas
        if (otro.GetComponent<YemaHaptica>() != null) return false;        // otra yema
        if (mano != null && mano.EsParteDeLaMano(otro.transform)) return false;  // la propia mano
        return string.IsNullOrEmpty(soloConTag) || otro.CompareTag(soloConTag);
    }

    void OnTriggerEnter(Collider otro) {
        if (!EsValido(otro)) return;
        contactos++;
        if (contactos == 1 && mano != null) mano.Tocar((int)dedo, true);
    }

    void OnTriggerExit(Collider otro) {
        if (!EsValido(otro)) return;
        if (contactos == 0) return;
        contactos--;
        if (contactos == 0 && mano != null) mano.Tocar((int)dedo, false);
    }

    void OnDisable() {
        if (contactos > 0 && mano != null) mano.Tocar((int)dedo, false);
        contactos = 0;
    }
}
