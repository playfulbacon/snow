using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Pins the internal render resolution near the GameCube's 480p output no
/// matter the window size, by driving URP's render scale at runtime. The
/// upscale back to the window uses bilinear filtering, giving the soft
/// CRT-through-component-cables look. Spawns itself on play; no scene setup
/// needed. The pipeline asset's authored render scale is restored on exit so
/// the asset is never left dirty in the editor.
/// </summary>
public class GameCubeLook : MonoBehaviour
{
    const float TargetVerticalResolution = 480f;

    static GameCubeLook s_Instance;

    UniversalRenderPipelineAsset m_Pipeline;
    float m_OriginalRenderScale;
    int m_LastHeight;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (s_Instance != null)
            return;
        var go = new GameObject("GameCubeLook");
        DontDestroyOnLoad(go);
        s_Instance = go.AddComponent<GameCubeLook>();
    }

    void OnEnable()
    {
        m_Pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (m_Pipeline == null)
        {
            enabled = false;
            return;
        }
        m_OriginalRenderScale = m_Pipeline.renderScale;
        Apply();
    }

    void LateUpdate()
    {
        if (Screen.height != m_LastHeight)
            Apply();
    }

    void Apply()
    {
        m_LastHeight = Screen.height;
        if (m_LastHeight > 0)
            m_Pipeline.renderScale = Mathf.Clamp(TargetVerticalResolution / m_LastHeight, 0.1f, 1f);
    }

    void OnDisable()
    {
        if (m_Pipeline != null)
            m_Pipeline.renderScale = m_OriginalRenderScale;
    }
}
