using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Limpieza temporal: quita de la escena abierta los componentes cuyo script ya se borró.
[InitializeOnLoad]
static class HaptiCleanup {
    static HaptiCleanup() { EditorApplication.delayCall += Run; }

    static void Run() {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        int removed = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++) {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) {
                    int n = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                    if (n > 0) { removed += n; Debug.Log("[Limpieza] Quitado script faltante de: " + t.name); }
                }
            if (removed > 0) EditorSceneManager.MarkSceneDirty(scene);
        }
        if (removed > 0) Debug.Log("[Limpieza] Listo: " + removed + " componente(s) quitados. Guarda la escena (Cmd+S).");
    }
}
