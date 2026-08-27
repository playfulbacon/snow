using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Full-screen height fog pass with access to the frame's global textures
/// (main light shadow map, depth), which the stock FullScreenPassRendererFeature
/// does not declare - its Render Graph pass never calls UseAllGlobalTextures,
/// so shadow sampling in the fog shader would read an unbound texture.
/// </summary>
public class SnowDaysFogFeature : ScriptableRendererFeature
{
    public Material material;

    class FogPass : ScriptableRenderPass
    {
        static readonly int s_BlitTextureId = Shader.PropertyToID("_BlitTexture");
        static readonly int s_BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        static readonly int s_LightCountId = Shader.PropertyToID("_FogAdditionalLightsCount");
        static readonly int s_ShadowDistanceId = Shader.PropertyToID("_FogShadowDistance");
        static readonly MaterialPropertyBlock s_PropertyBlock = new MaterialPropertyBlock();

        Material m_Material;

        public void Setup(Material mat)
        {
            m_Material = mat;
            requiresIntermediateTexture = true;
        }

        class PassData
        {
            public Material material;
            public TextureHandle source;
            public float lightCount;
            public float shadowDistance;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var lightData = frameData.Get<UniversalLightData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            var copyDesc = renderGraph.GetTextureDesc(resources.cameraColor);
            copyDesc.name = "_SnowDaysFogColorCopy";
            copyDesc.clearBuffer = false;
            TextureHandle copy = renderGraph.CreateTexture(copyDesc);
            renderGraph.AddBlitPass(resources.activeColorTexture, copy, Vector2.one, Vector2.zero,
                passName: "Copy Color Snow Fog");

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("SnowDays Height Fog",
                out var passData, profilingSampler))
            {
                passData.material = m_Material;
                passData.source = copy;
                // The raw visible-light count: _AdditionalLightsCount.x is the
                // per-object cap (default 4), not this, and the arrays beyond
                // the packed count hold stale lights from earlier frames.
                passData.lightCount = lightData.additionalLightsCount;
                passData.shadowDistance = cameraData.maxShadowDistance;
                builder.UseTexture(copy, AccessFlags.Read);
                builder.UseAllGlobalTextures(true);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                {
                    s_PropertyBlock.Clear();
                    s_PropertyBlock.SetTexture(s_BlitTextureId, data.source);
                    s_PropertyBlock.SetVector(s_BlitScaleBiasId, new Vector4(1, 1, 0, 0));
                    s_PropertyBlock.SetFloat(s_LightCountId, data.lightCount);
                    s_PropertyBlock.SetFloat(s_ShadowDistanceId, data.shadowDistance);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0,
                        MeshTopology.Triangles, 3, 1, s_PropertyBlock);
                });
            }
        }
    }

    FogPass m_Pass;

    public override void Create()
    {
        m_Pass = new FogPass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (material == null)
            return;
        var camType = renderingData.cameraData.cameraType;
        if (camType == CameraType.Preview || camType == CameraType.Reflection)
            return;
        m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        m_Pass.Setup(material);
        renderer.EnqueuePass(m_Pass);
    }
}
