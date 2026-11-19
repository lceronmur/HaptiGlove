using UnityEngine;

// Poner en la PUNTA de cada dedo de la mano 3D (hijo del ultimo hueso),
// con un SphereCollider pequeno. Cuando toca otro objeto con Collider,
// hace vibrar el motor de ese dedo en el guante.
[RequireComponent(typeof(Collider))]
public class YemaHaptica : MonoBehaviour {
    public enum Dedo { Pulgar = 0, Indice = 1, Medio = 2, Anular = 3, Menique = 4 }

    [Tooltip("Se busca solo en los padres si se deja vacio")]
    public HandDemo mano;
    public Dedo dedo = Dedo.Indice;

    [Header("Intensidades (0..1)")]
    [Range(0, 1)] public float intensidadContacto = 0.5f;  // mientras sigue tocando
    [Range(0, 1)] public float intensidadImpacto = 1f;     // golpe inicial al tocar
    public float duracionImpacto = 0.12f;

    [Tooltip("Vacio = cualquier objeto. Si se pone un Tag, solo vibra con objetos que lo tengan")]
    public string soloConTag = "";

    int contactos = 0;

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

        if (mano == null) mano = GetComponentInParent<HandDemo>();
        if (mano == null) Debug.LogError("YemaHaptica (" + name + "): asigne el campo 'mano'.");
    }

    bool EsValido(Collider otro) {
        if (otro.GetComponent<YemaHaptica>() != null) return false;          // otra yema
        if (mano != null && otro.transform.IsChildOf(mano.transform)) return false;  // la propia mano
        return string.IsNullOrEmpty(soloConTag) || otro.CompareTag(soloConTag);
    }

    void OnTriggerEnter(Collider otro) {
        if (!EsValido(otro)) return;
        contactos++;
        if (contactos == 1 && mano != null)
            mano.Pulso((int)dedo, intensidadImpacto, duracionImpacto);
    }

    void OnTriggerExit(Collider otro) {
        if (!EsValido(otro)) return;
        contactos = Mathf.Max(0, contactos - 1);
    }

    void Update() {
        if (mano != null) mano.SetContacto((int)dedo, contactos > 0 ? intensidadContacto : 0f);
    }

    void OnDisable() {
        contactos = 0;
        if (mano != null) mano.SetContacto((int)dedo, 0f);
    }
}
