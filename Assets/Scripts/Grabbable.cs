using UnityEngine;

// Objeto que la mano puede agarrar (seccion 11.4). Lo controla GrabController: al agarrarlo
// se vuelve cinematico y se hace hijo de PalmAnchor; al soltarlo recupera su fisica normal
// y su padre original, y cae por gravedad.
[RequireComponent(typeof(Rigidbody))]
public class Grabbable : MonoBehaviour {
    [HideInInspector] public bool agarrado = false;

    Rigidbody rb;
    Transform padreOriginal;
    bool eraKinematic;

    void Awake() {
        rb = GetComponent<Rigidbody>();
        padreOriginal = transform.parent;
    }

    public void Agarrar(Transform palmAnchor) {
        if (agarrado) return;
        agarrado = true;
        eraKinematic = rb.isKinematic;
        rb.isKinematic = true;
        if (palmAnchor != null) transform.SetParent(palmAnchor, true);
    }

    public void Soltar() {
        if (!agarrado) return;
        agarrado = false;
        transform.SetParent(padreOriginal, true);
        rb.isKinematic = eraKinematic;
    }
}
