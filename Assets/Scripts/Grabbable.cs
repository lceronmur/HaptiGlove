using UnityEngine;

// Objeto que la mano puede agarrar (seccion 11.4). Lo controla GrabController: al agarrarlo
// se vuelve cinematico y se hace hijo de PalmAnchor; al soltarlo recupera su padre original
// y cae por gravedad. Restablecer() lo devuelve a su posicion inicial, quieto en el aire.
[RequireComponent(typeof(Rigidbody))]
public class Grabbable : MonoBehaviour {
    [HideInInspector] public bool agarrado = false;

    Rigidbody rb;
    Transform padreOriginal;
    Vector3 posInicial;
    Quaternion rotInicial;

    void Awake() {
        rb = GetComponent<Rigidbody>();
        padreOriginal = transform.parent;
        FijarInicio();
    }

    // Guarda la pose actual como la inicial y deja el objeto quieto (sin gravedad) esperando ser tomado
    public void FijarInicio() {
        posInicial = transform.position;
        rotInicial = transform.rotation;
        rb.isKinematic = true;
    }

    public void Agarrar(Transform palmAnchor) {
        if (agarrado) return;
        agarrado = true;
        rb.isKinematic = true;
        if (palmAnchor != null) transform.SetParent(palmAnchor, true);
    }

    public void Soltar() {
        if (!agarrado) return;
        agarrado = false;
        transform.SetParent(padreOriginal, true);
        rb.isKinematic = false;  // cae por gravedad
    }

    public void Restablecer() {
        agarrado = false;
        transform.SetParent(padreOriginal, true);
        if (!rb.isKinematic) {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        rb.isKinematic = true;
        transform.SetPositionAndRotation(posInicial, rotInicial);
    }
}
