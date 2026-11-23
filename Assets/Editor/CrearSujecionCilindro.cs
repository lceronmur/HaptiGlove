using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Crea en la escena abierta el objeto vacio "SujecionCilindro" (punto donde queda el cilindro al
// agarrarlo) si no existe, ya colocado en la palma de la mano. Tambien en el menu HaptiGlove.
// Despues se puede mover/girar a mano: el eje Y (verde) es el largo del cilindro.
[InitializeOnLoad]
static class CrearSujecionCilindro {
    static CrearSujecionCilindro() { EditorApplication.delayCall += () => Crear(false); }

    [MenuItem("HaptiGlove/Crear SujecionCilindro")]
    static void CrearDesdeMenu() { Crear(true); }

    static void Crear(bool avisarSiExiste) {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (GameObject.Find(GrabController.NOMBRE_SUJECION) != null) {
            if (avisarSiExiste) Debug.Log("[HaptiGlove] SujecionCilindro ya existe en la escena.");
            return;
        }
        var mano = Object.FindFirstObjectByType<HandDemo>();
        if (mano == null || mano.middle == null || mano.middle.Length < 2 || mano.middle[0] == null) return;

        var h = mano.middle;
        Transform baseDedo = h[0], punta = h[h.Length - 1];
        float largoDedo = Mathf.Max(Vector3.Distance(baseDedo.position, punta.position), 1e-3f);
        Vector3 aLoLargo = (punta.position - baseDedo.position) / largoDedo;
        Vector3 eje = baseDedo.rotation * mano.flexAxis;  // eje en que se doblan los dedos = a lo ancho de la palma

        // Lado de la palma: hacia donde va la punta al doblar un poco el dedo
        Quaternion antes = baseDedo.localRotation;
        Vector3 p0 = punta.position;
        baseDedo.localRotation = antes * Quaternion.AngleAxis(20f, mano.flexAxis);
        Vector3 palma = Vector3.ProjectOnPlane(punta.position - p0, aLoLargo);
        baseDedo.localRotation = antes;
        palma = palma.sqrMagnitude > 1e-8f ? palma.normalized : Vector3.down;

        // Centro de la palma: un poco antes de los nudillos, separado por el radio del cilindro
        Vector3 pos = baseDedo.position - aLoLargo * (largoDedo * 0.25f) + palma * (largoDedo * 0.15f);

        var go = new GameObject(GrabController.NOMBRE_SUJECION);
        go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(aLoLargo, eje));
        Undo.RegisterCreatedObjectUndo(go, "Crear SujecionCilindro");
        EditorSceneManager.MarkSceneDirty(go.scene);
        Selection.activeGameObject = go;
        Debug.Log("[HaptiGlove] Creado SujecionCilindro en la palma. Ajustalo si hace falta y guarda la escena (Ctrl+S).");
    }
}
